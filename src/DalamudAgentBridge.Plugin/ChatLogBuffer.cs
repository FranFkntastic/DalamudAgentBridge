using System;
using System.Collections.Generic;

namespace DalamudAgentBridge.Plugin;

/// <summary>Bounded in-memory ring buffer of observed in-game chat lines with cursor-based incremental reads.</summary>
public sealed class ChatLogBuffer
{
    public const int MaxLimit = 500;
    private const int DefaultLimit = 100;
    private const int Capacity = 512;
    private readonly object gate = new();
    private readonly ChatLogEntry?[] entries = new ChatLogEntry?[Capacity];
    private long nextSequence = 1;
    private int head;
    private int count;

    public void Record(int typeId, string typeName, int timestamp, string sender, string message)
    {
        lock (gate)
        {
            int index;
            if (count == Capacity)
            {
                index = head;
                head = (head + 1) % Capacity;
            }
            else
            {
                index = (head + count) % Capacity;
                count++;
            }

            entries[index] = new ChatLogEntry(nextSequence++, DateTimeOffset.UtcNow, typeId, typeName, timestamp, sender, message);
        }
    }

    public ChatLogRead Read(long? cursor, int? limit)
    {
        lock (gate)
        {
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var newest = nextSequence - 1;
            if (count == 0)
                return new ChatLogRead(cursor ?? 0, Math.Max(newest, 0), false, []);

            var oldest = nextSequence - count;
            if (cursor is not null && cursor.Value >= nextSequence)
                return new ChatLogRead(cursor.Value, newest, true, Collect(oldest, take));
            var reset = cursor is not null && cursor.Value + 1 < oldest;
            if (cursor is null)
            {
                var start = Math.Max(oldest, newest - take + 1);
                return new ChatLogRead(start - 1, newest, false, Collect(start, take));
            }

            var floor = Math.Max(cursor.Value + 1, oldest);
            var selected = Collect(floor, take);
            return new ChatLogRead(cursor.Value, selected.Count > 0 ? selected[^1].Sequence : newest, reset, selected);
        }
    }

    private List<ChatLogEntry> Collect(long startSequence, int take)
    {
        var oldest = nextSequence - count;
        var selected = new List<ChatLogEntry>(Math.Min(take, count));
        for (var sequence = Math.Max(startSequence, oldest); sequence < nextSequence && selected.Count < take; sequence++)
        {
            var entry = entries[(head + (int)(sequence - oldest)) % Capacity];
            if (entry is not null)
                selected.Add(entry);
        }

        return selected;
    }
}

public sealed record ChatLogEntry(
    long Sequence,
    DateTimeOffset ObservedAtUtc,
    int TypeId,
    string TypeName,
    int Timestamp,
    string Sender,
    string Message);

public sealed record ChatLogRead(
    long FromCursor,
    long NextCursor,
    bool Reset,
    IReadOnlyList<ChatLogEntry> Entries);
