using D2Translator.TextTracking.Metrics;
using D2Translator.TextTracking.Text;

namespace D2Translator.TextTracking.Tests;

public class TextMetricsTests
{
    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("same", "same", 0)]
    [InlineData("チャット", "チヤット", 1)]
    public void Levenshtein_counts_edits(string a, string b, int expected)
    {
        Assert.Equal(expected, TextUtil.Levenshtein(a, b));
    }

    [Fact]
    public void Normalize_folds_full_width_and_spaces()
    {
        Assert.Equal("[クラン]宛て: B", TextUtil.Normalize("  ［クラン］宛て：　 B "));
    }

    [Fact]
    public void Character_error_rate_uses_the_closest_hypothesis()
    {
        (int edits, int length, string? best) = CharacterErrorRate.CompareBest("Sounds bugged", ["Soundsbugged", "Who can do lasers?"]);

        Assert.Equal((1, 13, "Soundsbugged"), (edits, length, best));
    }

    [Fact]
    public void Missing_hypothesis_counts_every_character()
    {
        Assert.Equal((5, 5, (string?)null), CharacterErrorRate.CompareBest("hello", []));
    }
}
