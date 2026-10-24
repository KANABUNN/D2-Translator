using D2Translator.Application.Ocr;
using D2Translator.Domain.Settings;
using D2Translator.Ocr.Windows;

namespace D2Translator.App.Services;

/// <summary>起動時に確認した OCR の利用可否。認識器を作れなければ Recognizer は null（OCR01）。</summary>
/// <param name="ClientLanguageMissing">ゲームの表示言語の OCR がなく、本文の言語だけで認識している。</param>
public sealed record OcrRuntime(OcrAvailability Availability, WindowsOcrRecognizer? Recognizer, bool ClientLanguageMissing)
{
    public static OcrRuntime Create(OcrSettings settings, TimeProvider time)
    {
        WindowsOcrRecognizer? recognizer = WindowsOcrRecognizer.TryCreate(
            settings.Language, settings.ClientLanguage, settings.Preprocessing.Scale, time);
        bool clientMissing = recognizer is not null && settings.ClientLanguage is not null &&
            !string.Equals(settings.ClientLanguage, settings.Language, StringComparison.OrdinalIgnoreCase) &&
            recognizer.ClientLanguageTag is null;
        return new(WindowsOcrRecognizer.CheckAvailability(settings.Language), recognizer, clientMissing);
    }

    /// <summary>拡大後もエンジンの上限に収まる ROI の一辺。OCR を使えなければ 0。</summary>
    public int MaxRoiDimension => Recognizer?.MaxImageDimension ?? 0;
}

/// <summary>OCR を使えない環境で受信パイプラインを組み立てるための代替。開始は画面側で止める。</summary>
internal sealed class UnavailableOcrRecognizer : IOcrRecognizer
{
    public int MaxImageDimension => 0;

    public Task<D2Translator.Domain.Ocr.OcrObservation> RecognizeAsync(
        D2Translator.Application.Capture.RoiFrame frame, CancellationToken ct) =>
        throw new InvalidOperationException("OCR を使えません（OCR01）。");
}
