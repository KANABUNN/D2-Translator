using D2Translator.Domain.Translation;

namespace D2Translator.Application.Translation;

/// <summary>仕様書 14.1 の翻訳境界。偽の API へ置き換えて試験し、普段の試験で課金を発生させない。</summary>
public interface ITranslationProvider
{
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct);
}

/// <summary>仕様書 14.1 の秘密情報の境界。値は短い利用期間だけ取り出す。</summary>
public interface ISecretStore
{
    bool Exists(string secretRef);

    /// <summary>登録されていなければ null。</summary>
    Task<SecretLease?> OpenAsync(string secretRef, CancellationToken ct);

    Task SaveAsync(string secretRef, string value, CancellationToken ct);

    Task DeleteAsync(string secretRef, CancellationToken ct);
}

/// <summary>
/// 秘密情報の一時的な利用。ToString や例外、ログに値を出さない。
/// マネージド文字列とクラッシュダンプの完全な消去は保証しない（仕様書 14.1）。
/// </summary>
public sealed class SecretLease(string value) : IDisposable
{
    private string? _value = value;

    public string Value => _value ?? throw new ObjectDisposedException(nameof(SecretLease));

    public void Dispose() => _value = null;

    public override string ToString() => "SecretLease(***)";
}
