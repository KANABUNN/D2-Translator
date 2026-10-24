namespace D2Translator.Application.Diagnostics;

/// <summary>
/// 本文なしの計測情報の 1 件（仕様書 15.1 の 7、16.3、17.4）。
/// 値は数値、タグはコードが決めた語（指標名、列挙値、型名等）だけにする。
/// 本文、話者名、画像、秘密、ファイルパスを入れない。
/// </summary>
public sealed record DiagnosticsEvent(string Kind, string Name)
{
    public IReadOnlyDictionary<string, double> Values { get; init; } = new Dictionary<string, double>();

    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
}

/// <summary>保存された 1 件。Session はプロセスごとの無作為な識別子で、利用者や端末を表さない。</summary>
public sealed record DiagnosticsEntry(DateTimeOffset Time, string Session, DiagnosticsEvent Event);

public static class DiagnosticsKinds
{
    public const string Latency = "latency";
    public const string Translation = "translation";
    public const string Resources = "resources";
    public const string Lifecycle = "lifecycle";

    /// <summary>未処理例外等。型名と HRESULT とメソッド名の並びだけを残し、メッセージは残さない。</summary>
    public const string Error = "error";

    /// <summary>警告以上のログ。メッセージの書式と、文字列以外の引数だけを残す。</summary>
    public const string Log = "log";
}

/// <summary>仕様書 16.2 の指標 ID。</summary>
public static class LatencyMetrics
{
    /// <summary>取得したフレームの時刻から原文行の表示まで。</summary>
    public const string OriginalShown = "P01";

    /// <summary>取得したフレームの時刻から有効な日本語訳の表示まで。</summary>
    public const string TranslationShown = "P02";

    /// <summary>返信の英訳開始から候補の表示まで。</summary>
    public const string ReplyCandidate = "P03";

    /// <summary>表示方式の切替操作から描画の完了まで。</summary>
    public const string DisplaySwitch = "P04";

    /// <summary>停止操作から新規の取得が止まるまで。</summary>
    public const string StopResponse = "P14";
}

/// <summary>計測情報の書込み先。どのスレッドからも呼ばれ得る。書込みの失敗で処理を止めない。</summary>
public interface IDiagnosticsSink
{
    void Write(DiagnosticsEvent entry);
}

/// <summary>診断を無効にした時（diagnostics.level = off）。</summary>
public sealed class NullDiagnosticsSink : IDiagnosticsSink
{
    public static NullDiagnosticsSink Instance { get; } = new();

    public void Write(DiagnosticsEvent entry)
    {
    }
}

public static class DiagnosticsSinkExtensions
{
    public static void Latency(this IDiagnosticsSink sink, string metric, TimeSpan elapsed, params (string Key, string Value)[] tags)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.Write(new DiagnosticsEvent(DiagnosticsKinds.Latency, metric)
        {
            Values = new Dictionary<string, double> { ["ms"] = Math.Round(elapsed.TotalMilliseconds, 1) },
            Tags = tags.ToDictionary(t => t.Key, t => t.Value),
        });
    }

    public static void Lifecycle(this IDiagnosticsSink sink, string name, params (string Key, string Value)[] tags)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.Write(new DiagnosticsEvent(DiagnosticsKinds.Lifecycle, name) { Tags = tags.ToDictionary(t => t.Key, t => t.Value) });
    }
}
