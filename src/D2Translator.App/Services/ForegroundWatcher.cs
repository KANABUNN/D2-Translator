using System.Windows.Threading;
using D2Translator.App.Native;
using D2Translator.Application.Display;

namespace D2Translator.App.Services;

/// <summary>
/// 前面ウィンドウの変化を見る（オーバーレイを隠す判定用、決定 D24）。
/// タイトルは取得対象の名前との比較にだけ使い、保存、記録、送信をしない。
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private ForegroundInfo _current = new(true, null);

    public ForegroundWatcher()
    {
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    public ForegroundInfo Current => _current;

    public event EventHandler<ForegroundInfo>? Changed;

    private void Poll()
    {
        nint hwnd = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        var info = new ForegroundInfo(processId == (uint)Environment.ProcessId, NativeMethods.WindowTitle(hwnd));
        if (info != _current)
        {
            _current = info;
            Changed?.Invoke(this, info);
        }
    }

    public void Dispose() => _timer.Stop();
}
