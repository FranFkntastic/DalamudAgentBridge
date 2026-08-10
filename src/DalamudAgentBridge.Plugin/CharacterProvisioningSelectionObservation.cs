using System;
using System.Collections.Generic;
using System.Linq;

namespace DalamudAgentBridge.Plugin;

public sealed record CharacterProvisioningSelectionObservation(
    int SchemaVersion,
    string? SelectedWorld,
    string? SelectedChoice,
    string Source,
    string Status);

public sealed record CharacterProvisioningSelectionCandidate(
    string SelectedWorld,
    string SelectedChoice,
    string Source);

public static class CharacterProvisioningSelectionResolver
{
    public static CharacterProvisioningSelectionObservation Resolve(
        IEnumerable<CharacterProvisioningSelectionCandidate> candidates)
    {
        var selections = candidates.Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.SelectedWorld) &&
                !string.IsNullOrWhiteSpace(candidate.SelectedChoice))
            .ToArray();
        return selections.Length switch
        {
            1 => new(1, selections[0].SelectedWorld, selections[0].SelectedChoice, selections[0].Source, "ok"),
            0 => new(1, null, null, "AddonSelectionState", "unknown"),
            _ => new(1, null, null, "AddonSelectionState", "ambiguous"),
        };
    }
}
