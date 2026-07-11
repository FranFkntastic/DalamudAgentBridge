using System.Text;

namespace DalamudAgentBridge;

public sealed class DalamudLogWatcher
{
    private const int DefaultLimit = 200;
    private const int MaximumLimit = 1000;
    private const int MaximumReadBytes = 1024 * 1024;

    public DalamudLogRead Read(BridgeInstance instance, long? cursor, int? requestedLimit)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var logPath = ResolveLogPath(instance);
        if (!File.Exists(logPath))
            return new DalamudLogRead(logPath, 0, 0, false, false, []);

        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var reset = cursor is < 0 || cursor > length;
        var start = reset ? 0 : cursor ?? Math.Max(0, length - MaximumReadBytes);
        var truncated = start > 0 && cursor is null;
        if (start > 0 && cursor is null)
            start = AdvancePastPartialLine(stream, start);

        stream.Position = start;
        var bytesToRead = (int)Math.Min(MaximumReadBytes, length - start);
        var bytes = new byte[bytesToRead];
        var count = stream.Read(bytes, 0, bytes.Length);
        var completeLength = LastCompleteLineLength(bytes.AsSpan(0, count));
        var limit = Math.Clamp(requestedLimit ?? DefaultLimit, 1, MaximumLimit);
        var decoded = DecodeCompleteLines(bytes.AsSpan(0, completeLength));
        var initialTailRead = cursor is null;
        var selected = initialTailRead ? decoded.TakeLast(limit).ToArray() : decoded.Take(limit).ToArray();
        var lines = selected.Select(entry => entry.Text).ToArray();
        if (start == 0 && lines.Length > 0)
            lines[0] = lines[0].TrimStart('\uFEFF');
        var deliveredLength = initialTailRead || selected.Length == 0 ? completeLength : selected[^1].EndOffset;
        var nextCursor = start + deliveredLength;
        return new DalamudLogRead(logPath, start, nextCursor, reset, truncated || completeLength < count, lines);
    }

    public static string ResolveLogPath(BridgeInstance instance)
    {
        var bridgeDirectory = Path.GetDirectoryName(instance.DiscoveryPath)
            ?? throw new InvalidOperationException("Bridge discovery path has no directory.");
        var pluginDirectory = Directory.GetParent(bridgeDirectory)?.FullName
            ?? throw new InvalidOperationException("Bridge discovery path is outside a plugin configuration directory.");
        var pluginConfigsDirectory = Directory.GetParent(pluginDirectory)?.FullName
            ?? throw new InvalidOperationException("Plugin configuration directory has no launcher root.");
        var launcherRoot = Directory.GetParent(pluginConfigsDirectory)?.FullName
            ?? throw new InvalidOperationException("Plugin configuration directory has no launcher root.");
        return Path.Combine(launcherRoot, "dalamud.log");
    }

    private static long AdvancePastPartialLine(FileStream stream, long position)
    {
        stream.Position = position;
        while (stream.Position < stream.Length)
        {
            if (stream.ReadByte() == '\n')
                return stream.Position;
        }
        return stream.Length;
    }

    private static int LastCompleteLineLength(ReadOnlySpan<byte> bytes)
    {
        for (var index = bytes.Length - 1; index >= 0; index--)
            if (bytes[index] == (byte)'\n')
                return index + 1;
        return 0;
    }

    private static List<DecodedLine> DecodeCompleteLines(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<DecodedLine>();
        var lineStart = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n')
                continue;
            var line = Encoding.UTF8.GetString(bytes[lineStart..index]).TrimEnd('\r');
            if (line.Length > 0)
                lines.Add(new DecodedLine(line, index + 1));
            lineStart = index + 1;
        }
        return lines;
    }

    private sealed record DecodedLine(string Text, int EndOffset);
}

public sealed record DalamudLogRead(
    string Path,
    long FromCursor,
    long NextCursor,
    bool Reset,
    bool Truncated,
    IReadOnlyList<string> Lines);
