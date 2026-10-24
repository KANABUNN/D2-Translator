using System.Runtime.InteropServices.WindowsRuntime;
using D2Translator.Application.Capture;
using D2Translator.Domain.Geometry;
using D2Translator.Domain.Ocr;
using D2Translator.Ocr.Windows;
using D2Translator.TextTracking.Metrics;
using D2Translator.TextTracking.Parsing;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

// 使い方:
//   D2Translator.Eval <フォルダー> [--lang en-US] [--client ja] [--scale 2]
//   D2Translator.Eval translate <原文の一覧.txt> <結果.tsv> [--ja-en] [--yes]
//   D2Translator.Eval diagnostics [診断記録のフォルダー] [--all] [--out 結果.md]
//   D2Translator.Eval soak [--minutes 30] [--out フォルダー]
//
// OCR の評価: フォルダー内の画像（png/jpg）ごとに、同名の .txt を正解として読む。正解の各行は次のどちらか。
//   本文だけを比べる行:   > Sounds bugged
//   行全体を比べる行:     [ファイアチーム]宛て: Need two more
// 空行と # で始まる行は無視する。正解ファイルがない画像は認識結果だけを表示する。
// 評価画像には他プレイヤーの名前が写るため、リポジトリに入れない（eval-data/ は .gitignore 済み）。
//
// 翻訳の評価（仕様書 16.3 P08、P09）: 原文を 1 行 1 例で GPT-6 Luna に送り、評価者 2 名が採点する TSV を作る。
// 既定は受信の英日。--ja-en で返信の日英を評価する。
// アプリで登録した API キー（DPAPI）を使い、API 利用料が生じる。--yes がなければ件数と推計費用だけを表示する。
//
// 性能と安定性の集計（仕様書 16.2 P01〜P04、P10〜P14）: アプリの診断記録を読み、判定表を作る。
// フォルダーを省くとアプリのデータ保存先の diagnostics を読む。

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: D2Translator.Eval <folder> [--lang en-US] [--client ja] [--scale 2]");
    Console.Error.WriteLine("       D2Translator.Eval translate <input.txt> <output.tsv> [--ja-en] [--yes]");
    Console.Error.WriteLine("       D2Translator.Eval diagnostics [folder] [--all] [--out report.md]");
    Console.Error.WriteLine("       D2Translator.Eval soak [--minutes 30] [--out folder]");
    return 2;
}

if (args[0] == "translate")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    return await TranslationEval.RunAsync(args[1..]);
}

if (args[0] == "diagnostics")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    return DiagnosticsReport.Run(args[1..]);
}

if (args[0] == "soak")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    return await SoakRun.RunAsync(args[1..]);
}

string folder = Path.GetFullPath(args[0]);
string language = Option("--lang") ?? "en-US";
string? client = Option("--client") ?? "ja";
int scale = int.Parse(Option("--scale") ?? "2", System.Globalization.CultureInfo.InvariantCulture);

WindowsOcrRecognizer recognizer = WindowsOcrRecognizer.TryCreate(language, client, scale, TimeProvider.System)
    ?? throw new InvalidOperationException($"OCR 言語 {language} を使えません。");
var parser = new ChatLineParser(ChatFormat.JapaneseClient);
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"OCR: {recognizer.LanguageTag} + {recognizer.ClientLanguageTag ?? "-"}、{recognizer.Scale}倍");

int totalEdits = 0, totalLength = 0;
var images = Directory.EnumerateFiles(folder)
    .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
    .Order(StringComparer.Ordinal);

foreach (string image in images)
{
    using RoiFrame frame = await LoadAsync(image);
    OcrObservation observation = await recognizer.RecognizeAsync(frame, CancellationToken.None);
    IReadOnlyList<ChatCandidate> candidates = parser.Parse(observation.Lines, observation.RoiBounds);

    Console.WriteLine();
    Console.WriteLine($"== {Path.GetFileName(image)} ({frame.Size}, OCR {observation.OcrDuration.TotalMilliseconds:0} ms)");
    foreach (OcrLine line in observation.Lines)
    {
        Console.WriteLine($"  row  {line.Text}");
    }

    foreach (ChatCandidate c in candidates)
    {
        Console.WriteLine($"  msg  {c.Kind,-6} {c.ChannelLabel ?? c.Speaker ?? "-",-24} {c.Body}");
    }

    string expectedPath = Path.ChangeExtension(image, ".txt");
    if (!File.Exists(expectedPath))
    {
        continue;
    }

    int edits = 0, length = 0;
    foreach (string raw in File.ReadAllLines(expectedPath))
    {
        string line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            continue;
        }

        bool bodyOnly = line.StartsWith('>');
        string expected = bodyOnly ? line[1..].Trim() : line;
        IEnumerable<string> hypotheses = bodyOnly ? candidates.Select(c => c.Body) : observation.Lines.Select(l => l.Text);
        (int e, int n, string? best) = CharacterErrorRate.CompareBest(expected, hypotheses);
        edits += e;
        length += n;
        Console.WriteLine($"  {(e == 0 ? "ok " : "NG ")} {e,3}/{n,-3} {expected}  <=  {best ?? "(なし)"}");
    }

    totalEdits += edits;
    totalLength += length;
    Console.WriteLine($"  CER {Rate(edits, length)}");
}

Console.WriteLine();
Console.WriteLine($"合計 CER {Rate(totalEdits, totalLength)}（{totalEdits} / {totalLength} 文字）");
return 0;

string? Option(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string Rate(int edits, int length) => length == 0 ? "-" : $"{100.0 * edits / length:0.0}%";

static async Task<RoiFrame> LoadAsync(string path)
{
    StorageFile file = await StorageFile.GetFileFromPathAsync(path);
    using IRandomAccessStream stream = await file.OpenReadAsync();
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
    PixelDataProvider data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
        new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
    byte[] pixels = data.DetachPixelData();
    var size = new PixelSize((int)decoder.PixelWidth, (int)decoder.PixelHeight);
    return RoiFrame.Create(size, new PixelRect(0, 0, size.Width, size.Height), size, 0, DateTimeOffset.UtcNow, 0,
        (span, _) => pixels.CopyTo(span));
}
