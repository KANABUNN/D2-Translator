using System.Windows.Threading;
using D2Translator.Application.Diagnostics;
using D2Translator.Capture.Windows;
using D2Translator.Infrastructure.Diagnostics;

namespace D2Translator.App.Services;

/// <summary>
/// 自プロセスの資源と UI の応答遅れを 1 分ごとに記録する（仕様書 16.2 P04、P10、P11、P13）。
/// </summary>
internal sealed class ResourceMonitor : IDisposable
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);

    /// <summary>UI スレッドへ入力と同じ優先度の処理を投げ、実行されるまでの遅れを測る間隔。</summary>
    public static readonly TimeSpan UiProbeInterval = TimeSpan.FromSeconds(5);

    private readonly IDiagnosticsSink _sink;
    private readonly TimeProvider _time;
    private readonly Dispatcher _dispatcher;
    private readonly ProcessResourceSampler _sampler;
    private readonly Lock _gate = new();
    private readonly List<double> _uiLags = [];
    private readonly ITimer _sampleTimer;
    private readonly ITimer _probeTimer;

    public ResourceMonitor(IDiagnosticsSink sink, TimeProvider time, Dispatcher dispatcher)
    {
        _sink = sink;
        _time = time;
        _dispatcher = dispatcher;
        _sampler = new ProcessResourceSampler(time, GpuMemoryProbe.TryQuery);
        _sampleTimer = time.CreateTimer(_ => Sample(), null, SampleInterval, SampleInterval);
        _probeTimer = time.CreateTimer(_ => ProbeUi(), null, UiProbeInterval, UiProbeInterval);
    }

    private void ProbeUi()
    {
        long posted = _time.GetTimestamp();
        _dispatcher.InvokeAsync(() =>
        {
            double lag = _time.GetElapsedTime(posted).TotalMilliseconds;
            lock (_gate)
            {
                _uiLags.Add(lag);
            }
        }, DispatcherPriority.Input);
    }

    private void Sample()
    {
        double[] lags;
        lock (_gate)
        {
            lags = [.. _uiLags.Order()];
            _uiLags.Clear();
        }

        var extra = new Dictionary<string, double>();
        if (lags.Length > 0)
        {
            extra["ui_lag_p95_ms"] = Math.Round(DiagnosticsSummary.Percentile(lags, 0.95), 1);
            extra["ui_lag_max_ms"] = Math.Round(lags[^1], 1);
        }

        _sink.Write(_sampler.Sample(extra));
    }

    public void Dispose()
    {
        _sampleTimer.Dispose();
        _probeTimer.Dispose();
        _sampler.Dispose();
    }
}
