using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace DalamudAgentBridge.Plugin;

/// <summary>Pure descriptor-driven validation for reviewed specialist capabilities.</summary>
public static class SpecialistRequestPolicy
{
    public const int MinimumTimeoutSeconds = 15;
    public const int MaximumTimeoutSeconds = 14_400;

    public static SpecialistRequestValidation Validate(
        string? capabilityId,
        JsonElement? envelope,
        IReadOnlyList<SpecialistCapabilityDescriptor> catalog)
    {
        if (string.IsNullOrWhiteSpace(capabilityId))
            return Fail("CapabilityRequired", "A specialist capability id is required.");
        var capability = catalog.SingleOrDefault(value => string.Equals(value.Id, capabilityId, StringComparison.Ordinal));
        if (capability is null)
            return Fail("UnsupportedCapability", "The requested specialist capability is not in DAB's reviewed adapter catalog.");
        if (envelope is not { ValueKind: JsonValueKind.Object } value)
            return Fail("InvalidRequest", "Specialist arguments must be a JSON object.");

        var timeoutSeconds = capability.DefaultTimeoutSeconds;
        if (value.TryGetProperty("timeoutSeconds", out var timeoutValue) &&
            (!timeoutValue.TryGetInt32(out timeoutSeconds) || timeoutSeconds is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds))
            return Fail("InvalidTimeout", $"timeoutSeconds must be between {MinimumTimeoutSeconds} and {MaximumTimeoutSeconds}.");
        var parameters = value.TryGetProperty("parameters", out var parameterValue) ? parameterValue : default;
        if (parameters.ValueKind is JsonValueKind.Undefined)
            parameters = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        if (parameters.ValueKind is not JsonValueKind.Object)
            return Fail("InvalidParameters", "parameters must be a JSON object.");

        var allowedNames = capability.Arguments.Select(argument => argument.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var property in parameters.EnumerateObject())
            if (!allowedNames.Contains(property.Name))
                return Fail("UnknownParameter", $"Parameter '{property.Name}' is not declared for {capability.Id}.");

        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var argument in capability.Arguments)
        {
            if (!parameters.TryGetProperty(argument.Name, out var property))
            {
                if (argument.DefaultValue is not null)
                {
                    normalized[argument.Name] = ParseDefault(argument);
                    continue;
                }
                if (argument.Required)
                    return Fail("MissingParameter", $"Parameter '{argument.Name}' is required for {capability.Id}.");
                continue;
            }

            if (!TryNormalize(argument, property, out var result, out var error))
                return Fail("InvalidParameter", error!);
            normalized[argument.Name] = result;
        }

        return new SpecialistRequestValidation(
            true,
            "Valid",
            "Specialist request is valid.",
            new SpecialistStartEnvelope(capability.Id, JsonSerializer.SerializeToElement(normalized), timeoutSeconds));
    }

    private static bool TryNormalize(
        SpecialistArgumentDescriptor descriptor,
        JsonElement value,
        out object? result,
        out string? error)
    {
        result = null;
        error = null;
        switch (descriptor.Kind)
        {
            case SpecialistArgumentKind.String:
                if (value.ValueKind is not JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    return Invalid($"Parameter '{descriptor.Name}' must be a non-empty string.", out error);
                var text = value.GetString()!.Trim();
                if (text.Length > descriptor.MaximumLength)
                    return Invalid($"Parameter '{descriptor.Name}' must be at most {descriptor.MaximumLength} characters.", out error);
                result = text;
                return true;
            case SpecialistArgumentKind.UInt32:
                if (!value.TryGetUInt32(out var unsigned) || !Within(unsigned, descriptor))
                    return Invalid(NumberError(descriptor), out error);
                result = unsigned;
                return true;
            case SpecialistArgumentKind.Integer:
                if (!value.TryGetInt32(out var integer) || !Within(integer, descriptor))
                    return Invalid(NumberError(descriptor), out error);
                result = integer;
                return true;
            case SpecialistArgumentKind.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Invalid($"Parameter '{descriptor.Name}' must be true or false.", out error);
                result = value.GetBoolean();
                return true;
            default:
                return Invalid($"Parameter '{descriptor.Name}' uses an unsupported argument kind.", out error);
        }
    }

    private static object ParseDefault(SpecialistArgumentDescriptor descriptor) => descriptor.Kind switch
    {
        SpecialistArgumentKind.String => descriptor.DefaultValue!,
        SpecialistArgumentKind.UInt32 => uint.Parse(descriptor.DefaultValue!, CultureInfo.InvariantCulture),
        SpecialistArgumentKind.Integer => int.Parse(descriptor.DefaultValue!, CultureInfo.InvariantCulture),
        SpecialistArgumentKind.Boolean => bool.Parse(descriptor.DefaultValue!),
        _ => throw new InvalidOperationException("Unsupported specialist default value kind."),
    };

    private static bool Within(long value, SpecialistArgumentDescriptor descriptor) =>
        (!descriptor.Minimum.HasValue || value >= descriptor.Minimum.Value) &&
        (!descriptor.Maximum.HasValue || value <= descriptor.Maximum.Value);

    private static string NumberError(SpecialistArgumentDescriptor descriptor) =>
        $"Parameter '{descriptor.Name}' must be a whole number" +
        (descriptor.Minimum.HasValue || descriptor.Maximum.HasValue
            ? $" between {descriptor.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "the minimum"} and {descriptor.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "the maximum"}."
            : ".");

    private static bool Invalid(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static SpecialistRequestValidation Fail(string code, string message) => new(false, code, message);
}
