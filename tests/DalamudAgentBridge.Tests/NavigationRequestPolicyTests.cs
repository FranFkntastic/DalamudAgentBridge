using DalamudAgentBridge.Plugin;
using System.Text.Json;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class NavigationRequestPolicyTests
{
    [Fact]
    public void ValidatesExplicitSameTerritoryWorldPoint()
    {
        using var document = JsonDocument.Parse("""
            {"territoryType":129,"x":12.5,"y":3,"z":-8.25,"arrivalRadius":2,"timeoutSeconds":45}
            """);

        var result = NavigationRequestPolicy.Validate(document.RootElement);

        Assert.True(result.Success);
        Assert.Equal(new NavigationPointRequest(129, 12.5f, 3f, -8.25f, 2f, 45), result.Request);
    }

    [Theory]
    [InlineData("{}", "InvalidTerritory")]
    [InlineData("{\"territoryType\":129,\"x\":1,\"y\":2}", "InvalidCoordinates")]
    [InlineData("{\"territoryType\":129,\"x\":100001,\"y\":2,\"z\":3}", "CoordinatesOutOfRange")]
    [InlineData("{\"territoryType\":129,\"x\":1,\"y\":2,\"z\":3,\"arrivalRadius\":0.1}", "InvalidArrivalRadius")]
    [InlineData("{\"territoryType\":129,\"x\":1,\"y\":2,\"z\":3,\"timeoutSeconds\":901}", "InvalidTimeout")]
    public void RejectsMalformedOrUnboundedRequests(string json, string code)
    {
        using var document = JsonDocument.Parse(json);

        var result = NavigationRequestPolicy.Validate(document.RootElement);

        Assert.False(result.Success);
        Assert.Equal(code, result.Code);
        Assert.Null(result.Request);
    }
}
