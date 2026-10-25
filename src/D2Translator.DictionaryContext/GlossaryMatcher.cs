using System.Text.RegularExpressions;
using D2Translator.Domain.Chat;
using D2Translator.Domain.Glossary;
using D2Translator.TextTracking;

namespace D2Translator.DictionaryContext;

/// <summary>
/// 本文中の D2 用語を探して注釈にする（仕様書 9.1）。
/// 単語境界と最長一致で照合し、別の単語の一部には一致させない。多義語は全ての語義を併記し、単一の訳に置き換えない。
/// </summary>
/// <remarks>
/// 照合語は単語の途中からは一致しないため、照合語の先頭の英数字の並びで索引を作り、本文の各単語の先頭と合う照合語だけを照合する。
/// 照合語ごとの正規表現は初めて使う時に作る。既定辞書（約 1 万の照合語）でも、照合の時間とメモリを照合語の数に比例させない（決定 D33）。
/// </remarks>
public sealed class GlossaryMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private readonly List<Surface> _surfaces;
    private readonly bool _prefilter;
    private readonly Dictionary<string, List<int>> _byLead = new(StringComparer.Ordinal);
    private readonly List<int> _unindexed = [];
    private readonly int _maxLeadLength;

    public GlossaryMatcher(IEnumerable<GlossaryEntry> entries, string dictionaryVersion)
        : this(entries, dictionaryVersion, prefilter: true)
    {
    }

    /// <param name="prefilter">false なら全ての照合語を照合する（索引と結果が一致することの試験用）。</param>
    internal GlossaryMatcher(IEnumerable<GlossaryEntry> entries, string dictionaryVersion, bool prefilter)
    {
        ArgumentNullException.ThrowIfNull(entries);
        DictionaryVersion = dictionaryVersion;
        _prefilter = prefilter;
        _surfaces = [.. entries
            .Where(e => e.Direction is GlossaryDirection.EnJa or GlossaryDirection.Both)
            .SelectMany(e => e.Aliases.Prepend(e.Source).Select(surface => (Surface: surface.Trim(), Entry: e)))
            .GroupBy(x => x.Surface, StringComparer.OrdinalIgnoreCase)
            .Select((g, index) => new Surface(index, g.Key, new Lazy<Regex>(() => BuildPattern(g.Key)), [.. g.Select(x => x.Entry).Distinct()]))];

        foreach (Surface surface in _surfaces)
        {
            string lead = LeadingWord(surface.Text);
            if (lead.Length == 0)
            {
                // 記号や {n} で始まる照合語は常に照合する
                _unindexed.Add(surface.Index);
                continue;
            }

            if (!_byLead.TryGetValue(lead, out List<int>? list))
            {
                _byLead[lead] = list = [];
            }

            list.Add(surface.Index);
            _maxLeadLength = Math.Max(_maxLeadLength, lead.Length);
        }
    }

    public string DictionaryVersion { get; }

    public static GlossaryMatcher FromDocument(GlossaryDocument document) => new(document.Entries, document.DictionaryVersion);

    /// <param name="activity">利用者が選んだ活動。Unknown なら活動条件付きの語義を使わない。</param>
    public IReadOnlyList<TextAnnotation> Find(string text, string activity = "Unknown")
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<TextAnnotation>();
        foreach (Surface surface in Candidates(text))
        {
            GlossaryEntry[] senses = [.. surface.Entries.Where(e => Applies(e, activity))];
            if (senses.Length == 0)
            {
                continue;
            }

            try
            {
                foreach (Match match in surface.Pattern.Value.Matches(text))
                {
                    string? number = match.Groups["n"].Success ? match.Groups["n"].Value : null;
                    found.Add(new TextAnnotation(AnnotationKind.Glossary, match.Index, match.Length, match.Value,
                        Describe(senses, number), senses[0].TermId));
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        return AnnotationSet.KeepLongestNonOverlapping(found);
    }

    /// <summary>
    /// 本文の各単語（英数字の並び）の先頭と、先頭の英数字の並びが合う照合語。一致は単語の先頭からしか始まらないため、
    /// ここで外した照合語は本文に一致しない。結果の順序は辞書の順序に揃える（同じ範囲の注釈の並びを変えない）。
    /// </summary>
    private IEnumerable<Surface> Candidates(string text)
    {
        if (!_prefilter)
        {
            return _surfaces;
        }

        string lower = text.ToLowerInvariant();
        Dictionary<string, List<int>>.AlternateLookup<ReadOnlySpan<char>> lookup = _byLead.GetAlternateLookup<ReadOnlySpan<char>>();
        var picked = new HashSet<int>(_unindexed);
        int i = 0;
        while (i < lower.Length)
        {
            if (!char.IsLetterOrDigit(lower[i]))
            {
                i++;
                continue;
            }

            int end = i;
            while (end < lower.Length && char.IsLetterOrDigit(lower[end]))
            {
                end++;
            }

            for (int length = 1; length <= Math.Min(end - i, _maxLeadLength); length++)
            {
                if (lookup.TryGetValue(lower.AsSpan(i, length), out List<int>? indexes))
                {
                    picked.UnionWith(indexes);
                }
            }

            i = end;
        }

        return picked.Order().Select(index => _surfaces[index]);
    }

    /// <summary>照合語の先頭の英数字の並び（小文字）。{n} や記号の手前まで。</summary>
    private static string LeadingWord(string surface) =>
        new string([.. surface.TakeWhile(char.IsLetterOrDigit)]).ToLowerInvariant();

    private static bool Applies(GlossaryEntry entry, string activity) =>
        entry.Conditions.Activity == "Any" ||
        string.Equals(entry.Conditions.Activity, activity, StringComparison.OrdinalIgnoreCase);

    private static string Describe(IReadOnlyList<GlossaryEntry> senses, string? number)
    {
        string targets = string.Join(" / ", senses.Select(s => number is null ? s.Target : s.Target.Replace("{n}", number, StringComparison.Ordinal)));
        string? note = senses.Select(s => s.Note).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        bool pending = senses.Any(s => s.Provenance.ReviewStatus == GlossaryReviewStatus.Pending);
        string ambiguity = senses.Count > 1 ? "（語義が複数）" : string.Empty;
        return $"{targets}{ambiguity}{(note is null ? string.Empty : $"。{note}")}{(pending ? "（未確認の辞書）" : string.Empty)}";
    }

    /// <summary>照合語を単語境界付きの正規表現にする。空白は1つ以上の空白、{n} は数字に一致させる。</summary>
    private static Regex BuildPattern(string surface)
    {
        string[] parts = surface.Split(GlossaryLoader.NumberPlaceholder);
        string body = string.Join(@"(?<n>\d{1,2})", parts.Select(p => Regex.Replace(Regex.Escape(p), @"(\\\s|\\ )+", @"\s+")));
        return new Regex($@"(?<![\p{{L}}\p{{N}}]){body}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    }

    private sealed record Surface(int Index, string Text, Lazy<Regex> Pattern, GlossaryEntry[] Entries);
}
