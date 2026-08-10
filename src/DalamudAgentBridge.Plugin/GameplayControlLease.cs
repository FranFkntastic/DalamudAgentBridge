using System;

namespace DalamudAgentBridge.Plugin;

public sealed record GameplayControlLeaseSnapshot(
    string? OperationId,
    string? Owner,
    string? CapabilityId,
    DateTimeOffset? AcquiredAtUtc)
{
    public bool IsOwned => !string.IsNullOrWhiteSpace(OperationId);
}

/// <summary>Serializes DAB-issued controllers so navigation and specialists cannot fight over the character.</summary>
public sealed class GameplayControlLease
{
    private readonly object gate = new();
    private GameplayControlLeaseSnapshot current = new(null, null, null, null);

    public GameplayControlLeaseSnapshot Observe()
    {
        lock (gate)
            return current;
    }

    public bool TryAcquire(string operationId, string owner, string capabilityId, out GameplayControlLeaseSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        lock (gate)
        {
            if (current.IsOwned)
            {
                snapshot = current;
                return false;
            }

            current = new GameplayControlLeaseSnapshot(operationId, owner, capabilityId, DateTimeOffset.UtcNow);
            snapshot = current;
            return true;
        }
    }

    public bool Release(string operationId)
    {
        lock (gate)
        {
            if (!string.Equals(current.OperationId, operationId, StringComparison.Ordinal))
                return false;
            current = new GameplayControlLeaseSnapshot(null, null, null, null);
            return true;
        }
    }
}
