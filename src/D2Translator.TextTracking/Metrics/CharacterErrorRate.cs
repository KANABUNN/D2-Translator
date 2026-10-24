using D2Translator.TextTracking.Text;

namespace D2Translator.TextTracking.Metrics;

/// <summary>
/// 仕様書 16.3 P05 の文字誤り率。置換、削除、挿入の合計を正解文字数で割る。
/// 空白は1つにまとめ、前後の空白を除いてから比較する（NFKC で全角英数を半角へ寄せる）。
/// </summary>
public static class CharacterErrorRate
{
    public static (int Edits, int ReferenceLength) Compare(string reference, string hypothesis)
    {
        string r = TextUtil.Normalize(reference);
        string h = TextUtil.Normalize(hypothesis);
        return (TextUtil.Levenshtein(r, h), r.Length);
    }

    /// <summary>候補の中で最も近いものとの比較結果。候補がなければ全文字の削除とする。</summary>
    public static (int Edits, int ReferenceLength, string? Best) CompareBest(string reference, IEnumerable<string> hypotheses)
    {
        int length = TextUtil.Normalize(reference).Length;
        (int Edits, int ReferenceLength, string? Best) best = (length, length, null);
        foreach (string hypothesis in hypotheses)
        {
            (int edits, _) = Compare(reference, hypothesis);
            if (edits < best.Edits)
            {
                best = (edits, length, hypothesis);
            }
        }

        return best;
    }
}
