using DalamudAgentBridge.Plugin;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class NativeSlashCommandPolicyTests
{
    private static readonly NativeTextCommandDefinition[] Commands =
    [
        new(1, 10, ["/say", "/s", "/sagen"]),
        new(2, 11, ["/shout", "/sh"]),
        new(3, 11, ["/yell", "/y"]),
        new(4, 11, ["/echo", "/e", "/eigen"]),
        new(5, 12, ["/emote", "/em"]),
        new(6, 11, ["/party", "/p"]),
        new(7, 13, ["/wave", "/wv"]),
        new(8, 14, ["/artisan"]),
    ];

    private static readonly IReadOnlySet<string> BlockedCommands =
        NativeSlashCommandCatalog.CreateBlockedCommands(Commands, [7]);

    private static readonly NativeSlashCommandPolicy Policy = new(BlockedCommands);

    [Theory]
    [InlineData("/say hello")]
    [InlineData("/S hello")]
    [InlineData("/sagen hello")]
    [InlineData("/shout")]
    [InlineData("/sh hello")]
    [InlineData("/yell hello")]
    [InlineData("/Y hello")]
    [InlineData("/party hello")]
    [InlineData("/p hello")]
    [InlineData("/emote hello")]
    [InlineData("/em hello")]
    [InlineData("/wave")]
    [InlineData("/wave motion")]
    [InlineData("/wv motion")]
    public void RejectsEveryAliasOfTransmittingAndEmoteCommands(string line)
    {
        var decision = Policy.Evaluate(line);

        Assert.False(decision.Allowed);
        Assert.Contains("blocked", decision.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/echo local output")]
    [InlineData("/e local output")]
    [InlineData("/eigen local output")]
    [InlineData("/artisan")]
    [InlineData("  /artisan start  ")]
    [InlineData("/sneak")]
    public void PermitsLocalEchoAndCommandsWithoutAnUnsafeToken(string line)
    {
        var decision = Policy.Evaluate(line);

        Assert.True(decision.Allowed);
        Assert.Equal(line.Trim(), decision.CommandLine);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("/")]
    [InlineData("/artisan\n/say hello")]
    public void RejectsAnythingOtherThanOneNamedSlashCommand(string line)
    {
        Assert.False(Policy.Evaluate(line).Allowed);
    }

    [Fact]
    public void FailsClosedWhenRequiredCurrentGameDataIsMissing()
    {
        var incomplete = Commands.Where(command => command.RowId != 3).ToArray();

        var exception = Assert.Throws<InvalidOperationException>(
            () => NativeSlashCommandCatalog.CreateBlockedCommands(incomplete, [7]));

        Assert.Contains("/yell", exception.Message);
    }
}
