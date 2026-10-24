using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace D2Translator.Infrastructure.Storage;

public enum StoreLoadStatus
{
    Missing,
    Loaded,
    Invalid,
}

public sealed record StoreLoadResult<T>(StoreLoadStatus Status, T? Value, string? Error)
    where T : class;

/// <summary>
/// 検証付き JSON ファイル。一時ファイルへ書いてから原子的に置換し、旧版を一世代 .bak に残す（仕様書 13.4）。
/// </summary>
public sealed class JsonFileStore<T>(string path)
    where T : class
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    public string Path { get; } = path;

    public string BackupPath => Path + ".bak";

    public StoreLoadResult<T> Load() => LoadFrom(Path);

    public StoreLoadResult<T> LoadBackup() => LoadFrom(BackupPath);

    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string json = JsonSerializer.Serialize(value, SerializerOptions);
        string directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        Directory.CreateDirectory(directory);

        string temp = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(Path))
            {
                File.Replace(temp, Path, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, Path);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static StoreLoadResult<T> LoadFrom(string path)
    {
        if (!File.Exists(path))
        {
            return new(StoreLoadStatus.Missing, null, null);
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            T? value = JsonSerializer.Deserialize<T>(stream, SerializerOptions);
            return value is null
                ? new(StoreLoadStatus.Invalid, null, "内容が空です。")
                : new(StoreLoadStatus.Loaded, value, null);
        }
        catch (JsonException ex)
        {
            // 例外全文や本文をそのまま出さず、位置だけを示す
            return new(StoreLoadStatus.Invalid, null, $"JSON の形式が不正です（行 {ex.LineNumber + 1}、位置 {ex.BytePositionInLine}）。");
        }
        catch (IOException ex)
        {
            return new(StoreLoadStatus.Invalid, null, $"読み込めません: {ex.GetType().Name}");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            // 仕様書 13.2: 未知の項目を検証する
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RemoveComputedProperties } },
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }

    // IsValid や AspectRatio 等の算出プロパティを保存対象から外す
    private static void RemoveComputedProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            if (typeInfo.Properties[i].Set is null)
            {
                typeInfo.Properties.RemoveAt(i);
            }
        }
    }
}
