using System.Windows;
using System.Windows.Interop;
using D2Translator.App.ViewModels;
using D2Translator.App.Views;
using D2Translator.Application.Capture;
using D2Translator.Application.Diagnostics;
using D2Translator.Application.Reply;
using D2Translator.Application.Translation;
using D2Translator.Capture.Windows;
using D2Translator.Domain.Capture;

namespace D2Translator.App.Services;

public interface IDialogService
{
    /// <summary>システムの選択 UI を開く。取り消されたら null。</summary>
    Task<ICaptureTarget?> PickCaptureTargetAsync();

    /// <summary>ROI 調整画面を開く。保存されなければ null。</summary>
    Task<CaptureProfile?> ShowRoiCalibrationAsync(ICaptureTarget target, CaptureProfile? current);

    void ShowSettings();

    /// <summary>返信画面を開く。開いていれば前面に出す（明示操作に限り自アプリへフォーカス、仕様書 11.5）。</summary>
    void ShowReply();
}

/// <summary>ホットキーの登録状態。メイン画面と設定画面で同じ表示を使う。</summary>
public sealed class HotkeyStatusStore
{
    private string _text = string.Empty;

    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            Changed?.Invoke(this, value);
        }
    }

    public event EventHandler<string>? Changed;
}

internal sealed class DialogService(
    OcrRuntime ocr,
    TimeProvider time,
    AppSettingsService settings,
    TranslationCoordinator translator,
    ISecretStore secrets,
    IClipboardService clipboard,
    IDiagnosticsSink diagnostics,
    HotkeyStatusStore hotkeys) : IDialogService
{
    private ReplyWindow? _reply;

    private static Window Owner => System.Windows.Application.Current.MainWindow;

    public async Task<ICaptureTarget?> PickCaptureTargetAsync()
    {
        nint hwnd = new WindowInteropHelper(Owner).Handle;
        return await WgcCaptureTarget.PickAsync(hwnd, time);
    }

    public async Task<CaptureProfile?> ShowRoiCalibrationAsync(ICaptureTarget target, CaptureProfile? current)
    {
        await using var viewModel = new RoiCalibrationViewModel(target, current, ocr.Recognizer, time);
        var window = new RoiCalibrationWindow(viewModel) { Owner = Owner };
        viewModel.StartPreview();
        window.ShowDialog();
        return viewModel.Result;
    }

    public void ShowSettings()
    {
        var translation = new TranslationSettingsViewModel(settings, translator, secrets);
        var window = new SettingsWindow(new SettingsViewModel(settings, translation, () => hotkeys.Text)) { Owner = Owner };
        window.ShowDialog();
    }

    public void ShowReply()
    {
        if (_reply is { IsLoaded: true })
        {
            if (_reply.WindowState == WindowState.Minimized)
            {
                _reply.WindowState = WindowState.Normal;
            }

            _reply.Activate();
            return;
        }

        // 返信画面はモーダルにしない。オーバーレイや受信履歴を見ながら書けるようにする
        var viewModel = new ReplyViewModel(translator, clipboard, settings, diagnostics, time,
            message => MessageBox.Show(Owner, message, "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        _reply = new ReplyWindow(viewModel) { Owner = Owner };
        _reply.Closed += (_, _) => _reply = null;
        _reply.Show();
        _reply.Activate();
    }
}
