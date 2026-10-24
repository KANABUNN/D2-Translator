using D2Translator.Domain.Chat;
using D2Translator.Domain.Ocr;
using D2Translator.TextTracking.Parsing;
using D2Translator.TextTracking.Text;

namespace D2Translator.TextTracking.Tracking;

public enum MessageUpdateKind
{
    /// <summary>新しい発言を見つけた。確定前（Recognizing）か、追跡開始時に表示済みだったもの。</summary>
    Added,

    /// <summary>確定した（GlossaryOnly または NeedsReview）。</summary>
    Confirmed,

    /// <summary>確定後の認識の訂正。Revision が増える。</summary>
    Revised,

    /// <summary>確定前に消えた誤検出。</summary>
    Removed,
}

public sealed record MessageUpdate(MessageUpdateKind Kind, ChatMessage Message);

public sealed record MessageTrackerOptions
{
    /// <summary>この間隔以上離れた2観測で本文が同じなら確定する（仕様書 8.2 の 4）。</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>初回観測からこの時間で安定しなければ要確認として確定する。</summary>
    public TimeSpan ReviewAfter { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>本文がこの類似度以上なら同じ発言とみなす。</summary>
    public double BodySimilarity { get; init; } = 0.75;

    /// <summary>この文字数以下の短い本文は完全一致でだけ同じとみなす（例: y と n）。</summary>
    public int ShortBodyLength { get; init; } = 3;

    /// <summary>確定後に訂正を受け付ける回数。</summary>
    public int MaxRevisions { get; init; } = 3;

    /// <summary>照合に使う直近の発言数。</summary>
    public int TrackingWindow { get; init; } = 60;
}

/// <summary>
/// 観測ごとの発言候補を前回までの発言に対応付け、新しい発言と訂正を見つける（仕様書 8.2、8.3）。
/// </summary>
/// <remarks>
/// 画面上の順序を保ったまま最長の対応を取り、対応した最後の発言より下に現れた候補を新着とする。
/// そのため、上へ移動した行は同じ発言、新しく下端に現れた同文は新しい発言になる。
/// 対応が1つもない観測（画面の消去、ROI 再設定等）は基準の取り直しとし、新着として扱わない。
/// </remarks>
public sealed class MessageTracker(TimeProvider time, MessageTrackerOptions? options = null)
{
    private readonly MessageTrackerOptions _options = options ?? new MessageTrackerOptions();
    private readonly List<Entry> _entries = [];
    private Guid _sessionId = Guid.NewGuid();
    private long _sequence;
    private bool _resync;

    public Guid SessionId => _sessionId;

    /// <summary>対象変更等で追跡基準を捨てる（仕様書 8.3）。</summary>
    public void Reset(Guid sessionId)
    {
        _sessionId = sessionId;
        _sequence = 0;
        _entries.Clear();
        _resync = false;
    }

    /// <summary>
    /// 停止からの再開。次の観測で既存の発言との対応は保ち、停止中に増えた発言は新着ではなく表示済みとする（仕様書 15.3）。
    /// </summary>
    public void Resync() => _resync = true;

    public IReadOnlyList<MessageUpdate> Apply(IReadOnlyList<ChatCandidate> candidates, DateTimeOffset observedAt, long timestamp)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var updates = new List<MessageUpdate>();
        if (candidates.Count == 0)
        {
            // チャットが消えている（フェード等）。追跡基準は残す
            ExpirePending(timestamp, updates);
            return updates;
        }

        List<Entry> window = _entries.TakeLast(_options.TrackingWindow).ToList();
        List<(int Entry, int Candidate)> pairs = Align(window, candidates);
        bool resync = _resync;
        _resync = false;

        if (pairs.Count == 0)
        {
            // 最初の観測、または画面全体が入れ替わった。表示中の発言は新着として扱わない
            foreach (Entry pending in window.Where(e => e.IsPending).ToList())
            {
                Remove(pending, updates);
            }

            foreach (ChatCandidate candidate in candidates)
            {
                Entry entry = CreateEntry(candidate, observedAt, timestamp, alreadyVisible: true);
                updates.Add(new MessageUpdate(MessageUpdateKind.Added, entry.Message));
            }

            return updates;
        }

        var matchedEntries = new HashSet<Entry>();
        foreach ((int e, int c) in pairs)
        {
            Observe(window[e], candidates[c], timestamp, updates);
            matchedEntries.Add(window[e]);
        }

        // 対応の間で数が一致する未対応は、読み違いとして位置で対応付ける
        for (int i = 0; i + 1 < pairs.Count; i++)
        {
            int entryGap = pairs[i + 1].Entry - pairs[i].Entry - 1;
            int candidateGap = pairs[i + 1].Candidate - pairs[i].Candidate - 1;
            if (entryGap > 0 && entryGap == candidateGap)
            {
                for (int k = 1; k <= entryGap; k++)
                {
                    Entry entry = window[pairs[i].Entry + k];
                    if (entry.Kind == candidates[pairs[i].Candidate + k].Kind)
                    {
                        Observe(entry, candidates[pairs[i].Candidate + k], timestamp, updates);
                        matchedEntries.Add(entry);
                    }
                }
            }
        }

        // 確定前に見えなくなった候補は誤検出として数える
        foreach (Entry pending in window.Where(e => e.IsPending && !matchedEntries.Contains(e)).ToList())
        {
            if (++pending.Misses >= 2)
            {
                Remove(pending, updates);
            }
        }

        // 最後に対応した発言より下にある候補が新着
        int lastMatched = pairs[^1].Candidate;
        for (int c = lastMatched + 1; c < candidates.Count; c++)
        {
            Entry entry = CreateEntry(candidates[c], observedAt, timestamp, alreadyVisible: resync);
            updates.Add(new MessageUpdate(MessageUpdateKind.Added, entry.Message));
        }

        ExpirePending(timestamp, updates);
        return updates;
    }

    /// <summary>画面が前回の観測から変わっていない。前回の候補をもう一度見たものとして扱う。</summary>
    public IReadOnlyList<MessageUpdate> ApplyUnchanged(long timestamp)
    {
        var updates = new List<MessageUpdate>();
        foreach (Entry entry in _entries.Where(e => e.IsPending).ToList())
        {
            Observe(entry, entry.LastCandidate, timestamp, updates);
        }

        ExpirePending(timestamp, updates);
        return updates;
    }

    /// <summary>観測がなくても時間で要確認へ進める。</summary>
    public IReadOnlyList<MessageUpdate> Tick(long timestamp)
    {
        var updates = new List<MessageUpdate>();
        ExpirePending(timestamp, updates);
        return updates;
    }

    private Entry CreateEntry(ChatCandidate candidate, DateTimeOffset observedAt, long timestamp, bool alreadyVisible)
    {
        var message = new ChatMessage
        {
            MessageId = Guid.NewGuid(),
            SessionId = _sessionId,
            Sequence = ++_sequence,
            Kind = candidate.Kind,
            Channel = candidate.Channel,
            ChannelLabel = candidate.ChannelLabel,
            SpeakerName = candidate.Speaker,
            RawText = candidate.Body,
            ObservedAt = observedAt,
            Status = !alreadyVisible ? MessageStatus.Recognizing
                : candidate.IsUncertain ? MessageStatus.NeedsReview
                : MessageStatus.GlossaryOnly,
            Quality = alreadyVisible && candidate.IsUncertain ? RecognitionQuality.NeedsReview : RecognitionQuality.Good,
            WasAlreadyVisible = alreadyVisible,
            Bounds = candidate.Bounds,
        };
        var entry = new Entry(message, candidate, timestamp) { IsPending = !alreadyVisible };
        _entries.Add(entry);
        if (_entries.Count > _options.TrackingWindow * 4)
        {
            _entries.RemoveRange(0, _entries.Count - _options.TrackingWindow * 2);
        }

        return entry;
    }

    private void Observe(Entry entry, ChatCandidate candidate, long timestamp, List<MessageUpdate> updates)
    {
        // 対応付けは近似でよいが、確定と訂正は同じ読みが続いた時だけにする
        string body = candidate.Body;
        entry.Misses = 0;
        if (entry.IsPending)
        {
            if (SameReading(entry.LastCandidate.Body, body))
            {
                if (time.GetElapsedTime(entry.LastChangedAt, timestamp) >= _options.StableAfter)
                {
                    Confirm(entry, RecognitionQuality.Good, updates);
                }
            }
            else
            {
                // 確定前の揺れは原文を差し替える
                entry.LastCandidate = candidate;
                entry.LastChangedAt = timestamp;
                entry.Message = entry.Message with { RawText = body, SpeakerName = candidate.Speaker ?? entry.Message.SpeakerName };
            }

            return;
        }

        entry.LastCandidate = candidate;
        if (SameReading(entry.Message.Text, body))
        {
            entry.Alternative = null;
            return;
        }

        if (entry.Alternative is not null && SameReading(entry.Alternative, body))
        {
            if (time.GetElapsedTime(entry.AlternativeSince, timestamp) >= _options.StableAfter &&
                entry.Message.Revision <= _options.MaxRevisions)
            {
                entry.Message = entry.Message with { CorrectedText = body, Revision = entry.Message.Revision + 1 };
                entry.Alternative = null;
                updates.Add(new MessageUpdate(MessageUpdateKind.Revised, entry.Message));
            }

            return;
        }

        entry.Alternative = body;
        entry.AlternativeSince = timestamp;
    }

    private void ExpirePending(long timestamp, List<MessageUpdate> updates)
    {
        foreach (Entry entry in _entries.Where(e => e.IsPending).ToList())
        {
            if (time.GetElapsedTime(entry.FirstSeenAt, timestamp) >= _options.ReviewAfter)
            {
                Confirm(entry, RecognitionQuality.NeedsReview, updates);
            }
        }
    }

    private static void Confirm(Entry entry, RecognitionQuality quality, List<MessageUpdate> updates)
    {
        // 話者名と本文の境界を推定した発言は、安定していても要確認にする（OCR02）
        if (entry.LastCandidate.IsUncertain)
        {
            quality = RecognitionQuality.NeedsReview;
        }

        entry.IsPending = false;
        entry.Message = entry.Message with
        {
            Status = quality == RecognitionQuality.Good ? MessageStatus.GlossaryOnly : MessageStatus.NeedsReview,
            Quality = quality,
        };
        updates.Add(new MessageUpdate(MessageUpdateKind.Confirmed, entry.Message));
    }

    private void Remove(Entry entry, List<MessageUpdate> updates)
    {
        _entries.Remove(entry);
        updates.Add(new MessageUpdate(MessageUpdateKind.Removed, entry.Message));
    }

    /// <summary>
    /// 画面上の順序を保つ最長の対応。同点なら古い発言を飛ばし、候補は上から順に対応させる。
    /// </summary>
    private List<(int Entry, int Candidate)> Align(List<Entry> entries, IReadOnlyList<ChatCandidate> candidates)
    {
        int n = entries.Count, m = candidates.Count;
        int[,] lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = IsSame(entries[i], candidates[j])
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var pairs = new List<(int, int)>();
        int a = 0, b = 0;
        while (a < n && b < m)
        {
            if (IsSame(entries[a], candidates[b]) && lcs[a, b] == lcs[a + 1, b + 1] + 1)
            {
                pairs.Add((a, b));
                a++;
                b++;
            }
            else if (lcs[a + 1, b] >= lcs[a, b + 1])
            {
                a++;
            }
            else
            {
                b++;
            }
        }

        return pairs;
    }

    private bool IsSame(Entry entry, ChatCandidate candidate)
    {
        if (entry.Kind != candidate.Kind)
        {
            return false;
        }

        string? speaker = entry.LastCandidate.Speaker;
        if (speaker is not null && candidate.Speaker is not null &&
            TextUtil.Similarity(TextUtil.MatchKey(speaker), TextUtil.MatchKey(candidate.Speaker)) < 0.7)
        {
            return false;
        }

        return SameText(entry.LastCandidate.Body, candidate.Body) || SameText(entry.Message.Text, candidate.Body);
    }

    private static bool SameReading(string a, string b) =>
        string.Equals(TextUtil.Normalize(a), TextUtil.Normalize(b), StringComparison.Ordinal);

    private bool SameText(string a, string b)
    {
        string x = TextUtil.MatchKey(a), y = TextUtil.MatchKey(b);
        if (x.Length <= _options.ShortBodyLength || y.Length <= _options.ShortBodyLength)
        {
            return x == y;
        }

        return TextUtil.Similarity(x, y) >= _options.BodySimilarity;
    }

    private sealed class Entry(ChatMessage message, ChatCandidate candidate, long timestamp)
    {
        public ChatMessage Message { get; set; } = message;

        public ChatLineKind Kind => Message.Kind;

        public ChatCandidate LastCandidate { get; set; } = candidate;

        public long FirstSeenAt { get; } = timestamp;

        public long LastChangedAt { get; set; } = timestamp;

        public bool IsPending { get; set; }

        public int Misses { get; set; }

        public string? Alternative { get; set; }

        public long AlternativeSince { get; set; }
    }
}
