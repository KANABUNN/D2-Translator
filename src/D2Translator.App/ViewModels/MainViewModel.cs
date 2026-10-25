using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D2Translator.App.Presentation;
using D2Translator.App.Services;
using D2Translator.Application.Capture;
using D2Translator.Application.Chat;
using D2Translator.Application.Diagnostics;
using D2Translator.Application.Translation;
using D2Translator.Capture.Windows;
using D2Translator.Domain.Chat;
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
    private readonly Dictionary<Guid, ChatMessage> _messages = [];
    private readonly Dictionary<Guid, TranslationView> _translations = [];

    /// <summary>P02 を計測する発言。保留、失敗、手動の翻訳を経たものは利用者の操作を含むため除く。</summary>
    private readonly HashSet<Guid> _awaitingTranslation = [];
    private int _hiddenCount;
    private readonly CaptureController _controller;
    private readonly AppSettingsService _settings;
    private readonly TranslationCoordinator _translator;
    private readonly CaptureProfileRepository _profiles;
    private readonly OcrRuntime _ocr;
    private readonly IDialogService _dialogs;
    private readonly IDiagnosticsSink _diagnostics;
    private readonly TimeProvider _time;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dispatcher _dispatcher;

    public MainViewModel(
        CaptureController controller,
        AppSettingsService settings,
        SettingsLoadResult settingsLoad,
        CaptureProfileRepository profiles,
        OcrRuntime ocr,
        GlossaryRuntime glossary,
        ChatFeed feed,
        TranslationCoordinator translator,
        AppDataLocation location,
        IDialogService dialogs,
        IDiagnosticsSink diagnostics,
        TimeProvider time,
        ILogger<MainViewModel> logger)
    {
        _controller = controller;
        _settings = settings;
        _translator = translator;
        _profiles = profiles;
        _ocr = ocr;
        _dialogs = dialogs;
        _diagnostics = diagnostics;
        _time = time;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        TargetName = "未選択";
        CaptureStateText = StatusLabels.Of(CaptureState.Idle);
        CloudStatusText = string.Empty;
        UsageText = string.Empty;
        UpdateTranslationStatus();
        ModelText = $"{OpenAIFixedProfile.ModelDisplayName}（{OpenAIFixedProfile.ModelId}、推論量 {OpenAIFixedProfile.ReasoningEffort}）";
        DisplayModeText = "独立ウィンドウ";
        Notice = string.Empty;
        WaitingText = string.Empty;
        LastObservationText = string.Empty;
        HiddenText = string.Empty;
        StatsText = "まだフレームを処理していません。";
        DataFolder = location.RootDirectory;
        VersionText = DescribeVersion(typeof(MainViewModel).Assembly);
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
            notices.Add($"OCR01 ゲーム表示言語（{_settings.Current.Ocr.ClientLanguage}）の OCR がないため、行頭のチャンネル表示とシステムメッセージを読めません。" +
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
        translator.Updated += (_, view) => _dispatcher.InvokeAsync(() => ApplyTranslation(view));
        translator.StateChanged += (_, _) => _dispatcher.InvokeAsync(UpdateTranslationStatus);

        OverlayBackground = System.Windows.Media.Brushes.Transparent;
        OverlayStatusText = string.Empty;
        HotkeyStatusText = string.Empty;
        CaptureExclusionText = "未確認";
        InitializeDisplay();
    }

    [ObservableProperty]
    public partial string UsageText { get; set; }

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
    public partial string HiddenText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SelectTargetCommand), nameof(CalibrateCommand), nameof(StartCommand), nameof(StopCommand))]
    public partial bool IsBusy { get; set; }

    public string DataFolder { get; }

    /// <summary>アプリの版とソースのリビジョン（更新と元の版へ戻す確認、仕様書 17.3、20.3）。</summary>
    public string VersionText { get; }

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
            _overlayPolicy.SetTarget(target.DisplayName);
            TargetName = target.DisplayName;
            Lines.Clear();

            // 保存した ROI を再検証し、使えなければ調整画面を開く
            CaptureProfile? saved = _settings.Current.Capture.ActiveProfileId is Guid id ? _profiles.Find(id) : null;
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
            _settings.Update(s => s with { Capture = s.Capture with { ActiveProfileId = profile.Id } });
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
        _controller.Start(_settings.Current.Capture.OcrRate);
    }

    /// <summary>利用者の明示操作による翻訳（開始前から表示、要確認、失敗、期限切れ、訂正後）。</summary>
    [RelayCommand]
    private void TranslateMessage(Guid messageId) => _translator.RequestManual(messageId);

    /// <summary>
    /// ウィンドウを閉じる前に取得を止める。取得対象の破棄は UI スレッドへ戻して行うため、
    /// UI スレッドを止める終了処理より前に済ませる。
    /// </summary>
    public Task ShutdownAsync() => _controller.StopAsync();

    private bool CanStop() => State == CaptureState.Running;

    // 停止は常に利用可能にする（仕様書 4.2）。IsBusy に依存させない
    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        long started = _time.GetTimestamp();
        await _controller.StopAsync();
        _diagnostics.Latency(LatencyMetrics.StopResponse, _time.GetElapsedTime(started));
    }

    /// <summary>UI スレッドの未処理例外を記録して処理を続けた。</summary>
    public void ReportUnexpectedError() =>
        Notice = "予期しないエラーが発生しましたが、処理を続けています。種類を診断記録（データ保存先の diagnostics フォルダー）に残しました。";

    private void OnStateChanged(CaptureStateChangedEventArgs e)
    {
        _diagnostics.Lifecycle("capture_state", ("state", e.Current.ToString()), ("reason", e.Reason?.ToString() ?? "-"));
        CaptureStateText = StatusLabels.Of(e.Current);
        UpdateOverlayStatus();
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

        CheckSelfCapture(observation.Lines.Select(l => l.Text));

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
        if (!update.Message.IsConversation)
        {
            // システム表示とチャット以外の画面表示は履歴に出さず、件数だけを診断に示す
            if (update.Kind == MessageUpdateKind.Added)
            {
                _hiddenCount++;
                HiddenText = $"履歴に表示しなかったシステム表示・画面表示: {_hiddenCount} 件";
            }

            return;
        }

        if (update.Kind == MessageUpdateKind.Removed)
        {
            _messages.Remove(id);
            _translations.Remove(id);
            _awaitingTranslation.Remove(id);
            if (_messageIndex.Remove(id, out MessageItem? removed))
            {
                Messages.Remove(removed);
                UpdateRecent();
            }

            return;
        }

        _messages[id] = update.Message;
        Render(id);
    }

    private void ApplyTranslation(TranslationView view)
    {
        _translations[view.MessageId] = view;
        if (view.Text is { } shown && view.Status is TranslationStatus.Translated or TranslationStatus.NeedsReview)
        {
            _selfCapture.Shown(shown);
        }

        if (_messages.TryGetValue(view.MessageId, out ChatMessage? message))
        {
            Render(view.MessageId);
            RecordTranslationLatency(message, view);
        }

        UpdateUsage();
    }

    /// <summary>
    /// P02: 取得したフレームの時刻から、自動で翻訳した訳の表示まで（仕様書 16.3）。
    /// キャッシュと文脈の有無で分ける。保留や失敗を経たものは除き、件数は翻訳の記録で数える。
    /// </summary>
    private void RecordTranslationLatency(ChatMessage message, TranslationView view)
    {
        if (!_awaitingTranslation.Contains(message.MessageId))
        {
            return;
        }

        switch (view.Status)
        {
            case TranslationStatus.Queued or TranslationStatus.Translating:
                return;
            case TranslationStatus.Translated or TranslationStatus.NeedsReview when view.Text is not null:
                _diagnostics.Latency(LatencyMetrics.TranslationShown, _time.GetUtcNow() - message.ObservedAt,
                    ("cache", view.Trace?.FromCache == true ? "hit" : "miss"),
                    ("context", view.Trace?.WithContext == true ? "on" : "off"));
                break;
        }

        _awaitingTranslation.Remove(message.MessageId);
    }

    /// <summary>原文と訳文の両方を1行にまとめて表示する（仕様書 11.2）。</summary>
    private void Render(Guid id)
    {
        var item = MessageItem.From(_messages[id], _translations.GetValueOrDefault(id));
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
            ChatMessage message = _messages[id];
            if (!message.WasAlreadyVisible)
            {
                // P01: 取得したフレームの時刻から原文行の表示まで（仕様書 16.3）
                _diagnostics.Latency(LatencyMetrics.OriginalShown, _time.GetUtcNow() - message.ObservedAt, ("kind", message.Kind.ToString()));
                if (message.Kind == ChatLineKind.Player)
                {
                    _awaitingTranslation.Add(id);
                }
            }

            while (Messages.Count > MaxHistory)
            {
                Guid oldest = Messages[0].MessageId;
                _messageIndex.Remove(oldest);
                _messages.Remove(oldest);
                _translations.Remove(oldest);
                _awaitingTranslation.Remove(oldest);
                Messages.RemoveAt(0);
            }
        }

        _messageIndex[id] = item;
        UpdateRecent();
    }

    private void UpdateTranslationStatus()
    {
        (TranslationServiceState state, string detail) = _translator.Status;
        CloudStatusText = state switch
        {
            TranslationServiceState.Disabled => "無効",
            TranslationServiceState.Ready => "有効",
            TranslationServiceState.NoApiKey => "有効（API キー未登録）",
            TranslationServiceState.CircuitOpen => "一時停止（通信障害）",
            TranslationServiceState.LimitReached => "停止（上限到達）",
            _ => "停止",
        };

        // 翻訳サービスの障害は通知欄にも出す。取得やローカル OCR は止めない（仕様書 15.3）
        if (state is TranslationServiceState.Stopped or TranslationServiceState.LimitReached or TranslationServiceState.NoApiKey)
        {
            Notice = $"クラウド翻訳: {detail}";
        }

        UpdateUsage();
    }

    private void UpdateUsage()
    {
        UsageSnapshot usage = _translator.Usage;
        UsageText = usage.Requests == 0 && usage.CacheHits == 0
            ? "翻訳要求はまだありません。"
            : $"要求 {usage.Requests} 回（HTTP {usage.HttpAttempts} 回、成功 {usage.Succeeded}、キャッシュ {usage.CacheHits}）{Environment.NewLine}" +
              $"入力 {usage.InputTokens}（うちキャッシュ {usage.CachedInputTokens}）/ 出力 {usage.OutputTokens} トークン{Environment.NewLine}" +
              $"推計 {usage.EstimatedUsd:0.000000} 米ドル（{OpenAIPricing.ReferenceDate:yyyy-MM-dd} 時点の公表単価、請求額の保証ではありません）" +
              (usage.UnknownUsage > 0 ? $"{Environment.NewLine}料金未確定 {usage.UnknownUsage} 回" : string.Empty);
    }

    private static string Ms(TimeSpan? value) => value is TimeSpan v ? $"{v.TotalMilliseconds:0} ms" : "-";

    /// <summary>例: 0.1.1+a9d42fd…（リビジョンは 7 桁に縮める）→ 0.1.1（a9d42fd）。</summary>
    private static string DescribeVersion(Assembly assembly)
    {
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "-";
        string[] parts = version.Split('+', 2);
        return parts.Length == 2 ? $"{parts[0]}（{parts[1][..Math.Min(7, parts[1].Length)]}）" : version;
    }
}
