using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using D2Translator.App.Native;
using D2Translator.Domain.Settings;

namespace D2Translator.App.Services;

public enum HotkeyAction
{
    TogglePause = 1,
    ToggleDisplayMode = 2,
    ToggleOverlayVisibility = 3,
    OpenReply = 4,
}

/// <summary>
/// RegisterHotKey による登録型ホットキー（仕様書 11.5）。低レベルのキーボードフックは使わない。
/// 他のアプリと競合して登録できなければ「未登録」とし、入力を横取りしない。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly HashSet<int> _registered = [];

    public HotkeyService(Window owner)
    {
        nint hwnd = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);
    }

    public event EventHandler<HotkeyAction>? Pressed;

    /// <summary>設定のキーを登録し直す。操作ごとの登録状態を返す。</summary>
    public IReadOnlyDictionary<HotkeyAction, string> Register(HotkeySettings settings)
    {
        UnregisterAll();
        var status = new Dictionary<HotkeyAction, string>
        {
            [HotkeyAction.TogglePause] = RegisterOne(HotkeyAction.TogglePause, settings.TogglePause),
            [HotkeyAction.ToggleDisplayMode] = RegisterOne(HotkeyAction.ToggleDisplayMode, settings.ToggleDisplayMode),
            [HotkeyAction.ToggleOverlayVisibility] = RegisterOne(HotkeyAction.ToggleOverlayVisibility, settings.ToggleOverlayVisibility),
            [HotkeyAction.OpenReply] = RegisterOne(HotkeyAction.OpenReply, settings.OpenReply),
        };
        return status;
    }

    private string RegisterOne(HotkeyAction action, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "無効";
        }

        if (!HotkeyGesture.TryParse(text, out HotkeyGesture gesture) || !TryVirtualKey(gesture.Key, out uint vk))
        {
            return $"未登録（{text} を解釈できません）";
        }

        uint modifiers = NativeMethods.MOD_NOREPEAT;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            modifiers |= 0x1;
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            modifiers |= 0x2;
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            modifiers |= 0x4;
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            modifiers |= 0x8;
        }

        if (!NativeMethods.RegisterHotKey(_source.Handle, (int)action, modifiers, vk))
        {
            return $"未登録（{gesture} は他のアプリと競合しています）";
        }

        _registered.Add((int)action);
        return $"{gesture}";
    }

    private static bool TryVirtualKey(string key, out uint vk)
    {
        string name = key.Length == 1 && char.IsAsciiDigit(key[0]) ? $"D{key}" : key;
        if (Enum.TryParse(name, ignoreCase: true, out Key wpfKey))
        {
            vk = (uint)KeyInterop.VirtualKeyFromKey(wpfKey);
            return vk != 0;
        }

        vk = 0;
        return false;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _registered.Contains((int)wParam))
        {
            handled = true;
            Pressed?.Invoke(this, (HotkeyAction)(int)wParam);
        }

        return 0;
    }

    private void UnregisterAll()
    {
        foreach (int id in _registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        _registered.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
    }
}
