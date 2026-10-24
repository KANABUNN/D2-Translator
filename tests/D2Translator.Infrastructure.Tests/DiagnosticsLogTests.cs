using D2Translator.Application.Diagnostics;
using D2Translator.Domain.Geometry;
using D2Translator.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;

namespace D2Translator.Infrastructure.Tests;

public class DiagnosticsLogTests
{
    private const string KeyLike = "sk-proj-AbCdEf0123456789_test";

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset Today = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private static DiagnosticsEvent Latency(double ms) =>
        new(DiagnosticsKinds.Latency, "P01") { Values = new Dictionary<string, double> { ["ms"] = ms } };

    private static string AllText(string directory) =>
        string.Concat(Directory.EnumerateFiles(directory).Select(File.ReadAllText));

    [Fact]
    public void Written_events_are_read_back_with_session_and_time()
    {
        using var temp = new TempDirectory();
        using (var log = new DiagnosticsLog(temp.Path, new ManualTime(Today)))
        {
            log.Write(Latency(312.5));
            log.Lifecycle("app_start", ("version", "0.1.0"));
        }

        DiagnosticsEntry[] entries = [.. DiagnosticsLog.ReadAll(temp.Path)];

        Assert.Equal(2, entries.Length);
        Assert.All(entries, e => Assert.Equal(Today, e.Time));
        Assert.Single(entries.Select(e => e.Session).Distinct());
        Assert.Equal(312.5, entries[0].Event.Values["ms"]);
        Assert.Equal("0.1.0", entries[1].Event.Tags["version"]);
        Assert.True(File.Exists(Path.Combine(temp.Path, "diag-20261024.jsonl")));
    }

    [Fact]
    public void Key_like_strings_and_auth_headers_are_redacted()
    {
        using var temp = new TempDirectory();
        using (var log = new DiagnosticsLog(temp.Path, new ManualTime(Today)))
        {
            log.Write(new DiagnosticsEvent(DiagnosticsKinds.Log, "test")
            {
                Tags = new Dictionary<string, string>
                {
                    ["format"] = $"Using {KeyLike}",
                    ["header"] = "Authorization: Bearer abc.def-ghi",
                },
            });
        }

        string text = AllText(temp.Path);
        Assert.DoesNotContain(KeyLike, text, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def-ghi", text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_older_than_seven_days_are_deleted()
    {
        using var temp = new TempDirectory();
        string old = Path.Combine(temp.Path, "diag-20261010.jsonl");
        string recent = Path.Combine(temp.Path, "diag-20261020.jsonl");
        File.WriteAllText(old, "{}\n");
        File.WriteAllText(recent, "{}\n");
        File.SetLastWriteTimeUtc(old, Today.UtcDateTime.AddDays(-8));
        File.SetLastWriteTimeUtc(recent, Today.UtcDateTime.AddDays(-4));

        using var log = new DiagnosticsLog(temp.Path, new ManualTime(Today));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Total_size_is_capped_by_deleting_older_files_first()
    {
        using var temp = new TempDirectory();
        string older = Path.Combine(temp.Path, "diag-20261022.jsonl");
        File.WriteAllText(older, new string('x', 3000));
        File.SetLastWriteTimeUtc(older, Today.UtcDateTime.AddDays(-2));
        bool full;
        using (var log = new DiagnosticsLog(temp.Path, new ManualTime(Today), maxTotalBytes: 4000))
        {
            for (int i = 0; i < 100; i++)
            {
                log.Write(Latency(i));
            }

            full = log.IsFull;
        }

        // 古い日の分を先に消し、当日分だけで上限に達したら書くのをやめる
        Assert.False(File.Exists(older));
        Assert.True(full);
        long total = Directory.EnumerateFiles(temp.Path).Sum(f => new FileInfo(f).Length);
        Assert.InRange(total, 3000, 4000);
    }

    [Fact]
    public void Logger_keeps_format_and_non_text_arguments_only()
    {
        using var temp = new TempDirectory();
        using (var log = new DiagnosticsLog(temp.Path, new ManualTime(Today)))
        using (var provider = new DiagnosticsLoggerProvider(log))
        {
            ILogger logger = provider.CreateLogger("Test");
            logger.LogInformation("Not recorded: {Body}", "below warning level");
            logger.LogWarning("Message {Body} from {Speaker} at {Size} attempt {Attempt}",
                "need adds cleared", "Kestrel#5678", new PixelSize(1920, 1080), 2);
            logger.LogError(new InvalidOperationException($"failed with {KeyLike}"), "Request failed");
        }

        DiagnosticsEntry[] entries = [.. DiagnosticsLog.ReadAll(temp.Path)];
        Assert.Equal(2, entries.Length);
        IReadOnlyDictionary<string, string> warning = entries[0].Event.Tags;
        Assert.Equal("Message {Body} from {Speaker} at {Size} attempt {Attempt}", warning["format"]);
        Assert.Equal("<string:17>", warning["arg.Body"]);
        Assert.Equal("<string:12>", warning["arg.Speaker"]);
        Assert.Equal("2", warning["arg.Attempt"]);
        Assert.Equal(new PixelSize(1920, 1080).ToString(), warning["arg.Size"]);
        Assert.Equal("System.InvalidOperationException", entries[1].Event.Tags["exception.type"]);

        string text = AllText(temp.Path);
        Assert.DoesNotContain("below warning level", text, StringComparison.Ordinal);
        Assert.DoesNotContain("need adds", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Kestrel", text, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyLike, text, StringComparison.Ordinal);
        Assert.DoesNotContain("failed with", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Partially_written_lines_are_skipped_when_reading()
    {
        using var temp = new TempDirectory();
        using (var log = new DiagnosticsLog(temp.Path, new ManualTime(Today)))
        {
            log.Write(Latency(1));
        }

        File.AppendAllText(Path.Combine(temp.Path, "diag-20261024.jsonl"), "{\"t\":\"2026-10-24T09:00");

        Assert.Single(DiagnosticsLog.ReadAll(temp.Path));
    }

    [Fact]
    public void Writing_does_not_throw_after_dispose()
    {
        using var temp = new TempDirectory();
        var log = new DiagnosticsLog(temp.Path, new ManualTime(Today));
        log.Dispose();

        log.Write(Latency(1));

        Assert.Empty(DiagnosticsLog.ReadAll(temp.Path));
    }
}
