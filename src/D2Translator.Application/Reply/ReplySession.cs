using System.Text.RegularExpressions;

namespace D2Translator.Application.Reply;

/// <summary>英訳候補の状態（仕様書 10.1）。状態名は「コピー済み」までで、ゲームへの送信済みは推測しない。</summary>
public enum CandidateState
{
    None,
    Translating,
    Ready,

    /// <summary>利用者が英訳を手で直した。</summary>
    Edited,
    Copied,
    Failed,
}

/// <param name="Text">コピーする文字列。改行は空白に置き換え済み。</param>
/// <param name="Notices">コピー前に利用者へ示すこと（改行の置換等）。</param>
/// <param name="LooksLikeCommand">先頭が / でゲーム内コマンドとして扱われる可能性がある。確認してからコピーする。</param>
public sealed record CopyPlan(string Text, IReadOnlyList<string> Notices, bool LooksLikeCommand);

/// <summary>
/// 返信の下書きと英訳候補（仕様書 10.1、13.2 OutgoingDraft、15.2）。
/// 翻訳要求時の下書き revision と現在値が違えば、古い結果で新しい下書きを上書きしない。
/// </summary>
public sealed partial class ReplySession
{
    /// <summary>下書きのアプリ上限（Unicode スカラー値、仕様書 10.2）。</summary>
    public const int MaxDraftScalars = 1000;

    [GeneratedRegex(@"[ \t]*[\r\n]+[ \t]*", RegexOptions.CultureInvariant, 50)]
    private static partial Regex LineBreaks();

    public Guid DraftId { get; } = Guid.NewGuid();

    public string Draft { get; private set; } = string.Empty;

    public int Revision { get; private set; }

    public string Candidate { get; private set; } = string.Empty;

    public int CandidateRevision { get; private set; }

    public CandidateState State { get; private set; }

    /// <summary>下書きを変えた後に戻った英訳。明示的に選ばれるまで候補にしない。</summary>
    public (string Text, int Revision)? LateCandidate { get; private set; }

    public int DraftLength => Draft.EnumerateRunes().Count();

    public int CandidateLength => Candidate.EnumerateRunes().Count();

    public bool CanTranslate => Draft.Trim().Length > 0 && DraftLength <= MaxDraftScalars && State != CandidateState.Translating;

    /// <summary>候補が下書きより古い（下書きを変えた後に翻訳していない）。</summary>
    public bool IsCandidateOutdated => Candidate.Length > 0 && CandidateRevision != Revision;

    public void UpdateDraft(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text == Draft)
        {
            return;
        }

        Draft = text;
        Revision++;
    }

    /// <summary>翻訳を始める。戻り値の revision を結果に添えて返す。</summary>
    public int BeginTranslation()
    {
        if (!CanTranslate)
        {
            throw new InvalidOperationException("翻訳できる下書きがありません。");
        }

        State = CandidateState.Translating;
        LateCandidate = null;
        return Revision;
    }

    /// <summary>英訳の結果を反映する。下書きが変わっていたら候補にせず false を返す。</summary>
    public bool ApplyTranslation(int revision, string? english)
    {
        if (english is null)
        {
            State = Candidate.Length > 0 ? CandidateState.Ready : CandidateState.Failed;
            return false;
        }

        if (revision != Revision)
        {
            LateCandidate = (english, revision);
            State = Candidate.Length > 0 ? CandidateState.Ready : CandidateState.None;
            return false;
        }

        Candidate = english;
        CandidateRevision = revision;
        State = CandidateState.Ready;
        return true;
    }

    /// <summary>下書きを変える前の入力に対する英訳を、利用者が明示的に選んだ。</summary>
    public void AcceptLateCandidate()
    {
        if (LateCandidate is not { } late)
        {
            return;
        }

        Candidate = late.Text;
        CandidateRevision = late.Revision;
        LateCandidate = null;
        State = CandidateState.Ready;
    }

    public void EditCandidate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text == Candidate)
        {
            return;
        }

        Candidate = text;
        State = text.Length == 0 ? CandidateState.None : CandidateState.Edited;
    }

    /// <summary>ゲームは改行を受け付けない（Phase 1 の実機確認）ため、空白へ置き換えてコピーする。</summary>
    public CopyPlan PrepareCopy()
    {
        var notices = new List<string>();
        string text = Candidate.Trim();
        if (LineBreaks().IsMatch(text))
        {
            text = LineBreaks().Replace(text, " ");
            notices.Add("ゲームのチャットは改行を送れないため、改行を空白に置き換えました。");
        }

        if (IsCandidateOutdated)
        {
            notices.Add("下書きを変えた後に翻訳していません。コピーするのは変える前の英訳です。");
        }

        return new CopyPlan(text, notices, text.StartsWith('/'));
    }

    public void MarkCopied() => State = CandidateState.Copied;

    public void Clear()
    {
        Draft = string.Empty;
        Revision++;
        Candidate = string.Empty;
        CandidateRevision = 0;
        LateCandidate = null;
        State = CandidateState.None;
    }
}

/// <summary>仕様書 14.1 のクリップボード境界。他アプリの内容を読み出さない。</summary>
public interface IClipboardService
{
    Task<CopyReceipt> CopyAsync(string text, CancellationToken ct);
}

public sealed record CopyReceipt(bool Success, string? Error, DateTimeOffset At, bool AutoClearScheduled);
