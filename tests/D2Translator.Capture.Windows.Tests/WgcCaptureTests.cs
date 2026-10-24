using D2Translator.Application.Capture;
using D2Translator.Domain.Capture;
using D2Translator.Domain.Geometry;
using D2Translator.Domain.Ocr;
using D2Translator.Ocr.Windows;

namespace D2Translator.Capture.Windows.Tests;

public class WgcCaptureTests
{
    private const string TopText = "Need two more for the raid";
    private const string BottomText = "Anyone have a spare sword";

    private static void SkipUnlessInteractive()
    {
        // GitHub の実行環境等、対話デスクトップがない場合は WGC を検証できない
        Assert.SkipWhen(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
            "CI では対話デスクトップがないためスキップします。");
        Assert.SkipUnless(WgcCaptureTarget.IsSupported(), "Windows Graphics Capture に対応していません。");
    }

    private static CaptureRequest Request(NormalizedRect roi, PixelSize frameSize) =>
        new(new CaptureProfile { Id = Guid.NewGuid(), Roi = roi, FrameSize = frameSize }, 10);

    [Fact]
    public async Task Roi_crop_reaches_ocr_with_frame_coordinates()
    {
        SkipUnlessInteractive();
        WindowsOcrRecognizer? ocr = WindowsOcrRecognizer.TryCreate("en-US", TimeProvider.System);
        Assert.SkipWhen(ocr is null, "英語 OCR の言語機能がインストールされていません。");
        CancellationToken ct = TestContext.Current.CancellationToken;

        using TestWindowHost window = await TestWindowHost.ShowAsync(TopText, BottomText);
        using WgcCaptureTarget target = WgcCaptureTarget.CreateForWindow(window.Hwnd, TimeProvider.System);
        await using ICaptureSource source = target.CreateSource();
        // 下半分だけを ROI にする
        var bottomHalf = new NormalizedRect(0, 0.5, 1, 0.5);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        OcrObservation? recognized = null;
        PixelRect expectedRoi = default;
        await foreach (RoiFrame frame in source.CaptureAsync(Request(bottomHalf, target.CurrentSize), timeout.Token))
        {
            using (frame)
            {
                expectedRoi = bottomHalf.ToPixels(frame.FrameSize);
                Assert.Equal(expectedRoi, frame.RoiInFrame);
                Assert.Equal(expectedRoi.Size, frame.Size);

                OcrObservation observation = await ocr!.RecognizeAsync(frame, timeout.Token);
                // 初回フレームは描画前のことがあるため、文字が読めるまで待つ
                if (observation.Lines.Count > 0)
                {
                    recognized = observation;
                    break;
                }
            }
        }

        Assert.NotNull(recognized);
        OcrLine line = Assert.Single(recognized.Lines);
        Assert.Equal(BottomText, line.Text);
        Assert.True(line.Bounds.Y >= expectedRoi.Y, $"bounds={line.Bounds} roi={expectedRoi}");
    }

    [Fact]
    public async Task Frames_are_throttled_to_requested_rate()
    {
        SkipUnlessInteractive();
        CancellationToken ct = TestContext.Current.CancellationToken;

        using TestWindowHost window = await TestWindowHost.ShowAsync(TopText, BottomText);
        using WgcCaptureTarget target = WgcCaptureTarget.CreateForWindow(window.Hwnd, TimeProvider.System);
        await using ICaptureSource source = target.CreateSource();
        var request = new CaptureRequest(
            new CaptureProfile { Id = Guid.NewGuid(), Roi = NormalizedRect.Full, FrameSize = target.CurrentSize }, 5);

        var timestamps = new List<long>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await foreach (RoiFrame frame in source.CaptureAsync(request, timeout.Token))
        {
            using (frame)
            {
                timestamps.Add(frame.Timestamp);
                if (timestamps.Count == 3)
                {
                    break;
                }
            }
        }

        // 試験ウィンドウは約 33 回毎秒で描画が変わるが、5 回毎秒（200ms 間隔）に間引かれる
        Assert.Equal(3, timestamps.Count);
        for (int i = 1; i < timestamps.Count; i++)
        {
            TimeSpan gap = TimeProvider.System.GetElapsedTime(timestamps[i - 1], timestamps[i]);
            Assert.True(gap >= TimeSpan.FromMilliseconds(190), $"gap={gap.TotalMilliseconds}ms");
        }
    }

    [Fact]
    public async Task Resizing_target_window_keeps_capturing_at_the_new_size()
    {
        SkipUnlessInteractive();
        CancellationToken ct = TestContext.Current.CancellationToken;

        using TestWindowHost window = await TestWindowHost.ShowAsync(TopText, BottomText);
        // アプリと同じく、取得対象は UI スレッド（メッセージループあり）で作る
        using WgcCaptureTarget target = window.Invoke(() => WgcCaptureTarget.CreateForWindow(window.Hwnd, TimeProvider.System));
        await using ICaptureSource source = target.CreateSource();
        PixelSize initial = target.CurrentSize;
        var request = new CaptureRequest(new CaptureProfile { Id = Guid.NewGuid(), Roi = NormalizedRect.Full, FrameSize = initial }, 10);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        PixelSize? resized = null;
        bool requested = false;
        await foreach (RoiFrame frame in source.CaptureAsync(request, timeout.Token))
        {
            using (frame)
            {
                if (!requested)
                {
                    // ゲームの解像度やウィンドウの大きさを変えた場合に当たる。フレームプールの作り直しで止まらないこと
                    window.Resize(820, 260);
                    requested = true;
                }
                else if (frame.FrameSize != initial)
                {
                    resized = frame.FrameSize;
                    break;
                }
            }
        }

        Assert.NotNull(resized);
        Assert.True(resized.Value.Width > initial.Width, $"initial={initial} resized={resized}");
    }

    [Fact]
    public async Task Closing_target_window_ends_capture_with_target_closed()
    {
        SkipUnlessInteractive();
        CancellationToken ct = TestContext.Current.CancellationToken;

        using TestWindowHost window = await TestWindowHost.ShowAsync(TopText, BottomText);
        // Closed イベントは対象を作ったスレッドのメッセージループで届く。アプリでも UI スレッドで選択する
        using WgcCaptureTarget target = window.Invoke(() => WgcCaptureTarget.CreateForWindow(window.Hwnd, TimeProvider.System));
        await using ICaptureSource source = target.CreateSource();
        var request = new CaptureRequest(
            new CaptureProfile { Id = Guid.NewGuid(), Roi = NormalizedRect.Full, FrameSize = target.CurrentSize }, 5);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<CaptureException>(async () =>
        {
            await foreach (RoiFrame frame in source.CaptureAsync(request, timeout.Token))
            {
                frame.Dispose();
                window.Close();
            }
        });

        Assert.Equal(CaptureFailureKind.TargetClosed, ex.Kind);
        Assert.True(target.IsClosed);
    }
}
