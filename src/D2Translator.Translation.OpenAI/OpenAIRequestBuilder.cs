using System.Text.Json;
using System.Text.Json.Nodes;
using D2Translator.Domain.Translation;

namespace D2Translator.Translation.OpenAI;

/// <summary>
/// 翻訳要求を Responses API の HTTP ボディへ変換する（仕様書 6.3、14.2）。
/// 指示は instructions、匿名化した本文と辞書と文脈は JSON データとして input に入れる。
/// </summary>
internal static class OpenAIRequestBuilder
{
    public const string SchemaName = "translation_result";

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static byte[] Build(TranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Direction != TranslationDirection.EnJa)
        {
            throw new NotSupportedException("日英の返信翻訳は Phase 4 で実装する。");
        }

        var body = new JsonObject
        {
            ["model"] = OpenAIFixedProfile.ModelId,
            ["instructions"] = OpenAIPrompt.IncomingInstructions,
            ["input"] = BuildInput(request),
            ["reasoning"] = new JsonObject { ["effort"] = OpenAIFixedProfile.ReasoningEffort },
            ["service_tier"] = OpenAIFixedProfile.ServiceTier,
            ["max_output_tokens"] = Math.Clamp(request.MaxOutputTokens, OpenAIFixedProfile.MinMaxOutputTokens, OpenAIFixedProfile.MaxMaxOutputTokens),
            ["store"] = OpenAIFixedProfile.Store,
            ["stream"] = OpenAIFixedProfile.Stream,
            ["background"] = OpenAIFixedProfile.Background,
            ["tools"] = new JsonArray(),
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = SchemaName,
                    ["strict"] = true,
                    ["schema"] = OpenAIPrompt.ResultSchema(),
                },
            },
        };

        return JsonSerializer.SerializeToUtf8Bytes(body, Compact);
    }

    /// <summary>モデルへ渡すデータ。プロンプトの指示としてではなく、翻訳対象のデータとして扱わせる（仕様書 9.3、18.3）。</summary>
    public static string BuildInput(TranslationRequest request)
    {
        var input = new JsonObject
        {
            ["task"] = "translate",
            ["direction"] = "en-ja",
            ["messageId"] = request.MessageId.ToString("D"),
            ["revision"] = request.Revision,
            ["speaker"] = request.SpeakerKey,
            ["activity"] = request.Activity,
            ["text"] = request.SourceText,
            ["glossary"] = new JsonArray([.. request.Glossary.Take(50).Select(g => (JsonNode)new JsonObject
            {
                ["termId"] = g.TermId,
                ["surface"] = g.Surface,
                ["meaning"] = g.Meaning,
            })]),
            ["context"] = new JsonArray([.. request.Context.Select(c => (JsonNode)new JsonObject
            {
                ["speaker"] = c.Speaker,
                ["text"] = c.Text,
            })]),
        };
        return input.ToJsonString(Compact);
    }
}
