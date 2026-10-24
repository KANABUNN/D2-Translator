using D2Translator.Domain.Geometry;

namespace D2Translator.Domain.Capture;

/// <summary>ROI の基準となる画像。現在は取得画像全体のみを扱う。</summary>
public enum ImageBasis
{
    CapturedImage,
}

/// <summary>
/// キャプチャ対象と ROI の保存単位（仕様書 13.2）。
/// HWND 等の対象を特定する値は恒久 ID として保存しない。
/// </summary>
public sealed record CaptureProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required Guid Id { get; init; }

    /// <summary>利用者が対象を見分けるための表示名。対象の再特定には使わない。</summary>
    public string? TargetDisplayName { get; init; }

    public ImageBasis ImageBasis { get; init; } = ImageBasis.CapturedImage;

    public required NormalizedRect Roi { get; init; }

    /// <summary>ROI を調整した時点の取得画像サイズ。</summary>
    public required PixelSize FrameSize { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
