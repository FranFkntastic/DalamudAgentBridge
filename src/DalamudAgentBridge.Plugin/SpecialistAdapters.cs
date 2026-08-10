using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DalamudAgentBridge.Plugin;

public static class SpecialistAdapters
{
    public static IReadOnlyList<ISpecialistAdapter> Create(IDalamudPluginInterface pluginInterface) =>
    [
        new QuestionableSpecialistAdapter(pluginInterface),
        new AutoDutySpecialistAdapter(pluginInterface),
        new HenchmanSpecialistAdapter(pluginInterface),
        new LifestreamSpecialistAdapter(pluginInterface),
    ];
}

internal abstract class SpecialistAdapterBase(IDalamudPluginInterface pluginInterface) : ISpecialistAdapter
{
    protected IDalamudPluginInterface PluginInterface { get; } = pluginInterface;
    public abstract string Plugin { get; }
    public abstract IReadOnlyList<SpecialistCapabilityDescriptor> Capabilities { get; }
    public abstract SpecialistPluginObservation Observe();
    public abstract SpecialistAdapterStartResult TryStart(string capabilityId, JsonElement parameters);
    public abstract SpecialistAdapterCancelResult TryCancel();

    protected (bool Installed, bool Loaded, string? Version) Identity()
    {
        var plugin = PluginInterface.InstalledPlugins.FirstOrDefault(value =>
            string.Equals(value.InternalName, Plugin, StringComparison.OrdinalIgnoreCase));
        return plugin is null
            ? (false, false, null)
            : (true, plugin.IsLoaded, plugin.Version.ToString());
    }

    protected SpecialistPluginObservation Unavailable(
        (bool Installed, bool Loaded, string? Version) identity,
        string? message = null) =>
        new(
            Plugin,
            identity.Version,
            identity.Installed,
            identity.Loaded,
            false,
            false,
            identity.Installed ? "PluginNotLoaded" : "PluginNotInstalled",
            message ?? (identity.Installed ? $"{Plugin} is installed but not loaded." : $"{Plugin} is not installed."),
            new Dictionary<string, string?>(),
            DateTimeOffset.UtcNow);

    protected SpecialistPluginObservation Compatible(
        (bool Installed, bool Loaded, string? Version) identity,
        bool busy,
        string code,
        string message,
        IReadOnlyDictionary<string, string?>? details = null) =>
        new(
            Plugin,
            identity.Version,
            identity.Installed,
            identity.Loaded,
            true,
            busy,
            code,
            message,
            details ?? new Dictionary<string, string?>(),
            DateTimeOffset.UtcNow);

    protected SpecialistPluginObservation Incompatible(
        (bool Installed, bool Loaded, string? Version) identity,
        Exception exception) =>
        new(
            Plugin,
            identity.Version,
            identity.Installed,
            identity.Loaded,
            false,
            false,
            "IpcContractUnavailable",
            $"{Plugin}'s reviewed IPC contract is unavailable: {exception.Message}",
            new Dictionary<string, string?>(),
            DateTimeOffset.UtcNow);

    protected SpecialistAdapterStartResult StartCall(Func<bool> action, string acceptedMessage)
    {
        try
        {
            return action()
                ? new(true, "Accepted", acceptedMessage)
                : new(false, "PluginRefused", $"{Plugin} refused the requested operation.");
        }
        catch (Exception exception)
        {
            return new(false, "IpcInvocationFailed", $"{Plugin} IPC failed: {exception.Message}");
        }
    }

    protected SpecialistAdapterStartResult StartAction(Action action, string acceptedMessage)
    {
        try
        {
            action();
            return new(true, "Accepted", acceptedMessage);
        }
        catch (Exception exception)
        {
            return new(false, "IpcInvocationFailed", $"{Plugin} IPC failed: {exception.Message}");
        }
    }

    protected SpecialistAdapterCancelResult CancelCall(Func<bool> action)
    {
        try
        {
            return action()
                ? new(true, "CancellationRequested", $"{Plugin} accepted cancellation.")
                : new(false, "CancelRefused", $"{Plugin} refused cancellation.");
        }
        catch (Exception exception)
        {
            return new(false, "CancelFailed", $"{Plugin} cancellation failed: {exception.Message}");
        }
    }

    protected SpecialistAdapterCancelResult CancelAction(Action action)
    {
        try
        {
            action();
            return new(true, "CancellationRequested", $"{Plugin} accepted cancellation.");
        }
        catch (Exception exception)
        {
            return new(false, "CancelFailed", $"{Plugin} cancellation failed: {exception.Message}");
        }
    }
}

internal sealed class QuestionableSpecialistAdapter : SpecialistAdapterBase
{
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<bool> isRunning;
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<string?> currentQuest;
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<string, bool> startSingleQuest;
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<string, bool> stop;

    public QuestionableSpecialistAdapter(IDalamudPluginInterface pluginInterface) : base(pluginInterface)
    {
        isRunning = pluginInterface.GetIpcSubscriber<bool>("Questionable.IsRunning");
        currentQuest = pluginInterface.GetIpcSubscriber<string?>("Questionable.GetCurrentQuestId");
        startSingleQuest = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.StartSingleQuest");
        stop = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.Stop");
    }

    public override string Plugin => "Questionable";
    public override IReadOnlyList<SpecialistCapabilityDescriptor> Capabilities { get; } =
    [
        new(
            "questionable.single-quest",
            "Questionable",
            "Run one supported quest",
            "Ask Questionable to execute one explicit supported quest and stop at its terminal boundary.",
            "QuestAutomation",
            3_600,
            [new("questId", SpecialistArgumentKind.String, "Questionable quest id, for example 1234.", MaximumLength: 32)]),
    ];

    public override SpecialistPluginObservation Observe()
    {
        var identity = Identity();
        if (!identity.Loaded)
            return Unavailable(identity);
        try
        {
            var running = isRunning.InvokeFunc();
            return Compatible(
                identity,
                running,
                running ? "Running" : "Idle",
                running ? "Questionable is executing quest automation." : "Questionable is idle.",
                new Dictionary<string, string?> { ["currentQuestId"] = currentQuest.InvokeFunc() });
        }
        catch (Exception exception)
        {
            return Incompatible(identity, exception);
        }
    }

    public override SpecialistAdapterStartResult TryStart(string capabilityId, JsonElement parameters) =>
        capabilityId == "questionable.single-quest"
            ? StartCall(() => startSingleQuest.InvokeFunc(parameters.GetProperty("questId").GetString()!), "Questionable accepted the quest.")
            : new(false, "UnsupportedCapability", "Questionable adapter does not expose that capability.");

    public override SpecialistAdapterCancelResult TryCancel() =>
        CancelCall(() => stop.InvokeFunc("Dalamud Agent Bridge cancellation"));
}

internal sealed class AutoDutySpecialistAdapter : SpecialistAdapterBase
{
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<uint, bool> contentHasPath;
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<bool> isNavigating;
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<bool> isLooping;
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<bool> isStopped;
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<uint, int, bool, object> run;
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<object> stop;

    public AutoDutySpecialistAdapter(IDalamudPluginInterface pluginInterface) : base(pluginInterface)
    {
        contentHasPath = pluginInterface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath");
        isNavigating = pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsNavigating");
        isLooping = pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsLooping");
        isStopped = pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsStopped");
        run = pluginInterface.GetIpcSubscriber<uint, int, bool, object>("AutoDuty.Run");
        stop = pluginInterface.GetIpcSubscriber<object>("AutoDuty.Stop");
    }

    public override string Plugin => "AutoDuty";
    public override IReadOnlyList<SpecialistCapabilityDescriptor> Capabilities { get; } =
    [
        new(
            "autoduty.run",
            "AutoDuty",
            "Run supported duty path",
            "Ask AutoDuty to run an explicit territory path using its existing user configuration.",
            "DutyAutomation",
            7_200,
            [
                new("territoryType", SpecialistArgumentKind.UInt32, "Territory type with an installed AutoDuty path.", Minimum: 1, Maximum: uint.MaxValue),
                new("loops", SpecialistArgumentKind.Integer, "Number of loops; zero uses AutoDuty's continuous-loop convention.", DefaultValue: "1", Minimum: 0, Maximum: 100),
                new("bareMode", SpecialistArgumentKind.Boolean, "Use AutoDuty bare mode.", DefaultValue: "false"),
            ]),
    ];

    public override SpecialistPluginObservation Observe()
    {
        var identity = Identity();
        if (!identity.Loaded)
            return Unavailable(identity);
        try
        {
            var stopped = isStopped.InvokeFunc();
            var looping = isLooping.InvokeFunc();
            var navigating = isNavigating.InvokeFunc();
            return Compatible(
                identity,
                !stopped,
                stopped ? "Idle" : "Running",
                stopped ? "AutoDuty is stopped." : "AutoDuty is executing a duty operation.",
                new Dictionary<string, string?>
                {
                    ["stopped"] = stopped.ToString(),
                    ["looping"] = looping.ToString(),
                    ["navigating"] = navigating.ToString(),
                });
        }
        catch (Exception exception)
        {
            return Incompatible(identity, exception);
        }
    }

    public override SpecialistAdapterStartResult TryStart(string capabilityId, JsonElement parameters)
    {
        if (capabilityId != "autoduty.run")
            return new(false, "UnsupportedCapability", "AutoDuty adapter does not expose that capability.");
        var territoryType = parameters.GetProperty("territoryType").GetUInt32();
        try
        {
            if (!contentHasPath.InvokeFunc(territoryType))
                return new(false, "PathUnavailable", $"AutoDuty does not advertise a path for territory {territoryType}.");
        }
        catch (Exception exception)
        {
            return new(false, "IpcInvocationFailed", $"AutoDuty path discovery failed: {exception.Message}");
        }
        return StartAction(
            () => run.InvokeAction(
                territoryType,
                parameters.GetProperty("loops").GetInt32(),
                parameters.GetProperty("bareMode").GetBoolean()),
            "AutoDuty accepted the duty operation.");
    }

    public override SpecialistAdapterCancelResult TryCancel() => CancelAction(stop.InvokeAction);
}

internal sealed class HenchmanSpecialistAdapter : SpecialistAdapterBase
{
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<bool> isBusy;
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<object> cancel;
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<object> startOnABoat;
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<object> startOnYourMark;

    public HenchmanSpecialistAdapter(IDalamudPluginInterface pluginInterface) : base(pluginInterface)
    {
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Henchman.IsBusy");
        cancel = pluginInterface.GetIpcSubscriber<object>("Henchman.CancelAllTasks");
        startOnABoat = pluginInterface.GetIpcSubscriber<object>("Henchman.StartOnABoat");
        startOnYourMark = pluginInterface.GetIpcSubscriber<object>("Henchman.StartOnYourMark");
    }

    public override string Plugin => "Henchman";
    public override IReadOnlyList<SpecialistCapabilityDescriptor> Capabilities { get; } =
    [
        new("henchman.on-a-boat", "Henchman", "Run On A Boat", "Start Henchman's published On A Boat task.", "TaskAutomation", 3_600, []),
        new("henchman.on-your-mark", "Henchman", "Run On Your Mark", "Start Henchman's published On Your Mark task.", "TaskAutomation", 3_600, []),
    ];

    public override SpecialistPluginObservation Observe()
    {
        var identity = Identity();
        if (!identity.Loaded)
            return Unavailable(identity);
        try
        {
            var busy = isBusy.InvokeFunc();
            return Compatible(identity, busy, busy ? "Running" : "Idle", busy ? "Henchman is running a task." : "Henchman is idle.");
        }
        catch (Exception exception)
        {
            return Incompatible(identity, exception);
        }
    }

    public override SpecialistAdapterStartResult TryStart(string capabilityId, JsonElement parameters) => capabilityId switch
    {
        "henchman.on-a-boat" => StartAction(startOnABoat.InvokeAction, "Henchman accepted On A Boat."),
        "henchman.on-your-mark" => StartAction(startOnYourMark.InvokeAction, "Henchman accepted On Your Mark."),
        _ => new(false, "UnsupportedCapability", "Henchman adapter does not expose that capability."),
    };

    public override SpecialistAdapterCancelResult TryCancel() => CancelAction(cancel.InvokeAction);
}

internal sealed class LifestreamSpecialistAdapter : SpecialistAdapterBase
{
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<bool> isBusy;
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<string, bool> aethernetTeleport;
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<string, bool> changeWorld;
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<object> abort;

    public LifestreamSpecialistAdapter(IDalamudPluginInterface pluginInterface) : base(pluginInterface)
    {
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        aethernetTeleport = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.AethernetTeleport");
        changeWorld = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
        abort = pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
    }

    public override string Plugin => "Lifestream";
    public override IReadOnlyList<SpecialistCapabilityDescriptor> Capabilities { get; } =
    [
        new(
            "lifestream.aethernet",
            "Lifestream",
            "Use local aethernet",
            "Ask Lifestream to use an explicit nearby aethernet destination.",
            "TravelAutomation",
            300,
            [new("destination", SpecialistArgumentKind.String, "Destination name understood by Lifestream.", MaximumLength: 128)]),
        new(
            "lifestream.change-world",
            "Lifestream",
            "Change world",
            "Ask Lifestream to travel to an explicit world. This may teleport and spend gil under normal game rules.",
            "GilAffectingTravel",
            1_800,
            [new("world", SpecialistArgumentKind.String, "Exact destination world name.", MaximumLength: 64)]),
    ];

    public override SpecialistPluginObservation Observe()
    {
        var identity = Identity();
        if (!identity.Loaded)
            return Unavailable(identity);
        try
        {
            var busy = isBusy.InvokeFunc();
            return Compatible(identity, busy, busy ? "Running" : "Idle", busy ? "Lifestream is executing travel." : "Lifestream is idle.");
        }
        catch (Exception exception)
        {
            return Incompatible(identity, exception);
        }
    }

    public override SpecialistAdapterStartResult TryStart(string capabilityId, JsonElement parameters) => capabilityId switch
    {
        "lifestream.aethernet" => StartCall(() => aethernetTeleport.InvokeFunc(parameters.GetProperty("destination").GetString()!), "Lifestream accepted aethernet travel."),
        "lifestream.change-world" => StartCall(() => changeWorld.InvokeFunc(parameters.GetProperty("world").GetString()!), "Lifestream accepted world travel."),
        _ => new(false, "UnsupportedCapability", "Lifestream adapter does not expose that capability."),
    };

    public override SpecialistAdapterCancelResult TryCancel() => CancelAction(abort.InvokeAction);
}
