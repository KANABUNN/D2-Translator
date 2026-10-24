namespace D2Translator.Infrastructure.Storage;

/// <summary>
/// ユーザー別ローカルデータ領域（仕様書 13.4）。
/// MSIX では LocalState を使い、固定の個人名やユーザーパスを埋め込まない。
/// </summary>
public sealed class AppDataLocation
{
    public const string UnpackagedFolderName = "D2ChatTranslator";

    public AppDataLocation(string rootDirectory, bool isPackaged)
    {
        RootDirectory = rootDirectory;
        IsPackaged = isPackaged;
    }

    public string RootDirectory { get; }

    public bool IsPackaged { get; }

    public string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    public string CaptureProfilesPath => Path.Combine(RootDirectory, "capture-profiles.json");

    public static AppDataLocation Resolve()
    {
        if (TryGetPackagedLocalFolder() is string packaged)
        {
            return new AppDataLocation(packaged, isPackaged: true);
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppDataLocation(Path.Combine(local, UnpackagedFolderName), isPackaged: false);
    }

    private static string? TryGetPackagedLocalFolder()
    {
        try
        {
            // 識別情報を持たないプロセスでは InvalidOperationException 等になる
            return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
