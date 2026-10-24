using D2Translator.Domain.Capture;
using D2Translator.Domain.Geometry;
using D2Translator.Infrastructure.Storage;

namespace D2Translator.Infrastructure.Tests;

public class CaptureProfileRepositoryTests
{
    private static CaptureProfile NewProfile() => new()
    {
        Id = Guid.NewGuid(),
        TargetDisplayName = "Destiny 2",
        Roi = new NormalizedRect(0.02, 0.55, 0.35, 0.3),
        FrameSize = new PixelSize(2560, 1440),
        UpdatedAt = new DateTimeOffset(2026, 10, 24, 3, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Upsert_then_find_round_trips()
    {
        using var temp = new TempDirectory();
        var repository = new CaptureProfileRepository(temp.Location);
        CaptureProfile profile = NewProfile();

        repository.Upsert(profile);

        Assert.Equal(profile, repository.Find(profile.Id));
    }

    [Fact]
    public void Upsert_replaces_existing_profile()
    {
        using var temp = new TempDirectory();
        var repository = new CaptureProfileRepository(temp.Location);
        CaptureProfile profile = NewProfile();
        repository.Upsert(profile);

        repository.Upsert(profile with { Roi = new NormalizedRect(0, 0, 0.5, 0.5) });

        CaptureProfileDocument document = repository.Load().Value!;
        Assert.Equal(new NormalizedRect(0, 0, 0.5, 0.5), Assert.Single(document.Profiles).Roi);
    }

    [Fact]
    public void Saved_json_contains_only_stored_fields()
    {
        using var temp = new TempDirectory();
        new CaptureProfileRepository(temp.Location).Upsert(NewProfile());

        string json = File.ReadAllText(temp.Location.CaptureProfilesPath);

        Assert.DoesNotContain("isValid", json, StringComparison.Ordinal);
        Assert.DoesNotContain("aspectRatio", json, StringComparison.Ordinal);
        Assert.Contains("\"imageBasis\": \"capturedImage\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Out_of_range_roi_in_file_is_invalid_and_not_overwritten()
    {
        using var temp = new TempDirectory();
        var repository = new CaptureProfileRepository(temp.Location);
        repository.Upsert(NewProfile());
        string path = temp.Location.CaptureProfilesPath;
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"width\": 0.35", "\"width\": 1.35", StringComparison.Ordinal));

        Assert.Equal(StoreLoadStatus.Invalid, repository.Load().Status);
        Assert.Throws<InvalidOperationException>(() => repository.Upsert(NewProfile()));
    }
}
