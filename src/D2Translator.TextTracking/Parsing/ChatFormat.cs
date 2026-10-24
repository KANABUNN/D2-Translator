using System.Text.RegularExpressions;
using D2Translator.Domain.Chat;

namespace D2Translator.TextTracking.Parsing;

/// <summary>
/// ゲームクライアントの表示言語ごとのチャット表示形式。
/// 実際に確認した表示形式を設定として持ち、日本語や色だけに固定した判定はしない（仕様書 13.2）。
/// </summary>
public sealed class ChatFormat
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private const string JapaneseLetter = @"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]";

    /// <summary>自分の発言（例: [ファイアチーム]宛て: 本文、To [ファイアチーム検索機能]: 本文）。</summary>
    public required IReadOnlyList<Regex> OwnMessagePatterns { get; init; }

    /// <summary>他プレイヤーの発言（例: [Name#1234]: 本文）。</summary>
    public required IReadOnlyList<Regex> PlayerMessagePatterns { get; init; }

    /// <summary>話者名を含むシステム表示（例: Name#1234がチームチャットに参加しました。）。</summary>
    public required IReadOnlyList<Regex> SpeakerNoticePatterns { get; init; }

    /// <summary>チャンネル表示名から内部値への対応。</summary>
    public required IReadOnlyDictionary<string, ChatChannel> ChannelLabels { get; init; }

    /// <summary>直前の行の右端が ROI 幅のこの割合を超えていたら、折り返しとみなす。</summary>
    public double WrapRightEdgeRatio { get; init; } = 0.75;

    /// <summary>最下行と直前の行の間隔が、通常の行間のこの倍率を超えていたら入力欄とみなす。</summary>
    public double InputRowGapRatio { get; init; } = 1.5;

    /// <summary>OCR の誤読（例: ファイアチーム → ワァイアチ-ム）を許す、チャンネル名の類似度。</summary>
    public double ChannelLabelSimilarity { get; init; } = 0.7;

    /// <summary>表示名に最も近い既知のチャンネル名を返す。近いものがなければ Other。</summary>
    public (string Label, ChatChannel Channel) ResolveChannel(string? label)
    {
        if (label is null)
        {
            return (string.Empty, ChatChannel.Unknown);
        }

        if (ChannelLabels.TryGetValue(label, out ChatChannel exact))
        {
            return (label, exact);
        }

        // 長音記号の代わりに読まれやすい記号を寄せてから比べる
        string folded = label.Replace('-', 'ー').Replace('一', 'ー').Replace('—', 'ー');
        KeyValuePair<string, ChatChannel> best = ChannelLabels
            .OrderByDescending(pair => Text.TextUtil.Similarity(pair.Key, folded))
            .First();
        return Text.TextUtil.Similarity(best.Key, folded) >= ChannelLabelSimilarity
            ? (best.Key, best.Value)
            : (label, ChatChannel.Other);
    }

    /// <summary>
    /// 日本語クライアント。2026年10月24日に利用者のスクリーンショットで確認した形式。
    /// 他プレイヤーの発言はチャンネル名を表示せず、話者名の色だけがチャンネルで異なる。
    /// </summary>
    public static ChatFormat JapaneseClient { get; } = new()
    {
        // 括弧は OCR で落ちやすいため省略を許す。チャンネル名は日本語を含むものに限る。
        // 「宛て」の後のコロンは、他プレイヤーの発言と同じく . や ・ 等への誤読も、読み落としも受け付ける
        OwnMessagePatterns =
        [
            Pattern(@"^\[?(?<label>[^\[\]:]{0,30}?" + JapaneseLetter + @"[^\[\]:]{0,30}?)\]?\s*宛て\s*[:;•·・.。]{0,3}\s*(?<body>.*)$"),
            Pattern(@"^To\s*\[?(?<label>[^\[\]:]{0,30}?" + JapaneseLetter + @"[^\[\]:]{0,30}?)\]?\s*:\s*(?<body>.*)$"),
        ],
        PlayerMessagePatterns =
        [
            // チャットが薄れるとコロンが •. や ・ 等に読まれるため、区切りの誤読も受け付ける
            Pattern(@"^\[(?<speaker>[^\[\]]{1,40})\]\s*[:;•·・.。]{1,3}\s*(?<body>.*)$"),
            // 括弧を読み落とした場合。話者名に # がある時だけ受け付ける
            Pattern(@"^\[?(?<speaker>[^\[\]:\s]{1,40}#[^\[\]:\s]{0,10})\]?\s*:\s*(?<body>.*)$"),
        ],
        SpeakerNoticePatterns =
        [
            Pattern(@"^(?<speaker>[^\s\[\]]{1,40}#[^\s\[\]]{0,10}?)\s*が"),
            // # を読み落としても、英字の名前（と空白で離れた識別番号）の直後に「が」が続く行は通知とみなす（例: Moonii 2468が）
            Pattern(@"^(?<speaker>[A-Za-z0-9_.\-]{2,40}(?:\s\d{2,6})?)\s*が"),
        ],
        ChannelLabels = new Dictionary<string, ChatChannel>(StringComparer.Ordinal)
        {
            ["ファイアチーム"] = ChatChannel.Fireteam,
            ["ファイアチーム検索機能"] = ChatChannel.FireteamFinder,
            ["クラン"] = ChatChannel.Clan,
            ["チーム"] = ChatChannel.Team,
        },
    };

    private static Regex Pattern(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, MatchTimeout);
}
