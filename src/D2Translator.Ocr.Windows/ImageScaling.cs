using D2Translator.Application.Capture;
using D2Translator.Domain.Geometry;

namespace D2Translator.Ocr.Windows;

/// <summary>OCR 前の前処理（仕様書 8.1）。</summary>
internal static class ImageScaling
{
    /// <summary>
    /// 最近傍法で整数倍に拡大する。小さいチャット文字で Windows OCR の認識が安定する（Phase 1 の実画面で確認）。
    /// </summary>
    public static byte[] UpscaleNearest(ArraySegment<byte> source, PixelSize size, int scale)
    {
        const int bpp = RoiFrame.BytesPerPixel;
        int sourceStride = size.Width * bpp;
        int targetWidth = size.Width * scale;
        int targetStride = targetWidth * bpp;
        var target = new byte[targetStride * size.Height * scale];
        ReadOnlySpan<byte> src = source.AsSpan();
        Span<byte> row = targetStride <= 16 * 1024 ? stackalloc byte[targetStride] : new byte[targetStride];

        for (int y = 0; y < size.Height; y++)
        {
            ReadOnlySpan<byte> sourceRow = src.Slice(y * sourceStride, sourceStride);
            for (int x = 0; x < size.Width; x++)
            {
                ReadOnlySpan<byte> pixel = sourceRow.Slice(x * bpp, bpp);
                for (int k = 0; k < scale; k++)
                {
                    pixel.CopyTo(row.Slice((x * scale + k) * bpp, bpp));
                }
            }

            for (int k = 0; k < scale; k++)
            {
                row.CopyTo(target.AsSpan((y * scale + k) * targetStride, targetStride));
            }
        }

        return target;
    }
}
