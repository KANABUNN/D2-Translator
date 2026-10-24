using System.Text;

namespace D2Translator.Application.Translation.Text;

internal static class TextNormalization
{
    /// <summary>キャッシュのキー用。NFKC と空白の正規化だけを行い、大文字小文字は区別する（意味が変わり得るため）。</summary>
    public static string ForCache(string text) =>
        string.Join(' ', text.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
