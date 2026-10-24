using D2Translator.Domain.Settings;

namespace D2Translator.Domain.Tests.Settings;

public class SettingsValidatorTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        Assert.Empty(SettingsValidator.Validate(new AppSettings()));
    }

    [Fact]
    public void Defaults_follow_specification()
    {
        var settings = new AppSettings();

        Assert.Equal(DisplayMode.Window, settings.Display.Mode);
        Assert.False(settings.Translation.CloudEnabled);
        Assert.False(settings.Context.Enabled);
        Assert.False(settings.Privacy.PersistHistory);
        Assert.False(settings.Privacy.SaveImages);
        Assert.Equal("gpt-6-luna", settings.Translation.ModelId);
        Assert.Equal("none", settings.Translation.ReasoningEffort);
        Assert.Equal("default", settings.Translation.ServiceTier);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("")]
    public void Reasoning_effort_other_than_none_is_a_fixed_value_violation(string effort)
    {
        var settings = new AppSettings { Translation = new TranslationSettings { ReasoningEffort = effort } };

        SettingsIssue issue = Assert.Single(SettingsValidator.Validate(settings));
        Assert.Equal("translation.reasoningEffort", issue.Path);
        Assert.True(issue.IsFixedValueViolation);
    }

    [Fact]
    public void Other_model_or_provider_is_rejected()
    {
        var settings = new AppSettings
        {
            Translation = new TranslationSettings { ModelId = "gpt-5", ProviderId = "other", AutoFallback = true },
        };

        IReadOnlyList<SettingsIssue> issues = SettingsValidator.Validate(settings);

        Assert.Equal(3, issues.Count);
        Assert.All(issues, i => Assert.True(i.IsFixedValueViolation));
    }

    [Fact]
    public void Overlay_width_and_hotkeys_are_validated()
    {
        var settings = new AppSettings
        {
            Display = new DisplaySettings { OverlayWidthDip = 200 },
            Hotkeys = new HotkeySettings { TogglePause = "T", OpenReply = null },
        };

        string[] paths = [.. SettingsValidator.Validate(settings).Select(i => i.Path)];

        Assert.Equal(["display.overlayWidthDip", "hotkeys.togglePause"], paths);
    }

    [Fact]
    public void Out_of_range_values_are_reported_by_key()
    {
        var settings = new AppSettings
        {
            Display = new DisplaySettings { Opacity = 0.1, FontSizeDip = 40 },
            Capture = new CaptureSettings { OcrRate = 11 },
            Translation = new TranslationSettings { MaxOutputTokens = 8192, DeadlineMs = 500 },
            Context = new ContextSettings { MaxMessages = 7 },
        };

        string[] paths = [.. SettingsValidator.Validate(settings).Select(i => i.Path)];

        Assert.Equal(
            ["display.opacity", "display.fontSizeDip", "capture.ocrRate", "translation.maxOutputTokens", "translation.deadlineMs", "context.maxMessages"],
            paths);
    }
}
