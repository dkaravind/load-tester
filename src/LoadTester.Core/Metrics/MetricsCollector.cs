using System.Collections.Concurrent;
using System.Diagnostics;

namespace LoadTester.Core.Metrics;

/// <summary>One measured request. Offset is seconds since the scenario's measurement started.</summary>
public readonly record struct RequestRecord(
    string Step,
    string Phase,
    double OffsetSeconds,
    double LatencyMs,
    int StatusCode,
    bool Success,
    long Bytes,
    string? Error);

/// <summary>
/// Per-scenario sink for request records. Recording is disabled during warmup;
/// StartMeasurement() zeroes the clock so offsets are relative to the measured window.
/// </summary>
public sealed class MetricsCollector
{
    private readonly ConcurrentQueue<RequestRecord> _records = new();
    private readonly Stopwatch _clock = new();
    private volatile bool _recording;

    public void StartMeasurement()
    {
        _clock.Restart();
        _recording = true;
    }

    public void StopMeasurement()
    {
        _recording = false;
        _clock.Stop();
    }

    public double ElapsedSeconds => _clock.Elapsed.TotalSeconds;

    public void Record(string step, string phase, double latencyMs, int statusCode, bool success, long bytes, string? error)
    {
        if (!_recording) return;
        _records.Enqueue(new RequestRecord(step, phase, _clock.Elapsed.TotalSeconds, latencyMs, statusCode, success, bytes, error));
    }

    public IReadOnlyList<RequestRecord> Snapshot() => _records.ToArray();
}
