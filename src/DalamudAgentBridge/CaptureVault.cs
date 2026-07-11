using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DalamudAgentBridge;

public sealed class CaptureVault : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(45);
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public string Store(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        PurgeExpired();
        var handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (!entries.TryAdd(handle, new Entry(pngBytes, DateTimeOffset.UtcNow.Add(Lifetime))))
            throw new InvalidOperationException("Could not allocate a capture delivery handle.");
        return handle;
    }

    public bool TryTake(string handle, out byte[] bytes)
    {
        bytes = [];
        if (!entries.TryRemove(handle, out var entry))
            return false;
        if (entry.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            CryptographicOperations.ZeroMemory(entry.Bytes);
            return false;
        }

        bytes = entry.Bytes;
        return true;
    }

    public void Dispose()
    {
        foreach (var entry in entries.Values)
            CryptographicOperations.ZeroMemory(entry.Bytes);
        entries.Clear();
    }

    private void PurgeExpired()
    {
        foreach (var pair in entries)
        {
            if (pair.Value.ExpiresAtUtc > DateTimeOffset.UtcNow || !entries.TryRemove(pair.Key, out var entry))
                continue;
            CryptographicOperations.ZeroMemory(entry.Bytes);
        }
    }

    private sealed record Entry(byte[] Bytes, DateTimeOffset ExpiresAtUtc);
}

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
