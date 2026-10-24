using D2Translator.Domain.Capture;
using D2Translator.Domain.Errors;
using D2Translator.Domain.Ocr;
using Microsoft.Extensions.Logging;

namespace D2Translator.Application.Capture;

/// <summary>
/// 取得対象、ROI、受信パイプラインの寿命を状態機械に沿って管理する。
/// イベントはバックグラウンドスレッドから発生し得る。
/// </summary>
public sealed class CaptureController : IAsyncDisposable
{
    /// <summary>これ以上フレームが届かなければ待機中として表示する（最小化等）。</summary>
    public static readonly TimeSpan FrameStallThreshold = TimeSpan.FromSeconds(3);

    private readonly ReceivePipeline _pipeline;
    private readonly TimeProvider _time;
    private readonly ILogger<CaptureController> _logger;
    private readonly ITimer _stallTimer;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private long _lastFrameTimestamp;
    private bool _isWaitingForFrames;

    public CaptureController(ReceivePipeline pipeline, TimeProvider time, ILogger<CaptureController> logger)
    {
        _pipeline = pipeline;
        _time = time;
        _logger = logger;
        _pipeline.ObservationReady += (_, observation) => ObservationReady?.Invoke(this, observation);
        _pipeline.StatsUpdated += OnStatsUpdated;
        _stallTimer = time.CreateTimer(_ => CheckStall(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public CaptureStateMachine StateMachine { get; } = new();

    public ICaptureTarget? Target { get; private set; }

    public CaptureProfile? Profile { get; private set; }

    public bool IsWaitingForFrames => _isWaitingForFrames;

    public event EventHandler<OcrObservation>? ObservationReady;

    public event EventHandler<PipelineStats>? StatsUpdated;

    public event EventHandler<bool>? WaitingForFramesChanged;

    /// <summary>対象の再選択を始める。実行中なら先に停止する。</summary>
    public async Task BeginSelectionAsync()
    {
        // 実行中だった場合、停止完了までに Paused へ移っている
        await StopAsync().ConfigureAwait(false);
        StateMachine.Transition(CaptureState.Selecting);
    }

    /// <summary>
    /// 選択を取り消した。以前の対象と ROI があれば再検証して戻り、
    /// 検証に失敗したら Calibrating のまま再調整を求める。
    /// </summary>
    public void CancelSelection(int ocrMaxImageDimension)
    {
        if (Target is null || Profile is null)
        {
            Target?.Dispose();
            Target = null;
            StateMachine.Transition(CaptureState.Idle);
            return;
        }

        StateMachine.Transition(CaptureState.Calibrating);
        TryApplyProfile(Profile, ocrMaxImageDimension);
    }

    public void SetTarget(ICaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target?.Dispose();
        Target = target;
        StateMachine.Transition(CaptureState.Calibrating);
    }

    public void BeginCalibration() => StateMachine.Transition(CaptureState.Calibrating);

    /// <summary>ROI を検証し、問題なければ Ready にする。Calibrating 中のみ。</summary>
    public RoiCheck TryApplyProfile(CaptureProfile profile, int ocrMaxImageDimension)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (Target is null)
        {
            throw new InvalidOperationException("取得対象が選択されていません。");
        }

        RoiCheck check = RoiRules.Check(profile, Target.CurrentSize, ocrMaxImageDimension);
        if (check.IsOk)
        {
            Profile = profile;
            StateMachine.Transition(CaptureState.Ready);
        }

        return check;
    }

    public void Start(double framesPerSecond)
    {
        lock (_gate)
        {
            if (Target is null || Profile is null)
            {
                throw new InvalidOperationException("取得対象と ROI が必要です。");
            }

            if (_runTask is not null)
            {
                throw new InvalidOperationException("既に実行中です。");
            }

            StateMachine.Transition(CaptureState.Running);
            _lastFrameTimestamp = _time.GetTimestamp();
            SetWaiting(false);

            var cts = new CancellationTokenSource();
            ICaptureSource source = Target.CreateSource();
            var request = new CaptureRequest(Profile, framesPerSecond);
            _runCts = cts;
            _runTask = Task.Run(() => RunAsync(source, request, cts.Token));
        }
    }

    /// <summary>新規の取得と OCR を止める。Running なら Paused へ移る。</summary>
    public async Task StopAsync()
    {
        Task? task;
        lock (_gate)
        {
            task = _runTask;
            _runCts?.Cancel();
        }

        if (task is not null)
        {
            await task.ConfigureAwait(false);
        }
    }

    private async Task RunAsync(ICaptureSource source, CaptureRequest request, CancellationToken ct)
    {
        (CaptureState next, ErrorCode? reason) outcome = (CaptureState.Paused, null);
        try
        {
            await using (source.ConfigureAwait(false))
            {
                await _pipeline.RunAsync(source, request, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (RoiReconfigurationRequiredException ex)
        {
            outcome = (CaptureState.ReconfigureRequired, ErrorCode.CAP03);
            _logger.LogWarning("Capture requires reconfiguration: {Status}", ex.Status);
        }
        catch (CaptureException ex)
        {
            outcome = (CaptureState.Faulted, ex.Code);
            _logger.LogWarning(ex, "Capture failed: {Kind}", ex.Kind);
        }
        catch (Exception ex)
        {
            outcome = (CaptureState.Faulted, null);
            _logger.LogError(ex, "Capture pipeline crashed");
        }
        finally
        {
            lock (_gate)
            {
                _runCts?.Dispose();
                _runCts = null;
                _runTask = null;
            }

            SetWaiting(false);
        }

        if (outcome.next == CaptureState.Faulted && outcome.reason == ErrorCode.CAP02)
        {
            Target?.Dispose();
            Target = null;
        }

        StateMachine.TryTransition(outcome.next, outcome.reason);
    }

    private void OnStatsUpdated(object? sender, PipelineStats stats)
    {
        Volatile.Write(ref _lastFrameTimestamp, _time.GetTimestamp());
        SetWaiting(false);
        StatsUpdated?.Invoke(this, stats);
    }

    private void CheckStall()
    {
        if (StateMachine.State != CaptureState.Running)
        {
            return;
        }

        TimeSpan sinceLast = _time.GetElapsedTime(Volatile.Read(ref _lastFrameTimestamp));
        if (sinceLast > FrameStallThreshold)
        {
            SetWaiting(true);
        }
    }

    private void SetWaiting(bool waiting)
    {
        if (_isWaitingForFrames == waiting)
        {
            return;
        }

        _isWaitingForFrames = waiting;
        WaitingForFramesChanged?.Invoke(this, waiting);
    }

    public async ValueTask DisposeAsync()
    {
        await _stallTimer.DisposeAsync().ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
        StateMachine.TryTransition(CaptureState.Stopped);
        Target?.Dispose();
        Target = null;
    }
}
