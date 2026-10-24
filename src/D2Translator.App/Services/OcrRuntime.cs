using D2Translator.Application.Ocr;
using D2Translator.Ocr.Windows;

namespace D2Translator.App.Services;

/// <summary>起動時に確認した OCR の利用可否。認識器を作れなければ Recognizer は null（OCR01）。</summary>
public sealed record OcrRuntime(OcrAvailability Availability, IOcrRecognizer? Recognizer)
{
    public static OcrRuntime Create(string languageTag, TimeProvider time) => new(
        WindowsOcrRecognizer.CheckAvailability(languageTag),
        WindowsOcrRecognizer.TryCreate(languageTag, time));
}

/// <summary>OCR を使えない環境で受信パイプラインを組み立てるための代替。開始は画面側で止める。</summary>
internal sealed class UnavailableOcrRecognizer : IOcrRecognizer
{
    public int MaxImageDimension => 0;

    public Task<D2Translator.Domain.Ocr.OcrObservation> RecognizeAsync(
        D2Translator.Application.Capture.RoiFrame frame, CancellationToken ct) =>
        throw new InvalidOperationException("OCR を使えません（OCR01）。");
}
