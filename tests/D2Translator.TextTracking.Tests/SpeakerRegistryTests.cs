using D2Translator.Domain.Chat;
using D2Translator.TextTracking.Speakers;

namespace D2Translator.TextTracking.Tests;

public class SpeakerRegistryTests
{
    private static SpeakerRegistry Registry()
    {
        var registry = new SpeakerRegistry();
        registry.Register("TheGreyWarden#9012");
        registry.Register("Kestrel#5678");
        registry.Register("Pancake916#1234");
        registry.Register("StormRiderTTV#2222");
        return registry;
    }

    [Fact]
    public void Keys_are_assigned_in_order_and_reused()
    {
        SpeakerRegistry registry = Registry();

        Assert.Equal("P2", registry.Register("Kestrel#5678"));
        Assert.Equal("P5", registry.Register("nightowlcream#1111"));
    }

    [Fact]
    public void Ocr_variants_of_the_same_name_share_a_key()
    {
        SpeakerRegistry registry = Registry();

        Assert.Equal("P3", registry.Register("Pancake916#1Z34"));
    }

    [Theory]
    [InlineData("Ready up grey warden", "grey warden", "P1")]
    [InlineData("Kestrel can u hear me?", "Kestrel", "P2")]
    [InlineData("thanks TheGreyWarden", "TheGreyWarden", "P1")]
    [InlineData("pancake you there", "pancake", "P3")]
    [InlineData("go storm rider", "storm rider", "P4")]
    public void Mentions_of_known_speakers_are_found(string text, string mention, string key)
    {
        TextAnnotation annotation = Assert.Single(Registry().FindMentions(text));

        Assert.Equal((AnnotationKind.SpeakerMention, mention, key), (annotation.Kind, annotation.Text, annotation.ReferenceId));
        Assert.Equal(mention, text.Substring(annotation.Start, annotation.Length));
    }

    [Theory]
    [InlineData("the grey cat")]
    [InlineData("warden")]
    [InlineData("KestrelX is here")]
    [InlineData("storm everywhere")]
    public void Common_words_and_partial_words_are_not_mentions(string text)
    {
        Assert.Empty(Registry().FindMentions(text));
    }

    [Fact]
    public void Display_name_drops_the_code()
    {
        Assert.Equal("Pancake916", SpeakerRegistry.DisplayName("Pancake916#1234"));
    }
}
