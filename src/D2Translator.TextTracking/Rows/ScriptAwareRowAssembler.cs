using D2Translator.Domain.Geometry;
using D2Translator.Domain.Ocr;
using D2Translator.TextTracking.Text;

namespace D2Translator.TextTracking.Rows;

/// <summary>
/// 英語 OCR と日本語 OCR の結果を、画面上の行ごとに1本へ組み立てる。
/// </summary>
/// <remarks>
/// 日本語クライアントでは行頭のチャンネル表示とシステムメッセージが日本語、本文が英語になる。
/// 英語 OCR は日本語を記号列にし、日本語 OCR は英単語の空白を落とすため、
/// 日本語の文字がある範囲は日本語 OCR、それ以外は英語 OCR の結果を使う。
/// また Windows OCR は同じ行でも隙間があると別の行として返すため、縦位置で行をまとめ直す。
/// </remarks>
public static class ScriptAwareRowAssembler
{
    // 日本語の範囲に含める記号。行頭の [ ] : や句点
    private const string AttachablePunctuation = "[]():;!?・ー-—";

    /// <summary>英単語がこの割合以上日本語の範囲に重なっていたら、日本語を誤読したものとして捨てる。</summary>
    private const double DropOverlapRatio = 0.5;

    /// <summary>日本語の範囲とみなすのに必要なかな・漢字の数。英字を和文字に誤読した1文字を除く。</summary>
    private const int MinimumCjkLetters = 2;

    /// <summary>隣り合う語の隙間が行の高さのこの割合を超えたら空白を入れる。</summary>
    private const double SpaceGapRatio = 0.25;

    public static IReadOnlyList<OcrLine> Assemble(IReadOnlyList<OcrLine> latinLines, IReadOnlyList<OcrLine> cjkLines)
    {
        ArgumentNullException.ThrowIfNull(latinLines);
        ArgumentNullException.ThrowIfNull(cjkLines);

        List<Token> spans = BuildCjkSpans(cjkLines, out List<Token> cjkLatinWords);
        var tokens = new List<Token>(spans);

        int lineId = 0;
        foreach (OcrLine line in latinLines)
        {
            lineId++;
            foreach (OcrWord word in line.Words)
            {
                if (TrimAgainstSpans(word, spans) is Token kept)
                {
                    tokens.Add(kept with { LineId = lineId });
                }
            }
        }

        // 英語 OCR が拾えなかった英字は日本語 OCR の読みで補う
        foreach (Token fallback in cjkLatinWords)
        {
            bool covered = tokens.Any(t => !t.IsCjk && SameRow(t.Bounds, fallback.Bounds) &&
                HorizontalOverlap(t.Bounds, fallback.Bounds) > 0.3 * Math.Min(t.Bounds.Width, fallback.Bounds.Width));
            if (!covered)
            {
                tokens.Add(fallback);
            }
        }

        return GroupIntoRows(tokens);
    }

    private sealed record Token(string Text, PixelRect Bounds, bool IsCjk, int LineId);

    private static List<Token> BuildCjkSpans(IReadOnlyList<OcrLine> cjkLines, out List<Token> latinWords)
    {
        var spans = new List<Token>();
        latinWords = [];
        foreach (OcrLine line in cjkLines)
        {
            IReadOnlyList<OcrWord> words = line.Words;
            bool[] inSpan = new bool[words.Count];
            for (int i = 0; i < words.Count; i++)
            {
                inSpan[i] = words[i].Text.Any(TextUtil.IsCjkLetter);
            }

            // 日本語の文字に隣接する記号を範囲へ含める（前後に広げる）
            int lineHeight = words.Count == 0 ? 0 : words.Max(w => w.Bounds.Height);
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < words.Count; i++)
                {
                    if (inSpan[i] || !IsAttachable(words[i].Text))
                    {
                        continue;
                    }

                    bool nearLeft = i > 0 && inSpan[i - 1] && Gap(words[i - 1].Bounds, words[i].Bounds) <= lineHeight;
                    bool nearRight = i + 1 < words.Count && inSpan[i + 1] && Gap(words[i].Bounds, words[i + 1].Bounds) <= lineHeight;
                    if (nearLeft || nearRight)
                    {
                        inSpan[i] = true;
                        changed = true;
                    }
                }
            }

            int start = -1;
            for (int i = 0; i <= words.Count; i++)
            {
                bool continues = i < words.Count && inSpan[i] &&
                    (start < 0 || Gap(words[i - 1].Bounds, words[i].Bounds) <= lineHeight);
                if (continues && start < 0)
                {
                    start = i;
                    continue;
                }

                if (!continues && start >= 0)
                {
                    OcrWord[] run = [.. words.Skip(start).Take(i - start)];
                    string text = FixLongVowelMarks(string.Concat(run.Select(w => w.Text)));
                    if (TextUtil.CountCjkLetters(text) >= MinimumCjkLetters)
                    {
                        spans.Add(new Token(text, Union(run.Select(w => w.Bounds)), IsCjk: true, LineId: 0));
                    }
                    else
                    {
                        // 英字の誤読（例: r → 「）の可能性が高いので日本語の範囲にしない
                        for (int k = start; k < i; k++)
                        {
                            inSpan[k] = false;
                        }
                    }

                    start = i < words.Count && inSpan[i] ? i : -1;
                }
            }

            for (int i = 0; i < words.Count; i++)
            {
                if (!inSpan[i])
                {
                    latinWords.Add(new Token(words[i].Text, words[i].Bounds, IsCjk: false, LineId: -1));
                }
            }
        }

        return spans;
    }

    /// <summary>日本語の範囲に重なる英単語を捨てるか、重なる側の文字を落とす。</summary>
    private static Token? TrimAgainstSpans(OcrWord word, List<Token> spans)
    {
        PixelRect bounds = word.Bounds;
        List<Token> overlapping = [.. spans.Where(s => SameRow(s.Bounds, bounds) && HorizontalOverlap(s.Bounds, bounds) > 0)];
        if (overlapping.Count == 0)
        {
            return new Token(word.Text, bounds, IsCjk: false, LineId: 0);
        }

        int overlap = overlapping.Sum(s => HorizontalOverlap(s.Bounds, bounds));
        if (bounds.Width <= 0 || overlap >= DropOverlapRatio * bounds.Width || word.Text.Length == 0)
        {
            return null;
        }

        // 文字が等幅に並ぶと仮定し、日本語の範囲に入る文字を落とす
        double charWidth = (double)bounds.Width / word.Text.Length;
        var kept = new System.Text.StringBuilder();
        int left = int.MaxValue, right = int.MinValue;
        for (int i = 0; i < word.Text.Length; i++)
        {
            int x0 = bounds.X + (int)Math.Floor(i * charWidth);
            int x1 = bounds.X + (int)Math.Ceiling((i + 1) * charWidth);
            double center = (x0 + x1) / 2.0;
            if (overlapping.Any(s => center >= s.Bounds.X && center < s.Bounds.Right))
            {
                continue;
            }

            kept.Append(word.Text[i]);
            left = Math.Min(left, x0);
            right = Math.Max(right, x1);
        }

        string text = kept.ToString().Trim();
        return text.Length == 0
            ? null
            : new Token(text, new PixelRect(left, bounds.Y, right - left, bounds.Height), IsCjk: false, LineId: 0);
    }

    private static List<OcrLine> GroupIntoRows(List<Token> tokens)
    {
        var rows = new List<List<Token>>();
        foreach (Token token in tokens.OrderBy(t => t.Bounds.Y + t.Bounds.Height / 2.0))
        {
            List<Token>? row = rows.FirstOrDefault(r => SameRow(Union(r.Select(t => t.Bounds)), token.Bounds));
            if (row is null)
            {
                rows.Add([token]);
            }
            else
            {
                row.Add(token);
            }
        }

        var lines = new List<OcrLine>(rows.Count);
        foreach (List<Token> row in rows.OrderBy(r => r.Min(t => t.Bounds.Y)))
        {
            row.Sort((a, b) => a.Bounds.X.CompareTo(b.Bounds.X));
            int height = Median(row.Select(t => t.Bounds.Height));
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < row.Count; i++)
            {
                if (i > 0 && NeedsSpace(row[i - 1], row[i], height))
                {
                    text.Append(' ');
                }

                text.Append(row[i].Text);
            }

            lines.Add(new OcrLine(
                text.ToString(),
                Union(row.Select(t => t.Bounds)),
                [.. row.Select(t => new OcrWord(t.Text, t.Bounds))]));
        }

        return lines;
    }

    private static bool NeedsSpace(Token left, Token right, int rowHeight)
    {
        // 英語 OCR が同じ行として返した語の区切りはそのまま使う
        if (!left.IsCjk && !right.IsCjk && left.LineId > 0 && left.LineId == right.LineId)
        {
            return true;
        }

        return Gap(left.Bounds, right.Bounds) > SpaceGapRatio * rowHeight;
    }

    /// <summary>縦方向に、低い方の高さの半分以上重なっていれば同じ行。</summary>
    private static bool SameRow(PixelRect a, PixelRect b)
    {
        int overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return overlap > 0.5 * Math.Min(a.Height, b.Height);
    }

    private static int HorizontalOverlap(PixelRect a, PixelRect b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));

    private static int Gap(PixelRect left, PixelRect right) => right.X - left.Right;

    /// <summary>日本語 OCR はカタカナの長音「ー」をハイフン類と読むことがある（例: チ-ム）。</summary>
    private static string FixLongVowelMarks(string text)
    {
        char[] chars = text.ToCharArray();
        for (int i = 1; i < chars.Length; i++)
        {
            if (chars[i] is '-' or '－' or '‐' or '–' or '—' && chars[i - 1] is >= 'ァ' and <= 'ヺ')
            {
                chars[i] = 'ー';
            }
        }

        return new string(chars);
    }

    private static bool IsAttachable(string text) =>
        text.Length > 0 && text.All(c => AttachablePunctuation.Contains(c) || TextUtil.IsCjk(c) || char.IsPunctuation(c));

    private static PixelRect Union(IEnumerable<PixelRect> rects)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (PixelRect r in rects)
        {
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.Right);
            bottom = Math.Max(bottom, r.Bottom);
        }

        return left == int.MaxValue ? default : new PixelRect(left, top, right - left, bottom - top);
    }

    private static int Median(IEnumerable<int> values)
    {
        int[] sorted = [.. values.Order()];
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }
}
