namespace D2Translator.Domain.Settings;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// 登録型ホットキーのキーの組合せ（仕様書 11.5）。例: Ctrl+Alt+T。
/// 修飾キーを 1 つ以上含むものだけを受け付け、単独キーでゲームの入力を奪わない。
/// </summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    private static readonly Dictionary<string, HotkeyModifiers> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = HotkeyModifiers.Control,
        ["Control"] = HotkeyModifiers.Control,
        ["Alt"] = HotkeyModifiers.Alt,
        ["Shift"] = HotkeyModifiers.Shift,
        ["Win"] = HotkeyModifiers.Windows,
    };

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        foreach (string part in parts[..^1])
        {
            if (!ModifierNames.TryGetValue(part, out HotkeyModifiers modifier) || modifiers.HasFlag(modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        string key = parts[^1];
        bool valid = (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) ||
            (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], out int f) && f is >= 1 and <= 24);
        if (!valid)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key.ToUpperInvariant());
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(Key);
        return string.Join('+', parts);
    }
}
