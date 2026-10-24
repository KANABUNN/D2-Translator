namespace D2Translator.Domain.Geometry;

/// <summary>取得画像のピクセル単位の大きさ。</summary>
public readonly record struct PixelSize(int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public double AspectRatio => IsEmpty ? 0 : (double)Width / Height;

    public override string ToString() => $"{Width}×{Height}";
}

/// <summary>取得画像のピクセル単位の矩形。右端と下端を含まない。</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PixelSize Size => new(Width, Height);

    public bool IsInside(PixelSize bounds) =>
        !IsEmpty && X >= 0 && Y >= 0 && Right <= bounds.Width && Bottom <= bounds.Height;

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>
    /// ドラッグの2点（画像ピクセル座標、小数可）から、両点を含む整数矩形を作り画像内へ収める。
    /// </summary>
    public static PixelRect FromCorners(double x1, double y1, double x2, double y2, PixelSize bounds)
    {
        if (bounds.IsEmpty)
        {
            throw new ArgumentException("画像サイズが空です。", nameof(bounds));
        }

        int left = Clamp((int)Math.Floor(Math.Min(x1, x2)), 0, bounds.Width);
        int top = Clamp((int)Math.Floor(Math.Min(y1, y2)), 0, bounds.Height);
        int right = Clamp((int)Math.Ceiling(Math.Max(x1, x2)), 0, bounds.Width);
        int bottom = Clamp((int)Math.Ceiling(Math.Max(y1, y2)), 0, bounds.Height);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    public override string ToString() => $"({X}, {Y}) {Width}×{Height}";

    private static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);
}
