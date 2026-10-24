using D2Translator.Domain.Errors;
using D2Translator.Domain.Settings;

namespace D2Translator.Infrastructure.Storage;

public enum SettingsLoadStatus
{
    /// <summary>ファイルがなく既定値を使う。</summary>
    Defaulted,
    Loaded,

    /// <summary>不正なため既定値で起動し、ファイルは上書きしない。</summary>
    Invalid,
}

public sealed record SettingsLoadResult(
    SettingsLoadStatus Status,
    AppSettings Settings,
    IReadOnlyList<SettingsIssue> Issues,
    bool BackupAvailable)
{
    public ErrorCode? Error => Status == SettingsLoadStatus.Invalid ? ErrorCode.DATA01 : null;

    /// <summary>固定値の改変があり、クラウド翻訳を開始してはならない。</summary>
    public bool HasFixedValueViolation => Issues.Any(i => i.IsFixedValueViolation);
}

/// <summary>settings.json の読み込みと保存。保存前にも検証する。</summary>
public sealed class SettingsRepository(AppDataLocation location)
{
    private readonly JsonFileStore<AppSettings> _store = new(location.SettingsPath);

    public SettingsLoadResult Load()
    {
        StoreLoadResult<AppSettings> loaded = _store.Load();
        bool backupAvailable = File.Exists(_store.BackupPath);
        switch (loaded.Status)
        {
            case StoreLoadStatus.Missing:
                return new(SettingsLoadStatus.Defaulted, new AppSettings(), [], backupAvailable);
            case StoreLoadStatus.Invalid:
                return new(SettingsLoadStatus.Invalid, new AppSettings(),
                    [new SettingsIssue("settings.json", loaded.Error ?? "不正な内容です。")], backupAvailable);
        }

        IReadOnlyList<SettingsIssue> issues = SettingsValidator.Validate(loaded.Value!);
        return issues.Count == 0
            ? new(SettingsLoadStatus.Loaded, loaded.Value!, [], backupAvailable)
            // 固定値が改変された設定は自動補正して通信を続けない（DATA01）
            : new(SettingsLoadStatus.Invalid, new AppSettings(), issues, backupAvailable);
    }

    public void Save(AppSettings settings)
    {
        IReadOnlyList<SettingsIssue> issues = SettingsValidator.Validate(settings);
        if (issues.Count > 0)
        {
            throw new InvalidOperationException($"不正な設定は保存できません: {string.Join(", ", issues.Select(i => i.Path))}");
        }

        _store.Save(settings);
    }
}
