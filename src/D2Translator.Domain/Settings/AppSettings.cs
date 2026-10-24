using D2Translator.Domain.Translation;

namespace D2Translator.Domain.Settings;

public enum DisplayMode
{
    Window,
    Overlay,
}

public enum CaptureTargetKind
{
    Window,
    Monitor,
}

public enum SendStyle
{
    Neutral,
    Short,
    Polite,
}

public enum DiagnosticsLevel
{
    Off,
    Metadata,
}

/// <summary>仕様書 12 章の設定。既定値は仕様書の初期値。</summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DisplaySettings Display { get; init; } = new();

    public CaptureSettings Capture { get; init; } = new();

    public OcrSettings Ocr { get; init; } = new();

    public TranslationSettings Translation { get; init; } = new();

    public ContextSettings Context { get; init; } = new();

    public SendSettings Send { get; init; } = new();

    public ClipboardSettings Clipboard { get; init; } = new();

    public PrivacySettings Privacy { get; init; } = new();

    public DiagnosticsSettings Diagnostics { get; init; } = new();

    public HotkeySettings Hotkeys { get; init; } = new();
}

public sealed record DisplaySettings
{
    public DisplayMode Mode { get; init; } = DisplayMode.Window;

    public double Opacity { get; init; } = 0.65;

    public double FontSizeDip { get; init; } = 18;

    public int MaxMessages { get; init; } = 4;

    public bool AutoFallback { get; init; }
}

public sealed record CaptureSettings
{
    /// <summary>初回未設定。システムの選択UIは種別を返さないため、選択後も null のことがある。</summary>
    public CaptureTargetKind? TargetKind { get; init; }

    /// <summary>capture-profiles.json 内の ROI 設定を指す。</summary>
    public Guid? ActiveProfileId { get; init; }

    public double OcrRate { get; init; } = 5;

    public bool PauseOnFocusLoss { get; init; } = true;
}

public sealed record OcrSettings
{
    /// <summary>チャット本文（英語）の認識言語。</summary>
    public string Language { get; init; } = "en-US";

    /// <summary>
    /// ゲームの表示言語。行頭のチャンネル表示やシステムメッセージに使われる。
    /// 本文と異なる場合は両方の言語で認識して合成する。null なら本文の言語だけで認識する。
    /// </summary>
    public string? ClientLanguage { get; init; } = "ja";

    public OcrPreprocessingSettings Preprocessing { get; init; } = new();
}

/// <summary>仕様書 8.1 の前処理。版付きで保存する。</summary>
public sealed record OcrPreprocessingSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>OCR 前の拡大倍率。小さいチャット文字の認識を安定させる。</summary>
    public int Scale { get; init; } = 2;
}

public sealed record TranslationSettings
{
    public bool CloudEnabled { get; init; }

    // 以下の固定値は読取り専用。改変されていれば DATA01。
    public string ProviderId { get; init; } = OpenAIFixedProfile.ProviderId;

    public string ModelId { get; init; } = OpenAIFixedProfile.ModelId;

    public string ReasoningEffort { get; init; } = OpenAIFixedProfile.ReasoningEffort;

    public string ServiceTier { get; init; } = OpenAIFixedProfile.ServiceTier;

    public bool AutoFallback { get; init; }

    public int MaxOutputTokens { get; init; } = OpenAIFixedProfile.DefaultMaxOutputTokens;

    public int DeadlineMs { get; init; } = 4000;

    public int RequestsPerMinute { get; init; } = 60;

    public int SessionRequestCap { get; init; } = 1000;
}

public sealed record ContextSettings
{
    public bool Enabled { get; init; }

    public int MaxMessages { get; init; } = 6;

    public string Activity { get; init; } = "Unknown";

    public string Encounter { get; init; } = "Unknown";
}

public sealed record SendSettings
{
    public SendStyle Style { get; init; } = SendStyle.Neutral;
}

public sealed record ClipboardSettings
{
    public bool AutoClear { get; init; }

    public int AutoClearSeconds { get; init; } = 60;
}

public sealed record PrivacySettings
{
    public bool PersistHistory { get; init; }

    public bool SaveImages { get; init; }
}

public sealed record DiagnosticsSettings
{
    public DiagnosticsLevel Level { get; init; } = DiagnosticsLevel.Metadata;
}

/// <summary>null は無効化。</summary>
public sealed record HotkeySettings
{
    public string? TogglePause { get; init; } = "Ctrl+Alt+T";

    public string? ToggleDisplayMode { get; init; } = "Ctrl+Alt+O";

    public string? ToggleOverlayVisibility { get; init; } = "Ctrl+Alt+H";

    public string? OpenReply { get; init; } = "Ctrl+Alt+J";
}
