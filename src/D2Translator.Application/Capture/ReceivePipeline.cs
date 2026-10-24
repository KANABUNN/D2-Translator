using D2Translator.Application.Chat;
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
public sealed class ReceivePipeline(IOcrRecognizer ocr, ChatFeed feed, TimeProvider time, ILogger<ReceivePipeline> logger)
{
    public event EventHandler<OcrObservation>? ObservationReady;

    public event EventHandler<PipelineStats>? StatsUpdated;

    /// <summary>取得対象の変更。行追跡の基準と話者の対応表を捨てる。</summary>
    public void ResetSession(Guid sessionId) => feed.Reset(sessionId);

    /// <summary>停止からの再開。見えている発言を新着として扱わない。</summary>
    public void Resync() => feed.Resync();

    /// <summary>フレームが届かない間も、確定待ちの発言を時間で要確認へ進める。</summary>
    public void Tick() => feed.Tick(time.GetTimestamp());

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
                    // 画面が同じなら前回の読みをもう一度見たことになり、確定の判定を進められる
                    feed.OnUnchanged(frame.Timestamp);
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
                    observation = observation with { OcrDuration = elapsed };
                    ObservationReady?.Invoke(this, observation);
                    feed.OnObservation(observation, frame.Timestamp);
                }

                StatsUpdated?.Invoke(this, new PipelineStats(
                    received, skipped, ocrRuns, lastOcr,
                    ocrRuns == 0 ? null : totalOcr / ocrRuns,
                    frame.CapturedAt, frame.FrameSize, frame.RoiInFrame));
            }
        }
    }
}
