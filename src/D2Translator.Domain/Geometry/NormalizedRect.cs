namespace D2Translator.Domain.Geometry;

/// <summary>
/// 選択対象の取得画像を基準にした 0〜1 の正規化矩形（仕様書 7.2）。
/// </summary>
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    // 浮動小数の誤差で端のピクセルが1つずれないための許容量
    private const double PixelEpsilon = 1e-6;
    private const double RangeEpsilon = 1e-9;

    public static NormalizedRect Full { get; } = new(0, 0, 1, 1);

    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) &&
        X >= 0 && Y >= 0 && Width > 0 && Height > 0 &&
        X + Width <= 1 + RangeEpsilon && Y + Height <= 1 + RangeEpsilon;

    /// <summary>画像ピクセルの矩形へ変換する。端は外側へ丸め、画像内に収める。</summary>
    public PixelRect ToPixels(PixelSize image)
    {
        if (!IsValid)
        {
            throw new InvalidOperationException($"無効な正規化矩形です: {this}");
        }

        if (image.IsEmpty)
        {
            throw new ArgumentException("画像サイズが空です。", nameof(image));
        }

        int left = (int)Math.Floor(X * image.Width + PixelEpsilon);
        int top = (int)Math.Floor(Y * image.Height + PixelEpsilon);
        int right = (int)Math.Ceiling((X + Width) * image.Width - PixelEpsilon);
        int bottom = (int)Math.Ceiling((Y + Height) * image.Height - PixelEpsilon);

        left = Math.Clamp(left, 0, image.Width - 1);
        top = Math.Clamp(top, 0, image.Height - 1);
        right = Math.Clamp(right, left + 1, image.Width);
        bottom = Math.Clamp(bottom, top + 1, image.Height);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    public static NormalizedRect FromPixels(PixelRect rect, PixelSize image)
    {
        if (image.IsEmpty)
        {
            throw new ArgumentException("画像サイズが空です。", nameof(image));
        }

        if (!rect.IsInside(image))
        {
            throw new ArgumentOutOfRangeException(nameof(rect), rect, $"矩形が画像 {image} の外にあります。");
        }

        return new NormalizedRect(
            (double)rect.X / image.Width,
            (double)rect.Y / image.Height,
            (double)rect.Width / image.Width,
            (double)rect.Height / image.Height);
    }

    public override string ToString() => $"x={X:0.####} y={Y:0.####} w={Width:0.####} h={Height:0.####}";
}
