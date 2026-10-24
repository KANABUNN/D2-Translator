using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace D2Translator.Capture.Windows.Tests;

/// <summary>専用の STA スレッドで、上下2段の文字を表示する試験用ウィンドウを開く。</summary>
internal sealed class TestWindowHost : IDisposable
{
    private Dispatcher? _dispatcher;
    private Window? _window;
    private Thread? _thread;

    public nint Hwnd { get; private set; }

    public static Task<TestWindowHost> ShowAsync(string topText, string bottomText)
    {
        var host = new TestWindowHost();
        var shown = new TaskCompletionSource<TestWindowHost>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var lines = new UniformGrid { Rows = 2, Columns = 1 };
                lines.Children.Add(Line(topText));
                lines.Children.Add(Line(bottomText));

                // WGC は描画が変わった時だけフレームを送るため、右上の小さな四角を点滅させ続ける
                var blinker = new Border
                {
                    Width = 12,
                    Height = 12,
                    Margin = new Thickness(0, 6, 6, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = Brushes.DimGray,
                };
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                timer.Tick += (_, _) => blinker.Background = blinker.Background == Brushes.DimGray ? Brushes.Gray : Brushes.DimGray;
                timer.Start();

                var panel = new Grid();
                panel.Children.Add(lines);
                panel.Children.Add(blinker);
                var window = new Window
                {
                    Title = "D2Translator capture test",
                    Width = 720,
                    Height = 220,
                    Left = 40,
                    Top = 40,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Topmost = true,
                    Background = Brushes.Black,
                    Content = panel,
                };
                window.Show();
                host._window = window;
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

    public void Close() => _dispatcher?.Invoke(() => _window?.Close());

    public void Resize(double width, double height) => _dispatcher?.Invoke(() =>
    {
        _window!.Width = width;
        _window.Height = height;
    });

    /// <summary>ウィンドウのスレッド（メッセージループあり）で実行する。</summary>
    public T Invoke<T>(Func<T> action) => _dispatcher!.Invoke(action);

    public void Dispose()
    {
        if (_dispatcher is null)
        {
            return;
        }

        Close();
        _dispatcher.InvokeShutdown();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _dispatcher = null;
    }

    private static TextBlock Line(string text) => new()
    {
        Text = text,
        Foreground = Brushes.White,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 30,
        Margin = new Thickness(24, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
