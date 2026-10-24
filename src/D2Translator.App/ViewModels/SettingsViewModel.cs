using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D2Translator.App.Services;
using D2Translator.Domain.Settings;

namespace D2Translator.App.ViewModels;

/// <summary>
/// UI06 設定（仕様書 11.4、12）。カテゴリー別に表示し、適用、取消、既定値に戻すを持つ。
/// 翻訳のタブは送信内容の確認と認証情報を伴うため、操作した時点で反映する。
/// OCR の言語と拡大倍率は取得を止めて適用する必要があるため、次回起動時に反映する。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;
    private readonly Func<string> _hotkeyStatus;

    public SettingsViewModel(AppSettingsService settings, TranslationSettingsViewModel translation, Func<string> hotkeyStatus)
    {
        _settings = settings;
        _hotkeyStatus = hotkeyStatus;
        Translation = translation;
        Message = string.Empty;
        TogglePause = ToggleDisplayMode = ToggleOverlayVisibility = OpenReply = string.Empty;
        OcrLanguage = string.Empty;
        ClientLanguage = string.Empty;
        HotkeyStatus = hotkeyStatus();
        Load(settings.Current);
    }

    public TranslationSettingsViewModel Translation { get; }

    public IReadOnlyList<SendStyle> Styles { get; } = [SendStyle.Neutral, SendStyle.Short, SendStyle.Polite];

    // 表示
    [ObservableProperty]
    public partial bool OverlayMode { get; set; }

    [ObservableProperty]
    public partial double Opacity { get; set; }

    [ObservableProperty]
    public partial double FontSize { get; set; }

    [ObservableProperty]
    public partial int MaxMessages { get; set; }

    [ObservableProperty]
    public partial double OverlayWidth { get; set; }

    [ObservableProperty]
    public partial bool AutoFallback { get; set; }

    [ObservableProperty]
    public partial bool HideWhenGameInactive { get; set; }

    // 取得と OCR
    [ObservableProperty]
    public partial double OcrRate { get; set; }

    [ObservableProperty]
    public partial string OcrLanguage { get; set; }

    [ObservableProperty]
    public partial string ClientLanguage { get; set; }

    [ObservableProperty]
    public partial int OcrScale { get; set; }

    // 返信とクリップボード
    [ObservableProperty]
    public partial SendStyle Style { get; set; }

    [ObservableProperty]
    public partial bool ClipboardAutoClear { get; set; }

    [ObservableProperty]
    public partial int ClipboardSeconds { get; set; }

    // ホットキー（空欄は無効）
    [ObservableProperty]
    public partial string TogglePause { get; set; }

    [ObservableProperty]
    public partial string ToggleDisplayMode { get; set; }

    [ObservableProperty]
    public partial string ToggleOverlayVisibility { get; set; }

    [ObservableProperty]
    public partial string OpenReply { get; set; }

    [ObservableProperty]
    public partial string HotkeyStatus { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; }

    [RelayCommand]
    private void Apply()
    {
        AppSettings current = _settings.Current;
        AppSettings next = current with
        {
            Display = current.Display with
            {
                Mode = OverlayMode ? DisplayMode.Overlay : DisplayMode.Window,
                Opacity = Math.Round(Opacity, 2),
                FontSizeDip = Math.Round(FontSize),
                MaxMessages = MaxMessages,
                OverlayWidthDip = Math.Round(OverlayWidth),
                AutoFallback = AutoFallback,
            },
            Capture = current.Capture with { OcrRate = Math.Round(OcrRate), PauseOnFocusLoss = HideWhenGameInactive },
            Ocr = current.Ocr with
            {
                Language = OcrLanguage.Trim(),
                ClientLanguage = string.IsNullOrWhiteSpace(ClientLanguage) ? null : ClientLanguage.Trim(),
                Preprocessing = current.Ocr.Preprocessing with { Scale = OcrScale },
            },
            Send = current.Send with { Style = Style },
            Clipboard = current.Clipboard with { AutoClear = ClipboardAutoClear, AutoClearSeconds = ClipboardSeconds },
            Hotkeys = new HotkeySettings
            {
                TogglePause = Gesture(TogglePause),
                ToggleDisplayMode = Gesture(ToggleDisplayMode),
                ToggleOverlayVisibility = Gesture(ToggleOverlayVisibility),
                OpenReply = Gesture(OpenReply),
            },
        };

        IReadOnlyList<SettingsIssue> issues = SettingsValidator.Validate(next);
        if (issues.Count > 0)
        {
            Message = "適用できません: " + string.Join(" / ", issues.Select(i => $"{i.Path}: {i.Message}"));
            return;
        }

        bool ocrChanged = next.Ocr != current.Ocr;
        bool saved = _settings.Update(_ => next);
        HotkeyStatus = _hotkeyStatus();
        Message = (saved ? "適用しました。" : "適用しました（設定ファイルが不正なため、この実行中だけ有効です）。") +
            (ocrChanged ? " OCR の言語と拡大倍率は次回起動時に反映します。" : string.Empty) +
            " 取得の頻度は次に「開始」した時から反映します。";
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        Load(new AppSettings() with { Translation = _settings.Current.Translation, Context = _settings.Current.Context });
        Message = "既定値を表示しました。「適用」で反映します。";
    }

    private void Load(AppSettings settings)
    {
        OverlayMode = settings.Display.Mode == DisplayMode.Overlay;
        Opacity = settings.Display.Opacity;
        FontSize = settings.Display.FontSizeDip;
        MaxMessages = settings.Display.MaxMessages;
        OverlayWidth = settings.Display.OverlayWidthDip;
        AutoFallback = settings.Display.AutoFallback;
        HideWhenGameInactive = settings.Capture.PauseOnFocusLoss;
        OcrRate = settings.Capture.OcrRate;
        OcrLanguage = settings.Ocr.Language;
        ClientLanguage = settings.Ocr.ClientLanguage ?? string.Empty;
        OcrScale = settings.Ocr.Preprocessing.Scale;
        Style = settings.Send.Style;
        ClipboardAutoClear = settings.Clipboard.AutoClear;
        ClipboardSeconds = settings.Clipboard.AutoClearSeconds;
        TogglePause = settings.Hotkeys.TogglePause ?? string.Empty;
        ToggleDisplayMode = settings.Hotkeys.ToggleDisplayMode ?? string.Empty;
        ToggleOverlayVisibility = settings.Hotkeys.ToggleOverlayVisibility ?? string.Empty;
        OpenReply = settings.Hotkeys.OpenReply ?? string.Empty;
    }

    private static string? Gesture(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
