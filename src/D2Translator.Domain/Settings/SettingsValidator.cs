using D2Translator.Domain.Translation;

namespace D2Translator.Domain.Settings;

/// <param name="Path">設定キー。例: translation.modelId</param>
/// <param name="IsFixedValueViolation">固定値の改変。自動補正せず停止する対象。</param>
public sealed record SettingsIssue(string Path, string Message, bool IsFixedValueViolation = false);

/// <summary>仕様書 12 章の型と範囲、13.4 の固定値改変の検査。</summary>
public static class SettingsValidator
{
    public static IReadOnlyList<SettingsIssue> Validate(AppSettings settings)
    {
        var issues = new List<SettingsIssue>();

        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
        {
            issues.Add(new("schemaVersion", $"未対応の版です: {settings.SchemaVersion}"));
        }

        DisplaySettings display = settings.Display;
        Range(issues, "display.opacity", display.Opacity, 0.20, 0.95);
        Range(issues, "display.fontSizeDip", display.FontSizeDip, 14, 32);
        Range(issues, "display.maxMessages", display.MaxMessages, 1, 10);
        Range(issues, "display.overlayWidthDip", display.OverlayWidthDip, 280, 1200);
        Defined(issues, "display.mode", display.Mode);
        if (display.OverlayLeft is double left && !double.IsFinite(left) || display.OverlayTop is double top && !double.IsFinite(top))
        {
            issues.Add(new("display.overlayLeft", "オーバーレイの位置が数値ではありません。"));
        }

        foreach ((string path, string? gesture) in new[]
        {
            ("hotkeys.togglePause", settings.Hotkeys.TogglePause),
            ("hotkeys.toggleDisplayMode", settings.Hotkeys.ToggleDisplayMode),
            ("hotkeys.toggleOverlayVisibility", settings.Hotkeys.ToggleOverlayVisibility),
            ("hotkeys.openReply", settings.Hotkeys.OpenReply),
        })
        {
            if (gesture is not null && !HotkeyGesture.TryParse(gesture, out _))
            {
                issues.Add(new(path, $"キーの組合せを解釈できません: {gesture}"));
            }
        }

        CaptureSettings capture = settings.Capture;
        Range(issues, "capture.ocrRate", capture.OcrRate, 2, 10);
        if (capture.TargetKind is CaptureTargetKind kind)
        {
            Defined(issues, "capture.targetKind", kind);
        }

        if (string.IsNullOrWhiteSpace(settings.Ocr.Language))
        {
            issues.Add(new("ocr.language", "言語タグが空です。"));
        }

        if (settings.Ocr.ClientLanguage is { } clientLanguage && string.IsNullOrWhiteSpace(clientLanguage))
        {
            issues.Add(new("ocr.clientLanguage", "言語タグが空です。使わない場合は null にします。"));
        }

        if (settings.Ocr.Preprocessing.Version != OcrPreprocessingSettings.CurrentVersion)
        {
            issues.Add(new("ocr.preprocessing.version", $"未対応の版です: {settings.Ocr.Preprocessing.Version}"));
        }

        Range(issues, "ocr.preprocessing.scale", settings.Ocr.Preprocessing.Scale, 1, 3);

        TranslationSettings translation = settings.Translation;
        Fixed(issues, "translation.providerId", translation.ProviderId, OpenAIFixedProfile.ProviderId);
        Fixed(issues, "translation.modelId", translation.ModelId, OpenAIFixedProfile.ModelId);
        Fixed(issues, "translation.reasoningEffort", translation.ReasoningEffort, OpenAIFixedProfile.ReasoningEffort);
        Fixed(issues, "translation.serviceTier", translation.ServiceTier, OpenAIFixedProfile.ServiceTier);
        if (translation.AutoFallback)
        {
            issues.Add(new("translation.autoFallback", "他モデルと他サービスへの代替は禁止されています。", IsFixedValueViolation: true));
        }

        Range(issues, "translation.maxOutputTokens", translation.MaxOutputTokens,
            OpenAIFixedProfile.MinMaxOutputTokens, OpenAIFixedProfile.MaxMaxOutputTokens);
        Range(issues, "translation.deadlineMs", translation.DeadlineMs, 1000, 10000);
        Range(issues, "translation.requestsPerMinute", translation.RequestsPerMinute, 1, 120);
        Range(issues, "translation.sessionRequestCap", translation.SessionRequestCap, 1, 10000);

        ContextSettings context = settings.Context;
        Range(issues, "context.maxMessages", context.MaxMessages, 0, 6);
        if (string.IsNullOrWhiteSpace(context.Activity))
        {
            issues.Add(new("context.activity", "空にはできません。未選択は Unknown です。"));
        }

        if (string.IsNullOrWhiteSpace(context.Encounter))
        {
            issues.Add(new("context.encounter", "空にはできません。未選択は Unknown です。"));
        }

        Defined(issues, "send.style", settings.Send.Style);
        Range(issues, "clipboard.autoClearSeconds", settings.Clipboard.AutoClearSeconds, 5, 600);
        Defined(issues, "diagnostics.level", settings.Diagnostics.Level);

        return issues;
    }

    private static void Range(List<SettingsIssue> issues, string path, double value, double min, double max)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            issues.Add(new(path, $"{min}〜{max} の範囲外です: {value}"));
        }
    }

    private static void Fixed(List<SettingsIssue> issues, string path, string? actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            issues.Add(new(path, $"固定値 {expected} から変更されています: {actual ?? "null"}", IsFixedValueViolation: true));
        }
    }

    private static void Defined<TEnum>(List<SettingsIssue> issues, string path, TEnum value)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            issues.Add(new(path, $"不明な値です: {value}"));
        }
    }
}
