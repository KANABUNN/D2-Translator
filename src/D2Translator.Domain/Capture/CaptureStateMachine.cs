using D2Translator.Domain.Errors;

namespace D2Translator.Domain.Capture;

/// <summary>仕様書 15.3 のキャプチャ状態。</summary>
public enum CaptureState
{
    Idle,
    Selecting,
    Calibrating,
    Ready,
    Running,
    Paused,
    ReconfigureRequired,
    Recovering,
    Faulted,
    Stopped,
}

public sealed class CaptureStateChangedEventArgs(CaptureState previous, CaptureState current, ErrorCode? reason) : EventArgs
{
    public CaptureState Previous { get; } = previous;

    public CaptureState Current { get; } = current;

    public ErrorCode? Reason { get; } = reason;
}

/// <summary>
/// 仕様書 15.3 の遷移表に、対象の再選択と終了処理（Stopped）への遷移を加えた状態機械。
/// </summary>
public sealed class CaptureStateMachine
{
    private static readonly Dictionary<CaptureState, CaptureState[]> Allowed = new()
    {
        [CaptureState.Idle] = [CaptureState.Selecting],
        [CaptureState.Selecting] = [CaptureState.Calibrating, CaptureState.Idle],
        [CaptureState.Calibrating] = [CaptureState.Ready, CaptureState.Selecting],
        [CaptureState.Ready] = [CaptureState.Running, CaptureState.Calibrating, CaptureState.Selecting],
        [CaptureState.Running] = [CaptureState.Paused, CaptureState.Recovering, CaptureState.ReconfigureRequired, CaptureState.Faulted],
        [CaptureState.Paused] = [CaptureState.Ready, CaptureState.Running, CaptureState.Calibrating, CaptureState.Selecting],
        [CaptureState.ReconfigureRequired] = [CaptureState.Calibrating, CaptureState.Selecting],
        [CaptureState.Recovering] = [CaptureState.Ready, CaptureState.Faulted],
        [CaptureState.Faulted] = [CaptureState.Ready, CaptureState.Selecting],
        [CaptureState.Stopped] = [],
    };

    private readonly Lock _gate = new();

    public CaptureState State { get; private set; } = CaptureState.Idle;

    public ErrorCode? LastReason { get; private set; }

    public event EventHandler<CaptureStateChangedEventArgs>? Changed;

    public static bool IsAllowed(CaptureState from, CaptureState to) =>
        to == CaptureState.Stopped ? from != CaptureState.Stopped : Allowed[from].Contains(to);

    public bool TryTransition(CaptureState to, ErrorCode? reason = null)
    {
        CaptureStateChangedEventArgs args;
        lock (_gate)
        {
            if (!IsAllowed(State, to))
            {
                return false;
            }

            args = new CaptureStateChangedEventArgs(State, to, reason);
            State = to;
            LastReason = reason;
        }

        Changed?.Invoke(this, args);
        return true;
    }

    public void Transition(CaptureState to, ErrorCode? reason = null)
    {
        if (!TryTransition(to, reason))
        {
            throw new InvalidOperationException($"状態 {State} から {to} へは遷移できません。");
        }
    }
}
