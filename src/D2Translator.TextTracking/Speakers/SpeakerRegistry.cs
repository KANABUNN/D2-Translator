using System.Text.RegularExpressions;
using D2Translator.Domain.Chat;
using D2Translator.TextTracking.Text;

namespace D2Translator.TextTracking.Speakers;

/// <summary>
/// セッション中に画面に出た話者名と、端末内だけで使う一時 ID（P1、P2…）の対応表（仕様書 9.2）。
/// 本文中の呼びかけも、ここに登録された名前から見つける。名前そのものは外部へ送らない。
/// </summary>
public sealed class SpeakerRegistry
{
    /// <summary>OCR の揺れ（# の後の数字の誤読等）を同じ人として扱う類似度。</summary>
    private const double SameSpeakerSimilarity = 0.85;

    private static readonly HashSet<string> LeadingStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "my", "i", "mr", "ms",
    };

    private readonly List<Speaker> _speakers = [];
    private readonly Lock _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _speakers.Count;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _speakers.Clear();
        }
    }

    /// <summary>話者名を登録し、一時 ID を返す。似た名前が既にあれば同じ ID。</summary>
    public string Register(string speakerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerName);
        string key = TextUtil.MatchKey(speakerName);
        lock (_gate)
        {
            Speaker? existing = _speakers.FirstOrDefault(s => s.MatchKey == key) ??
                _speakers.FirstOrDefault(s => TextUtil.Similarity(s.MatchKey, key) >= SameSpeakerSimilarity);
            if (existing is not null)
            {
                return existing.Id;
            }

            var speaker = new Speaker($"P{_speakers.Count + 1}", speakerName.Trim(), key, BuildMentionPattern(speakerName));
            _speakers.Add(speaker);
            return speaker.Id;
        }
    }

    /// <summary>本文中の、登録済みの話者への呼びかけを探す。重なる場合は長い方を残す。</summary>
    public IReadOnlyList<TextAnnotation> FindMentions(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<Speaker> speakers;
        lock (_gate)
        {
            speakers = [.. _speakers];
        }

        var found = new List<TextAnnotation>();
        foreach (Speaker speaker in speakers)
        {
            if (speaker.MentionPattern is null)
            {
                continue;
            }

            try
            {
                foreach (Match match in speaker.MentionPattern.Matches(text))
                {
                    found.Add(new TextAnnotation(AnnotationKind.SpeakerMention, match.Index, match.Length, match.Value,
                        $"話者名 {DisplayName(speaker.Name)}", speaker.Id));
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        return AnnotationSet.KeepLongestNonOverlapping(found);
    }

    /// <summary>名前#識別番号 の形か。識別番号は OCR で英字に誤読され得るため英数字 3 文字以上を許す。</summary>
    public static bool IsReliableName(string speakerName) =>
        Regex.IsMatch(speakerName, @"^[^\s#\[\]]{2,}#[0-9A-Za-z]{3,6}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary># の後の識別番号を除いた表示名。</summary>
    public static string DisplayName(string speakerName)
    {
        int hash = speakerName.IndexOf('#', StringComparison.Ordinal);
        return (hash > 0 ? speakerName[..hash] : speakerName).Trim();
    }

    /// <summary>
    /// 呼びかけの候補: 表示名全体、末尾の数字を除いた名前、大文字区切りの2語以上の連なり
    /// （例: TheGreyWarden → grey warden）。1語だけの一般語に当たらないよう、単独の部分語は使わない。
    /// </summary>
    private static Regex? BuildMentionPattern(string speakerName)
    {
        // Bungie 名の形（名前#識別番号）で読めた名前だけを呼びかけの検出に使う。
        // 崩れた行頭やチャット以外の表示から拾った文字列で、一般語を名前と誤認しないため
        if (!IsReliableName(speakerName))
        {
            return null;
        }

        string display = DisplayName(speakerName);
        if (display.Length < 3)
        {
            return null;
        }

        var alternatives = new HashSet<string>(StringComparer.Ordinal);
        string[] parts = SplitWords(display);
        alternatives.Add(string.Join(@"\s*", parts.Select(Regex.Escape)));

        string withoutDigits = display.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '_');
        if (withoutDigits.Length >= 4 && withoutDigits.Length < display.Length)
        {
            alternatives.Add(string.Join(@"\s*", SplitWords(withoutDigits).Select(Regex.Escape)));
        }

        for (int length = 2; length < parts.Length; length++)
        {
            for (int start = 0; start + length <= parts.Length; start++)
            {
                string[] run = parts[start..(start + length)];
                if (LeadingStopWords.Contains(run[0]) || run.Sum(p => p.Length) < 6 || run.All(p => p.All(char.IsDigit)))
                {
                    continue;
                }

                alternatives.Add(string.Join(@"\s+", run.Select(Regex.Escape)));
            }
        }

        string pattern = $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join('|', alternatives.OrderByDescending(a => a.Length))})(?![\p{{L}}\p{{N}}])";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }

    /// <summary>大文字の始まり、英字と数字の境目で区切る。</summary>
    private static string[] SplitWords(string name) =>
        Regex.Matches(name, @"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+|[^\W\d_]+", RegexOptions.CultureInvariant)
            .Select(m => m.Value)
            .ToArray();

    private sealed record Speaker(string Id, string Name, string MatchKey, Regex? MentionPattern);
}
