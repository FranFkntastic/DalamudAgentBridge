using System;
using System.Linq;

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
    int CompletedFrame,
    CaptureFreshnessStamp? Stamp = null,
    PopupRegion[]? Attached = null)
{
    public const string PopupCapability = "plugin-surface-attached-popups";

    public bool HasSameTargetAndGeometry(PluginSurfaceWindowMeasurement other) =>
        this with { CompletedFrame = 0, Stamp = null, Attached = null } == other with { CompletedFrame = 0, Stamp = null, Attached = null } &&
        (Attached is null ? other.Attached is null : other.Attached != null && Attached.SequenceEqual(other.Attached)) &&
        Stamp?.X == other.Stamp?.X && Stamp?.Y == other.Stamp?.Y && Stamp?.CellsPerRow == other.Stamp?.CellsPerRow;
}

// Stamp coordinates here are relative to the viewport, including when multiple regions share one stamp.
public sealed record PopupRegion(uint WindowId, string Name, long WindowHandle, uint ViewportId,
    int X, int Y, int Width, int Height, int ScreenX, int ScreenY, CaptureFreshnessStamp ViewportStamp);

public sealed record PixelRect(int X, int Y, int Width, int Height)
{
    public PixelRect Offset(int dx, int dy) => this with { X = checked(X + dx), Y = checked(Y + dy) };

    public static PixelRect Union(PixelRect a, PixelRect b)
    {
        checked
        {
            var x = Math.Min(a.X, b.X);
            var y = Math.Min(a.Y, b.Y);
            return new(x, y, Math.Max(a.X + a.Width, b.X + b.Width) - x,
                Math.Max(a.Y + a.Height, b.Y + b.Height) - y);
        }
    }
}

public sealed record CaptureRegionPlacement(uint WindowId, string Name, uint ViewportId, PixelRect Rect, PixelRect StampRect);

public sealed record SurfaceCompositeLayout(PixelRect ScreenBounds, PixelRect MainRect, CaptureRegionPlacement[] Attached)
{
    public static SurfaceCompositeLayout From(PluginSurfaceWindowMeasurement main)
    {
        var mainScreen = new PixelRect(main.X, main.Y, main.Width, main.Height).Offset(main.ScreenX, main.ScreenY);
        var bounds = mainScreen;
        var placed = (main.Attached ?? []).Select(p =>
        {
            var screen = new PixelRect(p.X, p.Y, p.Width, p.Height).Offset(p.ScreenX, p.ScreenY);
            bounds = PixelRect.Union(bounds, screen);
            var s = p.ViewportStamp;
            return new CaptureRegionPlacement(p.WindowId, p.Name, p.ViewportId, screen,
                new PixelRect(s.X, s.Y, s.Width, s.Height).Offset(p.ScreenX, p.ScreenY));
        }).ToArray();
        PixelRect Place(PixelRect r) => r with { X = checked(r.X - bounds.X), Y = checked(r.Y - bounds.Y) };
        return new(bounds, Place(mainScreen), placed.Select(p => p with { Rect = Place(p.Rect), StampRect = Place(p.StampRect) }).ToArray());
    }
}

/// <summary>Physical pixels relative to the published crop; sync, 32 nonce bits (LSB first), guard.</summary>
public sealed record CaptureFreshnessStamp(int Nonce, int X, int Y, int CellsPerRow)
{
    public const int CellSize = 3;
    public const int Cells = 40;
    public int Width => CellsPerRow * CellSize;
    public int Height => ((Cells + CellsPerRow - 1) / CellsPerRow) * CellSize;
    public int CellX(int cell) => X + (cell % CellsPerRow) * CellSize;
    public int CellY(int cell) => Y + (cell / CellsPerRow) * CellSize;

    public bool White(int cell) => cell switch
    {
        < 4 => ((0b1101 >> cell) & 1) != 0,
        >= 36 => ((0b0010 >> (cell - 36)) & 1) != 0,
        _ => ((Nonce >> (cell - 4)) & 1) != 0,
    };

    // Leave a dead band around the threshold: ambiguous pixels are not evidence.
    public StampReading Decode(System.Func<int, int, int> luminance, out int seen)
    {
        seen = 0;
        var guardMatches = 0;
        var ambiguous = false;
        for (var i = 0; i < Cells; i++)
        {
            var level = luminance(CellX(i) + CellSize / 2, CellY(i) + CellSize / 2);
            bool? white = level <= 80 ? false : level >= 175 ? true : null;
            if (i < 4 || i >= 36)
                guardMatches += white == White(i) ? 1 : 0;
            else if (white == true)
                seen |= 1 << (i - 4);
            else if (white is null)
                ambiguous = true;
        }
        if (guardMatches <= 5) return StampReading.Absent;
        if (guardMatches != 8 || ambiguous) return StampReading.Unreadable;
        return seen == Nonce ? StampReading.Match : StampReading.Different;
    }
}

public enum StampReading { Match, Different, Absent, Unreadable }
