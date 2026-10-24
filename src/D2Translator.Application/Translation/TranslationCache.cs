using System.Security.Cryptography;
using System.Text;
using D2Translator.Domain.Translation;

namespace D2Translator.Application.Translation;

/// <summary>
/// 翻訳結果のメモリ内キャッシュ（仕様書 9.3）。最大 500 件、30 分、セッション終了までの最短条件で保持する。
/// キーに文脈と辞書と固定プロファイルを含め、意味が変わり得る訳を本文だけで共有しない。
/// </summary>
public sealed class TranslationCache(TimeProvider time, int capacity = 500, TimeSpan? lifetime = null)
{
    private readonly TimeSpan _lifetime = lifetime ?? TimeSpan.FromMinutes(30);
    private readonly Dictionary<string, (TranslationResult Result, long StoredAt)> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public static string KeyOf(TranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var builder = new StringBuilder();
        Append(builder, Text.TextNormalization.ForCache(request.SourceText));
        Append(builder, request.Direction.ToString());
        Append(builder, request.GlossaryVersion);
        foreach (GlossaryHint hint in request.Glossary.OrderBy(h => h.TermId, StringComparer.Ordinal))
        {
            Append(builder, $"{hint.TermId}={hint.Meaning}");
        }

        foreach (ContextLine line in request.Context)
        {
            Append(builder, $"{line.Speaker}:{line.Text}");
        }

        Append(builder, request.Activity);
        Append(builder, OpenAIFixedProfile.ModelId);
        Append(builder, OpenAIFixedProfile.ReasoningEffort);
        Append(builder, request.MaxOutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, OpenAIFixedProfile.PromptVersion);
        Append(builder, OpenAIFixedProfile.SchemaVersion);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public bool TryGet(string key, out TranslationResult result)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && time.GetElapsedTime(entry.StoredAt) < _lifetime)
            {
                result = entry.Result;
                return true;
            }

            _entries.Remove(key);
            result = null!;
            return false;
        }
    }

    public void Set(string key, TranslationResult result)
    {
        lock (_gate)
        {
            _entries[key] = (result, time.GetTimestamp());
            if (_entries.Count > capacity)
            {
                string oldest = _entries.MinBy(e => e.Value.StoredAt).Key;
                _entries.Remove(oldest);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    // 区切り文字が値に含まれても衝突しないよう長さを前置する
    private static void Append(StringBuilder builder, string value) => builder.Append(value.Length).Append(':').Append(value).Append('|');
}
