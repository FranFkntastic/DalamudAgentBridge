namespace DalamudAgentBridge;

/// <summary>A measured region in the exact platform window owned by a live presentation lease.</summary>
public sealed record PluginSurfaceWindowMeasurement(
    string TransactionId,
    string TargetPlugin,
    string SurfaceId,
    string RuntimeInstanceId,
    long WindowHandle,
    uint ViewportId,
    int X,
    int Y,
    int Width,
    int Height,
    int ScreenX,
    int ScreenY,
    int CompletedFrame)
{
    public bool HasSameTargetAndGeometry(PluginSurfaceWindowMeasurement other) =>
        this with { CompletedFrame = 0 } == other with { CompletedFrame = 0 };
}
