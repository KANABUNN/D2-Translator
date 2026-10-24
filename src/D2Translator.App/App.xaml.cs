using System.Windows;
using D2Translator.App.Services;
using D2Translator.App.ViewModels;
using D2Translator.App.Views;
using D2Translator.Application.Capture;
using D2Translator.Application.Ocr;
using D2Translator.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace D2Translator.App;

public partial class App : System.Windows.Application
{
    // 二重起動による二重取得と重複課金を防ぐ（仕様書 17.1）
    private const string SingleInstanceMutexName = @"Local\D2ChatTranslator.SingleInstance";

    private Mutex? _singleInstance;
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("D2 Chat Translator は既に起動しています。", "D2 Chat Translator",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        _services = ConfigureServices();
        var window = new MainWindow(_services.GetRequiredService<MainViewModel>());
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 終了時はキュー投入停止、キャンセル、リソース解放の順（仕様書 17.1）
        // CaptureController は IAsyncDisposable のみのため、コンテナーごと非同期に破棄する
        _services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        // 本文、話者名、認証情報はログに書かない（仕様書 17.4）
        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => AppDataLocation.Resolve());
        services.AddSingleton<SettingsRepository>();
        services.AddSingleton(sp => sp.GetRequiredService<SettingsRepository>().Load());
        services.AddSingleton<CaptureProfileRepository>();
        services.AddSingleton(sp => OcrRuntime.Create(
            sp.GetRequiredService<SettingsLoadResult>().Settings.Ocr.Language,
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => sp.GetRequiredService<OcrRuntime>().Recognizer ?? new UnavailableOcrRecognizer());
        services.AddSingleton<ReceivePipeline>();
        services.AddSingleton<CaptureController>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<MainViewModel>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
