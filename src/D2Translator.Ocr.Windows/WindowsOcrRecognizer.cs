using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using D2Translator.Application.Capture;
using D2Translator.Application.Ocr;
using D2Translator.Domain.Geometry;
using D2Translator.Domain.Ocr;
using D2Translator.TextTracking.Rows;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace D2Translator.Ocr.Windows;

/// <summary>
/// Windows.Media.Ocr による端末内 OCR（仕様書 8.1）。
/// 公式サポートはパッケージ識別情報を持つデスクトップアプリに限られる（仕様書 6.2）。
/// </summary>
/// <remarks>
/// ゲームの表示言語（例: 日本語）が本文の言語と違う場合は両方の言語で認識し、
/// 日本語の文字がある範囲だけ表示言語側の結果を使って行を組み立てる（Phase 2、決定 D13）。
/// </remarks>
public sealed class WindowsOcrRecognizer : IOcrRecognizer
{
    private readonly WinOcr.OcrEngine _textEngine;
    private readonly WinOcr.OcrEngine? _clientEngine;
    private readonly int _scale;
    private readonly TimeProvider _time;

    private WindowsOcrRecognizer(WinOcr.OcrEngine textEngine, WinOcr.OcrEngine? clientEngine, int scale, TimeProvider time)
    {
        _textEngine = textEngine;
        _clientEngine = clientEngine;
        _scale = scale;
        _time = time;
    }

    /// <summary>拡大後にエンジンの上限に収まる ROI の一辺の上限。</summary>
    public int MaxImageDimension => (int)WinOcr.OcrEngine.MaxImageDimension / _scale;

    public string LanguageTag => _textEngine.RecognizerLanguage.LanguageTag;

    public string? ClientLanguageTag => _clientEngine?.RecognizerLanguage.LanguageTag;

    public int Scale => _scale;

    public static OcrAvailability CheckAvailability(string languageTag)
    {
        List<string> installed = [.. WinOcr.OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag)];
        bool available = TryCreateLanguage(languageTag) is Language language && WinOcr.OcrEngine.IsLanguageSupported(language);
        return new OcrAvailability(languageTag, available, installed, (int)WinOcr.OcrEngine.MaxImageDimension);
    }

    /// <summary>言語機能がなければ null（OCR01）。OS への追加は利用者が行う。</summary>
    public static WindowsOcrRecognizer? TryCreate(string languageTag, TimeProvider time) =>
        TryCreate(languageTag, clientLanguageTag: null, scale: 1, time);

    /// <param name="clientLanguageTag">
    /// ゲームの表示言語。本文の言語と同じか null なら1言語で認識する。言語機能がなければ1言語に戻す。
    /// </param>
    public static WindowsOcrRecognizer? TryCreate(string languageTag, string? clientLanguageTag, int scale, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);
        if (TryCreateEngine(languageTag) is not WinOcr.OcrEngine textEngine)
        {
            return null;
        }

        WinOcr.OcrEngine? clientEngine = null;
        if (!string.IsNullOrWhiteSpace(clientLanguageTag) &&
            !string.Equals(clientLanguageTag, languageTag, StringComparison.OrdinalIgnoreCase))
        {
            clientEngine = TryCreateEngine(clientLanguageTag);
        }

        return new WindowsOcrRecognizer(textEngine, clientEngine, scale, time);
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
        IReadOnlyList<OcrLine> textLines;
        IReadOnlyList<OcrLine> clientLines = [];
        using (SoftwareBitmap bitmap = CreateBitmap(frame, _scale))
        {
            textLines = await RecognizeLinesAsync(_textEngine, bitmap, frame.RoiInFrame, ct).ConfigureAwait(false);
            if (_clientEngine is not null)
            {
                clientLines = await RecognizeLinesAsync(_clientEngine, bitmap, frame.RoiInFrame, ct).ConfigureAwait(false);
            }
        }

        return new OcrObservation
        {
            ObservationId = Guid.NewGuid(),
            FrameSequence = frame.FrameSequence,
            CapturedAt = frame.CapturedAt,
            RoiBounds = frame.RoiInFrame,
            Lines = ScriptAwareRowAssembler.Assemble(textLines, clientLines),
            // Windows OCR は単語ごとの数値信頼度を返さない（仕様書 8.1）
            EngineConfidence = null,
            OcrDuration = _time.GetElapsedTime(started),
        };
    }

    private async Task<IReadOnlyList<OcrLine>> RecognizeLinesAsync(WinOcr.OcrEngine engine, SoftwareBitmap bitmap, PixelRect roi, CancellationToken ct)
    {
        WinOcr.OcrResult result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
        var lines = new List<OcrLine>(result.Lines.Count);
        foreach (WinOcr.OcrLine line in result.Lines)
        {
            var words = new List<OcrWord>(line.Words.Count);
            foreach (WinOcr.OcrWord word in line.Words)
            {
                words.Add(new OcrWord(word.Text, ToFrameRect(word.BoundingRect, roi)));
            }

            if (words.Count > 0)
            {
                lines.Add(new OcrLine(line.Text, Union(words), words));
            }
        }

        return lines;
    }

    /// <summary>拡大した画像上の位置を、取得画像全体のピクセル座標へ戻す。</summary>
    private PixelRect ToFrameRect(global::Windows.Foundation.Rect rect, PixelRect roi)
    {
        int left = (int)Math.Floor(rect.X / _scale);
        int top = (int)Math.Floor(rect.Y / _scale);
        int right = (int)Math.Ceiling((rect.X + rect.Width) / _scale);
        int bottom = (int)Math.Ceiling((rect.Y + rect.Height) / _scale);
        return new PixelRect(left + roi.X, top + roi.Y, right - left, bottom - top);
    }

    private static SoftwareBitmap CreateBitmap(RoiFrame frame, int scale)
    {
        if (!MemoryMarshal.TryGetArray(frame.Pixels, out ArraySegment<byte> segment))
        {
            throw new InvalidOperationException("ROI の画素バッファを参照できません。");
        }

        byte[] pixels = scale == 1 ? segment.Array! : ImageScaling.UpscaleNearest(segment, frame.Size, scale);
        int offset = scale == 1 ? segment.Offset : 0;
        int length = scale == 1 ? segment.Count : pixels.Length;
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, frame.Size.Width * scale, frame.Size.Height * scale, BitmapAlphaMode.Premultiplied);
        try
        {
            bitmap.CopyFromBuffer(pixels.AsBuffer(offset, length));
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static PixelRect Union(IReadOnlyList<OcrWord> words)
    {
        int left = words.Min(w => w.Bounds.X);
        int top = words.Min(w => w.Bounds.Y);
        int right = words.Max(w => w.Bounds.Right);
        int bottom = words.Max(w => w.Bounds.Bottom);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    private static WinOcr.OcrEngine? TryCreateEngine(string languageTag) =>
        TryCreateLanguage(languageTag) is Language language ? WinOcr.OcrEngine.TryCreateFromLanguage(language) : null;

    private static Language? TryCreateLanguage(string languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag) || !Language.IsWellFormed(languageTag))
        {
            return null;
        }

        return new Language(languageTag);
    }
}
