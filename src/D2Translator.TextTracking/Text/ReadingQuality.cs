using System.Text.RegularExpressions;

namespace D2Translator.TextTracking.Text;

/// <summary>
/// OCR の読みの崩れ具合。チャットが薄れていくと、区切りのコロンが •. のような記号列に読まれる等、読みが悪くなる。
/// </summary>
public static partial class ReadingQuality
{
    // 本文の先頭に残る、区切りの誤読らしい点や記号（例: •. 、・ 、:.）
    [GeneratedRegex(@"^[•·・.,:;'`´‘’｀‧∙⋅°*。、]{1,3}(?:\s+|$)", RegexOptions.CultureInvariant, 50)]
    private static partial Regex LeadingSeparatorNoise();

    // 英文のチャットにまず現れない記号
    private const string NoiseSymbols = "•·・‧∙⋅°¦|¬©®¤§¨´`｀‘’¸˜";

    /// <summary>本文の先頭に残った区切りの誤読を取り除く。:) のような顔文字は残す。</summary>
    public static string CleanBody(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return LeadingSeparatorNoise().Replace(body, string.Empty).Trim();
    }

    /// <summary>読みの崩れの量。小さいほどよい読み。</summary>
    public static int NoiseScore(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int score = text.Count(NoiseSymbols.Contains);
        if (LeadingSeparatorNoise().IsMatch(text))
        {
            score += 2;
        }

        return score;
    }
}
