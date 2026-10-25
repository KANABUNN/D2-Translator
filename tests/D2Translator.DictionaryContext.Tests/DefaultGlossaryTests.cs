using D2Translator.Domain.Chat;
using D2Translator.Domain.Glossary;

namespace D2Translator.DictionaryContext.Tests;

/// <summary>
/// 同梱の既定辞書（d2-chat-2026-10-24-v1、決定 D33）の検査。件数と上限、全件が未確認であること、
/// 募集人数、多義語、否定、単語境界を、実際のローダーと照合器で確かめる。
/// </summary>
public class DefaultGlossaryTests
{
    private static readonly GlossaryLoadResult Loaded = GlossaryLoader.LoadDefault();

    private static readonly GlossaryMatcher Matcher = GlossaryMatcher.FromDocument(Loaded.Document!);

    [Fact]
    public void Default_dictionary_is_valid_within_limits_and_unreviewed()
    {
        Assert.True(Loaded.IsValid, string.Join(", ", Loaded.Errors.Take(5)));
        GlossaryDocument document = Loaded.Document!;
        Assert.Equal("d2-chat-2026-10-24-v1", document.DictionaryVersion);
        Assert.Equal(9547, document.Entries.Count);
        Assert.True(document.Entries.Count <= GlossaryLoader.MaxEntries);
        // 人の確認前の辞書を確認済みとして扱わない（仕様書 9.1）
        Assert.All(document.Entries, e => Assert.Equal(GlossaryReviewStatus.Pending, e.Provenance.ReviewStatus));
        // 英日だけ。日英の返信には使わない（返信の辞書照合は未対応）
        Assert.All(document.Entries, e => Assert.Equal(GlossaryDirection.EnJa, e.Direction));

        using Stream resource = typeof(GlossaryLoader).Assembly.GetManifestResourceStream(GlossaryLoader.DefaultResourceName)!;
        Assert.True(resource.Length <= GlossaryLoader.MaxFileBytes, $"{resource.Length} bytes");
    }

    [Fact]
    public void Default_dictionary_keeps_the_specification_examples()
    {
        HashSet<string> ids = [.. Loaded.Document!.Entries.Select(e => e.TermId)];

        Assert.Subset(ids, new HashSet<string>
        {
            "d2.lfg.kwtd", "d2.lfg.lfnm", "d2.combat.adds", "d2.combat.wipe", "d2.combat.rez", "d2.combat.dps",
            "d2.subclass.well", "d2.weapon.div", "d2.chat.gg", "d2.chat.brb",
        });
    }

    [Theory]
    [InlineData("LF3M VoG KWTD", "LF3M", "あと3人募集")]
    [InlineData("LF12M", "LF12M", "あと12人募集")]
    public void Recruitment_counts_are_kept(string text, string term, string target)
    {
        TextAnnotation found = Assert.Single(Matcher.Find(text), a => a.Text == term);

        Assert.StartsWith(target, found.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Three_digit_counts_are_not_read_as_recruitment()
    {
        Assert.Empty(Matcher.Find("LF100M"));
    }

    [Theory]
    [InlineData("ADS")]
    [InlineData("DPS now")]
    [InlineData("GL")]
    [InlineData("FR")]
    [InlineData("OP")]
    [InlineData("NM")]
    [InlineData("Discord")]
    public void Ambiguous_abbreviations_list_every_sense(string text)
    {
        TextAnnotation found = Assert.Single(Matcher.Find(text));

        Assert.Contains("語義が複数", found.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Negated_requirement_is_matched_as_a_whole()
    {
        // 長い否定表現を優先し、「マイク必須」と逆の意味にしない
        TextAnnotation found = Assert.Single(Matcher.Find("LFG no mic required"), a => a.Text.Contains("mic", StringComparison.Ordinal));

        Assert.Equal(("no mic required", "d2.lfg.no-mic-required"), (found.Text, found.ReferenceId));
        Assert.StartsWith("マイク不要", found.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("send me the address")]
    [InlineData("individual")]
    [InlineData("eggs")]
    [InlineData("well done everyone")]
    public void Words_inside_other_words_and_plain_well_are_not_matched(string text)
    {
        Assert.Empty(Matcher.Find(text));
    }

    [Fact]
    public void Official_names_are_matched_on_word_boundaries()
    {
        TextAnnotation found = Assert.Single(Matcher.Find("who has the well of radiance"));

        Assert.Equal(("well of radiance", "d2.subclass.well"), (found.Text, found.ReferenceId));
    }
}
