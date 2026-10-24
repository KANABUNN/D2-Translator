using System.Runtime.CompilerServices;
using System.Threading.Channels;
using D2Translator.Application.Capture;

namespace D2Translator.Capture.Windows;

/// <summary>
/// WGC の到着フレームから、指定頻度で ROI だけを切り出して渡す（仕様書 7.2、7.3）。
/// 待ち行列は最新1件で、古い未処理画像は捨てる。
/// </summary>
internal sealed class WgcCaptureSource(WgcCaptureTarget target, TimeProvider time) : ICaptureSource
{
    private int _started;

    public async IAsyncEnumerable<RoiFrame> CaptureAsync(CaptureRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("取得は1回だけ開始できます。");
        }

        if (target.IsClosed)
        {
            throw new CaptureException(CaptureFailureKind.TargetClosed, "取得対象が閉じられています。");
        }

        var channel = Channel.CreateBounded<RoiFrame>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            dropped => dropped.Dispose());

        void OnClosed(object? sender, EventArgs e) =>
            channel.Writer.TryComplete(new CaptureException(CaptureFailureKind.TargetClosed, "取得対象が閉じられました。"));

        target.Closed += OnClosed;
        WgcFrameGrabber? grabber = null;
        try
        {
            grabber = await CreateGrabberAsync(request, channel.Writer).ConfigureAwait(false);

            await foreach (RoiFrame frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return frame;
            }
        }
        finally
        {
            target.Closed -= OnClosed;
            if (grabber is not null)
            {
                await target.RunOnOwnerAsync(grabber.Dispose).ConfigureAwait(false);
            }

            channel.Writer.TryComplete();
            while (channel.Reader.TryRead(out RoiFrame? leftover))
            {
                leftover.Dispose();
            }
        }
    }

    private async Task<WgcFrameGrabber> CreateGrabberAsync(CaptureRequest request, ChannelWriter<RoiFrame> writer)
    {
        try
        {
            // セッションの作成と破棄は取得対象を作ったスレッドで行う。フレームはフリースレッドで届く
            return await target.RunOnOwnerAsync(() => new WgcFrameGrabber(target.Item, request, writer, time)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException)
        {
            // GPU デバイスを作れない（喪失直後やドライバーの更新中等）。取得側で有限回作り直す
            throw new CaptureException(CaptureFailureKind.DeviceLost, "GPU デバイスを作成できません。", ex);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
