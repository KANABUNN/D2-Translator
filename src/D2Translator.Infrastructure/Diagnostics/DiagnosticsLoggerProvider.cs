using System.Globalization;
using D2Translator.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace D2Translator.Infrastructure.Diagnostics;

/// <summary>
/// 警告以上のログを診断記録へ残す。整形後の文ではなく、メッセージの書式と引数を分けて残し、
/// 文字列の引数は本文や名前を含み得るため長さだけにする（仕様書 17.4、T13）。
/// </summary>
public sealed class DiagnosticsLoggerProvider(IDiagnosticsSink sink) : ILoggerProvider
{
    private const string FormatKey = "{OriginalFormat}";

    public ILogger CreateLogger(string categoryName) => new DiagnosticsLogger(sink, categoryName);

    public void Dispose()
    {
    }

    /// <summary>引数の値の残し方。数値、真偽値、列挙値、時間、座標だけを値で残す。</summary>
    public static string Describe(object? value) => value switch
    {
        null => "null",
        string text => $"<string:{text.Length}>",
        bool or Enum or TimeSpan => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToString(value, CultureInfo.InvariantCulture)!,
        _ when value.GetType().Namespace == "D2Translator.Domain.Geometry" => value.ToString()!,
        _ => $"<{value.GetType().Name}>",
    };

    private sealed class DiagnosticsLogger(IDiagnosticsSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var tags = new Dictionary<string, string>
            {
                ["category"] = category,
                ["level"] = logLevel.ToString(),
            };
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach ((string key, object? value) in pairs)
                {
                    tags[key == FormatKey ? "format" : $"arg.{key}"] = key == FormatKey ? value as string ?? string.Empty : Describe(value);
                }
            }

            if (exception is not null)
            {
                foreach ((string key, string value) in ExceptionInfo.Describe(exception))
                {
                    tags[key] = value;
                }
            }

            sink.Write(new DiagnosticsEvent(DiagnosticsKinds.Log, eventId.Name ?? eventId.Id.ToString(CultureInfo.InvariantCulture)) { Tags = tags });
        }
    }
}
