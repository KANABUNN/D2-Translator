using System.Text;

namespace D2Translator.TextTracking.Text;

public static class TextUtil
{
    /// <summary>かな、漢字、和文の句読点。全角英数記号は含めない（NFKC で半角になるため）。</summary>
    public static bool IsCjk(char c) =>
        c is (>= '぀' and <= 'ヿ')   // ひらがな、カタカナ
        or (>= '　' and <= '〿')     // 和文の記号と句読点
        or (>= '㐀' and <= '䶿')     // CJK 統合漢字拡張 A
        or (>= '一' and <= '鿿')     // CJK 統合漢字
        or (>= '豈' and <= '﫿')     // CJK 互換漢字
        or (>= 'ｦ' and <= 'ﾟ');    // 半角カタカナ

    public static bool ContainsCjk(string text) => text.Any(IsCjk);

    /// <summary>かなと漢字。和文の記号（「」。等）は含めない。</summary>
    public static bool IsCjkLetter(char c) => IsCjk(c) && c is not (>= '　' and <= '〿');

    public static int CountCjkLetters(string text) => text.Count(IsCjkLetter);

    /// <summary>
    /// 比較用の正規化。全角英数記号を半角へ寄せ（NFKC）、空白を1つにまとめる。
    /// 原文の保持には使わない（仕様書 13.1）。
    /// </summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string folded = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(folded.Length);
        bool pendingSpace = false;
        foreach (char c in folded)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>照合用。正規化に加えて大文字小文字を無視する。</summary>
    public static string MatchKey(string text) => Normalize(text).ToLowerInvariant();

    public static int Levenshtein(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.IsEmpty)
        {
            return b.Length;
        }

        if (b.IsEmpty)
        {
            return a.Length;
        }

        Span<int> previous = b.Length < 256 ? stackalloc int[b.Length + 1] : new int[b.Length + 1];
        Span<int> current = b.Length < 256 ? stackalloc int[b.Length + 1] : new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            current.CopyTo(previous);
        }

        return previous[b.Length];
    }

    /// <summary>0〜1。1 が完全一致。</summary>
    public static double Similarity(string a, string b)
    {
        int length = Math.Max(a.Length, b.Length);
        return length == 0 ? 1 : 1 - (double)Levenshtein(a, b) / length;
    }
}
