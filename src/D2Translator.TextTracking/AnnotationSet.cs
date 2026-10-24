using D2Translator.Domain.Chat;

namespace D2Translator.TextTracking;

public static class AnnotationSet
{
    /// <summary>最長一致を優先し、重ならない注釈だけを位置順に残す（仕様書 9.1）。同じ範囲の注釈は併記する。</summary>
    public static IReadOnlyList<TextAnnotation> KeepLongestNonOverlapping(IEnumerable<TextAnnotation> annotations)
    {
        var kept = new List<TextAnnotation>();
        foreach (TextAnnotation candidate in annotations.OrderByDescending(a => a.Length).ThenBy(a => a.Start))
        {
            bool overlaps = kept.Any(k =>
                candidate.Start < k.Start + k.Length && k.Start < candidate.Start + candidate.Length &&
                !(k.Start == candidate.Start && k.Length == candidate.Length));
            if (!overlaps)
            {
                kept.Add(candidate);
            }
        }

        return [.. kept.OrderBy(a => a.Start).ThenBy(a => a.Kind)];
    }
}
