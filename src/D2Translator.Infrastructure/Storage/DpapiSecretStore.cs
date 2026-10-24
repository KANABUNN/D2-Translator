using System.Security.Cryptography;
using System.Text;
using D2Translator.Application.Translation;

namespace D2Translator.Infrastructure.Storage;

public sealed record SecretDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>保存名と、DPAPI で保護した値（Base64）。</summary>
    public IReadOnlyDictionary<string, string> Entries { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// 認証情報を Windows DPAPI の CurrentUser スコープで保護して secrets.dpapi に保存する（仕様書 13.4、18.2）。
/// 別ユーザーからの保護には役立つが、同じユーザー権限で動く侵害されたアプリや端末全体の侵害は防げない。
/// </summary>
public sealed class DpapiSecretStore(AppDataLocation location) : ISecretStore
{
    // アプリ固有の追加エントロピー。同じユーザーの他アプリが値を直接復号しにくくする
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("D2ChatTranslator/secrets/v1");

    private readonly JsonFileStore<SecretDocument> _store = new(location.SecretsPath);
    private readonly Lock _gate = new();

    public bool Exists(string secretRef)
    {
        lock (_gate)
        {
            return Load().Entries.ContainsKey(secretRef);
        }
    }

    public Task<SecretLease?> OpenAsync(string secretRef, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!Load().Entries.TryGetValue(secretRef, out string? protectedValue))
            {
                return Task.FromResult<SecretLease?>(null);
            }

            try
            {
                byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    return Task.FromResult<SecretLease?>(new SecretLease(Encoding.UTF8.GetString(bytes)));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // 別ユーザーや別端末で作られた値は読めない。値の中身は報告しない
                return Task.FromResult<SecretLease?>(null);
            }
        }
    }

    public Task SaveAsync(string secretRef, string value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        byte[] plain = Encoding.UTF8.GetBytes(value.Trim());
        try
        {
            string protectedValue = Convert.ToBase64String(ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
            lock (_gate)
            {
                var entries = new Dictionary<string, string>(Load().Entries, StringComparer.Ordinal) { [secretRef] = protectedValue };
                _store.Save(new SecretDocument { Entries = entries });
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string secretRef, CancellationToken ct)
    {
        lock (_gate)
        {
            SecretDocument current = Load();
            if (current.Entries.ContainsKey(secretRef))
            {
                var entries = current.Entries.Where(e => e.Key != secretRef).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
                _store.Save(new SecretDocument { Entries = entries });
                // 置換時に残る旧版にも値が残るため消す。OS やバックアップからの完全消去は保証しない（仕様書 13.4）
                if (File.Exists(_store.BackupPath))
                {
                    File.Delete(_store.BackupPath);
                }
            }
        }

        return Task.CompletedTask;
    }

    private SecretDocument Load()
    {
        StoreLoadResult<SecretDocument> result = _store.Load();
        return result.Status == StoreLoadStatus.Loaded ? result.Value! : new SecretDocument();
    }
}
