using D2Translator.Domain.Capture;
using D2Translator.Domain.Errors;

namespace D2Translator.Domain.Tests.Capture;

public class CaptureStateMachineTests
{
    [Fact]
    public void Normal_flow_reaches_running_and_back()
    {
        var machine = new CaptureStateMachine();
        var seen = new List<CaptureState>();
        machine.Changed += (_, e) => seen.Add(e.Current);

        machine.Transition(CaptureState.Selecting);
        machine.Transition(CaptureState.Calibrating);
        machine.Transition(CaptureState.Ready);
        machine.Transition(CaptureState.Running);
        machine.Transition(CaptureState.Paused);
        machine.Transition(CaptureState.Running);

        Assert.Equal(
            [CaptureState.Selecting, CaptureState.Calibrating, CaptureState.Ready, CaptureState.Running, CaptureState.Paused, CaptureState.Running],
            seen);
    }

    [Theory]
    [InlineData(CaptureState.Idle, CaptureState.Running)]
    [InlineData(CaptureState.Selecting, CaptureState.Running)]
    [InlineData(CaptureState.ReconfigureRequired, CaptureState.Running)]
    [InlineData(CaptureState.Faulted, CaptureState.Running)]
    [InlineData(CaptureState.Running, CaptureState.Calibrating)]
    public void Disallowed_transitions_are_rejected(CaptureState from, CaptureState to)
    {
        Assert.False(CaptureStateMachine.IsAllowed(from, to));
    }

    [Fact]
    public void Failed_transition_keeps_state_and_raises_nothing()
    {
        var machine = new CaptureStateMachine();
        bool raised = false;
        machine.Changed += (_, _) => raised = true;

        Assert.False(machine.TryTransition(CaptureState.Running));
        Assert.Equal(CaptureState.Idle, machine.State);
        Assert.False(raised);
    }

    [Fact]
    public void Reason_is_reported_with_transition()
    {
        var machine = new CaptureStateMachine();
        machine.Transition(CaptureState.Selecting);
        machine.Transition(CaptureState.Calibrating);
        machine.Transition(CaptureState.Ready);
        machine.Transition(CaptureState.Running);
        ErrorCode? reported = null;
        machine.Changed += (_, e) => reported = e.Reason;

        machine.Transition(CaptureState.ReconfigureRequired, ErrorCode.CAP03);

        Assert.Equal(ErrorCode.CAP03, reported);
        Assert.Equal(ErrorCode.CAP03, machine.LastReason);
    }

    [Fact]
    public void Every_state_except_stopped_can_stop()
    {
        foreach (CaptureState state in Enum.GetValues<CaptureState>())
        {
            Assert.Equal(state != CaptureState.Stopped, CaptureStateMachine.IsAllowed(state, CaptureState.Stopped));
        }
    }
}
