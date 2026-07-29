using System;
using System.Collections.Generic;
using System.Linq;

namespace DalamudAgentBridge.Plugin;

internal sealed record NativeTextCommandDefinition(
    uint RowId,
    uint ParameterRowId,
    IReadOnlyList<string> Aliases);

internal static class NativeSlashCommandCatalog
{
    private static readonly string[] TransmittingCommandAnchors = ["/say", "/shout", "/yell"];

    public static IReadOnlySet<string> CreateBlockedCommands(
        IReadOnlyCollection<NativeTextCommandDefinition> commands,
        IReadOnlyCollection<uint> emoteCommandRowIds)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(emoteCommandRowIds);

        var normalizedAliases = commands.ToDictionary(
            command => command.RowId,
            command => command.Aliases
                .Select(NormalizeCommand)
                .Where(alias => alias is not null)
                .Select(alias => alias!)
                .ToArray());
        var transmittingParameterRows = TransmittingCommandAnchors
            .Select(anchor => FindUniqueCommand(commands, normalizedAliases, anchor).ParameterRowId)
            .ToHashSet();
        var customEmote = FindUniqueCommand(commands, normalizedAliases, "/emote");
        var echo = FindUniqueCommand(commands, normalizedAliases, "/echo");
        var emoteRows = emoteCommandRowIds.ToHashSet();
        var blocked = commands
            .Where(command =>
                command.RowId != echo.RowId &&
                (transmittingParameterRows.Contains(command.ParameterRowId) ||
                 emoteRows.Contains(command.RowId) ||
                 command.RowId == customEmote.RowId))
            .SelectMany(command => normalizedAliases[command.RowId])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var anchor in TransmittingCommandAnchors.Append("/emote"))
        {
            if (!blocked.Contains(anchor))
                throw new InvalidOperationException($"Current FFXIV command data did not classify required command '{anchor}' as unsafe.");
        }
        foreach (var alias in normalizedAliases[echo.RowId])
        {
            if (blocked.Contains(alias))
                throw new InvalidOperationException($"Current FFXIV command data classified local-only echo alias '{alias}' as unsafe.");
        }

        return blocked;
    }

    private static NativeTextCommandDefinition FindUniqueCommand(
        IReadOnlyCollection<NativeTextCommandDefinition> commands,
        IReadOnlyDictionary<uint, string[]> normalizedAliases,
        string anchor)
    {
        var matches = commands
            .Where(command => normalizedAliases[command.RowId].Contains(anchor, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException(
                $"Current FFXIV command data contains {matches.Length} rows for required command '{anchor}'; refusing native command execution.");
    }

    private static string? NormalizeCommand(string? command)
    {
        var normalized = command?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;
        return normalized.StartsWith('/') ? normalized : $"/{normalized}";
    }
}

internal sealed class NativeSlashCommandPolicy
{
    private readonly HashSet<string> blockedCommands;

    public NativeSlashCommandPolicy(IEnumerable<string> blockedCommands)
    {
        ArgumentNullException.ThrowIfNull(blockedCommands);
        this.blockedCommands = blockedCommands.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public NativeSlashCommandDecision Evaluate(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return NativeSlashCommandDecision.Rejected("Only single-line slash commands may be submitted.");

        var commandLine = line.Trim();
        if (commandLine.IndexOfAny(['\r', '\n']) >= 0 || !commandLine.StartsWith('/'))
            return NativeSlashCommandDecision.Rejected("Only single-line slash commands may be submitted.");

        var separator = commandLine.IndexOfAny([' ', '\t']);
        var command = separator < 0 ? commandLine : commandLine[..separator];
        if (command.Length == 1)
            return NativeSlashCommandDecision.Rejected("A slash command name is required.");
        if (blockedCommands.Contains(command))
            return NativeSlashCommandDecision.Rejected(
                $"Command '{command}' is blocked because current FFXIV data identifies it as chat- or emote-capable.");

        return NativeSlashCommandDecision.Permitted(commandLine);
    }
}

internal sealed record NativeSlashCommandDecision(bool Allowed, string CommandLine, string Message)
{
    public static NativeSlashCommandDecision Permitted(string commandLine) =>
        new(true, commandLine, "Slash command permitted.");

    public static NativeSlashCommandDecision Rejected(string message) =>
        new(false, string.Empty, message);
}
