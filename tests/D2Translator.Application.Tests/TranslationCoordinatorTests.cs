using System.Collections.Concurrent;
using System.Diagnostics;
using D2Translator.Application.Chat;
using D2Translator.Application.Translation;
using D2Translator.Domain.Errors;
using D2Translator.Domain.Geometry;
using D2Translator.Domain.Ocr;
using D2Translator.Domain.Translation;
using Microsoft.Extensions.Logging.Abstractions;

namespace D2Translator.Application.Tests;

internal sealed class FakeTranslator : ITranslationProvider
{
    public ConcurrentQueue<TranslationRequest> Requests { get; } = new();

    public Func<TranslationRequest, TranslationResult> Respond { get; set; } = r => Success(r, r.SourceText.Replace("need adds cleared", "雑魚を片付けて", StringComparison.Ordinal));

    public SemaphoreSlim? Gate { get; set; }

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        if (Gate is not null)
        {
            await Gate.WaitAsync(ct);
        }

        return Respond(request);
    }

    public static TranslationResult Success(TranslationRequest request, string text) => new()
    {
        RequestId = request.RequestId,
        Outcome = TranslationOutcome.Success,
        TranslatedText = text,
        ResponseModel = OpenAIFixedProfile.ModelId,
        Attempts = 1,
        Usage = new TranslationUsage(100, 0, 20, 0),
    };
}

internal sealed class MemorySecrets(bool hasKey = true) : ISecretStore
{
    public bool HasKey { get; set; } = hasKey;

    public bool Exists(string secretRef) => HasKey;

    public Task<SecretLease?> OpenAsync(string secretRef, CancellationToken ct) => Task.FromResult(HasKey ? new SecretLease("k") : null);

    public Task SaveAsync(string secretRef, string value, CancellationToken ct) => Task.CompletedTask;

    public Task DeleteAsync(string secretRef, CancellationToken ct) => Task.CompletedTask;
}

public sealed class TranslationCoordinatorTests : IAsyncLifetime
{
    private static readonly PixelRect Roi = new(0, 0, 500, 300);
    private readonly ChatFeed _feed = TestPipelines.NewFeed();
    private readonly FakeTranslator _translator = new();
    private readonly ConcurrentQueue<TranslationView> _views = new();
    private TranslationCoordinator? _coordinator;

    private static TranslationOptions Options(bool cloud = true, int cap = 1000) =>
        new(cloud, ContextEnabled: false, "Unknown", TimeSpan.FromSeconds(4), RequestsPerMinute: 60, SessionRequestCap: cap, MaxOutputTokens: 1024);

    private TranslationCoordinator Start(TranslationOptions? options = null, ISecretStore? secrets = null)
    {
        _coordinator = new TranslationCoordinator(_feed, _translator, secrets ?? new MemorySecrets(), options ?? Options(),
            TimeProvider.System, NullLogger<TranslationCoordinator>.Instance);
        _coordinator.Updated += (_, v) => _views.Enqueue(v);
        _feed.Reset(Guid.NewGuid());
        return _coordinator;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync();
        }
    }

    private static long Ms(int milliseconds) => milliseconds * Stopwatch.Frequency / 1000;

    private static OcrObservation Observation(params string[] rows) => new()
    {
        ObservationId = Guid.NewGuid(),
        FrameSequence = 1,
        CapturedAt = DateTimeOffset.UtcNow,
        RoiBounds = Roi,
        Lines = [.. rows.Select((text, i) => new OcrLine(text, new PixelRect(10, i * 24, 200, 20), []))],
    };

    /// <summary>開始前から見えている行を置き、その下に新しい行を出して確定させる。</summary>
    private void ShowNew(int at, string[] visible, string newRow)
    {
        _feed.OnObservation(Observation([.. visible, newRow]), Ms(at));
        _feed.OnUnchanged(Ms(at + 300));
    }

    private async Task<TranslationView> WaitFor(Func<TranslationView, bool> predicate)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            TranslationView? match = _views.LastOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("期待した翻訳の状態になりませんでした: " + string.Join(" / ", _views.Select(v => $"{v.Status}:{v.Detail}")));
    }

    private const string Baseline = "[Kestrel#5678]: hello there";
    private const string Request = "[Pancake916#1234]: Kestrel need adds cleared";

    [Fact]
    public async Task Cloud_disabled_sends_nothing()
    {
        TranslationCoordinator coordinator = Start(Options(cloud: false));
        _feed.OnObservation(Observation(Baseline), Ms(0));
        ShowNew(1000, [Baseline], Request);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Empty(_translator.Requests);
        Assert.Equal(TranslationServiceState.Disabled, coordinator.Status.State);
        Assert.All(_views, v => Assert.Equal(TranslationStatus.NotRequested, v.Status));
    }

    [Fact]
    public async Task New_message_is_translated_with_names_masked_and_restored()
    {
        Start();
        _feed.OnObservation(Observation(Baseline), Ms(0));
        ShowNew(1000, [Baseline], Request);

        TranslationView view = await WaitFor(v => v.Status == TranslationStatus.Translated);

        TranslationRequest sent = Assert.Single(_translator.Requests);
        Assert.Equal("⟦P1⟧ need adds cleared", sent.SourceText);
        Assert.Equal("P2", sent.SpeakerKey);
        Assert.Contains(sent.Glossary, g => g.TermId == "d2.combat.adds");
        Assert.Equal("Kestrel 雑魚を片付けて", view.Text);
        // 開始前から見えていた行は自動では送らない
        Assert.Contains(_views, v => v.Status == TranslationStatus.Held && v.CanRequest);
    }

    [Fact]
    public async Task Preexisting_message_can_be_translated_manually()
    {
        Start();
        _feed.OnObservation(Observation(Request), Ms(0));
        TranslationView held = await WaitFor(v => v.Status == TranslationStatus.Held);

        Assert.True(_coordinator!.RequestManual(held.MessageId));
        await WaitFor(v => v.Status == TranslationStatus.Translated);
        Assert.Single(_translator.Requests);
    }

    [Fact]
    public async Task Own_messages_are_not_translated()
    {
        Start();
        _feed.OnObservation(Observation(Baseline), Ms(0));
        ShowNew(1000, [Baseline], "[ファイアチーム]宛て: on my way");
        await WaitFor(v => v.Status == TranslationStatus.NotRequested);

        Assert.Empty(_translator.Requests);
    }

    [Fact]
    public async Task Missing_api_key_reports_tr01_without_sending()
    {
        TranslationCoordinator coordinator = Start(secrets: new MemorySecrets(hasKey: false));
        _feed.OnObservation(Observation(Baseline), Ms(0));
        ShowNew(1000, [Baseline], Request);

        TranslationView view = await WaitFor(v => v.Status == TranslationStatus.Failed);

        Assert.Equal(ErrorCode.TR01, view.Error);
        Assert.Equal(TranslationServiceState.NoApiKey, coordinator.Status.State);
        Assert.Empty(_translator.Requests);
    }

    [Fact]
    public async Task Authentication_failure_stops_further_requests_until_reconfigured()
    {
        _translator.Respond = r => new TranslationResult { RequestId = r.RequestId, Outcome = TranslationOutcome.AuthenticationFailed, Attempts = 1, Detail = "401" };
        TranslationCoordinator coordinator = Start();
        _feed.OnObservation(Observation(Baseline), Ms(0));
        ShowNew(1000, [Baseline], Request);
        await WaitFor(v => v.Error == ErrorCode.TR01);

        ShowNew(2000, [Baseline, Request], "[Pancake916#1234]: anyone there");
        await WaitFor(v => v.Status == TranslationStatus.Failed && v.Detail?.StartsWith("TR01", StringComparison.Ordinal) == true && !v.CanRequest);

        Assert.Equal(TranslationServiceState.Stopped, coordinator.Status.State);
        Assert.Single(_translator.Requests);

        coordinator.Configure(Options());
        Assert.Equal(TranslationServiceState.Ready, coordinator.Status.State);
    }

    [Fact]
    public async Task Identical_text_is_served_from_cache()
    {
        TranslationCoordinator coordinator = Start();
        _feed.OnObservation(Observation(Baseline), Ms(0));
        ShowNew(1000, [Baseline], Request);
        await WaitFor(v => v.Status == TranslationStatus.Translated);

        ShowNew(2000, [Baseline, Request], Request);
        await WaitFor(v => v.Status == TranslationStatus.Translated && _views.Count(x => x.Status == TranslationStatus.Translated) == 2);

        Assert.Single(_translator.Requests);
        Assert.Equal(1, coordinator.Usage.CacheHits);
    }

    [Fact]
    public async Task Result_for_an_outdated_revision_is_discarded()
    {
        _translator.Gate = new SemaphoreSlim(0);
        Start();
        _feed.OnObservation(Observation(Baseline), Ms(0));
        ShowNew(1000, [Baseline], "[Pancake916#1234]: Kestrel need adds clearde");
        await WaitFor(v => v.Status == TranslationStatus.Translating);

        // 送信中に OCR の読みが訂正された
        const string corrected = "[Pancake916#1234]: Kestrel need adds cleared";
        _feed.OnObservation(Observation(Baseline, corrected), Ms(2000));
        _feed.OnObservation(Observation(Baseline, corrected), Ms(2300));
        _translator.Gate.Release(2);

        TranslationView view = await WaitFor(v => v.Status == TranslationStatus.Translated);

        Assert.Equal([1, 2], _translator.Requests.Select(r => r.Revision));
        Assert.Equal("Kestrel 雑魚を片付けて", view.Text);
        Assert.Single(_views, v => v.Status == TranslationStatus.Translated);
    }

    [Fact]
    public async Task Session_request_cap_stops_external_requests()
    {
        TranslationCoordinator coordinator = Start(Options(cap: 1));
        _feed.OnObservation(Observation(Baseline), Ms(0));
        ShowNew(1000, [Baseline], Request);
        await WaitFor(v => v.Status == TranslationStatus.Translated);

        ShowNew(2000, [Baseline, Request], "[Pancake916#1234]: one more please");
        TranslationView view = await WaitFor(v => v.Error == ErrorCode.TR02);

        Assert.Equal(TranslationServiceState.LimitReached, coordinator.Status.State);
        Assert.Single(_translator.Requests);
        Assert.False(view.CanRequest);
    }

    [Fact]
    public async Task Reply_masks_names_in_japanese_text_and_restores_them()
    {
        _translator.Respond = r => FakeTranslator.Success(r, "Thanks ⟦P1⟧, let's go with 2 more");
        TranslationCoordinator coordinator = Start();
        _feed.OnObservation(Observation(Baseline), Ms(0));

        ReplyTranslation reply = await coordinator.TranslateReplyAsync("Kestrelさん、ありがとう。あと2人で行こう",
            Domain.Settings.SendStyle.Short, useContext: false, TestContext.Current.CancellationToken);

        TranslationRequest sent = Assert.Single(_translator.Requests);
        Assert.Equal((TranslationDirection.JaEn, Domain.Settings.SendStyle.Short), (sent.Direction, sent.Style));
        Assert.Equal("⟦P1⟧さん、ありがとう。あと2人で行こう", sent.SourceText);
        Assert.True(reply.IsSuccess);
        Assert.Equal("Thanks Kestrel, let's go with 2 more", reply.Text);
        Assert.Empty(reply.Problems);
    }

    [Fact]
    public async Task Reply_is_not_sent_when_cloud_translation_is_disabled()
    {
        TranslationCoordinator coordinator = Start(Options(cloud: false));

        ReplyTranslation reply = await coordinator.TranslateReplyAsync("ありがとう", Domain.Settings.SendStyle.Neutral, false, TestContext.Current.CancellationToken);

        Assert.False(reply.IsSuccess);
        Assert.Empty(_translator.Requests);
    }

    [Fact]
    public async Task Translation_that_drops_a_name_token_needs_review()
    {
        _translator.Respond = r => FakeTranslator.Success(r, "雑魚を片付けて");
        Start();
        _feed.OnObservation(Observation(Baseline), Ms(0));
        ShowNew(1000, [Baseline], Request);

        TranslationView view = await WaitFor(v => v.Status == TranslationStatus.NeedsReview);

        Assert.Contains("⟦P1⟧", view.Detail, StringComparison.Ordinal);
    }
}
