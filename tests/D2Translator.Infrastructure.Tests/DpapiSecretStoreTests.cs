using D2Translator.Application.Translation;
using D2Translator.Infrastructure.Storage;

namespace D2Translator.Infrastructure.Tests;

public class DpapiSecretStoreTests
{
    private const string Ref = "openai-api-key";
    private const string Value = "sk-test-0123456789-not-real";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Saved_secret_round_trips_and_is_not_stored_in_plain_text()
    {
        using var temp = new TempDirectory();
        var store = new DpapiSecretStore(temp.Location);

        await store.SaveAsync(Ref, Value, Ct);
        using SecretLease? lease = await store.OpenAsync(Ref, Ct);

        Assert.True(store.Exists(Ref));
        Assert.Equal(Value, lease!.Value);
        Assert.Equal("SecretLease(***)", lease.ToString());
        string file = File.ReadAllText(temp.Location.SecretsPath);
        Assert.DoesNotContain(Value, file, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disposed_lease_does_not_expose_the_value()
    {
        using var temp = new TempDirectory();
        var store = new DpapiSecretStore(temp.Location);
        await store.SaveAsync(Ref, Value, Ct);

        SecretLease lease = (await store.OpenAsync(Ref, Ct))!;
        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => lease.Value);
    }

    [Fact]
    public async Task Delete_removes_the_secret_and_its_backup()
    {
        using var temp = new TempDirectory();
        var store = new DpapiSecretStore(temp.Location);
        await store.SaveAsync(Ref, Value, Ct);
        await store.SaveAsync(Ref, Value + "2", Ct);

        await store.DeleteAsync(Ref, Ct);

        Assert.False(store.Exists(Ref));
        Assert.Null(await store.OpenAsync(Ref, Ct));
        Assert.False(File.Exists(temp.Location.SecretsPath + ".bak"));
    }

    [Fact]
    public async Task Tampered_value_cannot_be_opened()
    {
        using var temp = new TempDirectory();
        var store = new DpapiSecretStore(temp.Location);
        await store.SaveAsync(Ref, Value, Ct);
        string path = temp.Location.SecretsPath;
        string tampered = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path), "(\"openai-api-key\":\\s*)\"[^\"]+\"", "$1\"AAAA\"");
        Assert.Contains("\"AAAA\"", tampered, StringComparison.Ordinal);
        File.WriteAllText(path, tampered);

        Assert.Null(await store.OpenAsync(Ref, Ct));
    }
}
