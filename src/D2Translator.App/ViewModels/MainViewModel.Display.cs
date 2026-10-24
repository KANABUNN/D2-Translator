using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D2Translator.App.Services;
using D2Translator.Application.Display;
using D2Translator.Domain.Capture;
using D2Translator.Domain.Settings;

namespace D2Translator.App.ViewModels;

/// <summary>オーバーレイの 1 行。訳文があれば訳文を主に、原文を小さく添える。</summary>
public sealed record OverlayItem(Guid MessageId, string Header, string Primary, string Secondary)
{
    public bool HasSecondary => Secondary.Length > 0;
}

/// <summary>
/// C方式（仕様書 4 章）。オーバーレイと独立ウィンドウは同じ ViewModel と履歴を参照し、
/// 取得と翻訳を表示ごとに起動しない。切替で履歴、下書き、待ち行列は変わらない。
/// </summary>
public sealed partial class MainViewModel
{
    private readonly OverlayPolicy _overlayPolicy = new();
    private readonly SelfCaptureDetector _selfCapture = new();
    private ForegroundInfo _foreground = new(true, null);
    private bool _selfCaptureReported;
    private (double Left, double Top)? _overlayPosition;

    /// <summary>オーバーレイに出す直近の発言（display.maxMessages 件）。</summary>
    public ObservableCollection<OverlayItem> RecentMessages { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BeginPlacementEditCommand))]
    public partial bool IsOverlayMode { get; set; }

    /// <summary>利用者がホットキー等で表示だけを隠した。取得は続く。</summary>
    [ObservableProperty]
    public partial bool IsOverlayHidden { get; set; }

    [ObservableProperty]
    public partial bool IsPlacementEditing { get; set; }

    [ObservableProperty]
    public partial bool OverlayVisible { get; set; }

    [ObservableProperty]
    public partial double OverlayFontSize { get; set; }

    [ObservableProperty]
    public partial double OverlayWidth { get; set; }

    [ObservableProperty]
    public partial Brush OverlayBackground { get; set; }

    [ObservableProperty]
    public partial string OverlayStatusText { get; set; }

    [ObservableProperty]
    public partial string HotkeyStatusText { get; set; }

    [ObservableProperty]
    public partial string CaptureExclusionText { get; set; }

    public double? OverlayLeft => _settings.Current.Display.OverlayLeft;

    public double? OverlayTop => _settings.Current.Display.OverlayTop;

    private void InitializeDisplay()
    {
        ApplyDisplaySettings(_settings.Current);
        _settings.Changed += (_, s) => _dispatcher.InvokeAsync(() => ApplyDisplaySettings(s));
    }

    private void ApplyDisplaySettings(AppSettings settings)
    {
        DisplaySettings display = settings.Display;
        IsOverlayMode = display.Mode == DisplayMode.Overlay;
        DisplayModeText = IsOverlayMode ? "オーバーレイ" : "独立ウィンドウ";
        OverlayFontSize = display.FontSizeDip;
        OverlayWidth = display.OverlayWidthDip;
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(display.Opacity * 255), 0x10, 0x12, 0x16));
        brush.Freeze();
        OverlayBackground = brush;
        if (!IsOverlayMode)
        {
            IsPlacementEditing = false;
        }

        UpdateRecent();
        UpdateOverlayVisibility();
    }

    [RelayCommand]
    private void ToggleDisplayMode()
    {
        DisplayMode next = IsOverlayMode ? DisplayMode.Window : DisplayMode.Overlay;
        _settings.Update(s => s with { Display = s.Display with { Mode = next } });
        ApplyDisplaySettings(_settings.Current);
    }

    /// <summary>表示だけを隠す。取得と翻訳は止めない（仕様書 4.2）。</summary>
    [RelayCommand]
    private void ToggleOverlayHidden()
    {
        IsOverlayHidden = !IsOverlayHidden;
        UpdateOverlayVisibility();
    }

    /// <summary>配置編集の開始と終了（仕様書 11.3: Esc または終了操作で表示専用に戻す）。</summary>
    [RelayCommand(CanExecute = nameof(IsOverlayMode))]
    private void BeginPlacementEdit()
    {
        if (IsPlacementEditing)
        {
            if (_overlayPosition is (double left, double top))
            {
                EndPlacementEdit(left, top);
            }
            else
            {
                IsPlacementEditing = false;
                UpdateOverlayVisibility();
            }

            return;
        }

        IsOverlayHidden = false;
        IsPlacementEditing = true;
        UpdateOverlayVisibility();
    }

    /// <summary>オーバーレイの現在位置。配置編集を終えた時に保存する。</summary>
    public void ReportOverlayPosition(double left, double top) => _overlayPosition = (left, top);

    /// <summary>配置編集を終え、位置を保存する。</summary>
    public void EndPlacementEdit(double left, double top)
    {
        IsPlacementEditing = false;
        _settings.Update(s => s with { Display = s.Display with { OverlayLeft = left, OverlayTop = top } });
        UpdateOverlayVisibility();
    }

    public void OnForegroundChanged(ForegroundInfo foreground)
    {
        _foreground = foreground;
        UpdateOverlayVisibility();
    }

    /// <summary>オーバーレイを表示できなかった（仕様書 4.2）。事前設定が有効な時だけ独立表示へ戻す。</summary>
    public void OnOverlayFailed(string reason)
    {
        Notice = $"UI01 オーバーレイを表示できません（{reason}）。独立ウィンドウで表示してください。";
        if (_settings.Current.Display.AutoFallback && IsOverlayMode)
        {
            ToggleDisplayMode();
        }
    }

    public void SetCaptureExclusion(bool applied, int error) =>
        CaptureExclusionText = applied
            ? "有効（オーバーレイを画面取得から除外。効果は OS と取得方式による）"
            : $"使えません（エラー {error}）。ウィンドウを選んで取得している場合、オーバーレイは取得に含まれません。" +
              "モニター全体を取得している場合は、オーバーレイを ROI と重ならない位置に置いてください";

    public void SetHotkeyStatus(IReadOnlyDictionary<HotkeyAction, string> status) =>
        HotkeyStatusText = string.Join(Environment.NewLine, status.Select(s => $"{Label(s.Key)}: {s.Value}"));

    public static string Label(HotkeyAction action) => action switch
    {
        HotkeyAction.TogglePause => "取得と翻訳の一時停止・再開",
        HotkeyAction.ToggleDisplayMode => "表示方式の切替",
        HotkeyAction.ToggleOverlayVisibility => "オーバーレイを隠す・表示",
        HotkeyAction.OpenReply => "返信画面を開く",
        _ => action.ToString(),
    };

    public void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.TogglePause:
                if (StopCommand.CanExecute(null))
                {
                    StopCommand.Execute(null);
                }
                else if (StartCommand.CanExecute(null))
                {
                    StartCommand.Execute(null);
                }

                break;
            case HotkeyAction.ToggleDisplayMode:
                ToggleDisplayMode();
                break;
            case HotkeyAction.ToggleOverlayVisibility:
                ToggleOverlayHidden();
                break;
            case HotkeyAction.OpenReply:
                OpenReply();
                break;
        }
    }

    [RelayCommand]
    private void OpenReply() => _dialogs.ShowReply();

    [RelayCommand]
    private void OpenSettings()
    {
        _dialogs.ShowSettings();
        UpdateTranslationStatus();
    }

    private void UpdateOverlayVisibility()
    {
        OverlayVisible = _overlayPolicy.ShouldShow(
            IsOverlayMode ? DisplayMode.Overlay : DisplayMode.Window,
            IsOverlayHidden,
            IsPlacementEditing,
            _settings.Current.Capture.PauseOnFocusLoss,
            _foreground);
        UpdateOverlayStatus();
    }

    /// <summary>オーバーレイには取得が止まっていることを必ず示す（仕様書 4.2、11.3）。</summary>
    private void UpdateOverlayStatus() =>
        OverlayStatusText = _controller.StateMachine.State == CaptureState.Running ? string.Empty : $"取得: {CaptureStateText}";

    private void UpdateRecent()
    {
        int count = _settings.Current.Display.MaxMessages;
        OverlayItem[] recent = [.. Messages.TakeLast(count).Select(m => new OverlayItem(
            m.MessageId,
            m.Header,
            m.HasTranslation ? m.Translation : m.Text,
            m.HasTranslation ? m.Text : string.Empty))];
        if (recent.SequenceEqual(RecentMessages))
        {
            return;
        }

        RecentMessages.Clear();
        foreach (OverlayItem item in recent)
        {
            RecentMessages.Add(item);
        }
    }

    /// <summary>取得した行に、表示した訳文が現れたら映り込みとして知らせる（仕様書 4.3）。</summary>
    private void CheckSelfCapture(IEnumerable<string> rows)
    {
        if (_selfCaptureReported || !_selfCapture.IsSelfCapture(rows))
        {
            return;
        }

        _selfCaptureReported = true;
        Notice = "自アプリの表示が取得範囲に映り込んでいます。オーバーレイを ROI と重ならない位置へ移動するか、独立ウィンドウに切り替えてください。";
    }
}
