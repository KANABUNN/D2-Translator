using System.Text;
using D2Translator.Domain.Chat;
using D2Translator.Domain.Glossary;

namespace D2Translator.DictionaryContext.Tests;

public class GlossaryTests
{
    private static readonly GlossaryMatcher Matcher = GlossaryMatcher.FromDocument(GlossaryLoader.LoadExample().Document!);

    [Fact]
    public void Example_dictionary_is_valid_and_unreviewed()
    {
        GlossaryLoadResult result = GlossaryLoader.LoadExample();

        Assert.True(result.IsValid, string.Join(", ", result.Errors));
        Assert.Equal(11, result.Document!.Entries.Count);
        Assert.All(result.Document.Entries, e => Assert.Equal(GlossaryReviewStatus.Pending, e.Provenance.ReviewStatus));
    }

    [Fact]
    public void Recruitment_terms_keep_numbers()
    {
        IReadOnlyList<TextAnnotation> found = Matcher.Find("LF3M raid KWTD");

        Assert.Equal(["LF3M", "KWTD"], found.Select(a => a.Text));
        Assert.StartsWith("あと3人募集", found[0].Note, StringComparison.Ordinal);
        Assert.StartsWith("手順を理解していること", found[1].Note, StringComparison.Ordinal);
        Assert.All(found, a => Assert.Contains("未確認", a.Note, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("clear the adds first", "adds")]
    [InlineData("we wiped again", "wiped")]
    [InlineData("can someone rez me", "rez")]
    [InlineData("I'm on Well of Radiance", "Well of Radiance")]
    public void Terms_match_on_word_boundaries(string text, string expected)
    {
        Assert.Equal(expected, Assert.Single(Matcher.Find(text)).Text);
    }

    [Theory]
    [InlineData("send me the address")]
    [InlineData("well done everyone")]
    [InlineData("individual")]
    [InlineData("eggs")]
    public void Words_inside_other_words_and_plain_well_are_not_matched(string text)
    {
        Assert.Empty(Matcher.Find(text));
    }

    [Fact]
    public void Ambiguous_term_lists_every_sense()
    {
        TextAnnotation dps = Assert.Single(Matcher.Find("dps now"));

        Assert.Contains("火力 / 攻撃フェーズ", dps.Note, StringComparison.Ordinal);
        Assert.Contains("語義が複数", dps.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "dictionaryVersion": "x", "entries": [], "extra": 1 }""")]
    [InlineData("""{ "schemaVersion": 2, "dictionaryVersion": "x", "entries": [] }""")]
    [InlineData("not json")]
    public void Malformed_documents_are_rejected(string json)
    {
        GlossaryLoadResult result = GlossaryLoader.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.False(result.IsValid);
        Assert.Null(result.Document);
    }

    [Fact]
    public void One_bad_entry_rejects_the_whole_import()
    {
        GlossaryDocument document = GlossaryLoader.LoadExample().Document!;
        GlossaryEntry bad = document.Entries[0] with { SenseId = "other", Source = "a(b|c)*{x}" };
        GlossaryEntry duplicate = document.Entries[1];

        List<string> errors = GlossaryLoader.Validate(document with { Entries = [.. document.Entries, bad, duplicate] });

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Control_characters_are_rejected()
    {
        GlossaryDocument document = GlossaryLoader.LoadExample().Document!;
        GlossaryEntry bad = document.Entries[0] with { SenseId = "x", Target = "bad\u0007" };

        Assert.Single(GlossaryLoader.Validate(document with { Entries = [bad] }));
    }
}
