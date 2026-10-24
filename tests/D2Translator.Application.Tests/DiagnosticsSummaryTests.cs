using D2Translator.Application.Diagnostics;

namespace D2Translator.Application.Tests;

public class DiagnosticsSummaryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static DiagnosticsEntry At(TimeSpan offset, DiagnosticsEvent e, string session = "s1") => new(T0 + offset, session, e);

    private static DiagnosticsEvent Latency(string metric, double ms, params (string, string)[] tags) =>
        new(DiagnosticsKinds.Latency, metric)
        {
            Values = new Dictionary<string, double> { ["ms"] = ms },
            Tags = tags.ToDictionary(t => t.Item1, t => t.Item2),
        };

    private static DiagnosticsEvent Resources(double cpu, double privateMb) =>
        new(DiagnosticsKinds.Resources, "process")
        {
            Values = new Dictionary<string, double> { ["cpu_pct"] = cpu, ["private_mb"] = privateMb },
        };

    [Fact]
    public void Percentile_uses_the_rounded_up_rank()
    {
        double[] sorted = [.. Enumerable.Range(1, 20).Select(i => (double)i)];

        // 20 件の p95 は 19 番目、p50 は 10 番目（仕様書 16.3）
        Assert.Equal(19, DiagnosticsSummary.Percentile(sorted, 0.95));
        Assert.Equal(10, DiagnosticsSummary.Percentile(sorted, 0.5));
        Assert.Equal(7, DiagnosticsSummary.Percentile([7], 0.95));
    }

    [Fact]
    public void Latencies_are_split_by_metric_and_tags()
    {
        DiagnosticsEntry[] entries =
        [
            At(TimeSpan.Zero, Latency("P02", 900, ("cache", "miss"), ("context", "off"))),
            At(TimeSpan.FromSeconds(1), Latency("P02", 1100, ("context", "off"), ("cache", "miss"))),
            At(TimeSpan.FromSeconds(2), Latency("P02", 40, ("cache", "hit"), ("context", "off"))),
            At(TimeSpan.FromSeconds(3), Latency("P01", 300)),
        ];

        SessionSummary summary = Assert.Single(DiagnosticsSummary.Summarize(entries));

        Assert.Equal(["P01:", "P02:cache=hit,context=off", "P02:cache=miss,context=off"],
            summary.Latencies.Select(l => $"{l.Metric}:{l.Variant}"));
        LatencyStats miss = summary.Latencies[2];
        Assert.Equal((2, 900, 1100, 1100), (miss.Count, miss.P50, miss.P95, miss.Max));
    }

    [Fact]
    public void Cpu_window_memory_growth_and_unhandled_exceptions_are_reported()
    {
        var entries = new List<DiagnosticsEntry> { At(TimeSpan.Zero, new DiagnosticsEvent(DiagnosticsKinds.Lifecycle, "app_start")) };
        for (int minute = 1; minute <= 60; minute++)
        {
            // 最初の 10 分だけ CPU が高い。ウォームアップ後はメモリが 1 分に 0.5MB 増え、7 分ごとに GC 前の一時的な山（+80MB）がある
            double memory = minute < 15 ? 200 : 200 + (minute - 15) * 0.5;
            entries.Add(At(TimeSpan.FromMinutes(minute), Resources(minute <= 10 ? 8 : 2, minute % 7 == 0 ? memory + 80 : memory)));
        }

        entries.Add(At(TimeSpan.FromMinutes(20), ExceptionInfo.Unhandled("dispatcher", new InvalidOperationException("secret body text"))));

        SessionSummary summary = Assert.Single(DiagnosticsSummary.Summarize(entries));

        Assert.Equal(60, summary.Resources.Samples);
        Assert.Equal(8, summary.Resources.MaxCpu10MinAverage);
        Assert.Equal(300.5, summary.Resources.MaxPrivateMb);
        // 15〜29 分の中央値 204、46〜60 分の中央値 219.5。一時的な山は増加に数えない
        Assert.Equal(15.5, summary.Resources.MemoryGrowthAfterWarmupMb);
        Assert.Equal(1, summary.Resources.UnhandledExceptions);
    }

    [Fact]
    public void Short_session_has_no_cpu_window_or_growth()
    {
        DiagnosticsEntry[] entries = [At(TimeSpan.Zero, Resources(3, 100)), At(TimeSpan.FromMinutes(5), Resources(5, 120))];

        ResourceStats resources = Assert.Single(DiagnosticsSummary.Summarize(entries)).Resources;

        Assert.Null(resources.MaxCpu10MinAverage);
        Assert.Null(resources.MemoryGrowthAfterWarmupMb);
        Assert.Equal(4, resources.AverageCpu);
    }

    [Fact]
    public void Translation_requests_exclude_cache_hits()
    {
        static DiagnosticsEvent Request(string outcome, string cache, string usage, double usd) => new(DiagnosticsKinds.Translation, "incoming")
        {
            Values = new Dictionary<string, double> { ["usd"] = usd },
            Tags = new Dictionary<string, string>
            {
                ["outcome"] = outcome, ["cache"] = cache, ["usage"] = usage,
                ["requested_model"] = "gpt-6-luna", ["response_model"] = outcome == "Success" ? "gpt-6-luna" : "none",
            },
        };

        DiagnosticsEntry[] entries =
        [
            At(TimeSpan.Zero, Request("Success", "miss", "reported", 0.0001)),
            At(TimeSpan.FromSeconds(1), Request("Transient", "miss", "unknown", 0)),
            At(TimeSpan.FromSeconds(2), Request("Success", "hit", "cached", 0)),
        ];

        TranslationStats translation = Assert.Single(DiagnosticsSummary.Summarize(entries)).Translation;

        Assert.Equal((2, 1, 1, 1), (translation.Requests, translation.CacheHits, translation.Succeeded, translation.UnknownUsage));
        Assert.Equal(0.5, translation.SuccessRate);
        Assert.Equal(2, translation.RequestedModels["gpt-6-luna"]);
        Assert.Equal(0.0001, translation.EstimatedUsd, 10);
    }

    [Fact]
    public void Sessions_are_summarized_separately()
    {
        DiagnosticsEntry[] entries =
        [
            At(TimeSpan.FromHours(1), Latency("P01", 100), "b"),
            At(TimeSpan.Zero, Latency("P01", 200), "a"),
        ];

        Assert.Equal(["a", "b"], DiagnosticsSummary.Summarize(entries).Select(s => s.Session));
    }

    [Fact]
    public void Exception_record_keeps_type_and_methods_but_not_the_message()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException("Kestrel said sk-test-1234567890abcdef", new IOException("inner body"));
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        DiagnosticsEvent recorded = ExceptionInfo.Unhandled("task", thrown);

        Assert.Equal("System.InvalidOperationException > System.IO.IOException", recorded.Tags["exception.type"]);
        Assert.Contains(nameof(Exception_record_keeps_type_and_methods_but_not_the_message), recorded.Tags["exception.stack"], StringComparison.Ordinal);
        string all = string.Join(" ", recorded.Tags.Values);
        Assert.DoesNotContain("Kestrel", all, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-test", all, StringComparison.Ordinal);
        Assert.DoesNotContain("inner body", all, StringComparison.Ordinal);
        // ファイル名と行番号（利用者名を含み得るパス）を残さない
        Assert.DoesNotContain(".cs:line", all, StringComparison.Ordinal);
    }
}
