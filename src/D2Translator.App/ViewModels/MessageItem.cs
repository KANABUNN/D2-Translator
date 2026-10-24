using D2Translator.Domain.Chat;
using D2Translator.TextTracking.Speakers;

namespace D2Translator.App.ViewModels;

/// <summary>受信履歴の1行。状態は色だけでなく文字でも示す（仕様書 11.2、17.2）。</summary>
public sealed record MessageItem(
    Guid MessageId,
    string Time,
    string Header,
    string Text,
    string Status,
    string Annotations,
    bool IsSystem)
{
    public bool HasAnnotations => Annotations.Length > 0;

    public static MessageItem From(ChatMessage message)
    {
        string header = message.Kind switch
        {
            ChatLineKind.Own => $"自分 → {message.ChannelLabel}",
            ChatLineKind.Player => $"{(message.SpeakerName is { } name ? SpeakerRegistry.DisplayName(name) : "不明")}（{message.SpeakerKey}）",
            _ => "システム",
        };

        string status = message.Status switch
        {
            MessageStatus.Recognizing => "認識中",
            MessageStatus.NeedsReview => "要確認",
            _ => message.Kind == ChatLineKind.System ? string.Empty : "辞書注釈のみ",
        };

        if (message.WasAlreadyVisible)
        {
            status = string.IsNullOrEmpty(status) ? "開始前から表示" : $"{status}・開始前から表示";
        }

        if (message.Revision > 1)
        {
            status += $"・訂正 {message.Revision - 1} 回";
        }

        string annotations = string.Join(Environment.NewLine, message.Annotations.Select(a => a.Kind == AnnotationKind.SpeakerMention
            ? $"「{a.Text}」は{a.Note}への呼びかけ候補"
            : $"「{a.Text}」= {a.Note}"));

        string text = message.Text.Length == 0 ? "（本文を読み取れませんでした）" : message.Text;
        return new MessageItem(message.MessageId, message.ObservedAt.ToLocalTime().ToString("HH:mm:ss"), header, text,
            status.TrimStart('・'), annotations, message.Kind == ChatLineKind.System);
    }
}
