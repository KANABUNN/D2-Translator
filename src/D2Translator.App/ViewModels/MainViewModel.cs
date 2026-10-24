using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D2Translator.App.Presentation;
using D2Translator.App.Services;
using D2Translator.Application.Capture;
using D2Translator.Application.Chat;
using D2Translator.Capture.Windows;
using D2Translator.Domain.Capture;
using D2Translator.Domain.Errors;
using D2Translator.Domain.Ocr;
using D2Translator.Domain.Settings;
using D2Translator.Domain.Translation;
using D2Translator.Infrastructure.Storage;
using D2Translator.TextTracking.Tracking;
using Microsoft.Extensions.Logging;

namespace D2Translator.App.ViewModels;

public sealed record OcrLineItem(string Text, string Bounds);

/// <summary>UI02 メイン独立ウィンドウ（Phase 2: 受信履歴、辞書注釈。翻訳は Phase 3）。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>メモリ内に保持する履歴の上限（仕様書 11.2）。</summary>
    private const int MaxHistory = 1000;

    private readonly Dictionary<Guid, MessageItem> _messageIndex = [];
    private readonly CaptureController _controller;
    private readonly SettingsRepository _settingsRepository;
    private readonly CaptureProfileRepository _profiles;
    private readonly OcrRuntime _ocr;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly bool _settingsWritable;
    private AppSettings _settings;

    public MainViewModel(
        CaptureController controller,
        SettingsRepository settingsRepository,
        SettingsLoadResult settingsLoad,
        CaptureProfileRepository profiles,
        OcrRuntime ocr,
        GlossaryRuntime glossary,
        ChatFeed feed,
        AppDataLocation location,
        IDialogService dialogs,
        ILogger<MainViewModel> logger)
    {
        _controller = controller;
        _settingsRepository = settingsRepository;
        _profiles = profiles;
        _ocr = ocr;
        _dialogs = dialogs;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _settings = settingsLoad.Settings;
        // 不正な設定ファイルは上書きしない（仕様書 13.4）
        _settingsWritable = settingsLoad.Status != SettingsLoadStatus.Invalid;

        TargetName = "未選択";
        CaptureStateText = StatusLabels.Of(CaptureState.Idle);
        CloudStatusText = _settings.Translation.CloudEnabled ? "有効" : "無効";
        ModelText = $"{OpenAIFixedProfile.ModelDisplayName}（{OpenAIFixedProfile.ModelId}、推論量 {OpenAIFixedProfile.ReasoningEffort}）";
        DisplayModeText = "独立ウィンドウ";
        Notice = string.Empty;
        WaitingText = string.Empty;
        LastObservationText = string.Empty;
        StatsText = "まだフレームを処理していません。";
        DataFolder = location.RootDirectory;
        PackageText = location.IsPackaged ? "あり（MSIX）" : "なし（開発実行。OCR の正式サポート条件外）";
        OcrText = ocr.Recognizer is { } recognizer
            ? $"{recognizer.LanguageTag}{(recognizer.ClientLanguageTag is { } client ? $" + {client}（ゲーム表示）" : string.Empty)}、" +
              $"{recognizer.Scale}倍拡大、ROI 上限 {recognizer.MaxImageDimension}px"
            : $"{ocr.Availability.RequestedLanguage} 利用不可（インストール済み: {string.Join(", ", ocr.Availability.InstalledLanguages)}）";
        GlossaryText = $"{glossary.Matcher.DictionaryVersion}（{glossary.EntryCount} 語義、未確認の用例を含む）";

        var notices = new List<string>();
        if (settingsLoad.Status == SettingsLoadStatus.Invalid)
        {
            notices.Add($"{StatusLabels.Of(ErrorCode.DATA01)} 既定値で起動しました。ファイルは上書きしません: "
                + string.Join(" / ", settingsLoad.Issues.Select(i => $"{i.Path}: {i.Message}")));
        }

        if (ocr.Recognizer is null)
        {
            notices.Add(StatusLabels.Of(ErrorCode.OCR01));
        }
        else if (ocr.ClientLanguageMissing)
        {
            notices.Add($"OCR01 ゲーム表示言語（{_settings.Ocr.ClientLanguage}）の OCR がないため、行頭のチャンネル表示とシステムメッセージを読めません。" +
                "Windows の設定で日本語の言語機能（OCR）を追加してください。");
        }

        if (glossary.UserGlossaryErrors.Count > 0)
        {
            notices.Add($"{StatusLabels.Of(ErrorCode.DATA01)} 利用者の辞書（glossary-user.json）を取り込みませんでした: " +
                string.Join(" / ", glossary.UserGlossaryErrors.Take(5)));
        }

        if (!WgcCaptureTarget.IsSupported())
        {
            notices.Add(StatusLabels.Of(ErrorCode.CAP01));
        }

        Notice = string.Join(Environment.NewLine, notices);

        _controller.StateMachine.Changed += (_, e) => _dispatcher.InvokeAsync(() => OnStateChanged(e));
        _controller.ObservationReady += (_, o) => _dispatcher.InvokeAsync(() => ShowObservation(o));
        _controller.StatsUpdated += (_, s) => _dispatcher.InvokeAsync(() => ShowStats(s));
        _controller.WaitingForFramesChanged += (_, waiting) => _dispatcher.InvokeAsync(() =>
            WaitingText = waiting ? $"待機中: 新しいフレームが届いていません（{CaptureController.FrameStallThreshold.TotalSeconds:0}秒以上）" : string.Empty);
        feed.Updated += (_, update) => _dispatcher.InvokeAsync(() => ApplyUpdate(update));
    }

    /// <summary>最新の観測で組み立てた行（診断用）。</summary>
    public ObservableCollection<OcrLineItem> Lines { get; } = [];

    /// <summary>受信履歴。</summary>
    public ObservableCollection<MessageItem> Messages { get; } = [];

    public string GlossaryText { get; }

    [ObservableProperty]
    public partial string TargetName { get; set; }

    [ObservableProperty]
    public partial string CaptureStateText { get; set; }

    [ObservableProperty]
    public partial string CloudStatusText { get; set; }

    [ObservableProperty]
    public partial string ModelText { get; set; }

    [ObservableProperty]
    public partial string DisplayModeText { get; set; }

    [ObservableProperty]
    public partial string Notice { get; set; }

    [ObservableProperty]
    public partial string WaitingText { get; set; }

    [ObservableProperty]
    public partial string StatsText { get; set; }

    [ObservableProperty]
    public partial string LastObservationText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SelectTargetCommand), nameof(CalibrateCommand), nameof(StartCommand), nameof(StopCommand))]
    public partial bool IsBusy { get; set; }

    public string DataFolder { get; }

    public string PackageText { get; }

    public string OcrText { get; }

    private CaptureState State => _controller.StateMachine.State;

    private int OcrMaxDimension => _ocr.MaxRoiDimension;

    private bool CanSelectTarget() => !IsBusy && WgcCaptureTarget.IsSupported() && State != CaptureState.Selecting;

    [RelayCommand(CanExecute = nameof(CanSelectTarget))]
    private async Task SelectTargetAsync()
    {
        IsBusy = true;
        try
        {
            await _controller.BeginSelectionAsync();
            ICaptureTarget? target;
            try
            {
                target = await _dialogs.PickCaptureTargetAsync();
            }
            catch (CaptureException ex)
            {
                _logger.LogWarning(ex, "Capture picker failed");
                _controller.CancelSelection(OcrMaxDimension);
                Notice = StatusLabels.Of(ex.Code);
                return;
            }

            if (target is null)
            {
                _controller.CancelSelection(OcrMaxDimension);
                return;
            }

            _controller.SetTarget(target);
            TargetName = target.DisplayName;
            Lines.Clear();

            // 保存した ROI を再検証し、使えなければ調整画面を開く
            CaptureProfile? saved = _settings.Capture.ActiveProfileId is Guid id ? _profiles.Find(id) : null;
            if (saved is not null && _controller.TryApplyProfile(saved, OcrMaxDimension).IsOk)
            {
                return;
            }

            await CalibrateCoreAsync(saved);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCalibrate() => !IsBusy && _controller.Target is not null &&
        State is CaptureState.Ready or CaptureState.Paused or CaptureState.ReconfigureRequired or CaptureState.Calibrating;

    [RelayCommand(CanExecute = nameof(CanCalibrate))]
    private async Task CalibrateAsync()
    {
        IsBusy = true;
        try
        {
            await CalibrateCoreAsync(_controller.Profile);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CalibrateCoreAsync(CaptureProfile? current)
    {
        ICaptureTarget? target = _controller.Target;
        if (target is null)
        {
            return;
        }

        if (State != CaptureState.Calibrating)
        {
            _controller.BeginCalibration();
        }

        CaptureProfile? result = await _dialogs.ShowRoiCalibrationAsync(target, current);
        if (result is not null && _controller.TryApplyProfile(result, OcrMaxDimension).IsOk)
        {
            SaveProfile(result);
        }
        else if (_controller.Profile is not null)
        {
            // 取り消し時は以前の ROI が今の画面でも有効なら戻す
            _controller.TryApplyProfile(_controller.Profile, OcrMaxDimension);
        }
    }

    private void SaveProfile(CaptureProfile profile)
    {
        try
        {
            _profiles.Upsert(profile);
            _settings = _settings with { Capture = _settings.Capture with { ActiveProfileId = profile.Id } };
            if (_settingsWritable)
            {
                _settingsRepository.Save(_settings);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Saving capture profile failed");
            Notice = $"{StatusLabels.Of(ErrorCode.DATA01)} ROI を保存できませんでした（今回の実行中は使用します）。";
        }
    }

    private bool CanStart() => !IsBusy && _ocr.Recognizer is not null && State is CaptureState.Ready or CaptureState.Paused;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        WaitingText = string.Empty;
        _controller.Start(_settings.Capture.OcrRate);
    }

    /// <summary>
    /// ウィンドウを閉じる前に取得を止める。取得対象の破棄は UI スレッドへ戻して行うため、
    /// UI スレッドを止める終了処理より前に済ませる。
    /// </summary>
    public Task ShutdownAsync() => _controller.StopAsync();

    private bool CanStop() => State == CaptureState.Running;

    // 停止は常に利用可能にする（仕様書 4.2）。IsBusy に依存させない
    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => _controller.StopAsync();

    private void OnStateChanged(CaptureStateChangedEventArgs e)
    {
        CaptureStateText = StatusLabels.Of(e.Current);
        if (e.Reason is ErrorCode reason)
        {
            Notice = StatusLabels.Of(reason);
        }
        else if (e.Current is CaptureState.Running or CaptureState.Ready)
        {
            Notice = string.Empty;
        }

        if (_controller.Target is null)
        {
            TargetName = "未選択";
        }

        SelectTargetCommand.NotifyCanExecuteChanged();
        CalibrateCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private void ShowObservation(OcrObservation observation)
    {
        Lines.Clear();
        foreach (OcrLine line in observation.Lines)
        {
            Lines.Add(new OcrLineItem(line.Text, line.Bounds.ToString()));
        }

        LastObservationText = $"観測 {observation.CapturedAt.ToLocalTime():HH:mm:ss.fff}（取得時刻） " +
            $"{observation.Lines.Count} 行、OCR {observation.OcrDuration.TotalMilliseconds:0} ms";
    }

    private void ShowStats(PipelineStats stats)
    {
        StatsText =
            $"取得画像: {stats.FrameSize}{Environment.NewLine}" +
            $"ROI: {stats.RoiPixels}{Environment.NewLine}" +
            $"処理フレーム: {stats.FramesReceived}（変化なしで省略 {stats.FramesSkippedUnchanged}）{Environment.NewLine}" +
            $"OCR 実行: {stats.OcrRuns} 回、直近 {Ms(stats.LastOcrDuration)}、平均 {Ms(stats.AverageOcrDuration)}{Environment.NewLine}" +
            $"最終取得: {stats.LastFrameAt?.ToLocalTime():HH:mm:ss}";
    }

    private void ApplyUpdate(ChatFeedUpdate update)
    {
        Guid id = update.Message.MessageId;
        if (update.Kind == MessageUpdateKind.Removed)
        {
            if (_messageIndex.Remove(id, out MessageItem? removed))
            {
                Messages.Remove(removed);
            }

            return;
        }

        var item = MessageItem.From(update.Message);
        if (_messageIndex.TryGetValue(id, out MessageItem? existing))
        {
            int index = Messages.IndexOf(existing);
            if (index >= 0)
            {
                Messages[index] = item;
            }
        }
        else
        {
            Messages.Add(item);
            while (Messages.Count > MaxHistory)
            {
                _messageIndex.Remove(Messages[0].MessageId);
                Messages.RemoveAt(0);
            }
        }

        _messageIndex[id] = item;
    }

    private static string Ms(TimeSpan? value) => value is TimeSpan v ? $"{v.TotalMilliseconds:0} ms" : "-";
}
