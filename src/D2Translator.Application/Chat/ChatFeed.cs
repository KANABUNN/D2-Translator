using D2Translator.DictionaryContext;
using D2Translator.Domain.Chat;
using D2Translator.Domain.Ocr;
using D2Translator.TextTracking;
using D2Translator.TextTracking.Parsing;
using D2Translator.TextTracking.Speakers;
using D2Translator.TextTracking.Tracking;

namespace D2Translator.Application.Chat;

public sealed record ChatFeedUpdate(MessageUpdateKind Kind, ChatMessage Message);

/// <summary>
/// 受信フローの行追跡からメッセージ確定まで（仕様書 15.1 の 3〜4）。
/// 観測を発言候補に分け、追跡し、話者の一時 ID と辞書注釈を付けて通知する。翻訳は行わない。
/// </summary>
public sealed class ChatFeed(ChatLineParser parser, MessageTracker tracker, SpeakerRegistry speakers, GlossaryMatcher glossary)
{
    private readonly Lock _gate = new();

    /// <summary>利用者が選んだ活動。Unknown なら活動条件付きの語義を使わない（仕様書 9.2）。</summary>
    public string Activity { get; set; } = "Unknown";

    public string DictionaryVersion => glossary.DictionaryVersion;

    /// <summary>バックグラウンドスレッドから発生する。</summary>
    public event EventHandler<ChatFeedUpdate>? Updated;

    /// <summary>対象の変更等で追跡基準と話者の対応表を捨てる。</summary>
    public void Reset(Guid sessionId)
    {
        lock (_gate)
        {
            tracker.Reset(sessionId);
            speakers.Clear();
        }
    }

    /// <summary>停止からの再開。次の観測で見えている発言は新着にしない（仕様書 15.3）。</summary>
    public void Resync()
    {
        lock (_gate)
        {
            tracker.Resync();
        }
    }

    public void OnObservation(OcrObservation observation, long timestamp)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Publish(() =>
        {
            IReadOnlyList<ChatCandidate> candidates = parser.Parse(observation.Lines, observation.RoiBounds);
            foreach (ChatCandidate candidate in candidates.Where(c => c.Speaker is not null))
            {
                speakers.Register(candidate.Speaker!);
            }

            return tracker.Apply(candidates, observation.CapturedAt, timestamp);
        });
    }

    public void OnUnchanged(long timestamp) => Publish(() => tracker.ApplyUnchanged(timestamp));

    public void Tick(long timestamp) => Publish(() => tracker.Tick(timestamp));

    private void Publish(Func<IReadOnlyList<MessageUpdate>> step)
    {
        List<ChatFeedUpdate> updates;
        lock (_gate)
        {
            updates = [.. step().Select(u => new ChatFeedUpdate(u.Kind, Enrich(u.Message)))];
        }

        foreach (ChatFeedUpdate update in updates)
        {
            Updated?.Invoke(this, update);
        }
    }

    private ChatMessage Enrich(ChatMessage message)
    {
        string? speakerKey = message.SpeakerName is null ? null : speakers.Register(message.SpeakerName);
        if (message.Kind == ChatLineKind.System)
        {
            return message with { SpeakerKey = speakerKey };
        }

        IEnumerable<TextAnnotation> annotations = speakers.FindMentions(message.Text).Concat(glossary.Find(message.Text, Activity));
        return message with
        {
            SpeakerKey = speakerKey,
            Annotations = AnnotationSet.KeepLongestNonOverlapping(annotations),
        };
    }
}
