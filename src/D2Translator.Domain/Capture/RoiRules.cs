using D2Translator.Domain.Geometry;

namespace D2Translator.Domain.Capture;

public enum RoiCheckStatus
{
    Ok,

    /// <summary>正規化矩形が範囲外、幅高さゼロ等。</summary>
    InvalidRoi,

    /// <summary>取得画像のサイズが空。</summary>
    EmptyFrame,

    /// <summary>調整時から縦横比が変わり、単純な拡大では継続できない。</summary>
    AspectRatioChanged,

    /// <summary>ROI が OCR に必要な最小サイズ未満。</summary>
    TooSmall,

    /// <summary>ROI が OCR エンジンの画像上限を超える。</summary>
    ExceedsOcrLimit,
}

public readonly record struct RoiCheck(RoiCheckStatus Status, PixelRect? PixelRoi)
{
    public bool IsOk => Status == RoiCheckStatus.Ok;
}

/// <summary>保存した ROI を現在の取得画像へ適用できるかの判定（仕様書 7.2）。</summary>
public static class RoiRules
{
    /// <summary>縦横比の相対変化がこれを超えたら再調整を求める。</summary>
    public const double AspectRatioTolerance = 0.01;

    public static PixelSize MinimumRoiSize { get; } = new(32, 12);

    public static RoiCheck Check(CaptureProfile profile, PixelSize currentFrame, int? ocrMaxImageDimension = null)
    {
        if (!profile.Roi.IsValid)
        {
            return new RoiCheck(RoiCheckStatus.InvalidRoi, null);
        }

        if (currentFrame.IsEmpty)
        {
            return new RoiCheck(RoiCheckStatus.EmptyFrame, null);
        }

        if (!profile.FrameSize.IsEmpty)
        {
            double change = Math.Abs(currentFrame.AspectRatio / profile.FrameSize.AspectRatio - 1);
            if (change > AspectRatioTolerance)
            {
                return new RoiCheck(RoiCheckStatus.AspectRatioChanged, null);
            }
        }

        PixelRect pixels = profile.Roi.ToPixels(currentFrame);
        if (pixels.Width < MinimumRoiSize.Width || pixels.Height < MinimumRoiSize.Height)
        {
            return new RoiCheck(RoiCheckStatus.TooSmall, pixels);
        }

        if (ocrMaxImageDimension is int max && (pixels.Width > max || pixels.Height > max))
        {
            return new RoiCheck(RoiCheckStatus.ExceedsOcrLimit, pixels);
        }

        return new RoiCheck(RoiCheckStatus.Ok, pixels);
    }
}
