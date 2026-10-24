using D2Translator.Domain.Geometry;
using D2Translator.Domain.Ocr;

namespace D2Translator.Domain.Chat;

/// <summary>
/// チャンネルの内部値（仕様書 13.2）。ゲーム表示から確実に判別できたものだけを割り当てる。
/// FireteamFinder は日本語クライアントの「ファイアチーム検索機能」チャンネル。
/// </summary>
public enum ChatChannel
{
    Unknown,
    Fireteam,
    FireteamFinder,
    Team,
    Clan,
    Local,
    Whisper,
    Other,
}

public enum ChatLineKind
{
    /// <summary>他プレイヤーの発言。話者名を持つ。</summary>
    Player,

    /// <summary>自分が送った発言。ゲーム側に表示されたもの。</summary>
    Own,

    /// <summary>参加通知等のシステム表示。</summary>
    System,
}

/// <summary>仕様書 11.2 の状態のうち、受信と認識に関わるもの。翻訳の状態は Phase 3 で加える。</summary>
public enum MessageStatus
{
    /// <summary>安定するまで確定を待っている。</summary>
    Recognizing,

    /// <summary>確定した。翻訳は行っていない。</summary>
    GlossaryOnly,

    /// <summary>1秒以内に安定しなかった。外部送信を保留する。</summary>
    NeedsReview,
}

/// <summary>メッセージ本文の中で、辞書や話者名に一致した範囲。</summary>
public enum AnnotationKind
{
    Glossary,
    SpeakerMention,
}

public sealed record TextAnnotation(AnnotationKind Kind, int Start, int Length, string Text, string Note, string? ReferenceId);

/// <summary>
/// 複数の観測を追跡して同一発言として確定したデータ（仕様書 1.2、13.2）。
/// RawText は初回確定後に上書きせず、訂正は CorrectedText に置く。
/// </summary>
public sealed record ChatMessage
{
    public required Guid MessageId { get; init; }

    public required Guid SessionId { get; init; }

    /// <summary>セッション内で単調増加する番号。</summary>
    public required long Sequence { get; init; }

    public int Revision { get; init; } = 1;

    public required ChatLineKind Kind { get; init; }

    public ChatChannel Channel { get; init; } = ChatChannel.Unknown;

    /// <summary>画面に表示されたチャンネル名（例: ファイアチーム）。判別できなければ null。</summary>
    public string? ChannelLabel { get; init; }

    /// <summary>画面上の話者名（例: Name#1234）。端末内でだけ使い、外部へ送らない。</summary>
    public string? SpeakerName { get; init; }

    /// <summary>端末内の一時 ID（例: P1）。外部送信時は名前の代わりにこれを使う。</summary>
    public string? SpeakerKey { get; init; }

    /// <summary>行頭の表示を除いた本文。</summary>
    public required string RawText { get; init; }

    public string? CorrectedText { get; init; }

    public string Text => CorrectedText ?? RawText;

    /// <summary>
    /// 会話（自分と他プレイヤーの発言）か。システム表示と、ROI に入ったチャット以外の画面表示は
    /// System として追跡には使うが、受信履歴と翻訳の対象にしない（決定 D18）。
    /// </summary>
    public bool IsConversation => Kind != ChatLineKind.System;

    /// <summary>画面上の取得時刻。ゲームの発信時刻ではない。</summary>
    public required DateTimeOffset ObservedAt { get; init; }

    public required MessageStatus Status { get; init; }

    public RecognitionQuality Quality { get; init; } = RecognitionQuality.Good;

    /// <summary>追跡開始時に既に表示されていた。新着として自動翻訳しない（仕様書 15.3）。</summary>
    public bool WasAlreadyVisible { get; init; }

    public PixelRect Bounds { get; init; }

    public IReadOnlyList<TextAnnotation> Annotations { get; init; } = [];
}
