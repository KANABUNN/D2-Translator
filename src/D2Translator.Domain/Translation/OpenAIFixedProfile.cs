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
}
