using D2Translator.Domain.Capture;

namespace D2Translator.Infrastructure.Storage;

public sealed record CaptureProfileDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public IReadOnlyList<CaptureProfile> Profiles { get; init; } = [];
}

/// <summary>capture-profiles.json の読み書き。</summary>
public sealed class CaptureProfileRepository(AppDataLocation location)
{
    private readonly JsonFileStore<CaptureProfileDocument> _store = new(location.CaptureProfilesPath);

    /// <summary>不正なファイルは読まずに空として扱い、上書き前の .bak で復旧できるようにする。</summary>
    public StoreLoadResult<CaptureProfileDocument> Load()
    {
        StoreLoadResult<CaptureProfileDocument> result = _store.Load();
        if (result.Status != StoreLoadStatus.Loaded)
        {
            return result;
        }

        CaptureProfileDocument document = result.Value!;
        if (document.SchemaVersion != CaptureProfileDocument.CurrentSchemaVersion ||
            document.Profiles.Any(p => p.SchemaVersion != CaptureProfile.CurrentSchemaVersion || !p.Roi.IsValid || p.FrameSize.IsEmpty))
        {
            return new(StoreLoadStatus.Invalid, null, "未対応の版または無効な ROI を含みます。");
        }

        return result;
    }

    public CaptureProfile? Find(Guid id)
    {
        StoreLoadResult<CaptureProfileDocument> result = Load();
        return result.Value?.Profiles.FirstOrDefault(p => p.Id == id);
    }

    public void Upsert(CaptureProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.Roi.IsValid || profile.FrameSize.IsEmpty)
        {
            throw new ArgumentException("無効な ROI は保存できません。", nameof(profile));
        }

        StoreLoadResult<CaptureProfileDocument> current = Load();
        if (current.Status == StoreLoadStatus.Invalid)
        {
            throw new InvalidOperationException($"既存の capture-profiles.json が不正なため上書きしません: {current.Error}");
        }

        IReadOnlyList<CaptureProfile> existing = current.Value?.Profiles ?? [];
        var profiles = existing.Where(p => p.Id != profile.Id).Append(profile).ToList();
        _store.Save(new CaptureProfileDocument { Profiles = profiles });
    }
}
