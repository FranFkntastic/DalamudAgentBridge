using System.Text.Json;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class ChatLogReadTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void ChatLogReadDeserializesPluginWireShape()
    {
        const string json = """
            {
              "fromCursor": 41,
              "nextCursor": 43,
              "reset": false,
              "entries": [
                {
                  "sequence": 42,
                  "observedAtUtc": "2026-07-27T18:00:00+00:00",
                  "typeId": 2104,
                  "typeName": "Debug",
                  "timestamp": 123456,
                  "sender": "",
                  "message": "[Census] bridge attached"
                },
                {
                  "sequence": 43,
                  "observedAtUtc": "2026-07-27T18:00:01+00:00",
                  "typeId": 2104,
                  "typeName": "Debug",
                  "timestamp": 123457,
                  "sender": "",
                  "message": "[Census] retbind6 result"
                }
              ]
            }
            """;

        var read = JsonSerializer.Deserialize<ChatLogRead>(json, JsonOptions);

        Assert.NotNull(read);
        Assert.Equal(41, read.FromCursor);
        Assert.Equal(43, read.NextCursor);
        Assert.False(read.Reset);
        Assert.Equal(2, read.Entries.Count);
        Assert.Equal(42, read.Entries[0].Sequence);
        Assert.Equal("Debug", read.Entries[0].TypeName);
        Assert.Equal("[Census] bridge attached", read.Entries[0].Message);
        Assert.Equal(new DateTimeOffset(2026, 7, 27, 18, 0, 1, TimeSpan.Zero), read.Entries[1].ObservedAtUtc);
    }

    [Fact]
    public void ChatLogReadRoundTripsThroughJson()
    {
        var original = new ChatLogRead(
            7,
            9,
            true,
            [
                new ChatLogEntry(8, new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero), 57, "TellIncoming", 999, "Sender Name", "hello"),
                new ChatLogEntry(9, new DateTimeOffset(2026, 7, 27, 12, 0, 2, TimeSpan.Zero), 2104, "Debug", 1000, string.Empty, "line two"),
            ]);

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var parsed = JsonSerializer.Deserialize<ChatLogRead>(json, JsonOptions);

        Assert.NotNull(parsed);
        Assert.Equal(original.FromCursor, parsed.FromCursor);
        Assert.Equal(original.NextCursor, parsed.NextCursor);
        Assert.Equal(original.Reset, parsed.Reset);
        Assert.Equal(original.Entries, parsed.Entries);
    }
}
