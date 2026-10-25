using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using D2Translator.Application.Capture;
using D2Translator.Application.Chat;
using D2Translator.Application.Diagnostics;
using D2Translator.Application.Translation;
using D2Translator.Capture.Windows;
using D2Translator.DictionaryContext;
using D2Translator.Domain.Capture;
using D2Translator.Domain.Chat;
using D2Translator.Domain.Geometry;
using D2Translator.Domain.Translation;
using D2Translator.Infrastructure.Diagnostics;
using D2Translator.Ocr.Windows;
using D2Translator.TextTracking.Metrics;
using D2Translator.TextTracking.Parsing;
using D2Translator.TextTracking.Speakers;
using D2Translator.TextTracking.Tracking;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// ゲームなしの耐久試験（仕様書 16.2 P13 の事前確認）。自プロセスの模擬チャット画面を WGC で取得し、
/// 2 言語 OCR、追跡、翻訳の調停（偽の API、外部送信なし）を指定時間動かして、診断記録と検出数を残す。
/// 実ゲームでの 8 時間試験の代わりにはならない。名前と本文は架空のもの。
/// </summary>
internal static class SoakRun
{
    private static readonly string[] Speakers = ["Pancake916#1234", "Kestrel#5678", "TheGreyWarden#4321", "Moonlit#2468"];

    private static readonly string[] Bodies =
    [
        "need two more for the raid",
        "anyone have a spare sword",
        "kwtd please, we are on the last encounter",
        "I will take adds on the left",
        "do not shoot the boss yet",
        "who has the well of radiance",
        "lf1m for the dungeon, need dps",
        "thanks for the run everyone",
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        double minutes = double.Parse(Option(args, "--minutes") ?? "30", CultureInfo.InvariantCulture);
        string output = Path.GetFullPath(Option(args, "--out")
            ?? Path.Combine(Path.GetTempPath(), "d2ct-soak", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
        if (!WgcCaptureTarget.IsSupported())
        {
            Console.Error.WriteLine("Windows Graphics Capture を使えません。");
            return 1;
        }

        TimeProvider time = TimeProvider.System;
        WindowsOcrRecognizer ocr = WindowsOcrRecognizer.TryCreate("en-US", "ja", 2, time)
            ?? throw new InvalidOperationException("英語の OCR を使えません。");

        string diagnosticsFolder = Path.Combine(output, DiagnosticsLog.FolderName);
        using var log = new DiagnosticsLog(diagnosticsFolder, time);
        log.Lifecycle("app_start",
            ("version", typeof(SoakRun).Assembly.GetName().Version?.ToString() ?? "-"),
            ("mode", "soak"),
            ("os", Environment.OSVersion.Version.ToString()),
            ("logical_cpus", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)),
            ("ocr_languages", $"{ocr.LanguageTag}+{ocr.ClientLanguageTag ?? "-"}"),
            ("ocr_rate", "5"),
            ("translation", "simulated"));
        Console.WriteLine($"耐久試験 {minutes:0.#} 分。診断記録: {diagnosticsFolder}");

        using ChatWindow window = await ChatWindow.ShowAsync();
        // アプリと同じく、取得対象はメッセージループのある UI スレッドで作る
        using WgcCaptureTarget target = window.Invoke(() => WgcCaptureTarget.CreateForWindow(window.Hwnd, time));

        var feed = new ChatFeed(new ChatLineParser(ChatFormat.JapaneseClient), new MessageTracker(time), new SpeakerRegistry(),
            GlossaryMatcher.FromDocument(GlossaryLoader.LoadDefault().Document!));
        var options = new TranslationOptions(CloudEnabled: true, ContextEnabled: false, "Unknown", TimeSpan.FromSeconds(4),
            RequestsPerMinute: 60, SessionRequestCap: int.MaxValue, MaxOutputTokens: 1024);
        await using var coordinator = new TranslationCoordinator(feed, new SimulatedTranslator(), new SimulatedSecrets(), options,
            time, NullLogger<TranslationCoordinator>.Instance, log);
        var pipeline = new ReceivePipeline(ocr, feed, time, NullLogger<ReceivePipeline>.Instance);
        await using var controller = new CaptureController(pipeline, time, NullLogger<CaptureController>.Instance);
        controller.StateMachine.Changed += (_, e) =>
        {
            log.Lifecycle("capture_state", ("state", e.Current.ToString()), ("reason", e.Reason?.ToString() ?? "-"));
            Console.WriteLine($"取得の状態: {e.Current}{(e.Reason is null ? string.Empty : $"（{e.Reason}）")}");
        };

        // アプリの画面と同じ点で計測する（MainViewModel の P01、P02）
        var observedAt = new ConcurrentDictionary<Guid, DateTimeOffset>();
        var detected = new ConcurrentQueue<string>();
        feed.Updated += (_, update) =>
        {
            ChatMessage message = update.Message;
            if (update.Kind != MessageUpdateKind.Added || !message.IsConversation || message.WasAlreadyVisible)
            {
                return;
            }

            log.Latency(LatencyMetrics.OriginalShown, time.GetUtcNow() - message.ObservedAt, ("kind", message.Kind.ToString()));
            detected.Enqueue(message.Text);
            if (message.Kind == ChatLineKind.Player)
            {
                observedAt[message.MessageId] = message.ObservedAt;
            }
        };
        coordinator.Updated += (_, view) =>
        {
            if (view.Status is TranslationStatus.Queued or TranslationStatus.Translating || !observedAt.TryRemove(view.MessageId, out DateTimeOffset at))
            {
                return;
            }

            if (view.Status is TranslationStatus.Translated or TranslationStatus.NeedsReview)
            {
                log.Latency(LatencyMetrics.TranslationShown, time.GetUtcNow() - at,
                    ("cache", view.Trace?.FromCache == true ? "hit" : "miss"), ("context", "off"));
            }
        };

        // 取得開始時に見えている行は表示済みとして扱われ、新着に数えない（仕様書 15.3）。基準になる行を先に置く
        window.AddLine($"{Speakers[0]}がチームチャットに参加しました。");
        window.AddLine($"{Speakers[1]}がチームチャットに参加しました。");

        await controller.BeginSelectionAsync();
        controller.SetTarget(target);
        var profile = new CaptureProfile { Id = Guid.NewGuid(), Roi = NormalizedRect.Full, FrameSize = target.CurrentSize };
        RoiCheck check = controller.TryApplyProfile(profile, ocr.MaxImageDimension);
        if (!check.IsOk)
        {
            Console.Error.WriteLine($"ROI を使えません: {check.Status}（画面の拡大率が大きい場合は試験ウィンドウが OCR の上限を超えます）");
            return 1;
        }

        controller.Start(5);
        using var sampler = new ProcessResourceSampler(time, GpuMemoryProbe.TryQuery);
        var generated = new List<string>();
        var random = new Random(24);
        DateTimeOffset end = time.GetUtcNow().AddMinutes(minutes);
        DateTimeOffset nextSample = time.GetUtcNow().AddMinutes(1);
        int sequence = 0;
        while (time.GetUtcNow() < end)
        {
            sequence++;
            (string line, string? body) = NextLine(random, sequence);
            window.AddLine(line);
            if (body is not null)
            {
                generated.Add(body);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(random.Next(1500, 4000)));
            if (time.GetUtcNow() >= nextSample)
            {
                log.Write(sampler.Sample());
                nextSample = nextSample.AddMinutes(1);
                Console.WriteLine($"{time.GetUtcNow().ToLocalTime():HH:mm} 表示 {generated.Count} 行、検出 {detected.Count} 件");
            }
        }

        // 最後の行が確定するまで待ってから止める
        await Task.Delay(TimeSpan.FromSeconds(3));
        long stopping = time.GetTimestamp();
        await controller.StopAsync();
        log.Latency(LatencyMetrics.StopResponse, time.GetElapsedTime(stopping));
        log.Write(sampler.Sample());
        log.Lifecycle("app_exit");

        // 表示した発言ごとに、まだ使っていない検出のうち最も近いものを、文字誤り率 10% 以内なら対応させる
        string[] found = [.. detected];
        var used = new bool[found.Length];
        var missed = new List<string>();
        int matched = 0;
        foreach (string expected in generated)
        {
            int index = -1, fewest = int.MaxValue;
            for (int i = 0; i < found.Length; i++)
            {
                (int edits, int length) = CharacterErrorRate.Compare(expected, found[i]);
                if (!used[i] && edits <= length / 10 && edits < fewest)
                {
                    (index, fewest) = (i, edits);
                }
            }

            if (index < 0)
            {
                missed.Add(expected);
                continue;
            }

            used[index] = true;
            matched++;
        }

        Console.WriteLine();
        Console.WriteLine($"表示した発言 {generated.Count} 件、検出 {found.Length} 件、対応 {matched} 件");
        // 模擬画面の文は架空なので、そのまま表示して原因を確かめられるようにする
        foreach (string line in missed.Take(10))
        {
            Console.WriteLine($"  未検出: {line}");
        }

        foreach (string line in found.Where((_, i) => !used[i]).Take(10))
        {
            Console.WriteLine($"  対応なし: {line}");
        }
        Console.WriteLine($"再現率 {Rate(matched, generated.Count)}、適合率 {Rate(matched, found.Length)}（模擬画面での参考値。P06 の受入値ではない）");
        Console.WriteLine();
        return DiagnosticsReport.Run([diagnosticsFolder]);
    }

    /// <summary>日本語クライアントの表示に合わせた 1 行。会話なら本文も返す（参加通知は null）。</summary>
    private static (string Line, string? Body) NextLine(Random random, int sequence)
    {
        int kind = random.Next(10);
        string body = $"{Bodies[random.Next(Bodies.Length)]} {sequence}";
        return kind switch
        {
            0 => ($"{Speakers[random.Next(Speakers.Length)]}がチームチャットに参加しました。", null),
            1 or 2 => ($"[ファイアチーム]宛て: {body}", body),
            _ => ($"[{Speakers[random.Next(Speakers.Length)]}]: {body}", body),
        };
    }

    private static string Rate(int part, int total) => total == 0 ? "-" : $"{100.0 * part / total:0.0}%";

    private static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>外部へ送らない翻訳。実 API の遅延に近い待ち時間と利用量を返す。</summary>
    private sealed class SimulatedTranslator : ITranslationProvider
    {
        private readonly Random _random = new(6);

        public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
        {
            TimeSpan latency;
            lock (_random)
            {
                latency = TimeSpan.FromMilliseconds(_random.Next(600, 2500));
            }

            await Task.Delay(latency, ct);
            return new TranslationResult
            {
                RequestId = request.RequestId,
                Outcome = TranslationOutcome.Success,
                TranslatedText = $"（模擬訳）{request.SourceText}",
                ResponseModel = OpenAIFixedProfile.ModelId,
                Usage = new TranslationUsage(500, 0, 80, 0),
                Latency = latency,
                Attempts = 1,
            };
        }
    }

    private sealed class SimulatedSecrets : ISecretStore
    {
        public bool Exists(string secretRef) => true;

        public Task<SecretLease?> OpenAsync(string secretRef, CancellationToken ct) => Task.FromResult<SecretLease?>(new SecretLease("simulated"));

        public Task SaveAsync(string secretRef, string value, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteAsync(string secretRef, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// 専用の STA スレッドで開く模擬チャット画面。ゲームと同じく背景の一部が動き続けるため、取得と OCR は毎回走る。
    /// 最前面にはせず、ほかの作業の邪魔にならないようにする（隠れていても WGC は取得できる）。
    /// </summary>
    private sealed class ChatWindow : IDisposable
    {
        private const int VisibleLines = 8;

        private Dispatcher? _dispatcher;
        private StackPanel? _lines;
        private Thread? _thread;

        public nint Hwnd { get; private set; }

        public static Task<ChatWindow> ShowAsync()
        {
            var host = new ChatWindow();
            var shown = new TaskCompletionSource<ChatWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 8, 16, 8) };
                    var pulse = new Border { Width = 10, Height = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 4, 0) };
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                    timer.Tick += (_, _) => pulse.Background = pulse.Background == Brushes.DimGray ? Brushes.Gray : Brushes.DimGray;
                    timer.Start();

                    var root = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1a)) };
                    root.Children.Add(lines);
                    root.Children.Add(pulse);
                    var window = new Window
                    {
                        Title = "D2Translator soak test",
                        Width = 640,
                        Height = 260,
                        Left = 60,
                        Top = 60,
                        ResizeMode = ResizeMode.NoResize,
                        ShowActivated = false,
                        Content = root,
                    };
                    window.Show();
                    host._lines = lines;
                    host._dispatcher = Dispatcher.CurrentDispatcher;
                    host.Hwnd = new WindowInteropHelper(window).Handle;
                    shown.SetResult(host);
                    Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    shown.TrySetException(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            host._thread = thread;
            thread.Start();
            return shown.Task;
        }

        public void AddLine(string text) => _dispatcher!.Invoke(() =>
        {
            _lines!.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI, Yu Gothic UI"),
                FontSize = 17,
                Margin = new Thickness(0, 2, 0, 2),
            });
            while (_lines.Children.Count > VisibleLines)
            {
                _lines.Children.RemoveAt(0);
            }
        });

        public T Invoke<T>(Func<T> action) => _dispatcher!.Invoke(action);

        public void Dispose()
        {
            if (_dispatcher is null)
            {
                return;
            }

            _dispatcher.Invoke(() => Window.GetWindow(_lines!)?.Close());
            _dispatcher.InvokeShutdown();
            _thread?.Join(TimeSpan.FromSeconds(5));
            _dispatcher = null;
        }
    }
}
