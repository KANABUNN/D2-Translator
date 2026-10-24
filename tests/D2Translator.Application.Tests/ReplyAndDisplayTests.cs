using D2Translator.Application.Display;
using D2Translator.Application.Reply;
using D2Translator.Application.Translation;
using D2Translator.Domain.Settings;

namespace D2Translator.Application.Tests;

public class ReplyAndDisplayTests
{
    [Fact]
    public void Late_translation_does_not_overwrite_a_changed_draft()
    {
        var session = new ReplySession();
        session.UpdateDraft("あと2人で行こう");
        int revision = session.BeginTranslation();

        session.UpdateDraft("あと3人で行こう");
        bool applied = session.ApplyTranslation(revision, "Let's go with 2 more");

        Assert.False(applied);
        Assert.Equal(string.Empty, session.Candidate);
        Assert.Equal(("Let's go with 2 more", revision), session.LateCandidate);

        session.AcceptLateCandidate();
        Assert.Equal("Let's go with 2 more", session.Candidate);
        Assert.True(session.IsCandidateOutdated);
    }

    [Fact]
    public void Current_translation_becomes_the_candidate_and_edits_are_tracked()
    {
        var session = new ReplySession();
        session.UpdateDraft("ありがとう");
        Assert.True(session.ApplyTranslation(session.BeginTranslation(), "thanks"));

        session.EditCandidate("thanks all");

        Assert.Equal((CandidateState.Edited, "thanks all", false), (session.State, session.Candidate, session.IsCandidateOutdated));
    }

    [Fact]
    public void Copy_replaces_line_breaks_and_flags_commands()
    {
        var session = new ReplySession();
        session.UpdateDraft("x");
        session.ApplyTranslation(session.BeginTranslation(), "/dance now\r\nplease ");

        CopyPlan plan = session.PrepareCopy();

        Assert.Equal("/dance now please", plan.Text);
        Assert.True(plan.LooksLikeCommand);
        Assert.Single(plan.Notices);
    }

    [Fact]
    public void Draft_over_the_limit_cannot_be_translated()
    {
        var session = new ReplySession();
        session.UpdateDraft(new string('あ', ReplySession.MaxDraftScalars + 1));

        Assert.False(session.CanTranslate);
        Assert.Throws<InvalidOperationException>(() => session.BeginTranslation());
    }

    [Theory]
    [InlineData("あと2人", "2 more", 0)]
    [InlineData("あと2人", "two more", 1)]
    [InlineData("⟦P1⟧ ありがとう", "thanks ⟦P1⟧", 0)]
    [InlineData("⟦P1⟧ ありがとう", "thanks", 1)]
    [InlineData("ありがとう", "ありがとう thanks", 1)]
    [InlineData("ありがとう", "thanks\nall", 1)]
    public void Reply_checks_tokens_numbers_language_and_line_breaks(string source, string translated, int problems)
    {
        Assert.Equal(problems, TranslationChecks.JaEn(source, translated).Count);
    }

    [Fact]
    public async Task Priority_gate_prefers_replies_but_lets_incoming_through()
    {
        var gate = new PriorityGate(capacity: 1, maxConsecutivePriority: 2);
        var order = new List<string>();
        IDisposable first = await gate.EnterAsync(priority: false, TestContext.Current.CancellationToken);

        Task Run(string name, bool priority) => Task.Run(async () =>
        {
            using IDisposable slot = await gate.EnterAsync(priority, TestContext.Current.CancellationToken);
            lock (order)
            {
                order.Add(name);
            }
        });

        Task incoming = Run("incoming", priority: false);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Task reply1 = Run("reply1", priority: true);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Task reply2 = Run("reply2", priority: true);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Task reply3 = Run("reply3", priority: true);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        first.Dispose();
        await Task.WhenAll(incoming, reply1, reply2, reply3);

        // 返信を 2 件続けて通したら、待っている受信を 1 件通す
        Assert.Equal(["reply1", "reply2", "incoming", "reply3"], order);
    }

    [Fact]
    public void Overlay_hides_only_after_the_target_window_was_seen_in_front()
    {
        var policy = new OverlayPolicy();
        policy.SetTarget("Destiny 2");
        var other = new ForegroundInfo(false, "Browser");

        Assert.True(policy.ShouldShow(DisplayMode.Overlay, false, false, true, other));
        Assert.True(policy.ShouldShow(DisplayMode.Overlay, false, false, true, new ForegroundInfo(false, "Destiny 2")));
        Assert.False(policy.ShouldShow(DisplayMode.Overlay, false, false, true, other));
        Assert.True(policy.ShouldShow(DisplayMode.Overlay, false, false, true, new ForegroundInfo(true, "D2 Chat Translator")));
        Assert.True(policy.ShouldShow(DisplayMode.Overlay, false, editing: true, true, other));
        Assert.True(policy.ShouldShow(DisplayMode.Overlay, false, false, hideWhenGameInactive: false, other));
        Assert.False(policy.ShouldShow(DisplayMode.Overlay, userHidden: true, false, true, new ForegroundInfo(false, "Destiny 2")));
        Assert.False(policy.ShouldShow(DisplayMode.Window, false, false, true, new ForegroundInfo(false, "Destiny 2")));
    }

    [Fact]
    public void Self_capture_is_detected_from_shown_translations()
    {
        var detector = new SelfCaptureDetector();
        detector.Shown("あと2人募集。手順を理解している人で。");

        Assert.True(detector.IsSelfCapture(["[Kestrel#5678]: LF2M", "あと2人募集。 手順を理解している人で"]));
        Assert.False(detector.IsSelfCapture(["[Kestrel#5678]: LF2M KWTD"]));
    }

    [Theory]
    [InlineData("Ctrl+Alt+T", HotkeyModifiers.Control | HotkeyModifiers.Alt, "T")]
    [InlineData("alt + shift + f5", HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F5")]
    public void Hotkey_gestures_parse(string text, HotkeyModifiers modifiers, string key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out HotkeyGesture gesture));
        Assert.Equal((modifiers, key), (gesture.Modifiers, gesture.Key));
    }

    [Theory]
    [InlineData("T")]
    [InlineData("Ctrl+Ctrl+T")]
    [InlineData("Ctrl+Enter")]
    [InlineData("Hyper+T")]
    [InlineData("")]
    public void Invalid_hotkey_gestures_are_rejected(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
    }
}
