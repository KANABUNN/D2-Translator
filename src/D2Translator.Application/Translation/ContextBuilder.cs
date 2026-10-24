using D2Translator.Domain.Chat;
using D2Translator.Domain.Translation;

namespace D2Translator.Application.Translation;

/// <summary>
/// 受信翻訳に添える会話文脈（仕様書 9.2）。同じセッションとチャンネルの確定した原文から、
/// 直近 6 件、120 秒、合計 1500 文字のすべてを満たす範囲を選ぶ。過去の機械訳は入れない。
/// </summary>
public static class ContextBuilder
{
    public const int MaxMessages = 6;
    public const int MaxCharacters = 1500;
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(120);

    /// <summary>自分の発言の話者表示。</summary>
    public const string OwnSpeaker = "me";

    /// <param name="history">セッション内の発言（古い順）。</param>
    public static IReadOnlyList<ContextLine> Build(IEnumerable<ChatMessage> history, ChatMessage target, int maxMessages = MaxMessages)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(target);
        var lines = new List<ContextLine>();
        int characters = 0;
        foreach (ChatMessage message in history
            .Where(m => m.IsConversation && m.SessionId == target.SessionId && m.Sequence < target.Sequence &&
                m.Status != MessageStatus.Recognizing && m.Text.Length > 0 &&
                SameConversation(m.Channel, target.Channel) && target.ObservedAt - m.ObservedAt <= MaxAge)
            .OrderByDescending(m => m.Sequence))
        {
            if (lines.Count >= Math.Min(maxMessages, MaxMessages))
            {
                break;
            }

            string text = Anonymizer.Anonymize(message).Text;
            if (characters + text.Length > MaxCharacters)
            {
                break;
            }

            characters += text.Length;
            lines.Add(new ContextLine(message.Kind == ChatLineKind.Own ? OwnSpeaker : message.SpeakerKey ?? "P?", text));
        }

        lines.Reverse();
        return lines;
    }

    /// <summary>返信の英訳に添える、直近の会話（利用者が文脈の利用を選んだ時だけ、仕様書 15.2）。</summary>
    public static IReadOnlyList<ContextLine> BuildRecent(IEnumerable<ChatMessage> history, Guid sessionId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(history);
        ChatMessage[] recent = [.. history.Where(m => m.SessionId == sessionId && m.IsConversation)];
        if (recent.Length == 0)
        {
            return [];
        }

        // 最新の発言の直後に仮の対象を置き、同じ規則（件数、時間、文字数）で選ぶ
        ChatMessage last = recent.MaxBy(m => m.Sequence)!;
        ChatMessage probe = last with { Sequence = last.Sequence + 1, ObservedAt = now, Channel = ChatChannel.Unknown };
        return Build(recent, probe);
    }

    /// <summary>
    /// 他プレイヤーの発言はチャンネルを判別できない（決定 D15）ため、Unknown はどのチャンネルとも同じ会話とみなす（決定 D19）。
    /// </summary>
    private static bool SameConversation(ChatChannel a, ChatChannel b) =>
        a == b || a == ChatChannel.Unknown || b == ChatChannel.Unknown;
}
