using D2Translator.Domain.Errors;
using D2Translator.Domain.Translation;

namespace D2Translator.Application.Translation;

/// <summary>返信の英訳または逆翻訳の結果。名前の記号は元へ戻し済み。</summary>
/// <param name="Problems">記号や数字の欠落等、機械的な検査で見つかった点。</param>
public sealed record ReplyTranslation(
    bool IsSuccess,
    string? Text,
    string? Detail,
    ErrorCode? Error,
    IReadOnlyList<string> Problems,
    IReadOnlyList<TranslationAmbiguity> Ambiguities,
    IReadOnlyList<string> Warnings,
    TranslationUsage? Usage)
{
    public static ReplyTranslation Failure(string detail, ErrorCode? error) => new(false, null, detail, error, [], [], [], null);

    public static ReplyTranslation From(TranslationResult result, AnonymizedText anonymized, Func<string, string, IReadOnlyList<string>> check)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsSuccess)
        {
            string detail = $"{result.Code} {result.Detail}";
            if (result.RetryAfter is TimeSpan retry)
            {
                detail += $"（{Math.Ceiling(retry.TotalSeconds):0} 秒後に再開できます）";
            }

            return new(false, null, detail, result.Code, [], [], [], result.Usage);
        }

        string translated = result.TranslatedText!;
        return new(true, Anonymizer.Restore(translated, anonymized.Originals), null, null,
            check(anonymized.Text, translated), result.Ambiguities, result.Warnings, result.Usage);
    }
}
