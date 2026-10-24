using System.Windows;
using D2Translator.App.Services;
using D2Translator.App.ViewModels;
using D2Translator.App.Views;
using D2Translator.Application.Capture;
using D2Translator.Application.Chat;
using D2Translator.Application.Ocr;
using D2Translator.Application.Reply;
using D2Translator.Application.Translation;
using D2Translator.Translation.OpenAI;
using D2Translator.Infrastructure.Storage;
using D2Translator.TextTracking.Parsing;
using D2Translator.TextTracking.Speakers;
using D2Translator.TextTracking.Tracking;
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
        var window = new MainWindow(
            _services.GetRequiredService<MainViewModel>(),
            _services.GetRequiredService<AppSettingsService>(),
            _services.GetRequiredService<HotkeyStatusStore>());
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
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        services.AddSingleton(_ => OpenAIResponsesProvider.CreateHttpClient());
        services.AddSingleton<ITranslationProvider, OpenAIResponsesProvider>();
        services.AddSingleton<CaptureProfileRepository>();
        services.AddSingleton(sp => OcrRuntime.Create(
            sp.GetRequiredService<SettingsLoadResult>().Settings.Ocr,
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IOcrRecognizer>(sp => (IOcrRecognizer?)sp.GetRequiredService<OcrRuntime>().Recognizer ?? new UnavailableOcrRecognizer());
        services.AddSingleton(sp => GlossaryRuntime.Load(sp.GetRequiredService<AppDataLocation>()));
        services.AddSingleton(_ => new ChatLineParser(ChatFormat.JapaneseClient));
        services.AddSingleton(sp => new MessageTracker(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<SpeakerRegistry>();
        services.AddSingleton(sp => new ChatFeed(
            sp.GetRequiredService<ChatLineParser>(),
            sp.GetRequiredService<MessageTracker>(),
            sp.GetRequiredService<SpeakerRegistry>(),
            sp.GetRequiredService<GlossaryRuntime>().Matcher)
        {
            Activity = sp.GetRequiredService<SettingsLoadResult>().Settings.Context.Activity,
        });
        services.AddSingleton(sp => new TranslationCoordinator(
            sp.GetRequiredService<ChatFeed>(),
            sp.GetRequiredService<ITranslationProvider>(),
            sp.GetRequiredService<ISecretStore>(),
            TranslationOptions.From(sp.GetRequiredService<AppSettingsService>().Current),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<TranslationCoordinator>>()));
        services.AddSingleton<ReceivePipeline>();
        services.AddSingleton<CaptureController>();
        services.AddSingleton<HotkeyStatusStore>();
        services.AddSingleton<IClipboardService, WpfClipboardService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<MainViewModel>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
