using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using D2Translator.Domain.Translation;

namespace D2Translator.Compliance.Tests;

/// <summary>
/// 仕様書 19.2 の禁止事項（コード挿入、描画 API のフック、プロセスメモリの読書き、パケット解析、
/// ドライバー導入、ゲームへの入力生成、登録型ホットキー以外のキー入力監視）が、
/// 配布する組立品と依存物に含まれないことをメタデータから確かめる（A11 の設計点検）。
/// </summary>
public class NonIntrusionTests
{
    /// <summary>本アプリの組立品が呼ぶネイティブ関数の承認済み一覧。追加する時は理由を書き、設計を見直す。</summary>
    private static readonly HashSet<string> ApprovedImports = new(StringComparer.OrdinalIgnoreCase)
    {
        // 自アプリのウィンドウのクリック透過と配置編集（仕様書 11.3）
        "user32.dll!GetWindowLongPtrW",
        "user32.dll!SetWindowLongPtrW",
        // 自アプリのウィンドウを画面取得から除く（仕様書 4.3）
        "user32.dll!SetWindowDisplayAffinity",
        // 登録型ホットキー（仕様書 11.5）。キーボードフックは使わない
        "user32.dll!RegisterHotKey",
        "user32.dll!UnregisterHotKey",
        // 前面ウィンドウが自アプリか、タイトルが取得対象と同じかだけを見る（決定 D24）。他プロセスは開かない
        "user32.dll!GetForegroundWindow",
        "user32.dll!GetWindowThreadProcessId",
        "user32.dll!GetWindowTextW",
        // 自アプリが書いたクリップボードの内容だけを消すため（仕様書 10.3）
        "user32.dll!GetClipboardSequenceNumber",
        // WGC のフレームを D3D11 で ROI だけ切り出す（仕様書 7.2）
        "d3d11.dll!CreateDirect3D11DeviceFromDXGIDevice",
    };

    /// <summary>依存物を含めて禁止するネイティブ関数。名前の末尾の A/W は区別しない。</summary>
    private static readonly string[] ForbiddenImports =
    [
        // ゲームへの入力生成
        "SendInput", "keybd_event", "mouse_event", "PostMessage", "PostThreadMessage", "SendMessage", "SendMessageTimeout",
        "SendNotifyMessage", "BlockInput", "SetCursorPos", "ClipCursor",
        // キー入力の監視とフック
        "SetWindowsHookEx", "RegisterRawInputDevices", "GetRawInputData", "GetAsyncKeyState", "GetKeyboardState", "SetWinEventHook",
        // 他プロセスへの接続、メモリの読書き、コード挿入
        "OpenProcess", "ReadProcessMemory", "WriteProcessMemory", "VirtualAllocEx", "VirtualProtectEx", "CreateRemoteThread",
        "CreateRemoteThreadEx", "NtCreateThreadEx", "RtlCreateUserThread", "QueueUserAPC", "SetThreadContext", "DebugActiveProcess",
        "NtReadVirtualMemory", "NtWriteVirtualMemory", "CreateToolhelp32Snapshot", "Process32First", "Process32Next",
        "Module32First", "Module32Next", "EnumProcesses", "EnumProcessModules",
        // ドライバーとサービスの導入
        "OpenSCManager", "CreateService", "StartService", "NtLoadDriver", "DeviceIoControl",
        // パケットの取得
        "WSAIoctl", "pcap_open", "pcap_open_live",
    ];

    /// <summary>本アプリの組立品から参照してはならないマネージド API（型名、または 型名::メンバー名）。</summary>
    private static readonly string[] ForbiddenManaged =
    [
        "Windows.UI.Input.Preview.Injection.InputInjector",
        "System.Windows.Forms.SendKeys",
        "System.Net.Sockets.Socket",
        "System.Runtime.InteropServices.NativeLibrary",
        "System.Diagnostics.Process::GetProcesses",
        "System.Diagnostics.Process::GetProcessesByName",
        "System.Diagnostics.Process::GetProcessById",
        // リンクやプログラムを自動で起動しない（T14）
        "System.Diagnostics.Process::Start",
    ];

    private static readonly string[] OwnAssemblies =
    [
        "D2Translator.App.dll",
        "D2Translator.Application.dll",
        "D2Translator.Capture.Windows.dll",
        "D2Translator.DictionaryContext.dll",
        "D2Translator.Domain.dll",
        "D2Translator.Infrastructure.dll",
        "D2Translator.Ocr.Windows.dll",
        "D2Translator.TextTracking.dll",
        "D2Translator.Translation.OpenAI.dll",
    ];

    private static string PathOf(string file) => Path.Combine(AppContext.BaseDirectory, file);

    /// <summary>試験の実行に使うだけの組立品。配布物には含まれない。</summary>
    private static bool IsTestInfrastructure(string file)
    {
        string name = Path.GetFileName(file);
        return name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.TestPlatform", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.VisualStudio", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.Testing", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("D2Translator.Compliance.Tests", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ShippedAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Where(f => !IsTestInfrastructure(f) && IsManaged(f));

    private static bool IsManaged(string file)
    {
        using var stream = File.OpenRead(file);
        using var pe = new PEReader(stream);
        return pe.HasMetadata;
    }

    private static List<string> ImportsOf(string file)
    {
        using var stream = File.OpenRead(file);
        using var pe = new PEReader(stream);
        MetadataReader reader = pe.GetMetadataReader();
        var imports = new List<string>();
        foreach (MethodDefinitionHandle handle in reader.MethodDefinitions)
        {
            MethodDefinition method = reader.GetMethodDefinition(handle);
            if ((method.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) == 0)
            {
                continue;
            }

            MethodImport import = method.GetImport();
            string module = import.Module.IsNil ? "?" : reader.GetString(reader.GetModuleReference(import.Module).Name);
            string entry = import.Name.IsNil ? reader.GetString(method.Name) : reader.GetString(import.Name);
            imports.Add($"{module}!{entry}");
        }

        return imports;
    }

    private static List<string> MemberReferencesOf(string file)
    {
        using var stream = File.OpenRead(file);
        using var pe = new PEReader(stream);
        MetadataReader reader = pe.GetMetadataReader();
        var references = new List<string>();
        foreach (TypeReferenceHandle handle in reader.TypeReferences)
        {
            references.Add(TypeName(reader, handle));
        }

        foreach (MemberReferenceHandle handle in reader.MemberReferences)
        {
            MemberReference member = reader.GetMemberReference(handle);
            if (member.Parent.Kind == HandleKind.TypeReference)
            {
                references.Add($"{TypeName(reader, (TypeReferenceHandle)member.Parent)}::{reader.GetString(member.Name)}");
            }
        }

        return references;
    }

    private static string TypeName(MetadataReader reader, TypeReferenceHandle handle)
    {
        TypeReference type = reader.GetTypeReference(handle);
        string ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? reader.GetString(type.Name) : $"{ns}.{reader.GetString(type.Name)}";
    }

    private static List<string> UserStringsOf(string file)
    {
        using var stream = File.OpenRead(file);
        using var pe = new PEReader(stream);
        MetadataReader reader = pe.GetMetadataReader();
        var strings = new List<string>();
        for (UserStringHandle handle = MetadataTokens.UserStringHandle(1); !handle.IsNil; handle = reader.GetNextHandle(handle))
        {
            strings.Add(reader.GetUserString(handle));
        }

        return strings;
    }

    private static bool IsForbiddenImport(string import)
    {
        string entry = import[(import.IndexOf('!') + 1)..];
        string bare = entry.EndsWith('A') || entry.EndsWith('W') ? entry[..^1] : entry;
        return ForbiddenImports.Any(f => string.Equals(f, entry, StringComparison.OrdinalIgnoreCase) || string.Equals(f, bare, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void All_own_assemblies_are_present()
    {
        Assert.All(OwnAssemblies, file => Assert.True(File.Exists(PathOf(file)), file));
    }

    [Fact]
    public void Own_assemblies_import_only_approved_native_functions()
    {
        HashSet<string> imports = new(OwnAssemblies.SelectMany(file => ImportsOf(PathOf(file))), StringComparer.OrdinalIgnoreCase);

        // 承認一覧にない関数があれば、その名前が失敗の内容に出る
        Assert.Subset(ApprovedImports, imports);
        Assert.NotEmpty(imports);
    }

    [Fact]
    public void Shipped_dependencies_import_no_intrusive_native_functions()
    {
        List<string> found = [.. ShippedAssemblies()
            .SelectMany(file => ImportsOf(file).Where(IsForbiddenImport).Select(i => $"{Path.GetFileName(file)}: {i}"))];

        Assert.Empty(found);
    }

    [Fact]
    public void Own_assemblies_do_not_reference_input_injection_process_access_or_link_launching()
    {
        List<string> found = [.. OwnAssemblies.SelectMany(file => MemberReferencesOf(PathOf(file))
            .Where(r => ForbiddenManaged.Any(f => r == f || r.StartsWith(f + "::", StringComparison.Ordinal)))
            .Select(r => $"{file}: {r}"))];

        Assert.Empty(found);
    }

    [Fact]
    public void Own_assemblies_do_not_target_the_game_process()
    {
        // ゲームのプロセス名や実行ファイル名で探して接続しない。取得対象は利用者がシステムの選択 UI で選ぶ（仕様書 7.1）
        List<string> found = [.. OwnAssemblies.SelectMany(file => UserStringsOf(PathOf(file))
            .Where(s => s.Contains("destiny2", StringComparison.OrdinalIgnoreCase) || s.Contains(".exe", StringComparison.OrdinalIgnoreCase))
            .Select(s => $"{file}: {s}"))];

        Assert.Empty(found);
    }

    [Fact]
    public void The_only_network_endpoint_is_the_fixed_openai_responses_url()
    {
        // AI 翻訳の送信先は固定し、任意の URL や他社 API へ本文と認証情報を送れる構成にしない（仕様書 18.2）
        string[] urls = [.. OwnAssemblies.SelectMany(file => UserStringsOf(PathOf(file)))
            .Where(s => s.Contains("http://", StringComparison.OrdinalIgnoreCase) || s.Contains("https://", StringComparison.OrdinalIgnoreCase))
            .Distinct()];

        Assert.Equal([OpenAIFixedProfile.Endpoint], urls);
    }

    [Fact]
    public void Executable_requests_no_elevation()
    {
        // 権限昇格を求めない（仕様書 17.1）。マニフェストは asInvoker
        string manifest = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "D2Translator.App", "app.manifest"));

        Assert.DoesNotContain("requireAdministrator", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("highestAvailable", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("uiAccess=\"true\"", manifest, StringComparison.Ordinal);
    }
}
