using D2Translator.Domain.Settings;

namespace D2Translator.Application.Display;

/// <summary>前面のウィンドウの情報。タイトルは比較にだけ使い、保存や送信をしない。</summary>
public sealed record ForegroundInfo(bool IsOwnProcess, string? Title);

/// <summary>
/// オーバーレイを表示するかの判定（仕様書 4.2）。ゲームが前面でなくなったら既定で隠す。
/// 取得対象のウィンドウ名と前面ウィンドウのタイトルが一度でも一致すれば、対象をウィンドウとみなして判定に使う。
/// モニターを選んだ場合等、一致しない対象では隠さない（決定 D24）。
/// </summary>
public sealed class OverlayPolicy
{
    private string? _targetTitle;
    private bool _targetIsWindow;

    public void SetTarget(string? displayName)
    {
        _targetTitle = displayName;
        _targetIsWindow = false;
    }

    public bool ShouldShow(DisplayMode mode, bool userHidden, bool editing, bool hideWhenGameInactive, ForegroundInfo foreground)
    {
        ArgumentNullException.ThrowIfNull(foreground);
        if (mode != DisplayMode.Overlay || userHidden)
        {
            return false;
        }

        bool foregroundIsTarget = _targetTitle is not null &&
            string.Equals(foreground.Title, _targetTitle, StringComparison.Ordinal);
        if (foregroundIsTarget)
        {
            _targetIsWindow = true;
        }

        if (editing || !hideWhenGameInactive || !_targetIsWindow)
        {
            return true;
        }

        return foregroundIsTarget || foreground.IsOwnProcess;
    }
}

/// <summary>
/// 自アプリの表示の映り込みの検出（仕様書 4.3）。取得した行に、最近表示した訳文がそのまま現れたら映り込みとみなす。
/// </summary>
public sealed class SelfCaptureDetector
{
    private const int MinimumLength = 6;
    private const int Remember = 50;
    private readonly Queue<string> _shown = new();
    private readonly Lock _gate = new();

    public void Shown(string translation)
    {
        string key = Key(translation);
        if (key.Length < MinimumLength)
        {
            return;
        }

        lock (_gate)
        {
            _shown.Enqueue(key);
            while (_shown.Count > Remember)
            {
                _shown.Dequeue();
            }
        }
    }

    public bool IsSelfCapture(IEnumerable<string> rows)
    {
        string[] keys = [.. rows.Select(Key).Where(k => k.Length >= MinimumLength)];
        lock (_gate)
        {
            return keys.Any(row => _shown.Any(shown => row.Contains(shown, StringComparison.Ordinal) || shown.Contains(row, StringComparison.Ordinal)));
        }
    }

    // 空白と句読点の差を無視して比べる
    private static string Key(string text) =>
        new([.. text.Normalize(System.Text.NormalizationForm.FormKC).Where(c => char.IsLetterOrDigit(c))]);
}
