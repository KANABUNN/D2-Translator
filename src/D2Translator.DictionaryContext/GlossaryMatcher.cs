using System.Text.RegularExpressions;
using D2Translator.Domain.Chat;
using D2Translator.Domain.Glossary;
using D2Translator.TextTracking;

namespace D2Translator.DictionaryContext;

/// <summary>
/// 本文中の D2 用語を探して注釈にする（仕様書 9.1）。
/// 単語境界と最長一致で照合し、別の単語の一部には一致させない。多義語は全ての語義を併記し、単一の訳に置き換えない。
/// </summary>
public sealed class GlossaryMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private readonly List<Surface> _surfaces;

    public GlossaryMatcher(IEnumerable<GlossaryEntry> entries, string dictionaryVersion)
    {
        ArgumentNullException.ThrowIfNull(entries);
        DictionaryVersion = dictionaryVersion;
        _surfaces = [.. entries
            .Where(e => e.Direction is GlossaryDirection.EnJa or GlossaryDirection.Both)
            .SelectMany(e => e.Aliases.Prepend(e.Source).Select(surface => (Surface: surface.Trim(), Entry: e)))
            .GroupBy(x => x.Surface, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Surface(g.Key, BuildPattern(g.Key), [.. g.Select(x => x.Entry).Distinct()]))];
    }

    public string DictionaryVersion { get; }

    public static GlossaryMatcher FromDocument(GlossaryDocument document) => new(document.Entries, document.DictionaryVersion);

    /// <param name="activity">利用者が選んだ活動。Unknown なら活動条件付きの語義を使わない。</param>
    public IReadOnlyList<TextAnnotation> Find(string text, string activity = "Unknown")
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<TextAnnotation>();
        foreach (Surface surface in _surfaces)
        {
            GlossaryEntry[] senses = [.. surface.Entries.Where(e => Applies(e, activity))];
            if (senses.Length == 0)
            {
                continue;
            }

            try
            {
                foreach (Match match in surface.Pattern.Matches(text))
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

    private sealed record Surface(string Text, Regex Pattern, GlossaryEntry[] Entries);
}
