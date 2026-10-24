using D2Translator.Domain.Translation;

namespace D2Translator.Application.Translation;

/// <summary>
/// 一時的な失敗が続いたら外部要求を止める（仕様書 15.4）。連続 5 回で 30 秒遮断し、
/// 期限後は 1 要求だけで回復を確かめる。
/// </summary>
public sealed class CircuitBreaker(TimeProvider time, int threshold = 5, TimeSpan? openFor = null)
{
    private readonly TimeSpan _openFor = openFor ?? TimeSpan.FromSeconds(30);
    private readonly Lock _gate = new();
    private int _failures;
    private long? _openedAt;
    private bool _probing;

    /// <summary>遮断中なら false と再開までの残り時間を返す。</summary>
    public bool TryEnter(out TimeSpan retryIn)
    {
        lock (_gate)
        {
            retryIn = TimeSpan.Zero;
            if (_openedAt is not long opened)
            {
                return true;
            }

            TimeSpan elapsed = time.GetElapsedTime(opened);
            if (elapsed < _openFor)
            {
                retryIn = _openFor - elapsed;
                return false;
            }

            if (_probing)
            {
                retryIn = TimeSpan.FromSeconds(1);
                return false;
            }

            _probing = true;
            return true;
        }
    }

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _openedAt is long opened && time.GetElapsedTime(opened) < _openFor;
            }
        }
    }

    /// <summary>TryEnter で得た回復確認の枠を、要求を送らずに返す。</summary>
    public void Release()
    {
        lock (_gate)
        {
            _probing = false;
        }
    }

    public void Record(TranslationOutcome outcome)
    {
        lock (_gate)
        {
            if (outcome == TranslationOutcome.Success)
            {
                _failures = 0;
                _openedAt = null;
                _probing = false;
                return;
            }

            if (outcome != TranslationOutcome.Transient)
            {
                _probing = false;
                return;
            }

            _failures++;
            if (_probing || _failures >= threshold)
            {
                _openedAt = time.GetTimestamp();
                _probing = false;
            }
        }
    }
}

/// <summary>1 分あたりの要求回数の上限（translation.requestsPerMinute）。</summary>
public sealed class RequestRateLimiter(TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Queue<long> _issued = new();
    private readonly Lock _gate = new();

    public bool TryAcquire(int perMinute, int count, out TimeSpan retryIn)
    {
        lock (_gate)
        {
            long now = time.GetTimestamp();
            while (_issued.Count > 0 && time.GetElapsedTime(_issued.Peek(), now) >= Window)
            {
                _issued.Dequeue();
            }

            if (_issued.Count + count > perMinute)
            {
                retryIn = Window - time.GetElapsedTime(_issued.Peek(), now);
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                _issued.Enqueue(now);
            }

            retryIn = TimeSpan.Zero;
            return true;
        }
    }
}

public sealed record UsageSnapshot(
    int Requests,
    int HttpAttempts,
    int Succeeded,
    int CacheHits,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    int UnknownUsage,
    decimal EstimatedUsd);

/// <summary>
/// アプリの要求回数、API が返した利用量、推計額を分けて数える（仕様書 17.4）。
/// 利用量が返らなかった要求は無料とみなさず「料金未確定」として数える。
/// </summary>
public sealed class UsageMeter
{
    private readonly Lock _gate = new();
    private UsageSnapshot _current = new(0, 0, 0, 0, 0, 0, 0, 0, 0m);

    public UsageSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void RecordCacheHit()
    {
        lock (_gate)
        {
            _current = _current with { CacheHits = _current.CacheHits + 1 };
        }
    }

    public void Record(TranslationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            UsageSnapshot c = _current;
            TranslationUsage? usage = result.Usage;
            _current = c with
            {
                Requests = c.Requests + 1,
                HttpAttempts = c.HttpAttempts + result.Attempts,
                Succeeded = c.Succeeded + (result.IsSuccess ? 1 : 0),
                InputTokens = c.InputTokens + (usage?.InputTokens ?? 0),
                CachedInputTokens = c.CachedInputTokens + (usage?.CachedInputTokens ?? 0),
                OutputTokens = c.OutputTokens + (usage?.OutputTokens ?? 0),
                UnknownUsage = c.UnknownUsage + (usage is null && result.Attempts > 0 ? 1 : 0),
                EstimatedUsd = c.EstimatedUsd + (usage is null ? 0m : OpenAIPricing.EstimateUsd(usage)),
            };
        }
    }
}
