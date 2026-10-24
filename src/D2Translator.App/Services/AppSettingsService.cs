using D2Translator.Domain.Settings;
using D2Translator.Infrastructure.Storage;

namespace D2Translator.App.Services;

/// <summary>
/// 実行中の設定。不正な設定ファイル（DATA01）は上書きせず、変更はこの実行中だけ有効にする（仕様書 13.4）。
/// </summary>
public sealed class AppSettingsService(SettingsRepository repository, SettingsLoadResult load)
{
    private readonly Lock _gate = new();

    public AppSettings Current { get; private set; } = load.Settings;

    public bool IsWritable { get; } = load.Status != SettingsLoadStatus.Invalid;

    /// <summary>固定値（モデル、推論量等）が改変された設定ファイルがある。直すまでクラウド翻訳を有効にしない。</summary>
    public bool HasFixedValueViolation { get; } = load.HasFixedValueViolation;

    public event EventHandler<AppSettings>? Changed;

    /// <summary>設定を変更して保存する。保存できなければ false（変更自体は実行中は有効）。</summary>
    public bool Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings updated;
        bool saved = true;
        lock (_gate)
        {
            updated = change(Current);
            Current = updated;
            if (IsWritable)
            {
                try
                {
                    repository.Save(updated);
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    saved = false;
                }
            }
        }

        Changed?.Invoke(this, updated);
        return saved && IsWritable;
    }
}
