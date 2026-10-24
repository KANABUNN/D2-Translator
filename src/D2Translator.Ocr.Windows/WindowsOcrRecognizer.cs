using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using D2Translator.Application.Capture;
using D2Translator.Application.Ocr;
using D2Translator.Domain.Geometry;
using D2Translator.Domain.Ocr;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace D2Translator.Ocr.Windows;

/// <summary>
/// Windows.Media.Ocr による端末内 OCR（仕様書 8.1）。
/// 公式サポートはパッケージ識別情報を持つデスクトップアプリに限られる（仕様書 6.2）。
/// </summary>
public sealed class WindowsOcrRecognizer : IOcrRecognizer
{
    private readonly WinOcr.OcrEngine _engine;
    private readonly TimeProvider _time;

    private WindowsOcrRecognizer(WinOcr.OcrEngine engine, TimeProvider time)
    {
        _engine = engine;
        _time = time;
    }

    public int MaxImageDimension => (int)WinOcr.OcrEngine.MaxImageDimension;

    public string LanguageTag => _engine.RecognizerLanguage.LanguageTag;

    public static OcrAvailability CheckAvailability(string languageTag)
    {
        List<string> installed = [.. WinOcr.OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag)];
        bool available = TryCreateLanguage(languageTag) is Language language && WinOcr.OcrEngine.IsLanguageSupported(language);
        return new OcrAvailability(languageTag, available, installed, (int)WinOcr.OcrEngine.MaxImageDimension);
    }

    /// <summary>言語機能がなければ null（OCR01）。OS への追加は利用者が行う。</summary>
    public static WindowsOcrRecognizer? TryCreate(string languageTag, TimeProvider time)
    {
        if (TryCreateLanguage(languageTag) is not Language language)
        {
            return null;
        }

        WinOcr.OcrEngine? engine = WinOcr.OcrEngine.TryCreateFromLanguage(language);
        return engine is null ? null : new WindowsOcrRecognizer(engine, time);
    }

    public async Task<OcrObservation> RecognizeAsync(RoiFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ct.ThrowIfCancellationRequested();

        if (frame.Size.Width > MaxImageDimension || frame.Size.Height > MaxImageDimension)
        {
            throw new ArgumentException($"OCR の画像上限 {MaxImageDimension} を超えています: {frame.Size}", nameof(frame));
        }

        long started = _time.GetTimestamp();
        WinOcr.OcrResult result;
        using (SoftwareBitmap bitmap = CreateBitmap(frame))
        {
            result = await _engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
        }

        int offsetX = frame.RoiInFrame.X;
        int offsetY = frame.RoiInFrame.Y;
        var lines = new List<OcrLine>(result.Lines.Count);
        foreach (WinOcr.OcrLine line in result.Lines)
        {
            var words = new List<OcrWord>(line.Words.Count);
            foreach (WinOcr.OcrWord word in line.Words)
            {
                words.Add(new OcrWord(word.Text, ToPixelRect(word.BoundingRect).Offset(offsetX, offsetY)));
            }

            lines.Add(new OcrLine(line.Text, Union(words), words));
        }

        return new OcrObservation
        {
            ObservationId = Guid.NewGuid(),
            FrameSequence = frame.FrameSequence,
            CapturedAt = frame.CapturedAt,
            Lines = lines,
            // Windows OCR は単語ごとの数値信頼度を返さない（仕様書 8.1）
            EngineConfidence = null,
            OcrDuration = _time.GetElapsedTime(started),
        };
    }

    private static SoftwareBitmap CreateBitmap(RoiFrame frame)
    {
        if (!MemoryMarshal.TryGetArray(frame.Pixels, out ArraySegment<byte> segment))
        {
            throw new InvalidOperationException("ROI の画素バッファを参照できません。");
        }

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, frame.Size.Width, frame.Size.Height, BitmapAlphaMode.Premultiplied);
        try
        {
            bitmap.CopyFromBuffer(segment.Array!.AsBuffer(segment.Offset, segment.Count));
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static PixelRect ToPixelRect(global::Windows.Foundation.Rect rect)
    {
        int left = (int)Math.Floor(rect.X);
        int top = (int)Math.Floor(rect.Y);
        int right = (int)Math.Ceiling(rect.X + rect.Width);
        int bottom = (int)Math.Ceiling(rect.Y + rect.Height);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    private static PixelRect Union(IReadOnlyList<OcrWord> words)
    {
        if (words.Count == 0)
        {
            return default;
        }

        int left = words.Min(w => w.Bounds.X);
        int top = words.Min(w => w.Bounds.Y);
        int right = words.Max(w => w.Bounds.Right);
        int bottom = words.Max(w => w.Bounds.Bottom);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    private static Language? TryCreateLanguage(string languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag) || !Language.IsWellFormed(languageTag))
        {
            return null;
        }

        return new Language(languageTag);
    }
}
