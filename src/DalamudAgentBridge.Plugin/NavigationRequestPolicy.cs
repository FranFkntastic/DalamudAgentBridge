using System;
using System.Text.Json;

namespace DalamudAgentBridge.Plugin;

public sealed record NavigationPointRequest(
    uint TerritoryType,
    float X,
    float Y,
    float Z,
    float ArrivalRadius,
    int TimeoutSeconds);

public sealed record NavigationRequestValidation(
    bool Success,
    string Code,
    string Message,
    NavigationPointRequest? Request = null);

/// <summary>Pure wire-request validation for same-territory world-space navigation.</summary>
public static class NavigationRequestPolicy
{
    public const float MinimumCoordinate = -100_000f;
    public const float MaximumCoordinate = 100_000f;
    public const float MinimumArrivalRadius = 0.5f;
    public const float MaximumArrivalRadius = 50f;
    public const int MinimumTimeoutSeconds = 5;
    public const int MaximumTimeoutSeconds = 900;

    public static NavigationRequestValidation Validate(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } value)
            return Fail("InvalidRequest", "Navigation arguments must be a JSON object.");
        if (!TryReadUInt32(value, "territoryType", out var territoryType) || territoryType == 0)
            return Fail("InvalidTerritory", "A non-zero territoryType is required.");
        if (!TryReadFiniteSingle(value, "x", out var x) ||
            !TryReadFiniteSingle(value, "y", out var y) ||
            !TryReadFiniteSingle(value, "z", out var z))
            return Fail("InvalidCoordinates", "Finite x, y, and z world coordinates are required.");
        if (!WithinCoordinateBounds(x) || !WithinCoordinateBounds(y) || !WithinCoordinateBounds(z))
            return Fail("CoordinatesOutOfRange", $"World coordinates must be between {MinimumCoordinate} and {MaximumCoordinate}.");

        var radius = 1.5f;
        if (value.TryGetProperty("arrivalRadius", out var radiusValue) &&
            (!radiusValue.TryGetSingle(out radius) || !float.IsFinite(radius) || radius is < MinimumArrivalRadius or > MaximumArrivalRadius))
            return Fail("InvalidArrivalRadius", $"arrivalRadius must be between {MinimumArrivalRadius} and {MaximumArrivalRadius} yalms.");

        var timeoutSeconds = 120;
        if (value.TryGetProperty("timeoutSeconds", out var timeoutValue) &&
            (!timeoutValue.TryGetInt32(out timeoutSeconds) || timeoutSeconds is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds))
            return Fail("InvalidTimeout", $"timeoutSeconds must be between {MinimumTimeoutSeconds} and {MaximumTimeoutSeconds}.");

        return new NavigationRequestValidation(
            true,
            "Valid",
            "Navigation request is valid.",
            new NavigationPointRequest(territoryType, x, y, z, radius, timeoutSeconds));
    }

    private static bool WithinCoordinateBounds(float value) =>
        value is >= MinimumCoordinate and <= MaximumCoordinate;

    private static bool TryReadUInt32(JsonElement value, string name, out uint result)
    {
        result = default;
        return value.TryGetProperty(name, out var property) && property.TryGetUInt32(out result);
    }

    private static bool TryReadFiniteSingle(JsonElement value, string name, out float result)
    {
        result = default;
        return value.TryGetProperty(name, out var property) && property.TryGetSingle(out result) && float.IsFinite(result);
    }

    private static NavigationRequestValidation Fail(string code, string message) =>
        new(false, code, message);
}
