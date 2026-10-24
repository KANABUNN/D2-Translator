using System.Runtime.InteropServices;

namespace D2Translator.App.Native;

/// <summary>
/// 自アプリのウィンドウ処理と登録型ホットキーに使う Win32 API（仕様書 11.3、11.5、19.2）。
/// ゲームへの入力生成やキーボードフックは使わない。
/// </summary>
internal static unsafe partial class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const nint WS_EX_TRANSPARENT = 0x00000020;
    public const nint WS_EX_TOOLWINDOW = 0x00000080;
    public const nint WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Windows 10 2004 以降。自アプリのウィンドウを画面取得から除く（補助的な対策、仕様書 4.3）。</summary>
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_NOREPEAT = 0x4000;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static partial int GetWindowText(nint hWnd, char* text, int maxCount);

    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    public static string? WindowTitle(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        char* buffer = stackalloc char[256];
        int length = GetWindowText(hwnd, buffer, 256);
        return length <= 0 ? null : new string(buffer, 0, length);
    }

    public static void SetExtendedStyle(nint hwnd, nint add, nint remove)
    {
        nint style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (style | add) & ~remove);
    }
}
