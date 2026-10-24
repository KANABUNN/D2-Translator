using D2Translator.Application.Capture;
using D2Translator.Domain.Capture;
using D2Translator.Domain.Geometry;
using D2Translator.Domain.Ocr;
using Microsoft.Extensions.Logging.Abstractions;

namespace D2Translator.Application.Tests;

public class ReceivePipelineTests
{
    private static readonly PixelSize Fhd = TestProfiles.Fhd;

    [Fact]
    public async Task Unchanged_frames_skip_ocr()
    {
        var ocr = new FakeOcr();
        var pipeline = TestPipelines.NewPipeline(ocr);
        var observations = new List<OcrObservation>();
        PipelineStats? last = null;
        pipeline.ObservationReady += (_, o) => observations.Add(o);
        pipeline.StatsUpdated += (_, s) => last = s;
        var source = new FakeCaptureSource([new(Fhd, 1), new(Fhd, 1), new(Fhd, 2), new(Fhd, 2), new(Fhd, 2)]);

        await pipeline.RunAsync(source, new CaptureRequest(TestProfiles.ChatArea(), 5), TestContext.Current.CancellationToken);

        Assert.Equal(2, ocr.Calls);
        Assert.Equal(["fill 1", "fill 2"], observations.Select(o => o.Lines[0].Text));
        Assert.NotNull(last);
        Assert.Equal(5, last.FramesReceived);
        Assert.Equal(3, last.FramesSkippedUnchanged);
        Assert.Equal(2, last.OcrRuns);
        Assert.Equal(Fhd, last.FrameSize);
    }

    [Fact]
    public async Task Ocr_positions_are_in_frame_coordinates()
    {
        var pipeline = TestPipelines.NewPipeline(new FakeOcr());
        OcrObservation? observation = null;
        pipeline.ObservationReady += (_, o) => observation = o;
        CaptureProfile profile = TestProfiles.ChatArea();

        await pipeline.RunAsync(new FakeCaptureSource([new(Fhd, 9)]), new CaptureRequest(profile, 5), TestContext.Current.CancellationToken);

        Assert.Equal(profile.Roi.ToPixels(Fhd), observation!.Lines[0].Bounds);
    }

    [Fact]
    public async Task Changed_aspect_ratio_stops_with_reconfiguration()
    {
        var ocr = new FakeOcr();
        var pipeline = TestPipelines.NewPipeline(ocr);
        var source = new FakeCaptureSource([new(Fhd, 1), new(new PixelSize(2560, 1080), 2)]);

        var ex = await Assert.ThrowsAsync<RoiReconfigurationRequiredException>(() =>
            pipeline.RunAsync(source, new CaptureRequest(TestProfiles.ChatArea(), 5), TestContext.Current.CancellationToken));

        Assert.Equal(RoiCheckStatus.AspectRatioChanged, ex.Status);
        Assert.Equal(1, ocr.Calls);
    }

    [Fact]
    public async Task Cancellation_stops_the_loop()
    {
        var pipeline = TestPipelines.NewPipeline(new FakeOcr());
        using var cts = new CancellationTokenSource();
        pipeline.ObservationReady += (_, _) => cts.Cancel();
        var source = new FakeCaptureSource([new(Fhd, 1), new(Fhd, 2), new(Fhd, 3)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.RunAsync(source, new CaptureRequest(TestProfiles.ChatArea(), 5), cts.Token));
    }
}
