using D2Translator.Application.Ocr;
using D2Translator.Domain.Capture;
using D2Translator.Domain.Geometry;
using D2Translator.Domain.Ocr;
using Microsoft.Extensions.Logging;

namespace D2Translator.Application.Capture;

public sealed record PipelineStats(
    long FramesReceived,
    long FramesSkippedUnchanged,
    long OcrRuns,
    TimeSpan? LastOcrDuration,
    TimeSpan? AverageOcrDuration,
    DateTimeOffset? LastFrameAt,
    PixelSize? FrameSize,
    PixelRect? RoiPixels);

/// <summary>取得画像が保存した ROI の前提から外れ、再調整が必要。</summary>
public sealed class RoiReconfigurationRequiredException(RoiCheckStatus status)
    : Exception($"ROI の再調整が必要です: {status}")
{
    public RoiCheckStatus Status { get; } = status;
}

/// <summary>
/// 受信フローの取得から OCR まで（仕様書 15.1 の 2〜3）。
/// フレームの待ち行列は取得側で最新1件、OCR の同時実行は1件。
/// </summary>
public sealed class ReceivePipeline(IOcrRecognizer ocr, TimeProvider time, ILogger<ReceivePipeline> logger)
{
    public event EventHandler<OcrObservation>? ObservationReady;

    public event EventHandler<PipelineStats>? StatsUpdated;

    public async Task RunAsync(ICaptureSource source, CaptureRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        byte[]? previousPixels = null;
        long received = 0, skipped = 0, ocrRuns = 0;
        TimeSpan totalOcr = TimeSpan.Zero;
        TimeSpan? lastOcr = null;

        await foreach (RoiFrame frame in source.CaptureAsync(request, ct).ConfigureAwait(false))
        {
            using (frame)
            {
                received++;
                RoiCheck check = RoiRules.Check(request.Profile, frame.FrameSize, ocr.MaxImageDimension);
                if (!check.IsOk)
                {
                    logger.LogWarning("ROI check failed: {Status} frame={FrameSize}", check.Status, frame.FrameSize);
                    throw new RoiReconfigurationRequiredException(check.Status);
                }

                // 変化がなければ OCR を省く（仕様書 7.3）
                ReadOnlySpan<byte> pixels = frame.Pixels.Span;
                if (previousPixels is not null && previousPixels.AsSpan().SequenceEqual(pixels))
                {
                    skipped++;
                }
                else
                {
                    if (previousPixels is null || previousPixels.Length != pixels.Length)
                    {
                        previousPixels = new byte[pixels.Length];
                    }

                    pixels.CopyTo(previousPixels);

                    long started = time.GetTimestamp();
                    OcrObservation observation = await ocr.RecognizeAsync(frame, ct).ConfigureAwait(false);
                    TimeSpan elapsed = time.GetElapsedTime(started);
                    ocrRuns++;
                    totalOcr += elapsed;
                    lastOcr = elapsed;
                    ObservationReady?.Invoke(this, observation with { OcrDuration = elapsed });
                }

                StatsUpdated?.Invoke(this, new PipelineStats(
                    received, skipped, ocrRuns, lastOcr,
                    ocrRuns == 0 ? null : totalOcr / ocrRuns,
                    frame.CapturedAt, frame.FrameSize, frame.RoiInFrame));
            }
        }
    }
}
