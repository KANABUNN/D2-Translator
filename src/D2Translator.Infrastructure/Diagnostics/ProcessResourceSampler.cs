using System.Diagnostics;
using D2Translator.Application.Diagnostics;

namespace D2Translator.Infrastructure.Diagnostics;

/// <summary>
/// 自プロセスの CPU、メモリ、GPU メモリを測る（仕様書 16.2 P10、P11、P13）。ゲームを含む他のプロセスは測らない。
/// CPU は前回の測定からの増分を経過時間と論理 CPU 数で割った、マシン全体比の値（16.3）。
/// </summary>
public sealed class ProcessResourceSampler : IDisposable
{
    private const double Megabyte = 1024 * 1024;

    private readonly TimeProvider _time;
    private readonly Func<(long Local, long NonLocal)?>? _gpuProbe;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly long _started;
    private TimeSpan _lastCpu;
    private long _lastSample;

    public ProcessResourceSampler(TimeProvider time, Func<(long Local, long NonLocal)?>? gpuProbe = null)
    {
        _time = time;
        _gpuProbe = gpuProbe;
        _lastCpu = _process.TotalProcessorTime;
        _started = _lastSample = time.GetTimestamp();
    }

    public DiagnosticsEvent Sample(IReadOnlyDictionary<string, double>? extra = null)
    {
        _process.Refresh();
        long now = _time.GetTimestamp();
        TimeSpan cpu = _process.TotalProcessorTime;
        double elapsedMs = _time.GetElapsedTime(_lastSample, now).TotalMilliseconds;
        double cpuPercent = elapsedMs <= 0 ? 0 : (cpu - _lastCpu).TotalMilliseconds / elapsedMs / Environment.ProcessorCount * 100;
        _lastCpu = cpu;
        _lastSample = now;

        var values = new Dictionary<string, double>
        {
            ["cpu_pct"] = Math.Round(cpuPercent, 3),
            ["private_mb"] = Math.Round(_process.PrivateMemorySize64 / Megabyte, 1),
            ["working_set_mb"] = Math.Round(_process.WorkingSet64 / Megabyte, 1),
            ["managed_mb"] = Math.Round(GC.GetTotalMemory(forceFullCollection: false) / Megabyte, 1),
            ["handles"] = _process.HandleCount,
            ["threads"] = _process.Threads.Count,
            ["uptime_min"] = Math.Round(_time.GetElapsedTime(_started, now).TotalMinutes, 2),
        };

        if (_gpuProbe?.Invoke() is (long local, long nonLocal))
        {
            values["gpu_local_mb"] = Math.Round(local / Megabyte, 1);
            values["gpu_nonlocal_mb"] = Math.Round(nonLocal / Megabyte, 1);
        }

        foreach ((string key, double value) in extra ?? new Dictionary<string, double>())
        {
            values[key] = value;
        }

        return new DiagnosticsEvent(DiagnosticsKinds.Resources, "process") { Values = values };
    }

    public void Dispose() => _process.Dispose();
}
