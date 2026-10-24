namespace D2Translator.Domain.Translation;

/// <summary>
/// 全ての AI 翻訳で使う固定値（仕様書 6.3、決定 D08 と D09）。
/// 設定ファイルにこれと異なる値があれば DATA01 として扱い、通信を続けない。
/// </summary>
public static class OpenAIFixedProfile
{
    public const string ProviderId = "openai";

    public const string ModelId = "gpt-6-luna";

    public const string ModelDisplayName = "GPT-6 Luna";

    public const string Endpoint = "https://api.openai.com/v1/responses";

    /// <summary>決定 D09: 推論量は none に固定し、low への変更も提供しない。</summary>
    public const string ReasoningEffort = "none";

    public const string ServiceTier = "default";

    public const bool Store = false;

    public const bool Stream = false;

    public const bool Background = false;

    public const int DefaultMaxOutputTokens = 1024;

    public const int MinMaxOutputTokens = 256;

    public const int MaxMaxOutputTokens = 4096;

    /// <summary>API キーの保存名。設定ファイルにはキーの値でなくこの名前だけを持つ。</summary>
    public const string SecretRef = "openai-api-key";

    /// <summary>受信の英日翻訳の指示文の版。変更したら品質と遅延を再評価する。</summary>
    public const string PromptVersion = "en-ja/2026-10-24.1";

    public const string SchemaVersion = "translation-result/1";

    /// <summary>
    /// 応答の model 値として受け入れるもの（仕様書 14.2）。公式に同一モデルと確認できた ID だけを加える。
    /// 公表されていない日付付き ID を推測で加えない（決定 D21）。
    /// </summary>
    public static IReadOnlySet<string> AcceptedResponseModels { get; } = new HashSet<string>(StringComparer.Ordinal) { ModelId };
}
