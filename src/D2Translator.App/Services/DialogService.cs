using System.Windows;
using System.Windows.Interop;
using D2Translator.App.ViewModels;
using D2Translator.App.Views;
using D2Translator.Application.Capture;
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

    void ShowTranslationSettings();
}

internal sealed class DialogService(
    OcrRuntime ocr,
    TimeProvider time,
    AppSettingsService settings,
    TranslationCoordinator translator,
    ISecretStore secrets) : IDialogService
{
    public void ShowTranslationSettings()
    {
        var window = new TranslationSettingsWindow(new TranslationSettingsViewModel(settings, translator, secrets)) { Owner = Owner };
        window.ShowDialog();
    }

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
}
