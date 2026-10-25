using System.Text.Json;
using System.Text.Json.Serialization;
using D2Translator.Domain.Glossary;

namespace D2Translator.DictionaryContext;

public sealed record GlossaryLoadResult(GlossaryDocument? Document, IReadOnlyList<string> Errors)
{
    public bool IsValid => Document is not null && Errors.Count == 0;
}

/// <summary>
/// 辞書ファイルの検証付き読み込み（仕様書 13.4、18.3）。
/// 不正な項目が1つでもあれば全体を取り込まず、件数と理由を返す。
/// </summary>
public static class GlossaryLoader
{
    public const long MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxEntries = 10_000;
    public const int MaxEntryCharacters = 2_000;
    public const int MaxSurfaceLength = 100;

    /// <summary>照合語で使える唯一の置換記号。任意の正規表現は受け付けない。</summary>
    public const string NumberPlaceholder = "{n}";

    /// <summary>同梱の既定辞書（Bungie の公式名称、D2 用語、ネットスラング。全件が未確認、決定 D33）。</summary>
    public const string DefaultResourceName = "glossary-default.json";

    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static GlossaryLoadResult LoadDefault()
    {
        using Stream stream = typeof(GlossaryLoader).Assembly.GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException("同梱の辞書が見つかりません。");
        return Load(stream);
    }

    public static GlossaryLoadResult LoadFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return new(null, ["ファイルがありません。"]);
        }

        if (info.Length > MaxFileBytes)
        {
            return new(null, [$"ファイルが上限 {MaxFileBytes / 1024 / 1024}MB を超えています。"]);
        }

        using FileStream stream = info.OpenRead();
        return Load(stream);
    }

    public static GlossaryLoadResult Load(Stream stream)
    {
        GlossaryDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<GlossaryDocument>(stream, Options);
        }
        catch (JsonException ex)
        {
            return new(null, [$"JSON の形式が不正です（行 {ex.LineNumber + 1}）。"]);
        }

        if (document is null)
        {
            return new(null, ["内容が空です。"]);
        }

        List<string> errors = Validate(document);
        return errors.Count == 0 ? new(document, []) : new(null, errors);
    }

    public static List<string> Validate(GlossaryDocument document)
    {
        var errors = new List<string>();
        if (document.SchemaVersion != GlossaryDocument.CurrentSchemaVersion)
        {
            errors.Add($"未対応の版です: {document.SchemaVersion}");
        }

        if (document.Entries.Count > MaxEntries)
        {
            errors.Add($"項目数が上限 {MaxEntries} を超えています: {document.Entries.Count}");
            return errors;
        }

        var seen = new HashSet<(string, string)>();
        for (int i = 0; i < document.Entries.Count; i++)
        {
            GlossaryEntry entry = document.Entries[i];
            string at = $"項目 {i + 1}（{entry.TermId}）";
            if (!seen.Add((entry.TermId, entry.SenseId)))
            {
                errors.Add($"{at}: termId と senseId の組が重複しています。");
            }

            IEnumerable<string> strings = new[] { entry.TermId, entry.SenseId, entry.Source, entry.Target, entry.Note ?? string.Empty }
                .Concat(entry.Aliases).Concat(entry.Examples);
            if (strings.Sum(s => s.Length) > MaxEntryCharacters)
            {
                errors.Add($"{at}: 文字数が上限 {MaxEntryCharacters} を超えています。");
            }

            if (strings.Any(s => s.Any(char.IsControl)))
            {
                errors.Add($"{at}: 制御文字を含みます。");
            }

            foreach (string surface in entry.Aliases.Prepend(entry.Source))
            {
                if (string.IsNullOrWhiteSpace(surface) || surface.Length > MaxSurfaceLength)
                {
                    errors.Add($"{at}: 照合語が空か長すぎます。");
                }
                else if (surface.Replace(NumberPlaceholder, string.Empty, StringComparison.Ordinal).IndexOfAny(['{', '}']) >= 0)
                {
                    errors.Add($"{at}: 照合語に使えない記号を含みます: {surface}");
                }
            }

            if (string.IsNullOrWhiteSpace(entry.Target))
            {
                errors.Add($"{at}: 訳語が空です。");
            }
        }

        return errors;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
