using Franthropy.Dalamud.AgentBridge;
using System;
using System.Threading.Tasks;

namespace DalamudAgentBridge.Plugin;

public sealed class CharacterProvisioningBridgeCommand(
    Func<object> createSnapshot,
    Func<Func<object>, Task<object>> onFrameworkAsync)
{
    public void Register(AgentBridgeCommandRouter router) =>
        router.Register("get-character-provisioning", async (_, _) =>
            AgentBridgeResponse.Ok(
                "Rendered character-provisioning state captured without mutating the client.",
                await onFrameworkAsync(createSnapshot).ConfigureAwait(false)));
}
