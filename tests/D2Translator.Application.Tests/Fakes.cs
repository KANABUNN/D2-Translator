using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using D2Translator.Application.Capture;
using D2Translator.Application.Chat;
using D2Translator.Application.Diagnostics;
using D2Translator.Application.Ocr;
using D2Translator.DictionaryContext;
using D2Translator.TextTracking.Parsing;
using D2Translator.TextTracking.Speakers;
using D2Translator.TextTracking.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using D2Translator.Domain.Capture;
using D2Translator.Domain.Geometry;
using D2Translator.Domain.Ocr;

namespace D2Translator.Application.Tests;

/// <param name="Fill">ROI の全画素をこの値で埋める。同じ値なら画素が変化していない。</param>
internal sealed record FakeFrame(PixelSize FrameSize, byte Fill);

internal sealed class FakeCaptureSource(IReadOnlyList<FakeFrame> frames, Exception? endWith = null) : ICaptureSource
{
    public bool Disposed { get; private set; }

    public async IAsyncEnumerable<RoiFrame> CaptureAsync(CaptureRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        long sequence = 0;
        foreach (FakeFrame frame in frames)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            PixelRect roi = request.Profile.Roi.ToPixels(frame.FrameSize);
            yield return RoiFrame.Create(roi.Size, roi, frame.FrameSize, ++sequence, DateTimeOffset.UtcNow, 0,
                (span, _) => span.Fill(frame.Fill));
        }

        if (endWith is not null)
        {
            throw endWith;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeCaptureTarget(PixelSize size, Func<ICaptureSource> createSource) : ICaptureTarget
{
    public string DisplayName => "Fake";

    public PixelSize CurrentSize => size;

    public bool Disposed { get; private set; }

    public ICaptureSource CreateSource() => createSource();

    public void Dispose() => Disposed = true;
}

internal sealed class FakeOcr : IOcrRecognizer
{
    public int Calls { get; private set; }

    public int MaxImageDimension => 10000;

    public Task<OcrObservation> RecognizeAsync(RoiFrame frame, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new OcrObservation
        {
            ObservationId = Guid.NewGuid(),
            FrameSequence = frame.FrameSequence,
            CapturedAt = frame.CapturedAt,
            Lines = [new OcrLine($"fill {frame.Pixels.Span[0]}", frame.RoiInFrame, [])],
        });
    }
}

internal sealed class MemorySink : IDiagnosticsSink
{
    private readonly ConcurrentQueue<DiagnosticsEvent> _events = new();

    public IReadOnlyList<DiagnosticsEvent> Events => [.. _events];

    public void Write(DiagnosticsEvent entry) => _events.Enqueue(entry);
}

internal static class TestPipelines
{
    public static ChatFeed NewFeed() => new(
        new ChatLineParser(ChatFormat.JapaneseClient),
        new MessageTracker(TimeProvider.System),
        new SpeakerRegistry(),
        GlossaryMatcher.FromDocument(GlossaryLoader.LoadExample().Document!));

    public static ReceivePipeline NewPipeline(IOcrRecognizer ocr, ChatFeed? feed = null) =>
        new(ocr, feed ?? NewFeed(), TimeProvider.System, NullLogger<ReceivePipeline>.Instance);
}

internal static class TestProfiles
{
    public static readonly PixelSize Fhd = new(1920, 1080);

    public static CaptureProfile ChatArea() => new()
    {
        Id = Guid.NewGuid(),
        Roi = new NormalizedRect(0.02, 0.6, 0.3, 0.25),
        FrameSize = Fhd,
    };
}
