using System.Security.Cryptography;
using System.Text.Json;
using Franthropy.Dalamud.AgentBridge;

namespace DalamudAgentBridge;

/// <summary>
/// Persists review captures only as DPAPI-encrypted data for the current Windows user.
/// Plain PNG bytes exist only while a request is importing or streaming a capture.
/// </summary>
public sealed class ReviewVault
{
    private const string ProtectionContext = "DalamudAgentBridge.ReviewVault.v1";
    private const int DefaultRetentionMinutes = 30;
    private readonly string rootDirectory;
    private readonly TimeSpan retention;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();

    public ReviewVault(IConfiguration configuration)
    {
        rootDirectory = configuration["Bridge:ReviewVaultRoot"] ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DalamudAgentBridge", "review-vault");
        retention = TimeSpan.FromMinutes(ParseRetentionMinutes(configuration["Bridge:ReviewRetentionMinutes"]));
    }

    public ReviewCapture Store(BridgeCaptureReceipt receipt, ReadOnlySpan<byte> pngBytes)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (pngBytes.IsEmpty)
            throw new ArgumentException("A review capture cannot be empty.", nameof(pngBytes));

        lock (gate)
        {
            PurgeExpiredCore();
            Directory.CreateDirectory(rootDirectory);

            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var review = new ReviewCapture(id, receipt, DateTimeOffset.UtcNow.Add(retention));
            var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(review, jsonOptions);
            var protectedMetadata = AgentBridgeDataProtection.ProtectBytes(metadataBytes, ProtectionContext);
            var protectedPng = AgentBridgeDataProtection.ProtectBytes(pngBytes, ProtectionContext);
            try
            {
                WriteAtomically(PngPath(id), protectedPng);
                try { WriteAtomically(MetadataPath(id), protectedMetadata); }
                catch
                {
                    DeleteIfExists(PngPath(id));
                    throw;
                }
                return review;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(metadataBytes);
                CryptographicOperations.ZeroMemory(protectedMetadata);
                CryptographicOperations.ZeroMemory(protectedPng);
            }
        }
    }

    public IReadOnlyList<ReviewCapture> List()
    {
        lock (gate)
        {
            PurgeExpiredCore();
            if (!Directory.Exists(rootDirectory))
                return [];

            return Directory.EnumerateFiles(rootDirectory, "*.metadata.dpapi")
                .Select(ReadMetadata)
                .Where(review => review != null)
                .Cast<ReviewCapture>()
                .OrderByDescending(review => review.Receipt.CapturedAtUtc)
                .ToArray();
        }
    }

    public bool TryRead(string id, out byte[] pngBytes)
    {
        pngBytes = [];
        if (!IsValidId(id))
            return false;

        lock (gate)
        {
            PurgeExpiredCore();
            var review = ReadMetadata(MetadataPath(id));
            if (review == null || review.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                return false;

            try
            {
                var protectedPng = File.ReadAllBytes(PngPath(id));
                try
                {
                    pngBytes = AgentBridgeDataProtection.UnprotectBytes(protectedPng, ProtectionContext);
                    return true;
                }
                finally { CryptographicOperations.ZeroMemory(protectedPng); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                CryptographicOperations.ZeroMemory(pngBytes);
                pngBytes = [];
                return false;
            }
        }
    }

    public bool Delete(string id)
    {
        if (!IsValidId(id))
            return false;

        lock (gate)
        {
            var exists = File.Exists(PngPath(id)) || File.Exists(MetadataPath(id));
            DeleteCore(id);
            return exists;
        }
    }

    private void PurgeExpiredCore()
    {
        if (!Directory.Exists(rootDirectory))
            return;

        foreach (var imagePath in Directory.EnumerateFiles(rootDirectory, "*.image.dpapi"))
        {
            var id = Path.GetFileName(imagePath).Replace(".image.dpapi", string.Empty, StringComparison.Ordinal);
            if (!IsValidId(id) || !File.Exists(MetadataPath(id)))
                DeleteIfExists(imagePath);
        }

        foreach (var metadataPath in Directory.EnumerateFiles(rootDirectory, "*.metadata.dpapi"))
        {
            var review = ReadMetadata(metadataPath);
            if (review?.ExpiresAtUtc > DateTimeOffset.UtcNow)
                continue;
            var id = Path.GetFileName(metadataPath).Replace(".metadata.dpapi", string.Empty, StringComparison.Ordinal);
            if (IsValidId(id))
                DeleteCore(id);
            else
                DeleteIfExists(metadataPath);
        }
    }

    private void DeleteCore(string id)
    {
        DeleteIfExists(PngPath(id));
        DeleteIfExists(MetadataPath(id));
    }

    private ReviewCapture? ReadMetadata(string metadataPath)
    {
        try
        {
            var protectedMetadata = File.ReadAllBytes(metadataPath);
            try
            {
                var metadataBytes = AgentBridgeDataProtection.UnprotectBytes(protectedMetadata, ProtectionContext);
                try { return JsonSerializer.Deserialize<ReviewCapture>(metadataBytes, jsonOptions); }
                finally { CryptographicOperations.ZeroMemory(metadataBytes); }
            }
            finally { CryptographicOperations.ZeroMemory(protectedMetadata); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    private string PngPath(string id) => Path.Combine(rootDirectory, $"{id}.image.dpapi");

    private string MetadataPath(string id) => Path.Combine(rootDirectory, $"{id}.metadata.dpapi");

    private static void WriteAtomically(string destination, byte[] bytes)
    {
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, destination);
        }
        finally { DeleteIfExists(temporary); }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static bool IsValidId(string id) => id.Length == 64 && id.All(Uri.IsHexDigit);

    private static int ParseRetentionMinutes(string? value) =>
        int.TryParse(value, out var minutes) ? Math.Clamp(minutes, 1, 1440) : DefaultRetentionMinutes;
}

public sealed record ReviewCapture(string Id, BridgeCaptureReceipt Receipt, DateTimeOffset ExpiresAtUtc);

public sealed class LocalDashboardSession
{
    private const string CookieName = "dab_session";
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public void Establish(HttpContext context)
    {
        if (IsAuthenticated(context))
            return;
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Strict,
            Secure = false,
            Path = "/",
        });
    }

    public bool IsAuthenticated(HttpContext context)
    {
        var value = context.Request.Cookies[CookieName];
        return value != null && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(value),
            System.Text.Encoding.UTF8.GetBytes(token));
    }
}
