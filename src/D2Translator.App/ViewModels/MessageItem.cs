using D2Translator.Domain.Chat;
using D2Translator.TextTracking.Speakers;

namespace D2Translator.App.ViewModels;

/// <summary>受信履歴の1行。会話だけを表示する（決定 D18）。状態は色だけでなく文字でも示す（仕様書 11.2、17.2）。</summary>
public sealed record MessageItem(
    Guid MessageId,
    string Time,
    string Header,
    string Text,
    string Status,
    string Annotations)
{
    public bool HasAnnotations => Annotations.Length > 0;

    public static MessageItem From(ChatMessage message)
    {
        string header = message.Kind == ChatLineKind.Own
            ? $"自分 → {message.ChannelLabel}"
            : $"{(message.SpeakerName is { } name ? SpeakerRegistry.DisplayName(name) : "不明")}（{message.SpeakerKey}）";

        string status = message.Status switch
        {
            MessageStatus.Recognizing => "認識中",
            MessageStatus.NeedsReview => "要確認",
            _ => "辞書注釈のみ",
        };

        if (message.WasAlreadyVisible)
        {
            status += "・開始前から表示";
        }

        if (message.Revision > 1)
        {
            status += $"・訂正 {message.Revision - 1} 回";
        }

        string annotations = string.Join(Environment.NewLine, message.Annotations.Select(a => a.Kind == AnnotationKind.SpeakerMention
            ? $"「{a.Text}」は{a.Note}への呼びかけ候補"
            : $"「{a.Text}」= {a.Note}"));

        string text = message.Text.Length == 0 ? "（本文を読み取れませんでした）" : message.Text;
        return new MessageItem(message.MessageId, message.ObservedAt.ToLocalTime().ToString("HH:mm:ss"), header, text, status, annotations);
    }
}
