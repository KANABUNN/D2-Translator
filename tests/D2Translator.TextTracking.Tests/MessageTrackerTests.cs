using System.Diagnostics;
using D2Translator.Domain.Chat;
using D2Translator.Domain.Ocr;
using D2Translator.TextTracking.Parsing;
using D2Translator.TextTracking.Tracking;

namespace D2Translator.TextTracking.Tests;

public class MessageTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static long Ms(int milliseconds) => milliseconds * Stopwatch.Frequency / 1000;

    private static ChatCandidate P(string speaker, string body) =>
        new(ChatLineKind.Player, speaker, null, ChatChannel.Unknown, body, default, 1);

    private static ChatCandidate Own(string body) =>
        new(ChatLineKind.Own, null, "ファイアチーム", ChatChannel.Fireteam, body, default, 1);

    private static MessageTracker Baseline(params ChatCandidate[] visible)
    {
        var tracker = new MessageTracker(TimeProvider.System);
        tracker.Apply(visible, Now, Ms(0));
        return tracker;
    }

    [Fact]
    public void First_observation_is_baseline_not_new()
    {
        var tracker = new MessageTracker(TimeProvider.System);

        IReadOnlyList<MessageUpdate> updates = tracker.Apply([P("A#1", "hello there"), Own("hi")], Now, Ms(0));

        Assert.Equal(2, updates.Count);
        Assert.All(updates, u =>
        {
            Assert.Equal(MessageUpdateKind.Added, u.Kind);
            Assert.True(u.Message.WasAlreadyVisible);
            Assert.Equal(MessageStatus.GlossaryOnly, u.Message.Status);
        });
        Assert.Equal([1L, 2L], updates.Select(u => u.Message.Sequence));
    }

    [Fact]
    public void New_message_is_confirmed_when_unchanged_for_200ms()
    {
        MessageTracker tracker = Baseline(P("A#1", "hello there"));

        MessageUpdate added = Assert.Single(tracker.Apply([P("A#1", "hello there"), P("B#2", "need two more")], Now, Ms(1000)));
        Assert.Equal((MessageUpdateKind.Added, MessageStatus.Recognizing, false),
            (added.Kind, added.Message.Status, added.Message.WasAlreadyVisible));

        Assert.Empty(tracker.ApplyUnchanged(Ms(1100)));
        MessageUpdate confirmed = Assert.Single(tracker.ApplyUnchanged(Ms(1250)));

        Assert.Equal((MessageUpdateKind.Confirmed, MessageStatus.GlossaryOnly, RecognitionQuality.Good),
            (confirmed.Kind, confirmed.Message.Status, confirmed.Message.Quality));
        Assert.Equal(added.Message.MessageId, confirmed.Message.MessageId);
    }

    [Fact]
    public void Unstable_reading_is_confirmed_as_needs_review_after_one_second()
    {
        MessageTracker tracker = Baseline(P("A#1", "hello there"));
        tracker.Apply([P("A#1", "hello there"), P("B#2", "need two more")], Now, Ms(1000));
        tracker.Apply([P("A#1", "hello there"), P("B#2", "need tvvo more")], Now, Ms(1150));
        tracker.Apply([P("A#1", "hello there"), P("B#2", "need two more")], Now, Ms(1300));

        MessageUpdate review = Assert.Single(tracker.Tick(Ms(2000)));

        Assert.Equal((MessageStatus.NeedsReview, RecognitionQuality.NeedsReview), (review.Message.Status, review.Message.Quality));
    }

    [Fact]
    public void Scrolled_messages_keep_identity_and_only_the_bottom_one_is_new()
    {
        MessageTracker tracker = Baseline(P("A#1", "first message"), P("B#2", "second message"), P("A#1", "third message"));

        IReadOnlyList<MessageUpdate> updates = tracker.Apply(
            [P("B#2", "second message"), P("A#1", "third message"), P("B#2", "fourth message")], Now, Ms(1000));

        MessageUpdate added = Assert.Single(updates);
        Assert.Equal("fourth message", added.Message.RawText);
        Assert.Equal(4, added.Message.Sequence);
    }

    [Fact]
    public void Repeated_identical_message_at_the_bottom_is_new()
    {
        MessageTracker tracker = Baseline(P("A#1", "ready"), P("B#2", "gg all"));

        IReadOnlyList<MessageUpdate> updates = tracker.Apply(
            [P("A#1", "ready"), P("B#2", "gg all"), P("B#2", "gg all")], Now, Ms(1000));

        Assert.Equal("gg all", Assert.Single(updates).Message.RawText);
    }

    [Fact]
    public void Short_bodies_must_match_exactly()
    {
        MessageTracker tracker = Baseline(P("Kestrel#5678", "y"));

        IReadOnlyList<MessageUpdate> updates = tracker.Apply([P("Kestrel#5678", "y"), P("Kestrel#5678", "n")], Now, Ms(1000));

        Assert.Equal("n", Assert.Single(updates).Message.RawText);
    }

    [Fact]
    public void No_overlap_rebaselines_instead_of_reporting_new_messages()
    {
        MessageTracker tracker = Baseline(P("A#1", "old one"), P("A#1", "old two"));

        IReadOnlyList<MessageUpdate> updates = tracker.Apply([P("C#3", "something else entirely")], Now, Ms(1000));

        Assert.True(Assert.Single(updates).Message.WasAlreadyVisible);
    }

    [Fact]
    public void Stable_correction_after_confirmation_is_a_revision()
    {
        MessageTracker tracker = Baseline(P("A#1", "Just type y or m"));

        Assert.Empty(tracker.Apply([P("A#1", "Just type y or n")], Now, Ms(1000)));
        MessageUpdate revised = Assert.Single(tracker.Apply([P("A#1", "Just type y or n")], Now, Ms(1300)));

        Assert.Equal(MessageUpdateKind.Revised, revised.Kind);
        Assert.Equal(("Just type y or m", "Just type y or n", 2),
            (revised.Message.RawText, revised.Message.CorrectedText, revised.Message.Revision));
    }

    [Fact]
    public void Noisier_reading_is_not_accepted_as_a_revision()
    {
        // チャットが薄れてコロンが •. に読まれた場合
        MessageTracker tracker = Baseline(P("A#1", "Sounds broken"));

        Assert.Empty(tracker.Apply([P("A#1", "•. Sounds broken")], Now, Ms(1000)));
        Assert.Empty(tracker.Apply([P("A#1", "•. Sounds broken")], Now, Ms(1300)));
        Assert.Empty(tracker.Apply([P("A#1", "•. Sounds broken")], Now, Ms(1600)));
    }

    [Fact]
    public void Late_change_is_not_accepted_as_a_revision()
    {
        MessageTracker tracker = Baseline(P("A#1", "Just type y or m"));

        Assert.Empty(tracker.Apply([P("A#1", "Just type y or n")], Now, Ms(6000)));
        Assert.Empty(tracker.Apply([P("A#1", "Just type y or n")], Now, Ms(6300)));
    }

    [Fact]
    public void Pending_ghost_that_disappears_is_removed()
    {
        MessageTracker tracker = Baseline(P("A#1", "hello there"));
        tracker.Apply([P("A#1", "hello there"), P("Z#9", "ghost row")], Now, Ms(1000));

        tracker.Apply([P("A#1", "hello there")], Now, Ms(1100));
        IReadOnlyList<MessageUpdate> updates = tracker.Apply([P("A#1", "hello there")], Now, Ms(1200));

        Assert.Equal(MessageUpdateKind.Removed, Assert.Single(updates).Kind);
    }

    [Fact]
    public void Resync_treats_messages_added_while_paused_as_already_visible()
    {
        MessageTracker tracker = Baseline(P("A#1", "hello there"));
        tracker.Resync();

        MessageUpdate update = Assert.Single(tracker.Apply([P("A#1", "hello there"), P("B#2", "while paused")], Now, Ms(5000)));
        Assert.True(update.Message.WasAlreadyVisible);

        MessageUpdate next = Assert.Single(tracker.Apply(
            [P("A#1", "hello there"), P("B#2", "while paused"), P("B#2", "after resume")], Now, Ms(6000)));
        Assert.False(next.Message.WasAlreadyVisible);
    }

    [Fact]
    public void Empty_observation_keeps_tracking_state()
    {
        MessageTracker tracker = Baseline(P("A#1", "hello there"));

        Assert.Empty(tracker.Apply([], Now, Ms(1000)));
        Assert.Empty(tracker.Apply([P("A#1", "hello there")], Now, Ms(2000)));
    }

    [Fact]
    public void Reset_starts_a_new_session()
    {
        MessageTracker tracker = Baseline(P("A#1", "hello there"));
        var session = Guid.NewGuid();

        tracker.Reset(session);
        MessageUpdate update = Assert.Single(tracker.Apply([P("A#1", "hello there")], Now, Ms(1000)));

        Assert.Equal((session, 1L, true), (update.Message.SessionId, update.Message.Sequence, update.Message.WasAlreadyVisible));
    }
}
