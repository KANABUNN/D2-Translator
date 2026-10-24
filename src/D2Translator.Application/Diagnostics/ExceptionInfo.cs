using System.Diagnostics;
using System.Globalization;

namespace D2Translator.Application.Diagnostics;

/// <summary>
/// 例外を本文なしで残す。メッセージには本文や URL が入り得るため残さず、型名、HRESULT、
/// ファイル名を除いたメソッドの並びだけにする（仕様書 17.4、T13）。
/// </summary>
public static class ExceptionInfo
{
    private const int MaxStackLength = 4000;

    public static Dictionary<string, string> Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var types = new List<string>();
        for (Exception? current = exception; current is not null && types.Count < 8; current = current.InnerException)
        {
            types.Add(current.GetType().FullName ?? current.GetType().Name);
        }

        string stack = new StackTrace(exception, fNeedFileInfo: false).ToString().Trim();
        if (stack.Length > MaxStackLength)
        {
            stack = stack[..MaxStackLength];
        }

        return new Dictionary<string, string>
        {
            ["exception.type"] = string.Join(" > ", types),
            ["exception.hresult"] = "0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture),
            ["exception.stack"] = stack,
        };
    }

    /// <summary>未処理例外の記録。source は発生元（dispatcher、appdomain、task）。</summary>
    public static DiagnosticsEvent Unhandled(string source, Exception exception) =>
        new(DiagnosticsKinds.Error, source) { Tags = Describe(exception) };
}
