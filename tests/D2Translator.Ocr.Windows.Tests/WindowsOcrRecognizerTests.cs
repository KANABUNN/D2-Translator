using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using D2Translator.Application.Capture;
using D2Translator.Application.Ocr;
using D2Translator.Domain.Chat;
using D2Translator.Domain.Geometry;
using D2Translator.Domain.Ocr;
using D2Translator.TextTracking.Parsing;

namespace D2Translator.Ocr.Windows.Tests;

public class WindowsOcrRecognizerTests
{
    private const string Language = "en-US";

    [Fact]
    public void Availability_reports_installed_languages_and_limit()
    {
        OcrAvailability availability = WindowsOcrRecognizer.CheckAvailability(Language);

        Assert.Equal(Language, availability.RequestedLanguage);
        Assert.True(availability.MaxImageDimension > 0);
        Assert.Equal(availability.IsAvailable, WindowsOcrRecognizer.TryCreate(Language, TimeProvider.System) is not null);
    }

    [Fact]
    public void Malformed_language_tag_is_unavailable()
    {
        Assert.False(WindowsOcrRecognizer.CheckAvailability("not a tag!").IsAvailable);
        Assert.Null(WindowsOcrRecognizer.TryCreate("not a tag!", TimeProvider.System));
    }

    [Fact]
    public async Task Recognizes_rendered_text_in_frame_coordinates()
    {
        WindowsOcrRecognizer? recognizer = WindowsOcrRecognizer.TryCreate(Language, TimeProvider.System);
        Assert.SkipWhen(recognizer is null, "英語 OCR の言語機能がインストールされていません。");

        const string text = "Looking for two more players";
        (byte[] pixels, PixelSize size) = await RenderTextAsync(text, 640, 64);
        var roiInFrame = new PixelRect(100, 900, size.Width, size.Height);
        using RoiFrame frame = RoiFrame.Create(size, roiInFrame, new PixelSize(1920, 1080), 7,
            DateTimeOffset.UtcNow, 0, (span, _) => pixels.CopyTo(span));

        OcrObservation observation = await recognizer!.RecognizeAsync(frame, TestContext.Current.CancellationToken);

        OcrLine line = Assert.Single(observation.Lines);
        Assert.Equal(text, line.Text);
        Assert.Equal(5, line.Words.Count);
        Assert.Equal(7, observation.FrameSequence);
        Assert.Null(observation.EngineConfidence);
        Assert.Null(observation.RecognitionQuality);
        Assert.True(line.Bounds.X >= roiInFrame.X && line.Bounds.Right <= roiInFrame.Right, $"bounds={line.Bounds}");
        Assert.True(line.Bounds.Y >= roiInFrame.Y && line.Bounds.Bottom <= roiInFrame.Bottom, $"bounds={line.Bounds}");
    }

    [Fact]
    public async Task Frame_over_engine_limit_is_rejected()
    {
        WindowsOcrRecognizer? recognizer = WindowsOcrRecognizer.TryCreate(Language, TimeProvider.System);
        Assert.SkipWhen(recognizer is null, "英語 OCR の言語機能がインストールされていません。");

        var size = new PixelSize(recognizer!.MaxImageDimension + 1, 1);
        using RoiFrame frame = RoiFrame.Create(size, new PixelRect(0, 0, size.Width, 1), size, 1,
            DateTimeOffset.UtcNow, 0, (span, _) => span.Clear());

        await Assert.ThrowsAsync<ArgumentException>(() => recognizer.RecognizeAsync(frame, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Japanese_client_rows_are_assembled_from_both_languages()
    {
        WindowsOcrRecognizer? recognizer = WindowsOcrRecognizer.TryCreate(Language, "ja", 2, TimeProvider.System);
        Assert.SkipWhen(recognizer?.ClientLanguageTag is null, "英語または日本語の OCR 言語機能がインストールされていません。");

        // 日本語クライアントの表示: 行頭は色付き、本文は白
        (string Prefix, string Body)[] rows =
        [
            ("[ファイアチーム]宛て: ", "Need two more for the raid"),
            ("", "Pancake916#1234がチームチャットに参加しました。"),
            ("[Kestrel#5678]: ", "Sounds broken"),
        ];
        (byte[] pixels, PixelSize size) = await RenderRowsAsync(rows, 560, 120);
        var roi = new PixelRect(0, 0, size.Width, size.Height);
        using RoiFrame frame = RoiFrame.Create(size, roi, size, 1, DateTimeOffset.UtcNow, 0, (span, _) => pixels.CopyTo(span));

        OcrObservation observation = await recognizer!.RecognizeAsync(frame, TestContext.Current.CancellationToken);
        IReadOnlyList<ChatCandidate> candidates = new ChatLineParser(ChatFormat.JapaneseClient).Parse(observation.Lines, observation.RoiBounds);

        string read = string.Join(" | ", observation.Lines.Select(l => l.Text));
        Assert.True(candidates.Count == 3, read);
        Assert.Equal((ChatLineKind.Own, ChatChannel.Fireteam, "Need two more for the raid"),
            (candidates[0].Kind, candidates[0].Channel, candidates[0].Body));
        Assert.Equal(ChatLineKind.System, candidates[1].Kind);
        Assert.True(candidates[1].Body.Contains("チームチャットに参加しました", StringComparison.Ordinal), read);
        Assert.StartsWith("Pancake916", candidates[1].Speaker, StringComparison.Ordinal);
        Assert.Equal((ChatLineKind.Player, "Sounds broken"), (candidates[2].Kind, candidates[2].Body));
        Assert.StartsWith("Kestrel", candidates[2].Speaker, StringComparison.Ordinal);
    }

    private static Task<(byte[] Pixels, PixelSize Size)> RenderRowsAsync((string Prefix, string Body)[] rows, int width, int height) =>
        RenderAsync(width, height, dc =>
        {
            var teal = new SolidColorBrush(Color.FromRgb(0x5F, 0x9E, 0x86));
            for (int i = 0; i < rows.Length; i++)
            {
                string text = rows[i].Prefix + rows[i].Body;
                var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight,
                    new Typeface("Yu Gothic UI"), 17, Brushes.White, pixelsPerDip: 1.0);
                if (rows[i].Prefix.Length > 0)
                {
                    formatted.SetForegroundBrush(teal, 0, rows[i].Prefix.Length);
                }

                dc.DrawText(formatted, new Point(8, 8 + i * 36));
            }
        });

    // D2 のチャットに近い、暗い背景の白文字を描く
    private static Task<(byte[] Pixels, PixelSize Size)> RenderTextAsync(string text, int width, int height) =>
        RenderAsync(width, height, dc =>
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 28, Brushes.White, pixelsPerDip: 1.0);
            dc.DrawText(formatted, new Point(16, 12));
        });

    private static Task<(byte[] Pixels, PixelSize Size)> RenderAsync(int width, int height, Action<DrawingContext> draw)
    {
        var completion = new TaskCompletionSource<(byte[], PixelSize)>();
        var thread = new Thread(() =>
        {
            try
            {
                var visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
                    draw(dc);
                }

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                completion.SetResult((pixels, new PixelSize(width, height)));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
