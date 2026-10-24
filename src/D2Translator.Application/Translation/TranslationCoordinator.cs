using System.Threading.Channels;
using D2Translator.Application.Chat;
using D2Translator.Domain.Chat;
using D2Translator.Domain.Errors;
using D2Translator.Domain.Settings;
using D2Translator.Domain.Translation;
using D2Translator.TextTracking.Tracking;
using Microsoft.Extensions.Logging;

namespace D2Translator.Application.Translation;

public enum TranslationServiceState
{
    /// <summary>クラウド翻訳が無効。外部へ何も送らない。</summary>
    Disabled,
    NoApiKey,
    Ready,

    /// <summary>連続した一時的な失敗で遮断中（TR03）。</summary>
    CircuitOpen,

    /// <summary>セッションの要求上限、または API の利用上限に到達（TR02）。</summary>
    LimitReached,

    /// <summary>認証失敗や gpt-6-luna を使えない（TR01、TR05）。利用者の操作まで再開しない。</summary>
    Stopped,
}

public enum TranslationStatus
{
    /// <summary>翻訳しない（クラウド無効、自分の発言等）。</summary>
    NotRequested,

    /// <summary>自動では送らない（開始前から表示、認識が不安定）。手動で翻訳できる。</summary>
    Held,
    Queued,
    Translating,
    Translated,

    /// <summary>訳はあるが、記号や数字の欠落等で確認が必要。</summary>
    NeedsReview,
    Failed,

    /// <summary>待ち時間の上限を超えて送らなかった。</summary>
    Expired,
}

/// <param name="IsStale">訂正前の原文に対する訳。</param>
/// <param name="CanRequest">利用者が手動で翻訳を依頼できる。</param>
public sealed record TranslationView(
    Guid MessageId,
    TranslationStatus Status,
    string? Text,
    string? Detail,
    IReadOnlyList<TranslationAmbiguity> Ambiguities,
    IReadOnlyList<string> Warnings,
    bool IsStale,
    bool CanRequest,
    ErrorCode? Error);

/// <summary>翻訳に関わる設定の写し。変更は <see cref="TranslationCoordinator.Configure"/> で反映する。</summary>
public sealed record TranslationOptions(
    bool CloudEnabled,
    bool ContextEnabled,
    string Activity,
    TimeSpan Deadline,
    int RequestsPerMinute,
    int SessionRequestCap,
    int MaxOutputTokens)
{
    public static TranslationOptions From(AppSettings settings) => new(
        settings.Translation.CloudEnabled,
        settings.Context.Enabled,
        settings.Context.Activity,
        TimeSpan.FromMilliseconds(settings.Translation.DeadlineMs),
        settings.Translation.RequestsPerMinute,
        settings.Translation.SessionRequestCap,
        settings.Translation.MaxOutputTokens);
}

/// <summary>
/// 受信翻訳の調停（仕様書 15.1 の 5〜6、15.4）。確定した他プレイヤーの発言を待ち行列へ入れ、
/// 期限、回数上限、遮断、キャッシュを適用して GPT-6 Luna へ送り、結果を検証して反映する。
/// 戻った結果は、セッション、メッセージ、revision が一致する時だけ適用する。
/// </summary>
public sealed class TranslationCoordinator : IAsyncDisposable
{
    public const int IncomingQueueCapacity = 20;
    public const int MaxConcurrentRequests = 2;
    public const int MaxAutoRetranslations = 1;
    public static readonly TimeSpan QueueExpiry = TimeSpan.FromSeconds(5);
    private const int HistoryLimit = 1000;
    private const string TestSentence = "LF2M for the raid, KWTD please. Need someone on adds.";

    private readonly ChatFeed _feed;
    private readonly ITranslationProvider _provider;
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _time;
    private readonly ILogger<TranslationCoordinator> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Record> _records = [];
    private readonly LinkedList<Guid> _order = new();
    private readonly Channel<WorkItem> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private readonly TranslationCache _cache;
    private readonly CircuitBreaker _breaker;
    private readonly RequestRateLimiter _rate;
    private readonly UsageMeter _usage = new();

    private TranslationOptions _options;
    private Guid _sessionId;
    private (TranslationServiceState State, string Detail)? _stopped;
    private int _sessionRequests;

    public TranslationCoordinator(ChatFeed feed, ITranslationProvider provider, ISecretStore secrets, TranslationOptions options,
        TimeProvider time, ILogger<TranslationCoordinator> logger)
    {
        _feed = feed;
        _provider = provider;
        _secrets = secrets;
        _options = options;
        _time = time;
        _logger = logger;
        _cache = new TranslationCache(time);
        _breaker = new CircuitBreaker(time);
        _rate = new RequestRateLimiter(time);
        _queue = Channel.CreateBounded<WorkItem>(
            new BoundedChannelOptions(IncomingQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest },
            dropped => Publish(Expire(dropped.MessageId, "待ち行列が上限に達したため送信しませんでした。")));
        _workers = [.. Enumerable.Range(0, MaxConcurrentRequests).Select(_ => Task.Run(WorkAsync))];
        feed.Updated += OnFeedUpdated;
        feed.SessionReset += OnSessionReset;
    }

    /// <summary>バックグラウンドスレッドから発生する。</summary>
    public event EventHandler<TranslationView>? Updated;

    public event EventHandler? StateChanged;

    public UsageSnapshot Usage => _usage.Snapshot;

    public TranslationOptions Options
    {
        get
        {
            lock (_gate)
            {
                return _options;
            }
        }
    }

    public (TranslationServiceState State, string Detail) Status
    {
        get
        {
            lock (_gate)
            {
                return ComputeState();
            }
        }
    }

    /// <summary>
    /// 設定の変更を反映する。利用者の操作なので、TR01 等による停止も解除する。
    /// 無効の間に届いた発言は一斉に送らず、手動で翻訳できる状態にする。
    /// </summary>
    public void Configure(TranslationOptions options)
    {
        var views = new List<TranslationView>();
        lock (_gate)
        {
            _options = options;
            _stopped = null;
            foreach (Record record in _records.Values.Where(r => r.Message.Kind == ChatLineKind.Player && r.View is not null))
            {
                bool notRequested = record.View!.Status == TranslationStatus.NotRequested;
                if (options.CloudEnabled && notRequested)
                {
                    views.Add(record.Set(TranslationStatus.Held, "クラウド翻訳を有効にする前の発言です。必要なら手動で翻訳できます。", canRequest: true));
                }
                else if (!options.CloudEnabled && record.View.CanRequest)
                {
                    views.Add(record.Set(record.View.Status, record.View.Detail, canRequest: false));
                }
            }
        }

        foreach (TranslationView view in views)
        {
            Publish(view);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>API キーの登録、差替え、削除の後に呼ぶ。</summary>
    public void OnApiKeyChanged() => Configure(Options);

    /// <summary>利用者の明示操作による翻訳（開始前から表示、要確認、失敗、期限切れ、訂正後の再翻訳）。</summary>
    public bool RequestManual(Guid messageId)
    {
        TranslationView? view;
        lock (_gate)
        {
            if (!_records.TryGetValue(messageId, out Record? record) || record.Message.Kind != ChatLineKind.Player)
            {
                return false;
            }

            view = TryEnqueue(record, manual: true);
        }

        Publish(view);
        return view?.Status == TranslationStatus.Queued;
    }

    /// <summary>
    /// 接続確認（仕様書 6.4）。外部送信を有効にした利用者の明示操作で、短い検証文を1回送る。API 利用料が生じ得る。
    /// </summary>
    public async Task<TranslationResult> TestConnectionAsync(CancellationToken ct)
    {
        TranslationOptions options;
        lock (_gate)
        {
            options = _options;
            if (!options.CloudEnabled)
            {
                throw new InvalidOperationException("クラウド翻訳が無効です。");
            }

            _stopped = null;
        }

        var request = new TranslationRequest
        {
            RequestId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            Revision = 1,
            Direction = TranslationDirection.EnJa,
            SourceText = TestSentence,
            GlossaryVersion = _feed.DictionaryVersion,
            Deadline = options.Deadline,
            MaxOutputTokens = options.MaxOutputTokens,
        };

        TranslationResult result = await _provider.TranslateAsync(request, ct).ConfigureAwait(false);
        _usage.Record(result);
        lock (_gate)
        {
            _sessionRequests++;
            ApplyServiceOutcome(result);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private void OnSessionReset(object? sender, Guid sessionId)
    {
        lock (_gate)
        {
            _sessionId = sessionId;
            _records.Clear();
            _order.Clear();
            _cache.Clear();
        }
    }

    private void OnFeedUpdated(object? sender, ChatFeedUpdate update)
    {
        ChatMessage message = update.Message;
        if (!message.IsConversation)
        {
            return;
        }

        TranslationView? view = null;
        lock (_gate)
        {
            if (update.Kind == MessageUpdateKind.Removed)
            {
                if (_records.Remove(message.MessageId))
                {
                    _order.Remove(message.MessageId);
                }

                return;
            }

            if (!_records.TryGetValue(message.MessageId, out Record? record))
            {
                record = new Record(message);
                _records[message.MessageId] = record;
                _order.AddLast(message.MessageId);
                while (_order.Count > HistoryLimit)
                {
                    _records.Remove(_order.First!.Value);
                    _order.RemoveFirst();
                }
            }

            record.Message = message;
            view = Decide(record, update.Kind);
        }

        Publish(view);
    }

    /// <summary>受信更新に対して自動翻訳するかを決める。</summary>
    private TranslationView? Decide(Record record, MessageUpdateKind kind)
    {
        ChatMessage message = record.Message;
        if (message.Kind == ChatLineKind.Own)
        {
            // 自分の発言は翻訳しない（決定 D20）
            return record.Set(TranslationStatus.NotRequested, "自分の発言", canRequest: false);
        }

        if (message.Status == MessageStatus.Recognizing)
        {
            return null;
        }

        if (kind == MessageUpdateKind.Revised)
        {
            switch (record.View?.Status)
            {
                case TranslationStatus.Queued or TranslationStatus.Translating:
                    // 送信前や送信中に原文が訂正された。古い revision の要求と結果は捨て、新しい revision で送り直す
                    return TryEnqueue(record, manual: false);
                case null or TranslationStatus.NotRequested or TranslationStatus.Held:
                    return Hold(record) ?? TryEnqueue(record, manual: false);
                case TranslationStatus.Failed or TranslationStatus.Expired:
                    return null;
            }

            // 翻訳後の訂正による自動の再翻訳は 1 メッセージ 1 回まで（仕様書 8.2）
            if (record.AutoRetranslations < MaxAutoRetranslations && message.Status == MessageStatus.GlossaryOnly && _options.CloudEnabled)
            {
                record.AutoRetranslations++;
                return TryEnqueue(record, manual: false);
            }

            return record.Set(record.View!.Status, "訂正前の原文に対する訳です。", canRequest: true, stale: true);
        }

        if (record.View is { Status: not TranslationStatus.NotRequested and not TranslationStatus.Held })
        {
            return null;
        }

        return Hold(record) ?? TryEnqueue(record, manual: false);
    }

    /// <summary>自動送信しない条件（仕様書 8.2、15.3）。該当しなければ null。</summary>
    private TranslationView? Hold(Record record)
    {
        ChatMessage message = record.Message;
        if (!_options.CloudEnabled)
        {
            return record.Set(TranslationStatus.NotRequested, null, canRequest: false);
        }

        if (message.WasAlreadyVisible)
        {
            return record.Set(TranslationStatus.Held, "開始前から表示されていた発言です。必要なら手動で翻訳できます。", canRequest: true);
        }

        if (message.Status == MessageStatus.NeedsReview)
        {
            return record.Set(TranslationStatus.Held, "認識が不安定なため自動送信を保留しました。必要なら手動で翻訳できます。", canRequest: true);
        }

        return null;
    }

    private TranslationView TryEnqueue(Record record, bool manual)
    {
        (TranslationServiceState state, string detail) = ComputeState();
        if (state is not (TranslationServiceState.Ready or TranslationServiceState.CircuitOpen))
        {
            return record.Set(state == TranslationServiceState.Disabled ? TranslationStatus.NotRequested : TranslationStatus.Failed,
                detail, canRequest: false, error: ErrorFor(state));
        }

        var item = new WorkItem(record.Message.MessageId, record.Message.Revision, _time.GetTimestamp(), manual);
        TranslationView view = record.Set(TranslationStatus.Queued, null, canRequest: false);
        // 満杯なら最も古い要求が期限切れになる
        _queue.Writer.TryWrite(item);
        return view;
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (WorkItem item in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(item).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Translation worker failed");
                    Publish(Fail(item, TranslationStatus.Failed, "翻訳処理で予期しないエラーが発生しました。", ErrorCode.TR03));
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessAsync(WorkItem item)
    {
        (Prepared? prepared, TranslationView? early, bool limitReached) = Prepare(item);
        Publish(early);
        if (limitReached)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        if (prepared is null)
        {
            return;
        }

        (TranslationRequest request, AnonymizedText anonymized, string cacheKey, Guid sessionId) = prepared;
        TranslationResult result = await _provider.TranslateAsync(request, _shutdown.Token).ConfigureAwait(false);
        _usage.Record(result);

        TranslationView? view;
        bool stateChanged;
        lock (_gate)
        {
            _breaker.Record(result.Outcome);
            stateChanged = ApplyServiceOutcome(result);
            if (sessionId != _sessionId || !IsCurrent(item, out Record? record))
            {
                // 古い世代や revision の結果は適用しない。課金が消えるとは扱わない（仕様書 15.3）
                view = null;
            }
            else if (result.IsSuccess)
            {
                _cache.Set(cacheKey, result);
                view = ApplySuccess(record, item, result, anonymized);
            }
            else
            {
                string detail = $"{result.Code} {result.Detail}";
                if (result.RetryAfter is TimeSpan retry)
                {
                    detail += $"（{Math.Ceiling(retry.TotalSeconds):0} 秒後に再開できます）";
                }

                view = record.Set(TranslationStatus.Failed, detail, canRequest: true, error: result.Code);
            }
        }

        if (stateChanged || _breaker.IsOpen)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        Publish(view);
    }

    /// <summary>
    /// 送信前の判定（期限切れ、サービス状態、遮断、キャッシュ、回数上限）。送るなら要求を返す。
    /// 通知はロックの外で行うため、表示の更新は戻り値で返す。
    /// </summary>
    private (Prepared? Prepared, TranslationView? View, bool LimitReached) Prepare(WorkItem item)
    {
        lock (_gate)
        {
            if (!IsCurrent(item, out Record? record))
            {
                return (null, null, false);
            }

            if (!item.Manual && _time.GetElapsedTime(item.EnqueuedAt) > QueueExpiry)
            {
                return (null, record.Set(TranslationStatus.Expired, "待ち時間が 5 秒を超えたため送信しませんでした。手動で翻訳できます。", canRequest: true), false);
            }

            (TranslationServiceState state, string detail) = ComputeState();
            if (state is not (TranslationServiceState.Ready or TranslationServiceState.CircuitOpen))
            {
                return (null, record.Set(TranslationStatus.Failed, detail, canRequest: false, error: ErrorFor(state)), false);
            }

            if (!_breaker.TryEnter(out TimeSpan reopenIn))
            {
                return (null, record.Set(TranslationStatus.Failed,
                    $"TR03 通信障害が続いたため外部送信を一時停止しています（あと {Math.Ceiling(reopenIn.TotalSeconds):0} 秒）。",
                    canRequest: true, error: ErrorCode.TR03), false);
            }

            AnonymizedText anonymized = Anonymizer.Anonymize(record.Message);
            TranslationRequest request = BuildRequest(record.Message, anonymized);
            string cacheKey = TranslationCache.KeyOf(request);
            if (_cache.TryGet(cacheKey, out TranslationResult cached))
            {
                _usage.RecordCacheHit();
                _breaker.Record(TranslationOutcome.Success);
                return (null, ApplySuccess(record, item, cached, anonymized), false);
            }

            if (_sessionRequests >= _options.SessionRequestCap)
            {
                _breaker.Release();
                _stopped = (TranslationServiceState.LimitReached, $"TR02 このセッションの要求上限 {_options.SessionRequestCap} 回に達しました。");
                return (null, record.Set(TranslationStatus.Failed, _stopped.Value.Detail, canRequest: false, error: ErrorCode.TR02), true);
            }

            if (!_rate.TryAcquire(_options.RequestsPerMinute, 1, out TimeSpan rateRetry))
            {
                _breaker.Release();
                return (null, record.Set(TranslationStatus.Failed,
                    $"TR02 1 分あたりの上限 {_options.RequestsPerMinute} 回に達しました（あと {Math.Ceiling(rateRetry.TotalSeconds):0} 秒）。",
                    canRequest: true, error: ErrorCode.TR02), false);
            }

            _sessionRequests++;
            return (new Prepared(request, anonymized, cacheKey, _sessionId), record.Set(TranslationStatus.Translating, null, canRequest: false), false);
        }
    }

    private TranslationRequest BuildRequest(ChatMessage message, AnonymizedText anonymized)
    {
        IReadOnlyList<ContextLine> context = _options.ContextEnabled
            ? ContextBuilder.Build(_order.Select(id => _records[id].Message), message)
            : [];
        GlossaryHint[] glossary = [.. message.Annotations
            .Where(a => a.Kind == AnnotationKind.Glossary && a.ReferenceId is not null)
            .Select(a => new GlossaryHint(a.ReferenceId!, a.Text, a.Note))
            .Take(50)];

        return new TranslationRequest
        {
            RequestId = Guid.NewGuid(),
            MessageId = message.MessageId,
            Revision = message.Revision,
            Direction = TranslationDirection.EnJa,
            SourceText = anonymized.Text,
            SpeakerKey = message.SpeakerKey,
            Glossary = glossary,
            Context = context,
            Activity = _options.Activity,
            GlossaryVersion = _feed.DictionaryVersion,
            Deadline = _options.Deadline,
            MaxOutputTokens = _options.MaxOutputTokens,
        };
    }

    private static TranslationView ApplySuccess(Record record, WorkItem item, TranslationResult result, AnonymizedText anonymized)
    {
        string translated = result.TranslatedText!;
        IReadOnlyList<string> problems = TranslationChecks.EnJa(anonymized.Text, translated);
        record.TranslatedRevision = item.Revision;
        return record.Set(
            problems.Count == 0 ? TranslationStatus.Translated : TranslationStatus.NeedsReview,
            problems.Count == 0 ? null : string.Join(" ", problems),
            canRequest: true,
            text: Anonymizer.Restore(translated, anonymized.Originals),
            ambiguities: result.Ambiguities,
            warnings: result.Warnings);
    }

    /// <summary>サービス全体を止める結果を反映する。状態が変わったら true。</summary>
    private bool ApplyServiceOutcome(TranslationResult result)
    {
        (TranslationServiceState, string)? stop = result.Outcome switch
        {
            TranslationOutcome.AuthenticationFailed => (TranslationServiceState.Stopped, $"TR01 {result.Detail} 設定画面で API キーを確認してください。"),
            TranslationOutcome.ModelUnavailable => (TranslationServiceState.Stopped, $"TR05 {result.Detail} 別のモデルへは切り替えません。接続設定を確認してください。"),
            TranslationOutcome.RateLimited when result.QuotaExhausted =>
                (TranslationServiceState.LimitReached, $"TR02 {result.Detail} OpenAI の契約と予算を確認してください。"),
            _ => null,
        };

        if (stop is null)
        {
            return false;
        }

        _stopped = stop;
        return true;
    }

    private (TranslationServiceState State, string Detail) ComputeState()
    {
        if (!_options.CloudEnabled)
        {
            return (TranslationServiceState.Disabled, "クラウド翻訳は無効です。");
        }

        if (!_secrets.Exists(Domain.Translation.OpenAIFixedProfile.SecretRef))
        {
            return (TranslationServiceState.NoApiKey, "TR01 OpenAI の API キーが登録されていません。");
        }

        if (_stopped is { } stopped)
        {
            return stopped;
        }

        if (_sessionRequests >= _options.SessionRequestCap)
        {
            return (TranslationServiceState.LimitReached, $"TR02 このセッションの要求上限 {_options.SessionRequestCap} 回に達しました。");
        }

        return _breaker.IsOpen
            ? (TranslationServiceState.CircuitOpen, "TR03 通信障害が続いたため一時停止しています。")
            : (TranslationServiceState.Ready, "有効");
    }

    private static ErrorCode? ErrorFor(TranslationServiceState state) => state switch
    {
        TranslationServiceState.NoApiKey => ErrorCode.TR01,
        TranslationServiceState.LimitReached => ErrorCode.TR02,
        TranslationServiceState.CircuitOpen => ErrorCode.TR03,
        _ => null,
    };

    private bool IsCurrent(WorkItem item, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Record? record) =>
        _records.TryGetValue(item.MessageId, out record) && record.Message.Revision == item.Revision;

    private TranslationView? Expire(Guid messageId, string detail)
    {
        lock (_gate)
        {
            return _records.TryGetValue(messageId, out Record? record)
                ? record.Set(TranslationStatus.Expired, detail, canRequest: true)
                : null;
        }
    }

    private TranslationView? Fail(WorkItem item, TranslationStatus status, string detail, ErrorCode code)
    {
        lock (_gate)
        {
            return _records.TryGetValue(item.MessageId, out Record? record)
                ? record.Set(status, detail, canRequest: true, error: code)
                : null;
        }
    }

    private void Publish(TranslationView? view)
    {
        if (view is not null)
        {
            Updated?.Invoke(this, view);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _feed.Updated -= OnFeedUpdated;
        _feed.SessionReset -= OnSessionReset;
        _queue.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_workers).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private sealed record WorkItem(Guid MessageId, int Revision, long EnqueuedAt, bool Manual);

    private sealed record Prepared(TranslationRequest Request, AnonymizedText Anonymized, string CacheKey, Guid SessionId);

    private sealed class Record(ChatMessage message)
    {
        public ChatMessage Message { get; set; } = message;

        public TranslationView? View { get; private set; }

        public int TranslatedRevision { get; set; }

        public int AutoRetranslations { get; set; }

        public TranslationView Set(TranslationStatus status, string? detail, bool canRequest, ErrorCode? error = null, bool stale = false,
            string? text = null, IReadOnlyList<TranslationAmbiguity>? ambiguities = null, IReadOnlyList<string>? warnings = null)
        {
            // 訳文は新しい訳が出るまで残し、状態だけを更新する
            View = new TranslationView(
                Message.MessageId,
                status,
                text ?? View?.Text,
                detail,
                ambiguities ?? View?.Ambiguities ?? [],
                warnings ?? View?.Warnings ?? [],
                stale || (text is null && View?.IsStale == true),
                canRequest,
                error);
            return View;
        }
    }
}
