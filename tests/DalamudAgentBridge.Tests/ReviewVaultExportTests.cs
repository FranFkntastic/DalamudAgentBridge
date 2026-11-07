using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class ReviewVaultExportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dab-export-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExportWritesVerifiedBytesAndRefusesOverwrite()
    {
        var vault = CreateVault();
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10];
        var review = vault.Store(new BridgeCaptureReceipt { Sha256 = Convert.ToHexString(SHA256.HashData(png)) }, png);
        var output = Path.Combine(root, "export", "capture.png");
        vault.Export(review, output);
        Assert.Equal(png, File.ReadAllBytes(output));
        Assert.Throws<IOException>(() => vault.Export(review, output));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.tmp"));
    }

    [Fact]
    public void HashMismatchDoesNotWritePlaintext()
    {
        var vault = CreateVault();
        var review = vault.Store(new BridgeCaptureReceipt { Sha256 = "incorrect" }, new byte[] { 1, 2, 3 });
        var output = Path.Combine(root, "capture.png");
        Assert.Throws<InvalidDataException>(() => vault.Export(review, output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void DeletedReviewDoesNotWritePlaintext()
    {
        var vault = CreateVault();
        var review = vault.Store(new BridgeCaptureReceipt(), new byte[] { 1 });
        vault.Delete(review.Id);
        Assert.Throws<InvalidDataException>(() => vault.Export(review, Path.Combine(root, "capture.png")));
    }

    private ReviewVault CreateVault() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Bridge:ReviewVaultRoot"] = Path.Combine(root, "vault") }).Build());

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
