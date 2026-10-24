using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D2Translator.App.Services;
using D2Translator.Application.Diagnostics;
using D2Translator.Application.Reply;
using D2Translator.Application.Translation;
using D2Translator.Domain.Settings;

namespace D2Translator.App.ViewModels;

/// <summary>
/// UI04 返信入力（仕様書 10 章、11.4）。日本語の下書きを英訳し、編集、文字数の確認、明示的なコピーまでを行う。
/// ゲームのチャット欄を開く、貼り付ける、送信するといった操作はアプリから行わない。
/// </summary>
public sealed partial class ReplyViewModel : ObservableObject
{
    private readonly TranslationCoordinator _translator;
    private readonly IClipboardService _clipboard;
    private readonly AppSettingsService _settings;
    private readonly IDiagnosticsSink _diagnostics;
    private readonly TimeProvider _time;
    private readonly Func<string, bool> _confirm;
    private readonly ReplySession _session = new();
    private bool _syncing;

    public ReplyViewModel(TranslationCoordinator translator, IClipboardService clipboard, AppSettingsService settings,
        IDiagnosticsSink diagnostics, TimeProvider time, Func<string, bool> confirm)
    {
        _translator = translator;
        _clipboard = clipboard;
        _settings = settings;
        _diagnostics = diagnostics;
        _time = time;
        _confirm = confirm;
        Style = settings.Current.Send.Style;
        Draft = string.Empty;
        Candidate = string.Empty;
        Status = string.Empty;
        Notes = string.Empty;
        ReverseText = string.Empty;
        LateCandidateText = string.Empty;
        DraftCount = string.Empty;
        CandidateCount = string.Empty;
        Refresh();
    }

    public IReadOnlyList<SendStyle> Styles { get; } = [SendStyle.Neutral, SendStyle.Short, SendStyle.Polite];

    public bool ContextAvailable => _settings.Current.Context.Enabled && _settings.Current.Translation.CloudEnabled;

    [ObservableProperty]
    public partial string Draft { get; set; }

    [ObservableProperty]
    public partial string Candidate { get; set; }

    [ObservableProperty]
    public partial SendStyle Style { get; set; }

    [ObservableProperty]
    public partial bool UseContext { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; }

    [ObservableProperty]
    public partial string ReverseText { get; set; }

    [ObservableProperty]
    public partial string LateCandidateText { get; set; }

    [ObservableProperty]
    public partial string DraftCount { get; set; }

    [ObservableProperty]
    public partial string CandidateCount { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TranslateCommand), nameof(CopyCommand), nameof(ReverseTranslateCommand))]
    public partial bool IsBusy { get; set; }

    public bool HasLateCandidate => LateCandidateText.Length > 0;

    partial void OnDraftChanged(string value)
    {
        _session.UpdateDraft(value);
        if (_session.IsCandidateOutdated && !_syncing)
        {
            Status = "入力が変更されました。英訳し直すか、今の英訳をそのまま使うかを選んでください。";
        }

        Refresh();
    }

    partial void OnCandidateChanged(string value)
    {
        if (_syncing)
        {
            return;
        }

        _session.EditCandidate(value);
        ReverseText = string.Empty;
        Refresh();
    }

    partial void OnLateCandidateTextChanged(string value) => OnPropertyChanged(nameof(HasLateCandidate));

    private bool CanTranslate() => !IsBusy && _session.CanTranslate;

    /// <summary>英訳する（Ctrl+Enter）。編集した英訳は、確認してから上書きする（仕様書 11.4）。</summary>
    [RelayCommand(CanExecute = nameof(CanTranslate))]
    private async Task TranslateAsync()
    {
        if (_session.State == CandidateState.Edited && !_confirm("手で直した英訳を破棄して、英訳し直しますか？"))
        {
            return;
        }

        IsBusy = true;
        int revision = _session.BeginTranslation();
        Status = "英訳しています…（API 利用料が生じ得ます）";
        long started = _time.GetTimestamp();
        bool withContext = UseContext && ContextAvailable;
        try
        {
            ReplyTranslation result = await _translator.TranslateReplyAsync(_session.Draft, Style, withContext, CancellationToken.None);
            if (!result.IsSuccess)
            {
                _session.ApplyTranslation(revision, null);
                Status = $"英訳できませんでした: {result.Detail}";
                return;
            }

            if (_session.ApplyTranslation(revision, result.Text))
            {
                SetCandidate(_session.Candidate);
                Status = result.Problems.Count == 0 ? "英訳しました。必要なら直してからコピーしてください。" : "英訳しました（要確認）。";
                // P03: 英訳の開始から候補の表示まで（仕様書 16.2）
                _diagnostics.Latency(LatencyMetrics.ReplyCandidate, _time.GetElapsedTime(started), ("context", withContext ? "on" : "off"));
            }
            else
            {
                Status = "英訳中に入力が変更されました。新しい入力で英訳し直すか、変更前の英訳を使うかを選んでください。";
            }

            Notes = Describe(result);
            LateCandidateText = _session.LateCandidate?.Text ?? string.Empty;
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private void UseLateCandidate()
    {
        _session.AcceptLateCandidate();
        SetCandidate(_session.Candidate);
        LateCandidateText = string.Empty;
        Status = "変更前の入力に対する英訳を使います。";
        Refresh();
    }

    private bool CanUseCandidate() => !IsBusy && Candidate.Trim().Length > 0;

    /// <summary>逆翻訳で英訳の意味を確かめる。追加の API 利用になる（仕様書 10.1）。</summary>
    [RelayCommand(CanExecute = nameof(CanUseCandidate))]
    private async Task ReverseTranslateAsync()
    {
        IsBusy = true;
        try
        {
            ReplyTranslation result = await _translator.ReverseTranslateAsync(Candidate, CancellationToken.None);
            ReverseText = result.IsSuccess ? result.Text! : $"逆翻訳できませんでした: {result.Detail}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>表示中の英訳だけをクリップボードへ書く。ゲームへの貼り付けと送信は利用者が行う。</summary>
    [RelayCommand(CanExecute = nameof(CanUseCandidate))]
    private async Task CopyAsync()
    {
        CopyPlan plan = _session.PrepareCopy();
        if (plan.LooksLikeCommand && !_confirm($"英訳が「/」で始まるため、ゲーム内でコマンドとして扱われる可能性があります。コピーしますか？{Environment.NewLine}{plan.Text}"))
        {
            return;
        }

        CopyReceipt receipt = await _clipboard.CopyAsync(plan.Text, CancellationToken.None);
        if (!receipt.Success)
        {
            Status = receipt.Error ?? "コピーできませんでした。";
            return;
        }

        _session.MarkCopied();
        var lines = new List<string> { "コピー済み。ゲームのチャット欄に貼り付けて、ご自身で送信してください。" };
        lines.AddRange(plan.Notices);
        if (receipt.AutoClearScheduled)
        {
            lines.Add($"{_settings.Current.Clipboard.AutoClearSeconds} 秒後、内容が変わっていなければクリップボードを消去します。");
        }

        Status = string.Join(Environment.NewLine, lines);
        Refresh();
    }

    [RelayCommand]
    private void Clear()
    {
        _session.Clear();
        _syncing = true;
        Draft = string.Empty;
        Candidate = string.Empty;
        _syncing = false;
        Notes = string.Empty;
        ReverseText = string.Empty;
        LateCandidateText = string.Empty;
        Status = string.Empty;
        Refresh();
    }

    private void SetCandidate(string text)
    {
        _syncing = true;
        Candidate = text;
        _syncing = false;
        ReverseText = string.Empty;
    }

    private void Refresh()
    {
        DraftCount = $"{_session.DraftLength} / {ReplySession.MaxDraftScalars} 文字";
        CandidateCount = _session.State == CandidateState.Edited
            ? $"{_session.CandidateLength} 文字（編集済み）"
            : $"{_session.CandidateLength} 文字";
        TranslateCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        ReverseTranslateCommand.NotifyCanExecuteChanged();
    }

    private static string Describe(ReplyTranslation result)
    {
        var lines = new List<string>();
        lines.AddRange(result.Problems.Select(p => $"確認: {p}"));
        lines.AddRange(result.Ambiguities.Select(a => $"曖昧: {a.Term}（{a.Note}）"));
        lines.AddRange(result.Warnings.Select(w => $"注意: {w}"));
        return string.Join(Environment.NewLine, lines);
    }
}
