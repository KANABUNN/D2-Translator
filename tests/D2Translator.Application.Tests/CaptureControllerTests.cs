using D2Translator.Application.Capture;
using D2Translator.Domain.Capture;
using D2Translator.Domain.Errors;
using D2Translator.Domain.Geometry;
using Microsoft.Extensions.Logging.Abstractions;

namespace D2Translator.Application.Tests;

public class CaptureControllerTests
{
    private static readonly PixelSize Fhd = TestProfiles.Fhd;

    private static CaptureController NewController(TimeSpan? recoveryDelay = null) => new(
        TestPipelines.NewPipeline(new FakeOcr()),
        TimeProvider.System,
        NullLogger<CaptureController>.Instance)
    {
        RecoveryDelay = recoveryDelay ?? TimeSpan.Zero,
    };

    private static async Task<CaptureController> ReadyControllerAsync(FakeCaptureTarget target, TimeSpan? recoveryDelay = null)
    {
        CaptureController controller = NewController(recoveryDelay);
        await controller.BeginSelectionAsync();
        controller.SetTarget(target);
        Assert.True(controller.TryApplyProfile(TestProfiles.ChatArea(), 10000).IsOk);
        Assert.Equal(CaptureState.Ready, controller.StateMachine.State);
        return controller;
    }

    private static Task<CaptureStateChangedEventArgs> NextStateAfterRunning(CaptureController controller)
    {
        var completion = new TaskCompletionSource<CaptureStateChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StateMachine.Changed += (_, e) =>
        {
            if (e.Previous == CaptureState.Running)
            {
                completion.TrySetResult(e);
            }
        };
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static Task<CaptureStateChangedEventArgs> WaitForState(CaptureController controller, CaptureState state)
    {
        var completion = new TaskCompletionSource<CaptureStateChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StateMachine.Changed += (_, e) =>
        {
            if (e.Current == state)
            {
                completion.TrySetResult(e);
            }
        };
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static CaptureException DeviceLost() => new(CaptureFailureKind.DeviceLost, "lost");

    [Fact]
    public async Task Source_end_returns_to_paused_and_disposes_source()
    {
        var source = new FakeCaptureSource([new(Fhd, 1), new(Fhd, 2)]);
        await using CaptureController controller = await ReadyControllerAsync(new FakeCaptureTarget(Fhd, () => source));
        Task<CaptureStateChangedEventArgs> next = NextStateAfterRunning(controller);

        controller.Start(5);
        CaptureStateChangedEventArgs e = await next;

        Assert.Equal(CaptureState.Paused, e.Current);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task Target_closed_faults_with_cap02_and_releases_target()
    {
        var target = new FakeCaptureTarget(Fhd, () => new FakeCaptureSource(
            [new(Fhd, 1)], new CaptureException(CaptureFailureKind.TargetClosed, "closed")));
        await using CaptureController controller = await ReadyControllerAsync(target);
        Task<CaptureStateChangedEventArgs> next = NextStateAfterRunning(controller);

        controller.Start(5);
        CaptureStateChangedEventArgs e = await next;

        Assert.Equal(CaptureState.Faulted, e.Current);
        Assert.Equal(ErrorCode.CAP02, e.Reason);
        Assert.Null(controller.Target);
        Assert.True(target.Disposed);
    }

    [Fact]
    public async Task Aspect_change_requires_reconfiguration_with_cap03()
    {
        var target = new FakeCaptureTarget(Fhd, () => new FakeCaptureSource([new(new PixelSize(1080, 1080), 1)]));
        await using CaptureController controller = await ReadyControllerAsync(target);
        Task<CaptureStateChangedEventArgs> next = NextStateAfterRunning(controller);

        controller.Start(5);
        CaptureStateChangedEventArgs e = await next;

        Assert.Equal(CaptureState.ReconfigureRequired, e.Current);
        Assert.Equal(ErrorCode.CAP03, e.Reason);
        Assert.NotNull(controller.Target);
    }

    [Fact]
    public async Task Device_loss_recreates_capture_and_keeps_running()
    {
        var sources = new Queue<FakeCaptureSource>([new([new(Fhd, 1)], DeviceLost()), new([new(Fhd, 2)])]);
        await using CaptureController controller = await ReadyControllerAsync(new FakeCaptureTarget(Fhd, sources.Dequeue));
        var states = new System.Collections.Concurrent.ConcurrentQueue<CaptureState>();
        controller.StateMachine.Changed += (_, e) => states.Enqueue(e.Current);
        Task<CaptureStateChangedEventArgs> paused = WaitForState(controller, CaptureState.Paused);

        controller.Start(5);
        await paused;

        Assert.Equal([CaptureState.Running, CaptureState.Recovering, CaptureState.Ready, CaptureState.Running, CaptureState.Paused], states);
        Assert.Empty(sources);
    }

    [Fact]
    public async Task Repeated_device_loss_stops_after_finite_attempts()
    {
        int created = 0;
        var target = new FakeCaptureTarget(Fhd, () =>
        {
            Interlocked.Increment(ref created);
            return new FakeCaptureSource([new(Fhd, 1)], DeviceLost());
        });
        await using CaptureController controller = await ReadyControllerAsync(target);
        Task<CaptureStateChangedEventArgs> faulted = WaitForState(controller, CaptureState.Faulted);

        controller.Start(5);
        CaptureStateChangedEventArgs e = await faulted;

        Assert.Equal(ErrorCode.CAP01, e.Reason);
        Assert.Equal(CaptureController.MaxRecoveryAttempts + 1, created);
        // GPU の喪失では対象を手放さず、利用者が開始し直せる
        Assert.Same(target, controller.Target);
    }

    [Fact]
    public async Task Stop_while_recovering_returns_to_ready()
    {
        var target = new FakeCaptureTarget(Fhd, () => new FakeCaptureSource([], DeviceLost()));
        await using CaptureController controller = await ReadyControllerAsync(target, recoveryDelay: TimeSpan.FromHours(1));
        Task<CaptureStateChangedEventArgs> recovering = WaitForState(controller, CaptureState.Recovering);
        controller.Start(5);
        await recovering;

        await controller.StopAsync().WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(CaptureState.Ready, controller.StateMachine.State);
    }

    [Fact]
    public async Task Cancelled_selection_without_target_returns_to_idle()
    {
        await using CaptureController controller = NewController();
        await controller.BeginSelectionAsync();

        controller.CancelSelection(10000);

        Assert.Equal(CaptureState.Idle, controller.StateMachine.State);
    }

    [Fact]
    public async Task Cancelled_reselection_keeps_previous_target()
    {
        var target = new FakeCaptureTarget(Fhd, () => new FakeCaptureSource([]));
        await using CaptureController controller = await ReadyControllerAsync(target);
        await controller.BeginSelectionAsync();

        controller.CancelSelection(10000);

        Assert.Equal(CaptureState.Ready, controller.StateMachine.State);
        Assert.Same(target, controller.Target);
        Assert.False(target.Disposed);
    }

    [Fact]
    public async Task Start_without_profile_is_rejected()
    {
        await using CaptureController controller = NewController();

        Assert.Throws<InvalidOperationException>(() => controller.Start(5));
    }
}
