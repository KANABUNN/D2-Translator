using System.Text.RegularExpressions;

namespace D2Translator.Application.Translation;

/// <summary>
/// 訳文の機械的な検査（仕様書 9.3）。保護した名前や数字の欠落、出力言語を確かめる。
/// 問題があれば「要確認」とし、訳の正しさは自動保証しない。
/// </summary>
public static partial class TranslationChecks
{
    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant, 50)]
    private static partial Regex Digits();

    [GeneratedRegex(@"[A-Za-z]", RegexOptions.CultureInvariant, 50)]
    private static partial Regex Latin();

    [GeneratedRegex(@"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]", RegexOptions.CultureInvariant, 50)]
    private static partial Regex Japanese();

    public static IReadOnlyList<string> EnJa(string anonymizedSource, string translated)
    {
        var problems = new List<string>();
        IReadOnlyList<string> missing = Anonymizer.MissingTokens(anonymizedSource, translated);
        if (missing.Count > 0)
        {
            problems.Add($"名前などを置き換えた記号が訳文から欠けています（{string.Join("、", missing)}）。");
        }

        string folded = translated.Normalize(System.Text.NormalizationForm.FormKC);
        string[] lostNumbers = [.. Digits().Matches(anonymizedSource).Select(m => m.Value).Distinct()
            .Where(n => !folded.Contains(n, StringComparison.Ordinal) && !IsInsidePlaceholder(anonymizedSource, n))];
        if (lostNumbers.Length > 0)
        {
            problems.Add($"数字が訳文にありません（{string.Join("、", lostNumbers)}）。");
        }

        if (Latin().Matches(anonymizedSource).Count >= 3 && !Japanese().IsMatch(translated))
        {
            problems.Add("訳文が日本語になっていません。");
        }

        return problems;
    }

    // ⟦P12⟧ の数字は記号の一部なので数字の保持の検査から外す
    private static bool IsInsidePlaceholder(string source, string number) =>
        !Digits().Matches(Regex.Replace(source, @"⟦[A-Z]+\d+⟧", string.Empty)).Any(m => m.Value == number);
}
