using System.Text;
using D2Translator.Application.Translation;
using D2Translator.DictionaryContext;
using D2Translator.Domain.Chat;
using D2Translator.Domain.Translation;
using D2Translator.Infrastructure.Storage;
using D2Translator.Translation.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// 翻訳品質の評価用 TSV を作る（仕様書 16.3 P08、P09、20.1）。
/// 評価用の英文は利用者が用意し、実プレイヤーの私的会話を無断で使わない。名前は事前に架空のものへ置き換えておく。
/// </summary>
internal static class TranslationEval
{
    // 入力 1 例あたりの目安（指示、辞書、本文）と出力。費用の事前表示にだけ使う
    private const int EstimatedInputTokens = 700;
    private const int EstimatedOutputTokens = 120;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: D2Translator.Eval translate <input.txt> <output.tsv> [--ja-en] [--yes]");
            return 2;
        }

        // 既定は受信の英日。--ja-en は返信の日英（アプリの返信画面と同じ指示、文体は一般）
        TranslationDirection direction = args.Contains("--ja-en") ? TranslationDirection.JaEn : TranslationDirection.EnJa;
        Func<string, string, IReadOnlyList<string>> check = direction == TranslationDirection.JaEn ? TranslationChecks.JaEn : TranslationChecks.EnJa;
        string[] lines = [.. File.ReadAllLines(args[0]).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))];
        decimal estimate = lines.Length * OpenAIPricing.EstimateUsd(new TranslationUsage(EstimatedInputTokens, 0, EstimatedOutputTokens, 0));
        Console.WriteLine($"{lines.Length} 例を gpt-6-luna（推論量 none、{(direction == TranslationDirection.JaEn ? "日英" : "英日")}）で翻訳します。推計 {estimate:0.0000} 米ドル（{OpenAIPricing.ReferenceDate:yyyy-MM-dd} 時点の単価）。");
        if (!args.Contains("--yes"))
        {
            Console.WriteLine("送信するには --yes を付けて実行してください。");
            return 0;
        }

        var secrets = new DpapiSecretStore(AppDataLocation.Resolve());
        if (!secrets.Exists(OpenAIFixedProfile.SecretRef))
        {
            Console.Error.WriteLine("API キーが登録されていません。アプリの「翻訳設定…」で登録してください。");
            return 1;
        }

        using HttpClient http = OpenAIResponsesProvider.CreateHttpClient();
        var provider = new OpenAIResponsesProvider(http, secrets, TimeProvider.System, NullLogger<OpenAIResponsesProvider>.Instance);
        GlossaryMatcher glossary = GlossaryMatcher.FromDocument(GlossaryLoader.LoadExample().Document!);
        var usage = new UsageMeter();

        var output = new StringBuilder();
        output.AppendLine("id\tsource\ttranslation\toutcome\tchecks\tambiguities\twarnings\tlatency_ms\tscore_a\tscore_b\tcritical_error\tnote");
        for (int i = 0; i < lines.Length; i++)
        {
            string source = lines[i];
            TranslationRequest request = new()
            {
                RequestId = Guid.NewGuid(),
                MessageId = Guid.NewGuid(),
                Revision = 1,
                Direction = direction,
                SourceText = source,
                Glossary = direction == TranslationDirection.JaEn ? []
                    : [.. glossary.Find(source).Where(a => a.ReferenceId is not null).Select(a => new GlossaryHint(a.ReferenceId!, a.Text, a.Note))],
                GlossaryVersion = glossary.DictionaryVersion,
                Deadline = TimeSpan.FromSeconds(10),
            };

            TranslationResult result = await provider.TranslateAsync(request, CancellationToken.None);
            usage.Record(result);
            string checks = result.IsSuccess ? string.Join(" ", check(source, result.TranslatedText!)) : string.Empty;
            output.AppendLine(string.Join('\t',
                i + 1,
                Clean(source),
                Clean(result.TranslatedText ?? string.Empty),
                result.IsSuccess ? "ok" : $"{result.Code} {result.Detail}",
                Clean(checks),
                Clean(string.Join(" / ", result.Ambiguities.Select(a => $"{a.Term}: {a.Note}"))),
                Clean(string.Join(" / ", result.Warnings)),
                $"{result.Latency.TotalMilliseconds:0}",
                string.Empty, string.Empty, string.Empty, string.Empty));
            Console.WriteLine($"{i + 1}/{lines.Length} {(result.IsSuccess ? "ok" : result.Code.ToString())} {result.Latency.TotalMilliseconds:0} ms");

            if (result.Outcome is TranslationOutcome.AuthenticationFailed or TranslationOutcome.ModelUnavailable || result.QuotaExhausted)
            {
                Console.Error.WriteLine($"中止しました: {result.Code} {result.Detail}");
                break;
            }
        }

        File.WriteAllText(args[1], output.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        UsageSnapshot total = usage.Snapshot;
        Console.WriteLine($"完了: 成功 {total.Succeeded}/{total.Requests}、入力 {total.InputTokens} / 出力 {total.OutputTokens} トークン、推計 {total.EstimatedUsd:0.0000} 米ドル");
        Console.WriteLine("採点: 5 問題なし、4 軽微な表現差、3 修正が必要、2 重要情報の欠落、1 意味反転または利用困難（仕様書 16.3）");
        return 0;
    }

    private static string Clean(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
