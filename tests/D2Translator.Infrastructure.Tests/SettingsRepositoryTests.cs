using D2Translator.Domain.Errors;
using D2Translator.Domain.Settings;
using D2Translator.Infrastructure.Storage;

namespace D2Translator.Infrastructure.Tests;

public class SettingsRepositoryTests
{
    [Fact]
    public void Missing_file_yields_defaults()
    {
        using var temp = new TempDirectory();

        SettingsLoadResult result = new SettingsRepository(temp.Location).Load();

        Assert.Equal(SettingsLoadStatus.Defaulted, result.Status);
        Assert.Equal(new AppSettings().Translation, result.Settings.Translation);
    }

    [Fact]
    public void Saved_settings_round_trip()
    {
        using var temp = new TempDirectory();
        var repository = new SettingsRepository(temp.Location);
        var settings = new AppSettings
        {
            Display = new DisplaySettings { Opacity = 0.8, Mode = DisplayMode.Overlay },
            Capture = new CaptureSettings { OcrRate = 3, ActiveProfileId = Guid.NewGuid() },
            Hotkeys = new HotkeySettings { OpenReply = null },
        };

        repository.Save(settings);
        SettingsLoadResult result = repository.Load();

        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal(settings.Display, result.Settings.Display);
        Assert.Equal(settings.Capture, result.Settings.Capture);
        Assert.Equal(settings.Hotkeys, result.Settings.Hotkeys);
    }

    [Fact]
    public void Second_save_keeps_one_backup_generation()
    {
        using var temp = new TempDirectory();
        var repository = new SettingsRepository(temp.Location);

        repository.Save(new AppSettings { Display = new DisplaySettings { FontSizeDip = 20 } });
        repository.Save(new AppSettings { Display = new DisplaySettings { FontSizeDip = 24 } });

        string backup = File.ReadAllText(temp.Location.SettingsPath + ".bak");
        Assert.Contains("\"fontSizeDip\": 20", backup, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void Tampered_model_is_data01_and_not_auto_corrected()
    {
        using var temp = new TempDirectory();
        var repository = new SettingsRepository(temp.Location);
        repository.Save(new AppSettings());
        string path = temp.Location.SettingsPath;
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"gpt-6-luna\"", "\"other-model\"", StringComparison.Ordinal));

        SettingsLoadResult result = repository.Load();

        Assert.Equal(SettingsLoadStatus.Invalid, result.Status);
        Assert.Equal(ErrorCode.DATA01, result.Error);
        Assert.True(result.HasFixedValueViolation);
        Assert.False(result.Settings.Translation.CloudEnabled);
        Assert.Contains("other-model", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Tampered_reasoning_effort_is_data01()
    {
        using var temp = new TempDirectory();
        var repository = new SettingsRepository(temp.Location);
        repository.Save(new AppSettings());
        string path = temp.Location.SettingsPath;
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"reasoningEffort\": \"none\"", "\"reasoningEffort\": \"low\"", StringComparison.Ordinal));

        SettingsLoadResult result = repository.Load();

        Assert.Equal(ErrorCode.DATA01, result.Error);
        Assert.Contains(result.Issues, i => i.Path == "translation.reasoningEffort");
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 1, \"unknownKey\": true }")]
    [InlineData("{ \"schemaVersion\": 1, \"display\": { \"mode\": \"fullscreen\" } }")]
    [InlineData("{ \"schemaVersion\": 1, \"ocr\": { \"language\": null } }")]
    [InlineData("not json")]
    public void Malformed_files_are_invalid(string json)
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Location.SettingsPath, json);

        SettingsLoadResult result = new SettingsRepository(temp.Location).Load();

        Assert.Equal(SettingsLoadStatus.Invalid, result.Status);
        Assert.Equal(ErrorCode.DATA01, result.Error);
    }

    [Fact]
    public void Invalid_settings_are_not_saved()
    {
        using var temp = new TempDirectory();

        Assert.Throws<InvalidOperationException>(() => new SettingsRepository(temp.Location)
            .Save(new AppSettings { Translation = new TranslationSettings { ModelId = "x" } }));
        Assert.False(File.Exists(temp.Location.SettingsPath));
    }
}
