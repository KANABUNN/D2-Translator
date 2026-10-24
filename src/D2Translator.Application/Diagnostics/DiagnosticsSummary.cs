namespace D2Translator.Application.Diagnostics;

/// <param name="Variant">タグによる区分（例: cache=miss,context=off）。区分なしは空文字。</param>
public sealed record LatencyStats(string Metric, string Variant, int Count, double P50, double P95, double Max);

/// <param name="MaxCpu10MinAverage">10 分間の平均 CPU（マシン全体比、%）の最大値。10 分に満たなければ null。</param>
/// <param name="MemoryGrowthAfterWarmupMb">
/// ウォームアップ後の Private Bytes の増加。最後の 15 分の中央値 − ウォームアップ直後の 15 分の中央値。
/// ガベージコレクションによる一時的な山を増加に数えない。45 分に満たなければ null。
/// </param>
public sealed record ResourceStats(
    int Samples,
    double? AverageCpu,
    double? MaxCpu10MinAverage,
    double? MaxPrivateMb,
    double? MaxGpuMb,
    double? MemoryGrowthAfterWarmupMb,
    double? MaxUiLagP95Ms,
    double? MaxUiLagMs,
    int UnhandledExceptions);

/// <param name="Requests">外部へ送った要求（キャッシュ命中を除く）。</param>
public sealed record TranslationStats(
    int Requests,
    int CacheHits,
    int Succeeded,
    int UnknownUsage,
    double EstimatedUsd,
    IReadOnlyDictionary<string, int> Outcomes,
    IReadOnlyDictionary<string, int> RequestedModels,
    IReadOnlyDictionary<string, int> ResponseModels)
{
    public double? SuccessRate => Requests == 0 ? null : (double)Succeeded / Requests;
}

public sealed record SessionSummary(
    string Session,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyDictionary<string, string> StartTags,
    IReadOnlyList<LatencyStats> Latencies,
    ResourceStats Resources,
    TranslationStats Translation)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// 診断記録から仕様書 16.2 の指標（P01〜P04、P10、P11、P13、P14）を集計する。
/// 判定に使う値は 16.3 の規則（p95 は昇順の 95% 位置を切り上げた順位）で求める。
/// </summary>
public static class DiagnosticsSummary
{
    /// <summary>メモリ増加の基準をとるまでの時間（仕様書 16.2 P13 のウォームアップ）。</summary>
    public static readonly TimeSpan Warmup = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan CpuWindow = TimeSpan.FromMinutes(10);

    /// <summary>メモリの水準をとる区間の長さ。この区間の中央値どうしを比べる。</summary>
    public static readonly TimeSpan MemoryWindow = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<SessionSummary> Summarize(IEnumerable<DiagnosticsEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return [.. entries
            .GroupBy(e => e.Session)
            .Select(g => SummarizeSession(g.Key, [.. g.OrderBy(e => e.Time)]))
            .OrderBy(s => s.Start)];
    }

    /// <summary>昇順に並べた値の p の位置を切り上げた順位の値（仕様書 16.3）。</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0)
        {
            throw new ArgumentException("値がありません。", nameof(sorted));
        }

        int rank = Math.Clamp((int)Math.Ceiling(p * sorted.Count), 1, sorted.Count);
        return sorted[rank - 1];
    }

    private static SessionSummary SummarizeSession(string session, List<DiagnosticsEntry> entries)
    {
        DiagnosticsEntry? start = entries.FirstOrDefault(e => e.Event.Kind == DiagnosticsKinds.Lifecycle && e.Event.Name == "app_start");
        return new SessionSummary(
            session,
            entries[0].Time,
            entries[^1].Time,
            start?.Event.Tags ?? new Dictionary<string, string>(),
            Latencies(entries),
            Resources(entries),
            Translation(entries));
    }

    private static List<LatencyStats> Latencies(List<DiagnosticsEntry> entries) =>
        [.. entries
            .Where(e => e.Event.Kind == DiagnosticsKinds.Latency && e.Event.Values.ContainsKey("ms"))
            .GroupBy(e => (e.Event.Name, Variant: string.Join(",", e.Event.Tags.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}={t.Value}"))))
            .Select(g =>
            {
                double[] sorted = [.. g.Select(e => e.Event.Values["ms"]).Order()];
                return new LatencyStats(g.Key.Name, g.Key.Variant, sorted.Length, Percentile(sorted, 0.5), Percentile(sorted, 0.95), sorted[^1]);
            })
            .OrderBy(s => s.Metric, StringComparer.Ordinal)
            .ThenBy(s => s.Variant, StringComparer.Ordinal)];

    private static ResourceStats Resources(List<DiagnosticsEntry> entries)
    {
        List<DiagnosticsEntry> samples = [.. entries.Where(e => e.Event.Kind == DiagnosticsKinds.Resources)];
        int unhandled = entries.Count(e => e.Event.Kind == DiagnosticsKinds.Error);
        if (samples.Count == 0)
        {
            return new ResourceStats(0, null, null, null, null, null, null, null, unhandled);
        }

        DateTimeOffset origin = entries[0].Time;
        List<(DateTimeOffset Time, double Cpu)> cpu = [.. samples
            .Where(s => s.Event.Values.ContainsKey("cpu_pct"))
            .Select(s => (s.Time, s.Event.Values["cpu_pct"]))];

        double? maxWindow = null;
        foreach ((DateTimeOffset time, _) in cpu.Where(c => c.Time - origin >= CpuWindow))
        {
            double average = cpu.Where(c => c.Time > time - CpuWindow && c.Time <= time).Average(c => c.Cpu);
            maxWindow = Math.Max(maxWindow ?? double.MinValue, average);
        }

        List<(DateTimeOffset Time, double Mb)> memory = [.. samples
            .Where(s => s.Event.Values.ContainsKey("private_mb"))
            .Select(s => (s.Time, s.Event.Values["private_mb"]))];
        double? growth = null;
        if (memory.Count > 0 && memory[^1].Time - origin >= Warmup + MemoryWindow * 2)
        {
            DateTimeOffset end = memory[^1].Time;
            double[] early = [.. memory.Where(m => m.Time - origin >= Warmup && m.Time - origin < Warmup + MemoryWindow).Select(m => m.Mb).Order()];
            double[] late = [.. memory.Where(m => m.Time > end - MemoryWindow).Select(m => m.Mb).Order()];
            if (early.Length > 0 && late.Length > 0)
            {
                growth = Percentile(late, 0.5) - Percentile(early, 0.5);
            }
        }

        return new ResourceStats(
            samples.Count,
            cpu.Count == 0 ? null : cpu.Average(c => c.Cpu),
            maxWindow,
            memory.Count == 0 ? null : memory.Max(m => m.Mb),
            MaxOf(samples, s => s.Values.GetValueOrDefault("gpu_local_mb") + s.Values.GetValueOrDefault("gpu_nonlocal_mb"), "gpu_local_mb"),
            growth,
            MaxOf(samples, s => s.Values["ui_lag_p95_ms"], "ui_lag_p95_ms"),
            MaxOf(samples, s => s.Values["ui_lag_max_ms"], "ui_lag_max_ms"),
            unhandled);
    }

    private static double? MaxOf(List<DiagnosticsEntry> samples, Func<DiagnosticsEvent, double> value, string requiredKey)
    {
        List<DiagnosticsEvent> having = [.. samples.Select(s => s.Event).Where(e => e.Values.ContainsKey(requiredKey))];
        return having.Count == 0 ? null : having.Max(value);
    }

    private static TranslationStats Translation(List<DiagnosticsEntry> entries)
    {
        List<DiagnosticsEvent> all = [.. entries.Select(e => e.Event).Where(e => e.Kind == DiagnosticsKinds.Translation)];
        List<DiagnosticsEvent> sent = [.. all.Where(e => e.Tags.GetValueOrDefault("cache") != "hit")];
        return new TranslationStats(
            sent.Count,
            all.Count - sent.Count,
            sent.Count(e => e.Tags.GetValueOrDefault("outcome") == "Success"),
            sent.Count(e => e.Tags.GetValueOrDefault("usage") == "unknown"),
            sent.Sum(e => e.Values.GetValueOrDefault("usd")),
            CountBy(sent, "outcome"),
            CountBy(sent, "requested_model"),
            CountBy(sent, "response_model"));
    }

    private static Dictionary<string, int> CountBy(List<DiagnosticsEvent> events, string tag) =>
        events.GroupBy(e => e.Tags.GetValueOrDefault(tag) ?? "-").ToDictionary(g => g.Key, g => g.Count());
}
