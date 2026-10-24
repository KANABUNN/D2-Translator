using D2Translator.Application.Capture;
using D2Translator.Domain.Ocr;

namespace D2Translator.Application.Ocr;

/// <summary>仕様書 14.1 の OCR 境界。</summary>
public interface IOcrRecognizer
{
    /// <summary>エンジンが受け付ける画像の一辺の上限（ピクセル）。</summary>
    int MaxImageDimension { get; }

    Task<OcrObservation> RecognizeAsync(RoiFrame frame, CancellationToken ct);
}

/// <param name="RequestedLanguage">設定の言語タグ。</param>
/// <param name="IsAvailable">要求した言語で認識器を作れるか。</param>
/// <param name="InstalledLanguages">OS にインストール済みの認識言語。</param>
public sealed record OcrAvailability(
    string RequestedLanguage,
    bool IsAvailable,
    IReadOnlyList<string> InstalledLanguages,
    int MaxImageDimension);
