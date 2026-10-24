using System.Buffers;
using D2Translator.Domain.Geometry;

namespace D2Translator.Application.Capture;

/// <summary>
/// 所有者付きの ROI 画素（BGRA 8bit、行間詰め）と取得情報（仕様書 14.1）。
/// 受け取った処理が読み取り後に破棄する。生の全画面フレームは持たない。
/// </summary>
public sealed class RoiFrame : IDisposable
{
    private byte[]? _buffer;

    private RoiFrame(byte[] buffer, PixelSize size, PixelRect roiInFrame, PixelSize frameSize,
        long frameSequence, DateTimeOffset capturedAt, long timestamp)
    {
        _buffer = buffer;
        Size = size;
        RoiInFrame = roiInFrame;
        FrameSize = frameSize;
        FrameSequence = frameSequence;
        CapturedAt = capturedAt;
        Timestamp = timestamp;
    }

    public const int BytesPerPixel = 4;

    public PixelSize Size { get; }

    /// <summary>取得画像全体の中での ROI の位置。</summary>
    public PixelRect RoiInFrame { get; }

    public PixelSize FrameSize { get; }

    public long FrameSequence { get; }

    /// <summary>取得時刻（UTC）。ゲームの発信時刻ではない。</summary>
    public DateTimeOffset CapturedAt { get; }

    /// <summary>単調時計の値（<see cref="TimeProvider.GetTimestamp"/>）。</summary>
    public long Timestamp { get; }

    public int Stride => Size.Width * BytesPerPixel;

    public int Length => Stride * Size.Height;

    public ReadOnlyMemory<byte> Pixels =>
        new(_buffer ?? throw new ObjectDisposedException(nameof(RoiFrame)), 0, Length);

    /// <summary>プールから必要量の領域を借りる。書き込みは <paramref name="fill"/> で行う。</summary>
    public static RoiFrame Create(PixelSize size, PixelRect roiInFrame, PixelSize frameSize,
        long frameSequence, DateTimeOffset capturedAt, long timestamp, SpanAction<byte, PixelSize> fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        if (size.IsEmpty)
        {
            throw new ArgumentException("ROI のサイズが空です。", nameof(size));
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(size.Width * BytesPerPixel * size.Height);
        try
        {
            fill(buffer.AsSpan(0, size.Width * BytesPerPixel * size.Height), size);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }

        return new RoiFrame(buffer, size, roiInFrame, frameSize, frameSequence, capturedAt, timestamp);
    }

    public void Dispose()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
