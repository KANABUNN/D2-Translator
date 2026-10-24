using System.Text.RegularExpressions;
using D2Translator.Domain.Chat;
using D2Translator.Domain.Geometry;
using D2Translator.Domain.Ocr;
using D2Translator.TextTracking.Text;

namespace D2Translator.TextTracking.Parsing;

/// <summary>1回の観測で見えた発言の候補。折り返しを結合済み。</summary>
/// <param name="Speaker">他プレイヤーの話者名。参加通知の話者名もここに入る。</param>
/// <param name="ChannelLabel">画面に表示されたチャンネル名。他プレイヤーの発言では表示されない。</param>
/// <param name="IsUncertain">行頭の表示が崩れていて、話者名と本文の境界を推定した。</param>
public sealed record ChatCandidate(
    ChatLineKind Kind,
    string? Speaker,
    string? ChannelLabel,
    ChatChannel Channel,
    string Body,
    PixelRect Bounds,
    int RowCount,
    bool IsUncertain = false);

/// <summary>
/// 組み立てた行を発言の候補へ分ける（仕様書 8.2 の 3）。
/// 行頭の表示で発言の開始を判定し、それ以外の行は直前の発言の折り返しとして結合する。
/// </summary>
public sealed class ChatLineParser(ChatFormat format)
{
    // 行頭の [ と本文の間に置かれる区切りとして読まれやすい文字
    private static readonly char[] PrefixSeparators = [']', ':', ';', '」', '}'];

    // 入力欄のカーソルとして読まれやすい文字
    private const string CursorLike = "|Il1ー-—_";

    private const string SentenceEnds = "。！？!?";

    public ChatFormat Format { get; } = format;

    public IReadOnlyList<ChatCandidate> Parse(IReadOnlyList<OcrLine> rows, PixelRect roi)
    {
        ArgumentNullException.ThrowIfNull(rows);
        List<OcrLine> ordered = [.. rows.Where(r => TextUtil.Normalize(r.Text).Length > 0).OrderBy(r => r.Bounds.Y)];
        int inputRow = FindInputRow(ordered);

        var candidates = new List<Builder>();
        Builder? current = null;
        OcrLine? previousRow = null;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i == inputRow)
            {
                current = null;
                continue;
            }

            OcrLine row = ordered[i];
            string text = TextUtil.Normalize(row.Text);
            Builder? started = TryStart(text, row);
            if (started is not null)
            {
                // 本文のない自分の行は入力欄
                if (started.Kind == ChatLineKind.Own && IsEmptyInput(started.Body))
                {
                    current = null;
                }
                else
                {
                    candidates.Add(started);
                    current = started;
                }
            }
            else if (current is not null && IsContinuation(text, previousRow, roi))
            {
                current.Append(text, row);
            }
            else if (HasJapanese(text))
            {
                // 話者のないシステム表示（例: チャット履歴が消去されました。）
                current = new Builder(ChatLineKind.System, null, null, ChatChannel.Unknown, text, row);
                candidates.Add(current);
            }
            else
            {
                // ROI の上端で途中から見えている発言の残り。発言として扱わない
                current = null;
            }

            previousRow = row;
        }

        return [.. candidates.Select(b => b.Build())];
    }

    private Builder? TryStart(string text, OcrLine row)
    {
        if (Match(Format.OwnMessagePatterns, text) is Match own)
        {
            (string label, ChatChannel channel) = Format.ResolveChannel(own.Groups["label"].Value.Trim());
            return new Builder(ChatLineKind.Own, null, label, channel, ReadingQuality.CleanBody(own.Groups["body"].Value), row);
        }

        if (Match(Format.PlayerMessagePatterns, text) is Match player)
        {
            return new Builder(ChatLineKind.Player, player.Groups["speaker"].Value.Trim(), null, ChatChannel.Unknown,
                ReadingQuality.CleanBody(player.Groups["body"].Value), row);
        }

        if (Match(Format.SpeakerNoticePatterns, text) is Match notice)
        {
            return new Builder(ChatLineKind.System, notice.Groups["speaker"].Value.Trim(), null, ChatChannel.Unknown, text, row);
        }

        // 行頭が [ で始まる英字の行は、話者名の部分が崩れていても他プレイヤーの発言とみなす。
        // 折り返し行が [ で始まることはまれで、続きとして結合すると以降の発言を全て巻き込むため
        if (text.Length > 1 && text[0] == '[' && !HasJapanese(text))
        {
            (string speaker, string body) = SplitDamagedPrefix(text);
            return new Builder(ChatLineKind.Player, speaker, null, ChatChannel.Unknown, body, row, uncertain: true);
        }

        return null;
    }

    /// <summary>区切り記号があればそこで、なければ最初の空白で話者名と本文を分ける。</summary>
    private static (string Speaker, string Body) SplitDamagedPrefix(string text)
    {
        int limit = Math.Min(text.Length, 48);
        int separator = text.IndexOfAny(PrefixSeparators, 1, limit - 1);
        int split = separator > 1 ? separator : text.IndexOf(' ', 1);
        if (split <= 1)
        {
            return (text[1..].Trim(), string.Empty);
        }

        string speaker = text[1..split].Trim().TrimEnd('.', ',', '#', ' ', '•', '·', '・');
        string body = text[(split + 1)..].TrimStart(']', ':', ';', '」', '}', '.', ',', ' ', '•', '·', '・');
        return (speaker, ReadingQuality.CleanBody(body));
    }

    /// <summary>
    /// 英字だけの行は、システム表示が日本語のため直前の発言の続きとみなす。
    /// 日本語の行は、直前の行が文末で終わらず右端近くまで埋まっていた場合だけ続きとみなす。
    /// </summary>
    private bool IsContinuation(string text, OcrLine? previousRow, PixelRect roi)
    {
        if (previousRow is null)
        {
            return false;
        }

        if (!HasJapanese(text))
        {
            return true;
        }

        string previous = TextUtil.Normalize(previousRow.Text);
        if (previous.Length > 0 && SentenceEnds.Contains(previous[^1]))
        {
            return false;
        }

        return roi.Width > 0 && previousRow.Bounds.Right >= roi.X + Format.WrapRightEdgeRatio * roi.Width;
    }

    /// <summary>
    /// 入力欄はチャット履歴の下に間隔を空けて表示される。最下行が自分の発言の形で、
    /// 直前の行との間隔が通常の行間より明らかに広ければ入力欄とみなす。
    /// </summary>
    private int FindInputRow(List<OcrLine> rows)
    {
        if (rows.Count < 3)
        {
            return -1;
        }

        int last = rows.Count - 1;
        if (Match(Format.OwnMessagePatterns, TextUtil.Normalize(rows[last].Text)) is null)
        {
            return -1;
        }

        int[] pitches = [.. Enumerable.Range(1, last).Select(i => rows[i].Bounds.Y - rows[i - 1].Bounds.Y).Order()];
        int median = pitches[pitches.Length / 2];
        int gap = rows[last].Bounds.Y - rows[last - 1].Bounds.Y;
        return median > 0 && gap > Format.InputRowGapRatio * median ? last : -1;
    }

    /// <summary>かな・漢字を含むか。和文の記号だけの行（英字の誤読を含む）は英字の行として扱う。</summary>
    private static bool HasJapanese(string text) => text.Any(TextUtil.IsCjkLetter);

    private static bool IsEmptyInput(string body) => body.Length == 0 || body.All(c => CursorLike.Contains(c) || char.IsWhiteSpace(c));

    private static Match? Match(IReadOnlyList<Regex> patterns, string text)
    {
        foreach (Regex pattern in patterns)
        {
            try
            {
                Match match = pattern.Match(text);
                if (match.Success)
                {
                    return match;
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        return null;
    }

    private sealed class Builder(ChatLineKind kind, string? speaker, string? label, ChatChannel channel, string body, OcrLine row, bool uncertain = false)
    {
        private readonly List<string> _parts = body.Length == 0 ? [] : [body];
        private PixelRect _bounds = row.Bounds;
        private int _rows = 1;

        public ChatLineKind Kind { get; } = kind;

        public string Body => string.Join(' ', _parts);

        public void Append(string text, OcrLine row)
        {
            // 日本語どうしの折り返しは空白なしで続ける
            if (_parts.Count > 0 && TextUtil.ContainsCjk(_parts[^1][^1..]) && TextUtil.ContainsCjk(text[..1]))
            {
                _parts[^1] += text;
            }
            else
            {
                _parts.Add(text);
            }

            int left = Math.Min(_bounds.X, row.Bounds.X);
            int top = Math.Min(_bounds.Y, row.Bounds.Y);
            int right = Math.Max(_bounds.Right, row.Bounds.Right);
            int bottom = Math.Max(_bounds.Bottom, row.Bounds.Bottom);
            _bounds = new PixelRect(left, top, right - left, bottom - top);
            _rows++;
        }

        public ChatCandidate Build() => new(Kind, speaker, label, channel, Body, _bounds, _rows, uncertain);
    }
}
