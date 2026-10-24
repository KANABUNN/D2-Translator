using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using D2Translator.Application.Capture;
using D2Translator.Domain.Geometry;

namespace D2Translator.App.Presentation;

/// <summary>取得した BGRA 画素を WPF のプレビューへ書く。UI スレッドで呼ぶ。</summary>
internal static class BgraBitmap
{
    // 取得画像は 1 ピクセル = 1 DIP として扱い、表示側の Viewbox で縮小する
    public static WriteableBitmap Create(PixelSize size) =>
        new(size.Width, size.Height, 96, 96, PixelFormats.Bgr32, null);

    public static void Write(WriteableBitmap bitmap, byte[] pixels, PixelSize size) =>
        bitmap.WritePixels(new Int32Rect(0, 0, size.Width, size.Height), pixels, size.Width * RoiFrame.BytesPerPixel, 0);

    /// <summary>全体画像から ROI を切り出す（ROI 調整画面の試し読み用）。</summary>
    public static void CopyRegion(byte[] source, PixelSize sourceSize, PixelRect region, Span<byte> destination)
    {
        int rowBytes = region.Width * RoiFrame.BytesPerPixel;
        int sourceStride = sourceSize.Width * RoiFrame.BytesPerPixel;
        for (int y = 0; y < region.Height; y++)
        {
            int sourceOffset = (region.Y + y) * sourceStride + region.X * RoiFrame.BytesPerPixel;
            source.AsSpan(sourceOffset, rowBytes).CopyTo(destination.Slice(y * rowBytes, rowBytes));
        }
    }
}
