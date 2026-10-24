using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using D2Translator.App.Services;
using D2Translator.App.ViewModels;
using D2Translator.App.Views;
using D2Translator.Application.Capture;
using D2Translator.Application.Chat;
using D2Translator.Application.Diagnostics;
using D2Translator.Application.Ocr;
using D2Translator.Application.Reply;
using D2Translator.Application.Translation;
using D2Translator.Domain.Settings;
using D2Translator.Domain.Translation;
using D2Translator.Translation.OpenAI;
using D2Translator.Infrastructure.Diagnostics;
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
    private IDiagnosticsSink _diagnostics = NullDiagnosticsSink.Instance;
    private ResourceMonitor? _resources;

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
        _diagnostics = _services.GetRequiredService<IDiagnosticsSink>();
        RegisterUnhandledExceptionHandlers();
        RecordStart(_services);

        var viewModel = _services.GetRequiredService<MainViewModel>();
        var window = new MainWindow(
            viewModel,
            _services.GetRequiredService<AppSettingsService>(),
            _services.GetRequiredService<HotkeyStatusStore>());
        MainWindow = window;
        window.Show();
        _resources = new ResourceMonitor(_diagnostics, _services.GetRequiredService<TimeProvider>(), Dispatcher);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _resources?.Dispose();
        _diagnostics.Lifecycle("app_exit", ("code", e.ApplicationExitCode.ToString(CultureInfo.InvariantCulture)));
        // 終了時はキュー投入停止、キャンセル、リソース解放の順（仕様書 17.1）
        // CaptureController は IAsyncDisposable のみのため、コンテナーごと非同期に破棄する
        _services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 未処理例外を本文なしで記録する（仕様書 16.2 P13、17.4）。UI スレッドの例外は記録して処理を続け、
    /// 利用者に知らせる。それ以外はプロセスが終わる前に記録だけを残す。
    /// </summary>
    private void RegisterUnhandledExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            _diagnostics.Write(ExceptionInfo.Unhandled("dispatcher", args.Exception));
            args.Handled = true;
            (MainWindow?.DataContext as MainViewModel)?.ReportUnexpectedError();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                _diagnostics.Write(ExceptionInfo.Unhandled("appdomain", exception));
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _diagnostics.Write(ExceptionInfo.Unhandled("task", args.Exception));
            args.SetObserved();
        };
    }

    /// <summary>試験記録に必要な版と条件（仕様書 20.3）。本文、名前、パスは含めない。</summary>
    private void RecordStart(ServiceProvider services)
    {
        AppSettings settings = services.GetRequiredService<AppSettingsService>().Current;
        _diagnostics.Lifecycle("app_start",
            ("version", typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "-"),
            ("os", Environment.OSVersion.Version.ToString()),
            ("runtime", RuntimeInformation.FrameworkDescription),
            ("logical_cpus", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)),
            ("packaged", services.GetRequiredService<AppDataLocation>().IsPackaged ? "yes" : "no"),
            ("display", settings.Display.Mode.ToString()),
            ("ocr_rate", settings.Capture.OcrRate.ToString(CultureInfo.InvariantCulture)),
            ("ocr_languages", $"{settings.Ocr.Language}+{settings.Ocr.ClientLanguage ?? "-"}"),
            ("ocr_scale", settings.Ocr.Preprocessing.Scale.ToString(CultureInfo.InvariantCulture)),
            ("cloud", settings.Translation.CloudEnabled ? "on" : "off"),
            ("context", settings.Context.Enabled ? "on" : "off"),
            ("model", OpenAIFixedProfile.ModelId),
            ("effort", OpenAIFixedProfile.ReasoningEffort),
            ("service_tier", OpenAIFixedProfile.ServiceTier),
            ("glossary", services.GetRequiredService<GlossaryRuntime>().Matcher.DictionaryVersion));
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
        services.AddSingleton(CreateDiagnosticsSink);
        services.AddSingleton<ILoggerProvider>(sp => new DiagnosticsLoggerProvider(sp.GetRequiredService<IDiagnosticsSink>()));
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
            sp.GetRequiredService<ILogger<TranslationCoordinator>>(),
            sp.GetRequiredService<IDiagnosticsSink>()));
        services.AddSingleton<ReceivePipeline>();
        services.AddSingleton<CaptureController>();
        services.AddSingleton<HotkeyStatusStore>();
        services.AddSingleton<IClipboardService, WpfClipboardService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<MainViewModel>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>診断メタデータの保存先（diagnostics.level = metadata の時）。作れなければ記録しない。</summary>
    private static IDiagnosticsSink CreateDiagnosticsSink(IServiceProvider sp)
    {
        if (sp.GetRequiredService<SettingsLoadResult>().Settings.Diagnostics.Level == DiagnosticsLevel.Off)
        {
            return NullDiagnosticsSink.Instance;
        }

        try
        {
            string directory = Path.Combine(sp.GetRequiredService<AppDataLocation>().RootDirectory, DiagnosticsLog.FolderName);
            return new DiagnosticsLog(directory, sp.GetRequiredService<TimeProvider>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NullDiagnosticsSink.Instance;
        }
    }
}
