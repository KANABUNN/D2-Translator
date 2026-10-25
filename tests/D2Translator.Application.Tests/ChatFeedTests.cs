using System.Diagnostics;
using D2Translator.Application.Chat;
using D2Translator.DictionaryContext;
using D2Translator.Domain.Chat;
using D2Translator.Domain.Geometry;
using D2Translator.Domain.Glossary;
using D2Translator.Domain.Ocr;
using D2Translator.TextTracking.Parsing;
using D2Translator.TextTracking.Speakers;
using D2Translator.TextTracking.Tracking;

namespace D2Translator.Application.Tests;

public class ChatFeedTests
{
    private static readonly PixelRect Roi = new(0, 0, 500, 300);

    private static long Ms(int milliseconds) => milliseconds * Stopwatch.Frequency / 1000;

    private static OcrObservation Observation(params string[] rows) => new()
    {
        ObservationId = Guid.NewGuid(),
        FrameSequence = 1,
        CapturedAt = DateTimeOffset.UtcNow,
        RoiBounds = Roi,
        Lines = [.. rows.Select((text, i) => new OcrLine(text, new PixelRect(10, i * 24, 200, 20), []))],
    };

    [Fact]
    public void Messages_get_speaker_keys_mentions_and_glossary_notes()
    {
        ChatFeed feed = TestPipelines.NewFeed();
        var updates = new List<ChatFeedUpdate>();
        feed.Updated += (_, u) => updates.Add(u);

        feed.OnObservation(Observation("[Pancake916#1234]: KWTD LF2M", "[TheGreyWarden#9012]: ok"), Ms(0));
        feed.OnObservation(Observation("[Pancake916#1234]: KWTD LF2M", "[TheGreyWarden#9012]: ok",
            "[Pancake916#1234]: Ready up grey warden"), Ms(1000));
        feed.OnUnchanged(Ms(1300));

        Assert.Equal([MessageUpdateKind.Added, MessageUpdateKind.Added, MessageUpdateKind.Added, MessageUpdateKind.Confirmed],
            updates.Select(u => u.Kind));

        ChatMessage recruitment = updates[0].Message;
        Assert.Equal("P1", recruitment.SpeakerKey);
        Assert.Equal(["KWTD", "LF2M"], recruitment.Annotations.Select(a => a.Text));

        ChatMessage confirmed = updates[3].Message;
        Assert.Equal((MessageStatus.GlossaryOnly, "P1"), (confirmed.Status, confirmed.SpeakerKey));
        TextAnnotation mention = Assert.Single(confirmed.Annotations);
        Assert.Equal((AnnotationKind.SpeakerMention, "grey warden", "P2"), (mention.Kind, mention.Text, mention.ReferenceId));
    }

    [Fact]
    public void Speaker_name_that_is_also_a_glossary_term_is_kept_only_as_a_mention()
    {
        // 既定辞書は Bungie の固有名を含むため、プレイヤー名と用語が同じことがある（例: Riven）。
        // 名前を辞書のヒントとして外部へ送らないよう、話者名の注釈だけを残す（決定 D14、D33）
        var riven = new GlossaryEntry
        {
            TermId = "d2.npc.riven",
            SenseId = "boss",
            Source = "Riven",
            Target = "リベン",
            Provenance = new GlossaryProvenance { Kind = "test" },
        };
        var feed = new ChatFeed(new ChatLineParser(ChatFormat.JapaneseClient), new MessageTracker(TimeProvider.System),
            new SpeakerRegistry(), new GlossaryMatcher([riven], "test"));
        var updates = new List<ChatFeedUpdate>();
        feed.Updated += (_, u) => updates.Add(u);

        feed.OnObservation(Observation("[Riven#4242]: hi all"), Ms(0));
        feed.OnObservation(Observation("[Riven#4242]: hi all", "[Kestrel#5678]: Riven is down"), Ms(1000));

        TextAnnotation annotation = Assert.Single(updates[^1].Message.Annotations);
        Assert.Equal((AnnotationKind.SpeakerMention, "Riven"), (annotation.Kind, annotation.Text));
    }

    [Fact]
    public void Reset_clears_speakers_and_tracking()
    {
        ChatFeed feed = TestPipelines.NewFeed();
        var updates = new List<ChatFeedUpdate>();
        feed.Updated += (_, u) => updates.Add(u);
        feed.OnObservation(Observation("[Kestrel#5678]: hello there"), Ms(0));

        feed.Reset(Guid.NewGuid());
        feed.OnObservation(Observation("[Pancake916#1234]: hi all"), Ms(1000));

        ChatMessage after = updates[^1].Message;
        Assert.Equal(("P1", true), (after.SpeakerKey, after.WasAlreadyVisible));
    }
}
