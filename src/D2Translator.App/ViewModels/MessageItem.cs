using D2Translator.Application.Translation;
using D2Translator.Domain.Chat;
using D2Translator.TextTracking.Speakers;

namespace D2Translator.App.ViewModels;

/// <summary>
/// 受信履歴の1行。会話だけを表示する（決定 D18）。原文と訳文の両方を表示し、
/// 状態は色だけでなく文字でも示す（仕様書 11.2、17.2）。
/// </summary>
public sealed record MessageItem(
    Guid MessageId,
    string Time,
    string Header,
    string Text,
    string Status,
    string Annotations,
    string Translation,
    string TranslationNote,
    bool CanTranslate)
{
    public bool HasAnnotations => Annotations.Length > 0;

    public bool HasTranslation => Translation.Length > 0;

    public bool HasTranslationNote => TranslationNote.Length > 0;

    /// <summary>スクリーンリーダーが読む 1 行の要約（仕様書 17.2）。既定ではレコードの文字列表現が読まれてしまう。</summary>
    public string AccessibleName =>
        $"{Time} {Header}{(Status.Length > 0 ? $"（{Status}）" : string.Empty)}。原文: {Text}" +
        (HasTranslation ? $"。訳: {Translation}" : string.Empty);

    public static MessageItem From(ChatMessage message, TranslationView? translation)
    {
        string header = message.Kind == ChatLineKind.Own
            ? $"自分 → {message.ChannelLabel}"
            : $"{(message.SpeakerName is { } name ? SpeakerRegistry.DisplayName(name) : "不明")}（{message.SpeakerKey}）";

        var status = new List<string>
        {
            message.Status switch
            {
                MessageStatus.Recognizing => "認識中",
                MessageStatus.NeedsReview => "認識要確認",
                _ => string.Empty,
            },
        };

        if (message.Kind == ChatLineKind.Player)
        {
            status.Add(translation?.Status switch
            {
                TranslationStatus.Queued => "翻訳待ち",
                TranslationStatus.Translating => "翻訳中",
                TranslationStatus.Translated => "翻訳済み",
                TranslationStatus.NeedsReview => "翻訳済み（要確認）",
                TranslationStatus.Failed => "失敗",
                TranslationStatus.Expired => "期限切れ",
                TranslationStatus.Held => "未翻訳",
                _ => message.Status == MessageStatus.Recognizing ? string.Empty : "辞書注釈のみ",
            });
        }

        if (message.WasAlreadyVisible)
        {
            status.Add("開始前から表示");
        }

        if (message.Revision > 1)
        {
            status.Add($"訂正 {message.Revision - 1} 回");
        }

        if (translation?.IsStale == true)
        {
            status.Add("訳は訂正前");
        }

        string annotations = string.Join(Environment.NewLine, message.Annotations.Select(a => a.Kind == AnnotationKind.SpeakerMention
            ? $"「{a.Text}」は{a.Note}への呼びかけ候補"
            : $"「{a.Text}」= {a.Note}"));

        var notes = new List<string>();
        if (translation?.Detail is { Length: > 0 } detail)
        {
            notes.Add(detail);
        }

        notes.AddRange(translation?.Ambiguities.Select(a => $"曖昧: {a.Term}（{a.Note}）") ?? []);
        notes.AddRange(translation?.Warnings.Select(w => $"注意: {w}") ?? []);

        string text = message.Text.Length == 0 ? "（本文を読み取れませんでした）" : message.Text;
        return new MessageItem(
            message.MessageId,
            message.ObservedAt.ToLocalTime().ToString("HH:mm:ss"),
            header,
            text,
            string.Join("・", status.Where(s => s.Length > 0)),
            annotations,
            translation?.Text ?? string.Empty,
            string.Join(Environment.NewLine, notes),
            translation?.CanRequest == true);
    }
}
