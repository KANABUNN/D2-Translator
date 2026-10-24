using D2Translator.Domain.Chat;
using D2Translator.Domain.Geometry;
using D2Translator.Domain.Ocr;
using D2Translator.TextTracking.Parsing;

namespace D2Translator.TextTracking.Tests;

public class ChatLineParserTests
{
    private static readonly PixelRect Roi = new(0, 0, 500, 300);
    private static readonly ChatLineParser Parser = new(ChatFormat.JapaneseClient);

    /// <param name="width">省略時は文字数から決める。折り返し行は ROI 幅の 8 割以上にする。</param>
    private static OcrLine Row(int index, string text, int? width = null) =>
        new(text, new PixelRect(10, index * 24, width ?? Math.Min(300, text.Length * 7), 20), []);

    [Fact]
    public void Fireteam_conversation_from_japanese_client()
    {
        OcrLine[] rows =
        [
            Row(0, "[Pancake916#1234]: Just type y or n"),
            Row(1, "[ファイアチーム]宛て: y"),
            Row(2, "[Kestrel#5678]: N"),
            Row(3, "[TheGreyWarden#9012]: Can I take left side while", width: 400),
            Row(4, "you 3 handle the relic?"),
            Row(5, "[Kestrel#5678]: Sounds broken"),
            Row(6, "[ファイアチーム]宛て:"),
        ];

        IReadOnlyList<ChatCandidate> candidates = Parser.Parse(rows, Roi);

        Assert.Equal(5, candidates.Count);
        Assert.Equal(new[] { ChatLineKind.Player, ChatLineKind.Own, ChatLineKind.Player, ChatLineKind.Player, ChatLineKind.Player },
            candidates.Select(c => c.Kind));
        Assert.Equal("Pancake916#1234", candidates[0].Speaker);
        Assert.Equal("Just type y or n", candidates[0].Body);
        Assert.Equal(("ファイアチーム", ChatChannel.Fireteam, "y"), (candidates[1].ChannelLabel, candidates[1].Channel, candidates[1].Body));
        Assert.Equal("N", candidates[2].Body);
        Assert.Equal("Can I take left side while you 3 handle the relic?", candidates[3].Body);
        Assert.Equal(2, candidates[3].RowCount);
    }

    [Fact]
    public void Join_notices_and_wrapped_japanese()
    {
        OcrLine[] rows =
        [
            Row(0, "加わりました。"),
            Row(1, "[Pancake916#1234]: Ready up grey warden"),
            Row(2, "nightowlcream#1111がチームチャットに参加しました。"),
            Row(3, "StormRiderTTV#2222がチームチャットに参加しまし", width: 460),
            Row(4, "た。"),
            Row(5, "チャット履歴が消去されました。"),
        ];

        IReadOnlyList<ChatCandidate> candidates = Parser.Parse(rows, Roi);

        Assert.Equal(5, candidates.Count);
        Assert.Equal(ChatLineKind.System, candidates[0].Kind);
        Assert.Equal("nightowlcream#1111", candidates[2].Speaker);
        Assert.Equal(ChatLineKind.System, candidates[2].Kind);
        Assert.Equal("StormRiderTTV#2222", candidates[3].Speaker);
        Assert.Equal("StormRiderTTV#2222がチームチャットに参加しました。", candidates[3].Body);
        Assert.Equal("チャット履歴が消去されました。", candidates[4].Body);
    }

    [Fact]
    public void Fireteam_finder_channel_is_recognized()
    {
        IReadOnlyList<ChatCandidate> candidates = Parser.Parse([Row(0, "To [ファイアチーム検索機能]: C")], Roi);

        ChatCandidate own = Assert.Single(candidates);
        Assert.Equal((ChatLineKind.Own, ChatChannel.FireteamFinder, "C"), (own.Kind, own.Channel, own.Body));
    }

    [Fact]
    public void Latin_fragment_at_the_top_is_not_a_message()
    {
        IReadOnlyList<ChatCandidate> candidates = Parser.Parse(
            [Row(0, "you 3 handle the relic?"), Row(1, "[Kestrel#5678]: Sounds broken")], Roi);

        Assert.Equal("Sounds broken", Assert.Single(candidates).Body);
    }

    [Fact]
    public void Full_width_punctuation_and_missing_close_bracket_are_tolerated()
    {
        IReadOnlyList<ChatCandidate> candidates = Parser.Parse(
            [Row(0, "［クラン］宛て：B"), Row(1, "[Kestrel#5678: hello there")], Roi);

        Assert.Equal((ChatChannel.Clan, "B"), (candidates[0].Channel, candidates[0].Body));
        Assert.Equal(("Kestrel#5678", "hello there"), (candidates[1].Speaker, candidates[1].Body));
    }

    [Theory]
    [InlineData("ワァイアチ-ム]宛て: Need two more", "ファイアチーム", ChatChannel.Fireteam)]
    [InlineData("[ファイアデアム]宛て: Need two more", "ファイアチーム", ChatChannel.Fireteam)]
    [InlineData("To ファイアチーム検索機能]: Need two more", "ファイアチーム検索機能", ChatChannel.FireteamFinder)]
    [InlineData("[レイド]宛て: Need two more", "レイド", ChatChannel.Other)]
    public void Misread_channel_labels_resolve_to_the_closest_known_channel(string text, string label, ChatChannel channel)
    {
        ChatCandidate own = Assert.Single(Parser.Parse([Row(0, text)], Roi));

        Assert.Equal((ChatLineKind.Own, label, channel, "Need two more"), (own.Kind, own.ChannelLabel, own.Channel, own.Body));
    }

    [Fact]
    public void English_line_starting_with_to_is_not_an_own_message()
    {
        IReadOnlyList<ChatCandidate> candidates = Parser.Parse(
            [Row(0, "[Kestrel#5678]: ok"), Row(1, "To be honest: no")], Roi);

        Assert.Equal("ok To be honest: no", Assert.Single(candidates).Body);
    }

    [Fact]
    public void Damaged_speaker_prefix_still_starts_a_new_message()
    {
        // 名前の部分が崩れても、続く発言を巻き込まない
        IReadOnlyList<ChatCandidate> candidates = Parser.Parse(
        [
            Row(0, "[Pancake916Å 」: Just type y or n"),
            Row(1, "[TheGreyWarden Can I take left side while", width: 400),
            Row(2, "you 3 handle the relic?"),
            Row(3, "[Pancake916#_ 1;.Kestrel can u hear me?"),
        ], Roi);

        Assert.Equal(3, candidates.Count);
        Assert.All(candidates, c => Assert.True(c.IsUncertain && c.Kind == ChatLineKind.Player));
        Assert.Equal(("Pancake916Å", "Just type y or n"), (candidates[0].Speaker, candidates[0].Body));
        Assert.Equal(("TheGreyWarden", "Can I take left side while you 3 handle the relic?"), (candidates[1].Speaker, candidates[1].Body));
        Assert.Equal("Kestrel can u hear me?", candidates[2].Body);
    }

    [Theory]
    [InlineData("[Kestrel#5678]•. Sounds broken")]
    [InlineData("[Kestrel#5678]:•. Sounds broken")]
    [InlineData("[Kestrel#5678]: •. Sounds broken")]
    [InlineData("[Kestrel#5678]・Sounds broken")]
    public void Colon_misread_as_dots_is_not_left_in_the_body(string row)
    {
        ChatCandidate candidate = Assert.Single(Parser.Parse([Row(0, row)], Roi));

        Assert.Equal(("Kestrel#5678", "Sounds broken", false), (candidate.Speaker, candidate.Body, candidate.IsUncertain));
    }

    [Fact]
    public void Damaged_prefix_with_dots_is_cleaned()
    {
        ChatCandidate candidate = Assert.Single(Parser.Parse([Row(0, "[Kestrel#5678 •. Sounds broken")], Roi));

        Assert.Equal(("Kestrel#5678", "Sounds broken"), (candidate.Speaker, candidate.Body));
    }

    [Theory]
    [InlineData("[Kestrel#5678]: :) nice", ":) nice")]
    [InlineData("[Kestrel#5678]: ...?", "...?")]
    [InlineData("[Kestrel#5678]: 3.5 dps", "3.5 dps")]
    public void Legitimate_leading_symbols_are_kept(string row, string body)
    {
        Assert.Equal(body, Assert.Single(Parser.Parse([Row(0, row)], Roi)).Body);
    }

    [Fact]
    public void Input_row_below_a_wide_gap_is_ignored()
    {
        IReadOnlyList<ChatCandidate> candidates = Parser.Parse(
        [
            Row(0, "[Kestrel#5678]: one"),
            Row(1, "[Kestrel#5678]: two"),
            Row(2, "[Kestrel#5678]: three"),
            new OcrLine("[ファイアチーム]宛て: typing", new PixelRect(10, 2 * 24 + 45, 150, 20), []),
        ], Roi);

        Assert.Equal(["one", "two", "three"], candidates.Select(c => c.Body));
    }

    [Fact]
    public void Join_notices_ending_with_period_are_not_merged()
    {
        IReadOnlyList<ChatCandidate> candidates = Parser.Parse(
        [
            Row(0, "Pancake916#1234がチームチャットに参加しました。", width: 470),
            Row(1, "TheGreyWardeniがチームチャットに参加しました。", width: 470),
        ], Roi);

        Assert.Equal(["Pancake916#1234", "TheGreyWardeni"], candidates.Select(c => c.Speaker));
    }

    [Fact]
    public void Japanese_row_after_short_line_is_a_new_system_message()
    {
        IReadOnlyList<ChatCandidate> candidates = Parser.Parse(
            [Row(0, "[Kestrel#5678]: ok"), Row(1, "ファイアチーム検索機能チャンネルに参加しました。")], Roi);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(ChatLineKind.System, candidates[1].Kind);
    }
}
