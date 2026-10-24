using D2Translator.Domain.Errors;

namespace D2Translator.Domain.Translation;

public enum TranslationDirection
{
    EnJa,
    JaEn,
}

/// <summary>翻訳要求に添える辞書項目（仕様書 9.3、最大 50 件）。</summary>
public sealed record GlossaryHint(string TermId, string Surface, string Meaning);

/// <summary>文脈として渡す直近の原文（匿名化済み、仕様書 9.2）。</summary>
/// <param name="Speaker">一時 ID（P1 等）。自分の発言は me。</param>
public sealed record ContextLine(string Speaker, string Text);

/// <summary>
/// 外部へ送る翻訳要求（仕様書 13.2）。本文と文脈は匿名化済みで、話者の名前を含めない。
/// 要求時の設定を固定し、モデルは gpt-6-luna だけ。
/// </summary>
public sealed record TranslationRequest
{
    public required Guid RequestId { get; init; }

    public required Guid MessageId { get; init; }

    public required int Revision { get; init; }

    public required TranslationDirection Direction { get; init; }

    /// <summary>匿名化した本文。名前等は ⟦P1⟧ のような記号に置き換えてある。</summary>
    public required string SourceText { get; init; }

    public string? SpeakerKey { get; init; }

    public IReadOnlyList<GlossaryHint> Glossary { get; init; } = [];

    public IReadOnlyList<ContextLine> Context { get; init; } = [];

    public string Activity { get; init; } = "Unknown";

    public required string GlossaryVersion { get; init; }

    /// <summary>要求発行からの全試行の合計期限。</summary>
    public required TimeSpan Deadline { get; init; }

    public int MaxOutputTokens { get; init; } = OpenAIFixedProfile.DefaultMaxOutputTokens;
}

public enum TranslationOutcome
{
    Success,

    /// <summary>TR01 認証失敗、権限不足、API キー未登録。</summary>
    AuthenticationFailed,

    /// <summary>TR02 回数制限、429、予算上限。</summary>
    RateLimited,

    /// <summary>TR03 通信失敗、時間超過、5xx。</summary>
    Transient,

    /// <summary>TR04 スキーマ不一致、拒否、出力打切り。</summary>
    InvalidResponse,

    /// <summary>TR05 gpt-6-luna を使えない、未確認の応答モデル。</summary>
    ModelUnavailable,
}

public sealed record TranslationAmbiguity(string Term, string Note);

/// <summary>API が返した利用量。返らなければ null（料金未確定）。</summary>
public sealed record TranslationUsage(int InputTokens, int CachedInputTokens, int OutputTokens, int ReasoningTokens);

/// <summary>仕様書 13.2 の翻訳結果。成功以外では訳文を持たない。</summary>
public sealed record TranslationResult
{
    public required Guid RequestId { get; init; }

    public required TranslationOutcome Outcome { get; init; }

    public string? TranslatedText { get; init; }

    public IReadOnlyList<string> AppliedTermIds { get; init; } = [];

    public IReadOnlyList<TranslationAmbiguity> Ambiguities { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string RequestedModel { get; init; } = OpenAIFixedProfile.ModelId;

    public string? ResponseModel { get; init; }

    public TranslationUsage? Usage { get; init; }

    public TimeSpan Latency { get; init; }

    /// <summary>HTTP の試行回数。再試行も利用量に数える（仕様書 17.4）。</summary>
    public int Attempts { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    /// <summary>API の利用上限や予算に達した（429 insufficient_quota）。再試行しても回復しない。</summary>
    public bool QuotaExhausted { get; init; }

    /// <summary>利用者向けの短い理由。本文、キー、例外全文を含めない。</summary>
    public string? Detail { get; init; }

    public bool IsSuccess => Outcome == TranslationOutcome.Success;

    public ErrorCode? Code => Outcome switch
    {
        TranslationOutcome.AuthenticationFailed => ErrorCode.TR01,
        TranslationOutcome.RateLimited => ErrorCode.TR02,
        TranslationOutcome.Transient => ErrorCode.TR03,
        TranslationOutcome.InvalidResponse => ErrorCode.TR04,
        TranslationOutcome.ModelUnavailable => ErrorCode.TR05,
        _ => null,
    };
}

/// <summary>
/// GPT-6 Luna の Standard 料金（仕様書 17.4、2026年10月24日確認、100万トークン当たり米ドル）。
/// 推計に使うだけで請求額を保証しない。
/// </summary>
public static class OpenAIPricing
{
    public static readonly DateOnly ReferenceDate = new(2026, 10, 24);

    public const decimal InputPerMillion = 0.10m;

    public const decimal CachedInputPerMillion = 0.01m;

    public const decimal OutputPerMillion = 0.50m;

    public static decimal EstimateUsd(TranslationUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        int uncached = Math.Max(0, usage.InputTokens - usage.CachedInputTokens);
        // 推論トークンは出力トークンに含まれるため再加算しない
        return (uncached * InputPerMillion + usage.CachedInputTokens * CachedInputPerMillion + usage.OutputTokens * OutputPerMillion) / 1_000_000m;
    }
}
