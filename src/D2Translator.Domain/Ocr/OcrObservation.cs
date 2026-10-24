using D2Translator.Domain.Geometry;

namespace D2Translator.Domain.Ocr;

/// <summary>本アプリ独自の認識品質（仕様書 8.1）。翻訳の正しさの確率ではない。</summary>
public enum RecognitionQuality
{
    Good,
    NeedsReview,
    Hold,
}

/// <summary>位置は取得画像全体のピクセル座標。</summary>
public sealed record OcrWord(string Text, PixelRect Bounds);

public sealed record OcrLine(string Text, PixelRect Bounds, IReadOnlyList<OcrWord> Words);

/// <summary>
/// 1回の OCR で得た行、単語、位置、取得時刻の組（仕様書 1.2 観測）。
/// </summary>
public sealed record OcrObservation
{
    public required Guid ObservationId { get; init; }

    public required long FrameSequence { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>取得画像全体の中での ROI の位置。折り返しの判定に使う。</summary>
    public PixelRect RoiBounds { get; init; }

    public required IReadOnlyList<OcrLine> Lines { get; init; }

    /// <summary>行追跡で算出する（Phase 2）。未算出なら null。</summary>
    public RecognitionQuality? RecognitionQuality { get; init; }

    /// <summary>エンジンが数値を返さない場合は null。値を作らない。</summary>
    public double? EngineConfidence { get; init; }

    public TimeSpan OcrDuration { get; init; }
}
