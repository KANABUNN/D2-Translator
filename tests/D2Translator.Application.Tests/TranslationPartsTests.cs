using System.Diagnostics;
using D2Translator.Application.Translation;
using D2Translator.Domain.Chat;
using D2Translator.Domain.Translation;

namespace D2Translator.Application.Tests;

public class TranslationPartsTests
{
    private static ChatMessage Message(string text, long sequence = 1, ChatChannel channel = ChatChannel.Unknown,
        ChatLineKind kind = ChatLineKind.Player, string? speakerKey = "P1", DateTimeOffset? at = null, params TextAnnotation[] annotations) => new()
    {
        MessageId = Guid.NewGuid(),
        SessionId = Session,
        Sequence = sequence,
        Kind = kind,
        Channel = channel,
        SpeakerKey = speakerKey,
        RawText = text,
        ObservedAt = at ?? Now,
        Status = MessageStatus.GlossaryOnly,
        Annotations = annotations,
    };

    private static readonly Guid Session = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Mentions_urls_and_mails_are_replaced_and_restored()
    {
        ChatMessage message = Message("grey warden see https://example.com/x or mail a.b@example.org", annotations:
            new TextAnnotation(AnnotationKind.SpeakerMention, 0, 11, "grey warden", "話者名 TheGreyWarden", "P2"));

        AnonymizedText anonymized = Anonymizer.Anonymize(message);

        Assert.Equal("⟦P2⟧ see ⟦URL1⟧ or mail ⟦MAIL1⟧", anonymized.Text);
        Assert.Equal("grey warden、見て https://example.com/x",
            Anonymizer.Restore("⟦P2⟧、見て ⟦URL1⟧", anonymized.Originals));
        Assert.Equal(["⟦MAIL1⟧"], Anonymizer.MissingTokens(anonymized.Text, "⟦P2⟧、見て ⟦URL1⟧"));
    }

    [Fact]
    public void Context_respects_count_age_session_and_excludes_target()
    {
        var history = new List<ChatMessage>();
        for (int i = 1; i <= 9; i++)
        {
            history.Add(Message($"line {i}", sequence: i, at: Now.AddSeconds(-10 * (10 - i))));
        }

        history.Add(Message("own reply", sequence: 10, kind: ChatLineKind.Own, channel: ChatChannel.Fireteam, speakerKey: null));
        history.Add(Message("system", sequence: 11, kind: ChatLineKind.System));
        history.Add(Message("other session", sequence: 12) with { SessionId = Guid.NewGuid() });
        ChatMessage target = Message("target", sequence: 13);

        IReadOnlyList<ContextLine> context = ContextBuilder.Build([.. history, target], target);

        Assert.Equal(6, context.Count);
        Assert.Equal(("me", "own reply"), (context[^1].Speaker, context[^1].Text));
        Assert.Equal("line 5", context[0].Text);
        Assert.DoesNotContain(context, c => c.Text is "system" or "target" or "other session");
    }

    [Fact]
    public void Context_drops_old_lines_and_respects_character_limit()
    {
        ChatMessage old = Message("too old", sequence: 1, at: Now.AddSeconds(-121));
        ChatMessage longLine = Message(new string('a', 1490), sequence: 2);
        ChatMessage recent = Message("recent line", sequence: 3);
        ChatMessage target = Message("target", sequence: 4);

        IReadOnlyList<ContextLine> context = ContextBuilder.Build([old, longLine, recent, target], target);

        Assert.Equal(["recent line"], context.Select(c => c.Text));
    }

    [Fact]
    public void Context_keeps_different_known_channels_apart()
    {
        ChatMessage clan = Message("clan talk", sequence: 1, channel: ChatChannel.Clan);
        ChatMessage target = Message("target", sequence: 2, channel: ChatChannel.Fireteam);

        Assert.Empty(ContextBuilder.Build([clan, target], target));
    }

    [Theory]
    [InlineData("LF2M raid", "あと2人募集のレイド", 0)]
    [InlineData("LF2M raid", "あと二人募集のレイド", 1)]
    [InlineData("⟦P2⟧ come here", "⟦P2⟧、こっち来て", 0)]
    [InlineData("⟦P2⟧ come here", "こっち来て", 1)]
    [InlineData("need two more", "need two more", 1)]
    [InlineData("gg", "おつかれ", 0)]
    public void Checks_flag_lost_tokens_numbers_and_wrong_language(string source, string translated, int problems)
    {
        Assert.Equal(problems, TranslationChecks.EnJa(source, translated).Count);
    }

    [Fact]
    public void Cache_key_changes_with_context_and_glossary_but_not_request_ids()
    {
        TranslationRequest a = Request("need adds clear");
        TranslationRequest sameText = a with { RequestId = Guid.NewGuid(), MessageId = Guid.NewGuid(), SpeakerKey = "P9" };
        TranslationRequest withContext = a with { Context = [new ContextLine("P2", "boss phase")] };
        TranslationRequest otherDictionary = a with { GlossaryVersion = "v2" };

        Assert.Equal(TranslationCache.KeyOf(a), TranslationCache.KeyOf(sameText));
        Assert.NotEqual(TranslationCache.KeyOf(a), TranslationCache.KeyOf(withContext));
        Assert.NotEqual(TranslationCache.KeyOf(a), TranslationCache.KeyOf(otherDictionary));
    }

    [Fact]
    public void Cache_expires_and_evicts_oldest()
    {
        var time = new ManualTime();
        var cache = new TranslationCache(time, capacity: 2, lifetime: TimeSpan.FromMinutes(30));
        TranslationResult result = new() { RequestId = Guid.NewGuid(), Outcome = TranslationOutcome.Success, TranslatedText = "x" };

        cache.Set("a", result);
        time.Advance(TimeSpan.FromSeconds(1));
        cache.Set("b", result);
        cache.Set("c", result);
        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));

        time.Advance(TimeSpan.FromMinutes(31));
        Assert.False(cache.TryGet("c", out _));
    }

    [Fact]
    public void Breaker_opens_after_five_transient_failures_and_probes_once()
    {
        var time = new ManualTime();
        var breaker = new CircuitBreaker(time);
        for (int i = 0; i < 5; i++)
        {
            Assert.True(breaker.TryEnter(out _));
            breaker.Record(TranslationOutcome.Transient);
        }

        Assert.False(breaker.TryEnter(out TimeSpan retryIn));
        Assert.True(retryIn > TimeSpan.FromSeconds(29));

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(breaker.TryEnter(out _));
        Assert.False(breaker.TryEnter(out _));
        breaker.Record(TranslationOutcome.Success);
        Assert.True(breaker.TryEnter(out _));
    }

    [Fact]
    public void Failed_probe_reopens_the_breaker()
    {
        var time = new ManualTime();
        var breaker = new CircuitBreaker(time, threshold: 1);
        breaker.Record(TranslationOutcome.Transient);
        time.Advance(TimeSpan.FromSeconds(30));

        Assert.True(breaker.TryEnter(out _));
        breaker.Record(TranslationOutcome.Transient);

        Assert.False(breaker.TryEnter(out _));
    }

    [Fact]
    public void Rate_limiter_uses_a_one_minute_window()
    {
        var time = new ManualTime();
        var limiter = new RequestRateLimiter(time);

        Assert.True(limiter.TryAcquire(2, 1, out _));
        Assert.True(limiter.TryAcquire(2, 1, out _));
        Assert.False(limiter.TryAcquire(2, 1, out _));

        time.Advance(TimeSpan.FromSeconds(60));
        Assert.True(limiter.TryAcquire(2, 1, out _));
    }

    [Fact]
    public void Usage_estimate_follows_the_published_prices()
    {
        // 仕様書 17.4 の例: 入力 1000、出力 200 で 0.0002 米ドル
        Assert.Equal(0.0002m, OpenAIPricing.EstimateUsd(new TranslationUsage(1000, 0, 200, 0)));

        var meter = new UsageMeter();
        meter.Record(new TranslationResult { RequestId = Guid.NewGuid(), Outcome = TranslationOutcome.Success, Attempts = 2, Usage = new TranslationUsage(1000, 0, 200, 50) });
        meter.Record(new TranslationResult { RequestId = Guid.NewGuid(), Outcome = TranslationOutcome.Transient, Attempts = 1 });

        UsageSnapshot usage = meter.Snapshot;
        Assert.Equal((2, 3, 1, 1), (usage.Requests, usage.HttpAttempts, usage.Succeeded, usage.UnknownUsage));
        Assert.Equal(0.0002m, usage.EstimatedUsd);
    }

    private static TranslationRequest Request(string text) => new()
    {
        RequestId = Guid.NewGuid(),
        MessageId = Guid.NewGuid(),
        Revision = 1,
        Direction = TranslationDirection.EnJa,
        SourceText = text,
        GlossaryVersion = "v1",
        Deadline = TimeSpan.FromSeconds(4),
    };
}

/// <summary>手で進める時計。</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks = 1_000_000;

    public override long TimestampFrequency => Stopwatch.Frequency;

    public override long GetTimestamp() => _ticks;

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);

    public void Advance(TimeSpan by) => _ticks += (long)(by.TotalSeconds * Stopwatch.Frequency);
}
