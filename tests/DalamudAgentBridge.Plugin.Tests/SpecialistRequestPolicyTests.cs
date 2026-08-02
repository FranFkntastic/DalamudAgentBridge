using DalamudAgentBridge.Plugin;
using System.Text.Json;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class SpecialistRequestPolicyTests
{
    private static readonly SpecialistCapabilityDescriptor Capability = new(
        "test.run",
        "TestPlugin",
        "Run test",
        "Run a fake specialist.",
        "Test",
        60,
        [
            new("name", SpecialistArgumentKind.String, "Name", MaximumLength: 8),
            new("count", SpecialistArgumentKind.Integer, "Count", DefaultValue: "2", Minimum: 1, Maximum: 5),
            new("enabled", SpecialistArgumentKind.Boolean, "Enabled", DefaultValue: "true"),
        ]);

    [Fact]
    public void ValidatesAndNormalizesDeclaredParameters()
    {
        var envelope = JsonSerializer.SerializeToElement(new
        {
            timeoutSeconds = 90,
            parameters = new { name = " alpha " },
        });

        var result = SpecialistRequestPolicy.Validate("test.run", envelope, [Capability]);

        Assert.True(result.Success);
        Assert.Equal(90, result.Request!.TimeoutSeconds);
        Assert.Equal("alpha", result.Request.Parameters.GetProperty("name").GetString());
        Assert.Equal(2, result.Request.Parameters.GetProperty("count").GetInt32());
        Assert.True(result.Request.Parameters.GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData("missing", "UnsupportedCapability")]
    [InlineData("", "CapabilityRequired")]
    public void RejectsUnknownOrMissingCapabilities(string capabilityId, string code)
    {
        var envelope = JsonSerializer.SerializeToElement(new { parameters = new { name = "alpha" } });

        var result = SpecialistRequestPolicy.Validate(capabilityId, envelope, [Capability]);

        Assert.False(result.Success);
        Assert.Equal(code, result.Code);
    }

    [Fact]
    public void RejectsUndeclaredParameters()
    {
        var envelope = JsonSerializer.SerializeToElement(new
        {
            parameters = new { name = "alpha", rawIpc = "Anything.Invoke" },
        });

        var result = SpecialistRequestPolicy.Validate("test.run", envelope, [Capability]);

        Assert.False(result.Success);
        Assert.Equal("UnknownParameter", result.Code);
    }

    [Fact]
    public void RejectsOutOfBoundsValuesAndTimeouts()
    {
        var badCount = JsonSerializer.SerializeToElement(new
        {
            parameters = new { name = "alpha", count = 99 },
        });
        var badTimeout = JsonSerializer.SerializeToElement(new
        {
            timeoutSeconds = 14_401,
            parameters = new { name = "alpha" },
        });

        Assert.Equal("InvalidParameter", SpecialistRequestPolicy.Validate("test.run", badCount, [Capability]).Code);
        Assert.Equal("InvalidTimeout", SpecialistRequestPolicy.Validate("test.run", badTimeout, [Capability]).Code);
    }
}
