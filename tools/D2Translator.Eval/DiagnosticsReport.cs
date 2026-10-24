using System.Globalization;
using System.Text;
using D2Translator.Application.Diagnostics;
using D2Translator.Infrastructure.Diagnostics;
using D2Translator.Infrastructure.Storage;

/// <summary>
/// アプリの診断記録から仕様書 16.2 の指標を集計し、判定表（Markdown）を作る（16.3、20.3、A10）。
/// 判定は「合格」「不合格」「未実施」と、件数や時間が足りない場合の「参考」に分ける。
/// 診断記録は本文を含まないが、試験の記録として公開する前に内容を確認する。
/// </summary>
internal static class DiagnosticsReport
{
    /// <summary>P01〜P03 の判定に必要な件数（仕様書 16.3）。</summary>
    private const int RequiredLatencySamples = 500;

    private static readonly TimeSpan RequiredDuration = TimeSpan.FromHours(8);

    public static int Run(string[] args)
    {
        string folder = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? Path.GetFullPath(args[0])
            : Path.Combine(AppDataLocation.Resolve().RootDirectory, DiagnosticsLog.FolderName);
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"診断記録のフォルダーがありません: {folder}");
            return 1;
        }

        IReadOnlyList<SessionSummary> sessions = DiagnosticsSummary.Summarize(DiagnosticsLog.ReadAll(folder));
        if (sessions.Count == 0)
        {
            Console.Error.WriteLine("診断記録がありません。");
            return 1;
        }

        // 既定では最も長く動かしたセッションを報告する（8 時間試験を想定）
        bool all = args.Contains("--all");
        string report = string.Join(Environment.NewLine, (all ? sessions : [sessions.MaxBy(s => s.Duration)!]).Select(Render));
        Console.WriteLine(report);
        int outIndex = Array.IndexOf(args, "--out");
        if (outIndex >= 0 && outIndex + 1 < args.Length)
        {
            File.WriteAllText(args[outIndex + 1], report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        return 0;
    }

    private static string Render(SessionSummary s)
    {
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"## セッション {s.Session}");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"- 期間: {s.Start.ToLocalTime():yyyy-MM-dd HH:mm} 〜 {s.End.ToLocalTime():yyyy-MM-dd HH:mm}（{s.Duration.TotalHours:0.00} 時間）");
        foreach ((string key, string value) in s.StartTags)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"- {key}: {value}");
        }

        md.AppendLine();
        md.AppendLine("| 指標 | 区分 | 件数 | p50 | p95 | 最大 | 目標 | 判定 |");
        md.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach ((string metric, string goal, double limit, bool useMax) in new[]
        {
            (LatencyMetrics.OriginalShown, "p95 800 ms 以内", 800.0, false),
            (LatencyMetrics.TranslationShown, "未命中の p95 3 秒以内", 3000.0, false),
            (LatencyMetrics.ReplyCandidate, "p95 3 秒以内", 3000.0, false),
            (LatencyMetrics.DisplaySwitch, "p95 1 秒以内", 1000.0, false),
            (LatencyMetrics.StopResponse, "1 秒以内", 1000.0, true),
        })
        {
            List<LatencyStats> rows = [.. s.Latencies.Where(l => l.Metric == metric)];
            if (rows.Count == 0)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"| {metric} | - | 0 | - | - | - | {goal} | 未実施 |");
                continue;
            }

            foreach (LatencyStats l in rows)
            {
                // P02 はキャッシュ未命中の結果で判定する（仕様書 16.3）
                bool judged = metric != LatencyMetrics.TranslationShown || l.Variant.Contains("cache=miss", StringComparison.Ordinal);
                bool needsSamples = metric is LatencyMetrics.OriginalShown or LatencyMetrics.TranslationShown or LatencyMetrics.ReplyCandidate;
                double value = useMax ? l.Max : l.P95;
                string verdict = !judged ? "対象外（キャッシュ命中）"
                    : needsSamples && l.Count < RequiredLatencySamples ? $"参考（{RequiredLatencySamples} 件未満）"
                    : value <= limit ? "合格" : "不合格";
                md.AppendLine(CultureInfo.InvariantCulture,
                    $"| {metric} | {(l.Variant.Length == 0 ? "-" : l.Variant)} | {l.Count} | {l.P50:0} | {l.P95:0} | {l.Max:0} | {goal} | {verdict} |");
            }
        }

        ResourceStats r = s.Resources;
        bool longEnough = s.Duration >= RequiredDuration;
        md.AppendLine();
        md.AppendLine("| 指標 | 値 | 目標 | 判定 |");
        md.AppendLine("| --- | --- | --- | --- |");
        md.AppendLine(Row("P04 UI の応答遅れ（1 分ごとの p95 の最大）", r.MaxUiLagP95Ms, "ms", "100 ms 以内", 100, true));
        md.AppendLine(Row("P10 CPU（10 分平均の最大、マシン全体比）", r.MaxCpu10MinAverage, "%", "5% 以下", 5, true));
        md.AppendLine(Row("P11 Private Bytes（最大）", r.MaxPrivateMb, "MB", "500 MB 以下", 500, true));
        md.AppendLine(Row("P11 アプリ由来の GPU メモリ（最大）", r.MaxGpuMb, "MB", "256 MB 以下", 256, true));
        md.AppendLine(Row("P13 ウォームアップ後のメモリ増加", r.MemoryGrowthAfterWarmupMb, "MB", "100 MB 以下（8 時間）", 100, longEnough));
        md.AppendLine(CultureInfo.InvariantCulture,
            $"| P13 未処理例外 | {r.UnhandledExceptions} 件 | 0 件（8 時間） | {(r.UnhandledExceptions > 0 ? "不合格" : longEnough ? "合格" : "参考（8 時間未満）")} |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| 稼働時間 | {s.Duration.TotalHours:0.00} 時間 | 8 時間 | {(longEnough ? "達成" : "未達")} |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| CPU 平均 | {Format(r.AverageCpu, "%")} | - | - |");

        TranslationStats t = s.Translation;
        md.AppendLine();
        md.AppendLine("| 翻訳 | 値 |");
        md.AppendLine("| --- | --- |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| 外部要求（キャッシュ命中を除く） | {t.Requests} 回（成功 {t.Succeeded}、成功率 {Format(t.SuccessRate * 100, "%")}） |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| キャッシュ命中 | {t.CacheHits} 回 |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| 結果の内訳 | {Join(t.Outcomes)} |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| 要求モデル | {Join(t.RequestedModels)} |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| 応答モデル | {Join(t.ResponseModels)} |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| 推計額 | {t.EstimatedUsd:0.000000} 米ドル（料金未確定 {t.UnknownUsage} 回） |");
        return md.ToString();
    }

    private static string Row(string name, double? value, string unit, string goal, double limit, bool judge) =>
        $"| {name} | {Format(value, unit)} | {goal} | {(value is null ? "未実施" : !judge ? "参考（8 時間未満）" : value <= limit ? "合格" : "不合格")} |";

    private static string Format(double? value, string unit) =>
        value is double v ? string.Create(CultureInfo.InvariantCulture, $"{v:0.##} {unit}") : "-";

    private static string Join(IReadOnlyDictionary<string, int> counts) =>
        counts.Count == 0 ? "-" : string.Join("、", counts.OrderByDescending(c => c.Value).Select(c => $"{c.Key} {c.Value}"));
}
