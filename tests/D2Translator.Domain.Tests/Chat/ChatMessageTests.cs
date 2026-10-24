using D2Translator.Domain.Chat;

namespace D2Translator.Domain.Tests.Chat;

public class ChatMessageTests
{
    private static ChatMessage Message(ChatLineKind kind) => new()
    {
        MessageId = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        Sequence = 1,
        Kind = kind,
        RawText = "text",
        ObservedAt = DateTimeOffset.UtcNow,
        Status = MessageStatus.GlossaryOnly,
    };

    [Theory]
    [InlineData(ChatLineKind.Player, true)]
    [InlineData(ChatLineKind.Own, true)]
    [InlineData(ChatLineKind.System, false)]
    public void Only_player_and_own_lines_are_conversation(ChatLineKind kind, bool expected)
    {
        Assert.Equal(expected, Message(kind).IsConversation);
    }

    [Fact]
    public void Correction_replaces_text_but_keeps_raw()
    {
        ChatMessage corrected = Message(ChatLineKind.Player) with { CorrectedText = "fixed" };

        Assert.Equal(("text", "fixed"), (corrected.RawText, corrected.Text));
    }
}
