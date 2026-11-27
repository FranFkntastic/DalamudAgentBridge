using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;

namespace DalamudAgentBridge.Plugin;

/// <summary>
/// Read-only process, managed-heap, and system memory observation for the game process hosting this connector,
/// with a bounded once-a-minute history so growth since load can be read in one call.
/// </summary>
/// <remarks>
/// Every loaded Dalamud plugin shares one .NET runtime and one GC heap, so managed figures cover all plugins
/// together; per-plugin attribution needs a disable/enable bisect. Nothing here reads or writes game memory.
/// </remarks>
public sealed class MemoryDiagnosticsMonitor : IDisposable
{
    public const int SchemaVersion = 1;
    public const int HistoryCapacity = 240;
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);
    private const int TrendWindowSamples = 30;
    private readonly object gate = new();
    private readonly MemoryHistorySample[] history = new MemoryHistorySample[HistoryCapacity];
    private readonly DateTimeOffset monitorStartedAtUtc = DateTimeOffset.UtcNow;
    private readonly Timer timer;
    private int head;
    private int count;
    private bool disposed;

    public MemoryDiagnosticsMonitor()
    {
        timer = new Timer(_ => RecordSample(), null, TimeSpan.Zero, SampleInterval);
    }

    public MemoryDiagnosticsSnapshot Snapshot()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        using var process = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo(GCKind.Any);
        var privateBytes = process.PrivateMemorySize64;

        var processMemory = new ProcessMemory(
            process.Id,
            TryGetStartTime(process),
            privateBytes,
            process.WorkingSet64,
            process.PeakWorkingSet64,
            process.PagedMemorySize64,
            process.VirtualMemorySize64,
            process.HandleCount,
            process.Threads.Count);

        var generations = new List<GcGeneration>(gcInfo.GenerationInfo.Length);
        for (var index = 0; index < gcInfo.GenerationInfo.Length; index++)
        {
            var generation = gcInfo.GenerationInfo[index];
            generations.Add(new GcGeneration(
                GenerationName(index),
                generation.SizeAfterBytes,
                generation.FragmentationAfterBytes,
                index <= GC.MaxGeneration ? GC.CollectionCount(index) : null));
        }

        var managed = new ManagedMemory(
            GC.GetTotalMemory(forceFullCollection: false),
            gcInfo.Index,
            gcInfo.HeapSizeBytes,
            gcInfo.TotalCommittedBytes,
            gcInfo.FragmentedBytes,
            gcInfo.PromotedBytes,
            GC.GetTotalAllocatedBytes(precise: false),
            gcInfo.PauseTimePercentage,
            GCSettings.IsServerGC,
            GCSettings.LatencyMode.ToString(),
            generations);

        return new MemoryDiagnosticsSnapshot(
            SchemaVersion,
            capturedAt,
            monitorStartedAtUtc,
            (int)SampleInterval.TotalSeconds,
            processMemory,
            managed,
            Math.Max(0, privateBytes - gcInfo.TotalCommittedBytes),
            ReadSystemMemory(),
            ReadHistoryTrend(),
            ReadHistory());
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
        }

        timer.Dispose();
    }

    private void RecordSample()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var gcInfo = GC.GetGCMemoryInfo(GCKind.Any);
            var sample = new MemoryHistorySample(
                DateTimeOffset.UtcNow,
                process.PrivateMemorySize64,
                process.WorkingSet64,
                GC.GetTotalMemory(forceFullCollection: false),
                gcInfo.TotalCommittedBytes,
                process.HandleCount);

            lock (gate)
            {
                if (disposed)
                    return;
                int index;
                if (count == HistoryCapacity)
                {
                    index = head;
                    head = (head + 1) % HistoryCapacity;
                }
                else
                {
                    index = (head + count) % HistoryCapacity;
                    count++;
                }

                history[index] = sample;
            }
        }
        catch
        {
            // Observation is best-effort; a failed sample must never disturb the game process.
        }
    }

    private IReadOnlyList<MemoryHistorySample> ReadHistory()
    {
        lock (gate)
        {
            var samples = new MemoryHistorySample[count];
            for (var offset = 0; offset < count; offset++)
                samples[offset] = history[(head + offset) % HistoryCapacity];
            return samples;
        }
    }

    private MemoryTrend? ReadHistoryTrend()
    {
        MemoryHistorySample first, windowStart, last;
        int windowSamples;
        lock (gate)
        {
            if (count < 2)
                return null;
            first = history[head];
            last = history[(head + count - 1) % HistoryCapacity];
            windowSamples = Math.Min(count, TrendWindowSamples);
            windowStart = history[(head + count - windowSamples) % HistoryCapacity];
        }

        var windowMinutes = (last.CapturedAtUtc - windowStart.CapturedAtUtc).TotalMinutes;
        return new MemoryTrend(
            first.CapturedAtUtc,
            last.PrivateBytes - first.PrivateBytes,
            last.GcCommittedBytes - first.GcCommittedBytes,
            windowSamples,
            windowMinutes > 0 ? (long)((last.PrivateBytes - windowStart.PrivateBytes) / windowMinutes) : 0,
            windowMinutes > 0 ? (long)((last.GcCommittedBytes - windowStart.GcCommittedBytes) / windowMinutes) : 0);
    }

    private static DateTimeOffset? TryGetStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static string GenerationName(int index) => index switch
    {
        0 => "gen0",
        1 => "gen1",
        2 => "gen2",
        3 => "loh",
        4 => "poh",
        _ => $"generation{index}",
    };

    private static SystemMemory? ReadSystemMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            return null;
        return new SystemMemory(
            status.MemoryLoad,
            (long)status.TotalPhys,
            (long)status.AvailPhys,
            (long)status.TotalPageFile,
            (long)(status.TotalPageFile - status.AvailPageFile),
            (long)status.AvailPageFile);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}

/// <param name="NonGcPrivateBytesEstimate">
/// Process private bytes minus GC-committed bytes: the game itself, D3D11 system-memory resources, native
/// libraries, and unmanaged plugin allocations. An estimate, because the two counters are read separately.
/// </param>
public sealed record MemoryDiagnosticsSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset MonitorStartedAtUtc,
    int SampleIntervalSeconds,
    ProcessMemory Process,
    ManagedMemory Managed,
    long NonGcPrivateBytesEstimate,
    SystemMemory? Machine,
    MemoryTrend? Trend,
    IReadOnlyList<MemoryHistorySample> History);

public sealed record ProcessMemory(
    int ProcessId,
    DateTimeOffset? StartedAtUtc,
    long PrivateBytes,
    long WorkingSetBytes,
    long PeakWorkingSetBytes,
    long PagedBytes,
    long VirtualBytes,
    int HandleCount,
    int ThreadCount);

/// <param name="CurrentHeapBytes">Live estimate of bytes held by managed objects right now.</param>
/// <param name="LastGcIndex">Index of the collection that the heap, committed, fragmentation, promotion, and generation figures describe; they are as of that GC, not live.</param>
/// <param name="TotalAllocatedBytes">Cumulative bytes ever allocated on the shared heap; its growth rate measures churn, not retention.</param>
public sealed record ManagedMemory(
    long CurrentHeapBytes,
    long LastGcIndex,
    long HeapSizeBytes,
    long CommittedBytes,
    long FragmentedBytes,
    long PromotedBytes,
    long TotalAllocatedBytes,
    double PauseTimePercentage,
    bool ServerGc,
    string LatencyMode,
    IReadOnlyList<GcGeneration> Generations);

public sealed record GcGeneration(string Name, long SizeAfterBytes, long FragmentationAfterBytes, int? CollectionCount);

/// <param name="CommitLimitBytes">Physical memory plus page files; allocations fail machine-wide once commit reaches it.</param>
public sealed record SystemMemory(
    uint MemoryLoadPercent,
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    long CommitLimitBytes,
    long CommittedBytes,
    long CommitAvailableBytes);

/// <param name="PrivateBytesPerMinute">Rate across the newest <paramref name="WindowSamples"/> samples.</param>
public sealed record MemoryTrend(
    DateTimeOffset FirstSampleAtUtc,
    long PrivateBytesSinceFirstSample,
    long GcCommittedBytesSinceFirstSample,
    int WindowSamples,
    long PrivateBytesPerMinute,
    long GcCommittedBytesPerMinute);

public sealed record MemoryHistorySample(
    DateTimeOffset CapturedAtUtc,
    long PrivateBytes,
    long WorkingSetBytes,
    long GcCurrentHeapBytes,
    long GcCommittedBytes,
    int HandleCount);
