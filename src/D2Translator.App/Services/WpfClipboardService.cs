using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using D2Translator.App.Native;
using D2Translator.Application.Reply;

namespace D2Translator.App.Services;

/// <summary>
/// 明示的なコピー（仕様書 10.3）。失敗時は有限回だけ再試行し、他アプリの内容を読み出さない。
/// 自動消去は、クリップボードの世代番号が本アプリの書込みから変わっていない場合だけ行う。
/// Windows のクリップボード履歴やクラウド同期に残った分までは消せない。
/// </summary>
public sealed class WpfClipboardService(AppSettingsService settings, TimeProvider time) : IClipboardService
{
    private const int MaxAttempts = 5;
    private DispatcherTimer? _clearTimer;
    private uint _writtenSequence;

    public async Task<CopyReceipt> CopyAsync(string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                _writtenSequence = NativeMethods.GetClipboardSequenceNumber();
                bool scheduled = ScheduleClear();
                return new CopyReceipt(true, null, time.GetUtcNow(), scheduled);
            }
            catch (ExternalException) when (attempt < MaxAttempts)
            {
                // 他のアプリがクリップボードを開いている
                await Task.Delay(60, ct);
            }
            catch (ExternalException)
            {
                break;
            }
        }

        return new CopyReceipt(false, "クリップボードを開けませんでした。他のアプリが使用中の可能性があります。もう一度試してください。", time.GetUtcNow(), false);
    }

    private bool ScheduleClear()
    {
        _clearTimer?.Stop();
        Domain.Settings.ClipboardSettings clipboard = settings.Current.Clipboard;
        if (!clipboard.AutoClear)
        {
            return false;
        }

        uint expected = _writtenSequence;
        _clearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(clipboard.AutoClearSeconds) };
        _clearTimer.Tick += (_, _) =>
        {
            _clearTimer.Stop();
            if (NativeMethods.GetClipboardSequenceNumber() == expected)
            {
                try
                {
                    Clipboard.Clear();
                }
                catch (ExternalException)
                {
                }
            }
        };
        _clearTimer.Start();
        return true;
    }
}
