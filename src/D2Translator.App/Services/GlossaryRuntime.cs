using D2Translator.DictionaryContext;
using D2Translator.Domain.Glossary;
using D2Translator.Infrastructure.Storage;

namespace D2Translator.App.Services;

/// <summary>
/// 起動時に読み込んだ辞書。同梱の既定辞書に、利用者の辞書（glossary-user.json）があれば先に照合されるよう加える。
/// 利用者の辞書が不正なら全体を取り込まず、理由を表示する（仕様書 13.4）。
/// </summary>
public sealed record GlossaryRuntime(GlossaryMatcher Matcher, int EntryCount, int PendingCount, IReadOnlyList<string> UserGlossaryErrors)
{
    public static GlossaryRuntime Load(AppDataLocation location)
    {
        GlossaryDocument bundled = GlossaryLoader.LoadDefault().Document
            ?? throw new InvalidOperationException("同梱の辞書を読み込めません。");
        IReadOnlyList<GlossaryEntry> entries = bundled.Entries;
        string version = bundled.DictionaryVersion;
        IReadOnlyList<string> errors = [];

        if (System.IO.File.Exists(location.UserGlossaryPath))
        {
            GlossaryLoadResult user = GlossaryLoader.LoadFile(location.UserGlossaryPath);
            if (user.IsValid)
            {
                entries = [.. user.Document!.Entries, .. bundled.Entries];
                version = $"{user.Document.DictionaryVersion}+{bundled.DictionaryVersion}";
            }
            else
            {
                errors = user.Errors;
            }
        }

        int pending = entries.Count(e => e.Provenance.ReviewStatus == GlossaryReviewStatus.Pending);
        return new GlossaryRuntime(new GlossaryMatcher(entries, version), entries.Count, pending, errors);
    }
}
