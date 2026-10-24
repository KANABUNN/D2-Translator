using System.Net;
using System.Text.Json;
using D2Translator.Domain.Translation;

namespace D2Translator.Translation.OpenAI;

/// <summary>1回の HTTP 試行の結果。Retryable なら期限内で1回だけ再試行してよい（仕様書 15.4）。</summary>
internal sealed record AttemptOutcome(
    TranslationOutcome Outcome,
    string? Detail,
    bool Retryable = false,
    TimeSpan? RetryAfter = null,
    ParsedTranslation? Translation = null,
    string? ResponseModel = null,
    TranslationUsage? Usage = null,
    bool QuotaExhausted = false);

internal sealed record ParsedTranslation(
    string TranslatedText,
    IReadOnlyList<string> AppliedTermIds,
    IReadOnlyList<TranslationAmbiguity> Ambiguities,
    IReadOnlyList<string> Warnings);

/// <summary>
/// HTTP 応答の分類と、構造化出力の検証（仕様書 14.2、14.3）。
/// output[0] が訳文とは仮定せず、拒否や打切りを成功として扱わない。モデルが返す ID を信用しない。
/// </summary>
internal static class OpenAIResponseParser
{
    public const int MaxTranslatedScalars = 2000;
    public const int MaxListItems = 20;
    public const int MaxItemLength = 500;

    private static readonly string[] ResultKeys = ["messageId", "revision", "translatedText", "appliedTermIds", "ambiguities", "warnings"];

    public static AttemptOutcome Classify(HttpStatusCode status, TimeSpan? retryAfter, byte[] payload, TranslationRequest request)
    {
        if (status == HttpStatusCode.OK)
        {
            return ParseSuccess(payload, request);
        }

        string? code = ErrorCode(payload);
        return (int)status switch
        {
            401 or 403 => new(TranslationOutcome.AuthenticationFailed, $"認証に失敗しました（HTTP {(int)status}{Suffix(code)}）。"),
            404 => new(TranslationOutcome.ModelUnavailable, $"gpt-6-luna を利用できません（HTTP 404{Suffix(code)}）。"),
            400 when code is "model_not_found" || ErrorParam(payload) == "model" =>
                new(TranslationOutcome.ModelUnavailable, $"gpt-6-luna を利用できません{Suffix(code)}。"),
            400 => new(TranslationOutcome.InvalidResponse, $"要求が受け付けられませんでした（HTTP 400{Suffix(code)}）。"),
            429 when code == "insufficient_quota" => new(TranslationOutcome.RateLimited, "API の利用上限または予算に達しました。", QuotaExhausted: true),
            429 => new(TranslationOutcome.RateLimited, "API の回数制限に達しました。", Retryable: true, RetryAfter: retryAfter),
            408 or 409 or >= 500 => new(TranslationOutcome.Transient, $"一時的なエラーです（HTTP {(int)status}）。", Retryable: true, RetryAfter: retryAfter),
            _ => new(TranslationOutcome.InvalidResponse, $"想定外の応答です（HTTP {(int)status}）。"),
        };
    }

    private static AttemptOutcome ParseSuccess(byte[] payload, TranslationRequest request)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return Invalid("応答の JSON が不正です。");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("応答の形式が不正です。");
            }

            string? model = String(root, "model");
            TranslationUsage? usage = ParseUsage(root);
            string? status = String(root, "status");
            switch (status)
            {
                case "completed":
                    break;
                case "incomplete":
                    string reason = root.TryGetProperty("incomplete_details", out JsonElement details) && details.ValueKind == JsonValueKind.Object
                        ? String(details, "reason") ?? "不明" : "不明";
                    return Invalid($"出力が打ち切られました（{reason}）。") with { ResponseModel = model, Usage = usage };
                case "failed":
                    return new AttemptOutcome(TranslationOutcome.Transient, "応答の生成に失敗しました。", Retryable: true, ResponseModel: model, Usage: usage);
                default:
                    return Invalid($"未対応の応答状態です（{status ?? "なし"}）。") with { ResponseModel = model, Usage = usage };
            }

            // 別モデルの出力を採用しない。未確認の ID は保留する（仕様書 14.2）
            if (model is null || !OpenAIFixedProfile.AcceptedResponseModels.Contains(model))
            {
                return new AttemptOutcome(TranslationOutcome.ModelUnavailable, $"未確認の応答モデルです（{model ?? "なし"}）。", ResponseModel: model, Usage: usage);
            }

            string? text = null;
            if (root.TryGetProperty("output", out JsonElement output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in output.EnumerateArray())
                {
                    if (String(item, "type") != "message" || !item.TryGetProperty("content", out JsonElement content) ||
                        content.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (JsonElement part in content.EnumerateArray())
                    {
                        string? type = String(part, "type");
                        if (type == "refusal")
                        {
                            return Invalid("モデルが応答を拒否しました。") with { ResponseModel = model, Usage = usage };
                        }

                        if (type == "output_text" && text is null)
                        {
                            text = String(part, "text");
                        }
                    }
                }
            }

            if (text is null)
            {
                return Invalid("訳文が含まれていません。") with { ResponseModel = model, Usage = usage };
            }

            (ParsedTranslation? parsed, string? problem) = ParseResult(text, request);
            return parsed is null
                ? Invalid(problem!) with { ResponseModel = model, Usage = usage }
                : new AttemptOutcome(TranslationOutcome.Success, null, Translation: parsed, ResponseModel: model, Usage: usage);
        }
    }

    /// <summary>構造化出力をローカルでも検証する。スキーマの保証に頼らない。</summary>
    internal static (ParsedTranslation? Result, string? Problem) ParseResult(string text, TranslationRequest request)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            return (null, "訳文の JSON が不正です。");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "訳文の形式が不正です。");
            }

            HashSet<string> keys = [.. root.EnumerateObject().Select(p => p.Name)];
            if (!keys.SetEquals(ResultKeys))
            {
                return (null, "訳文の項目がスキーマと一致しません。");
            }

            // 更新先はローカルの要求で決める。モデルが返した ID は照合にだけ使う
            if (!Guid.TryParse(String(root, "messageId"), out Guid messageId) || messageId != request.MessageId ||
                root.GetProperty("revision").ValueKind != JsonValueKind.Number ||
                !root.GetProperty("revision").TryGetInt32(out int revision) || revision != request.Revision)
            {
                return (null, "訳文の対象 ID が要求と一致しません。");
            }

            string? translated = String(root, "translatedText")?.Trim();
            if (string.IsNullOrEmpty(translated))
            {
                return (null, "訳文が空です。");
            }

            if (translated.EnumerateRunes().Count() > MaxTranslatedScalars)
            {
                return (null, $"訳文が上限 {MaxTranslatedScalars} 文字を超えています。");
            }

            List<string>? terms = StringList(root, "appliedTermIds", 50, 100);
            List<string>? warnings = StringList(root, "warnings", MaxListItems, MaxItemLength);
            List<TranslationAmbiguity>? ambiguities = AmbiguityList(root);
            if (terms is null || warnings is null || ambiguities is null)
            {
                return (null, "訳文の補足情報の形式が不正です。");
            }

            return (new ParsedTranslation(translated, terms, ambiguities, warnings), null);
        }
    }

    private static List<string>? StringList(JsonElement root, string name, int maxItems, int maxLength)
    {
        JsonElement array = root.GetProperty(name);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maxItems)
        {
            return null;
        }

        var list = new List<string>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString()!.Length > maxLength)
            {
                return null;
            }

            list.Add(item.GetString()!);
        }

        return list;
    }

    private static List<TranslationAmbiguity>? AmbiguityList(JsonElement root)
    {
        JsonElement array = root.GetProperty("ambiguities");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxListItems)
        {
            return null;
        }

        var list = new List<TranslationAmbiguity>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(["note", "term"]) ||
                String(item, "term") is not { Length: <= MaxItemLength } term ||
                String(item, "note") is not { Length: <= MaxItemLength } note)
            {
                return null;
            }

            list.Add(new TranslationAmbiguity(term, note));
        }

        return list;
    }

    private static TranslationUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int input = Int(usage, "input_tokens");
        int output = Int(usage, "output_tokens");
        int cached = usage.TryGetProperty("input_tokens_details", out JsonElement inDetails) && inDetails.ValueKind == JsonValueKind.Object
            ? Int(inDetails, "cached_tokens") : 0;
        int reasoning = usage.TryGetProperty("output_tokens_details", out JsonElement outDetails) && outDetails.ValueKind == JsonValueKind.Object
            ? Int(outDetails, "reasoning_tokens") : 0;
        return new TranslationUsage(input, cached, output, reasoning);
    }

    private static string? ErrorCode(byte[] payload) => ErrorField(payload, "code");

    private static string? ErrorParam(byte[] payload) => ErrorField(payload, "param");

    /// <summary>エラー応答から機械可読な項目だけを取り出す。message は利用者向け表示やログに流さない。</summary>
    private static string? ErrorField(byte[] payload, string field)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object
                ? String(error, field)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Suffix(string? code) => code is null ? string.Empty : $"、{code}";

    private static AttemptOutcome Invalid(string detail) => new(TranslationOutcome.InvalidResponse, detail);

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
            ? result
            : 0;
}
