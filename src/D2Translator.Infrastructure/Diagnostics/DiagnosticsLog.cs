using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using D2Translator.Application.Diagnostics;

namespace D2Translator.Infrastructure.Diagnostics;

/// <summary>
/// 診断メタデータの保存（仕様書 17.4）。日ごとの JSON Lines に追記し、7 日より古いものと、
/// 合計 30MB を超えた分を古い順に消す。外部へは送らない。
/// 呼出し側は本文を渡さない決まりだが、念のため API キーや認証ヘッダーに似た文字列は伏せる（T13）。
/// </summary>
public sealed partial class DiagnosticsLog : IDiagnosticsSink, IDisposable
{
    public const string FolderName = "diagnostics";
    public const long MaxTotalBytes = 30L * 1024 * 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private const string FilePattern = "diag-*.jsonl";
    private const int MaxTextLength = 4000;

    private readonly TimeProvider _time;
    private readonly long _maxTotalBytes;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private string? _currentPath;
    private long _currentBytes;
    private long _otherBytes;
    private bool _full;
    private bool _disposed;

    public DiagnosticsLog(string directory, TimeProvider time, long maxTotalBytes = MaxTotalBytes)
    {
        DirectoryPath = directory;
        _time = time;
        _maxTotalBytes = maxTotalBytes;
        Directory.CreateDirectory(directory);
        lock (_gate)
        {
            Prune();
        }
    }

    public string DirectoryPath { get; }

    /// <summary>このプロセスの無作為な識別子。利用者や端末を表さない。</summary>
    public string Session { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    public void Write(DiagnosticsEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        DateTimeOffset now = _time.GetUtcNow();
        string line = Serialize(new DiagnosticsEntry(now, Session, entry));
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                string path = Path.Combine(DirectoryPath, $"diag-{now.UtcDateTime:yyyyMMdd}.jsonl");
                if (path != _currentPath)
                {
                    _writer?.Dispose();
                    _writer = null;
                    _currentPath = path;
                    // 書込み中のファイルは一覧の大きさが更新されないことがあるため、自分で数える
                    _currentBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
                    Prune();
                }

                long bytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                if (TotalBytes + bytes > _maxTotalBytes)
                {
                    Prune(_maxTotalBytes - bytes);
                    if (TotalBytes + bytes > _maxTotalBytes)
                    {
                        // 当日分だけで上限に達した。これ以上は書かない
                        _full = true;
                        return;
                    }
                }

                _writer ??= new StreamWriter(
                    new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                _writer.WriteLine(line);
                // 異常終了でも直前までの記録を残す
                _writer.Flush();
                _currentBytes += bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 診断の失敗で本来の処理を止めない
            }
        }
    }

    /// <summary>上限に達して記録をやめた。</summary>
    public bool IsFull
    {
        get
        {
            lock (_gate)
            {
                return _full;
            }
        }
    }

    public static IEnumerable<DiagnosticsEntry> ReadAll(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(directory, FilePattern).Order(StringComparer.Ordinal))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is string line)
            {
                if (TryParse(line) is DiagnosticsEntry entry)
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>保存前に伏せる。API キー（sk-…）、Bearer トークン、Authorization ヘッダー。</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string redacted = SecretPattern().Replace(text, "[REDACTED]");
        return redacted.Length > MaxTextLength ? redacted[..MaxTextLength] : redacted;
    }

    [GeneratedRegex(@"sk-[A-Za-z0-9_\-]{8,}|(?i:authorization)\s*[:=][^\r\n]*|(?i:bearer)\s+[A-Za-z0-9._~+/\-]+=*")]
    private static partial Regex SecretPattern();

    private static string Serialize(DiagnosticsEntry entry)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("t", entry.Time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            json.WriteString("s", entry.Session);
            json.WriteString("kind", Redact(entry.Event.Kind));
            json.WriteString("name", Redact(entry.Event.Name));
            if (entry.Event.Values.Count > 0)
            {
                json.WriteStartObject("v");
                foreach ((string key, double value) in entry.Event.Values)
                {
                    if (double.IsFinite(value))
                    {
                        json.WriteNumber(Redact(key), value);
                    }
                }

                json.WriteEndObject();
            }

            if (entry.Event.Tags.Count > 0)
            {
                json.WriteStartObject("tags");
                foreach ((string key, string value) in entry.Event.Tags)
                {
                    json.WriteString(Redact(key), Redact(value));
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static DiagnosticsEntry? TryParse(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            var values = new Dictionary<string, double>();
            if (root.TryGetProperty("v", out JsonElement v))
            {
                foreach (JsonProperty property in v.EnumerateObject())
                {
                    values[property.Name] = property.Value.GetDouble();
                }
            }

            var tags = new Dictionary<string, string>();
            if (root.TryGetProperty("tags", out JsonElement t))
            {
                foreach (JsonProperty property in t.EnumerateObject())
                {
                    tags[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }

            return new DiagnosticsEntry(
                DateTimeOffset.Parse(root.GetProperty("t").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                root.GetProperty("s").GetString()!,
                new DiagnosticsEvent(root.GetProperty("kind").GetString()!, root.GetProperty("name").GetString()!) { Values = values, Tags = tags });
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // 異常終了で途中まで書かれた行等は読み飛ばす
            return null;
        }
    }

    private long TotalBytes => _otherBytes + _currentBytes;

    /// <summary>保持期間を過ぎたものを消し、合計が limit を超えていれば現在のファイル以外を古い順に消す。</summary>
    private void Prune(long? limit = null)
    {
        DateTime cutoff = _time.GetUtcNow().UtcDateTime - Retention;
        var others = new List<FileInfo>();
        foreach (FileInfo file in new DirectoryInfo(DirectoryPath).EnumerateFiles(FilePattern))
        {
            if (string.Equals(file.FullName, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (file.LastWriteTimeUtc < cutoff)
            {
                TryDelete(file);
            }
            else
            {
                others.Add(file);
            }
        }

        _otherBytes = others.Sum(f => f.Length);
        foreach (FileInfo file in others.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (TotalBytes <= (limit ?? _maxTotalBytes))
            {
                break;
            }

            if (TryDelete(file))
            {
                _otherBytes -= file.Length;
            }
        }
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
