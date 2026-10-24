using System.Diagnostics.CodeAnalysis;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D2Translator.App.Presentation;
using D2Translator.Application.Capture;
using D2Translator.Application.Ocr;
using D2Translator.Domain.Capture;
using D2Translator.Domain.Chat;
using D2Translator.Domain.Geometry;
using D2Translator.Domain.Ocr;
using D2Translator.TextTracking.Parsing;

namespace D2Translator.App.ViewModels;

/// <summary>
/// UI08 ROI 調整。対象全体を低頻度でプレビューし、利用者が矩形を選ぶ。
/// 全体画像はこの画面を開いている間だけメモリに置き、保存も外部送信もしない。
/// </summary>
public sealed partial class RoiCalibrationViewModel : ObservableObject, IAsyncDisposable
{
    private const double PreviewFramesPerSecond = 2;
    private const string OcrUnavailableMessage = "OCR を使えないため試し読みできません（OCR01）。";

    private readonly ICaptureTarget _target;
    private readonly IOcrRecognizer? _ocr;
    private readonly TimeProvider _time;
    private readonly Guid _profileId;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _frameGate = new();
    private readonly ChatLineParser _parser = new(ChatFormat.JapaneseClient);

    private ICaptureSource? _source;
    private Task? _previewTask;
    private byte[]? _latestFrame;
    private PixelSize _latestSize;
    private PixelSize _displayedSize;
    private NormalizedRect? _selection;
    private volatile bool _frozen;

    public RoiCalibrationViewModel(ICaptureTarget target, CaptureProfile? current, IOcrRecognizer? ocr, TimeProvider time)
    {
        _target = target;
        _ocr = ocr;
        _time = time;
        _profileId = current?.Id ?? Guid.NewGuid();
        _selection = current?.Roi;
        _dispatcher = Dispatcher.CurrentDispatcher;

        TargetName = target.DisplayName;
        StatusText = "プレビューを準備しています…";
        SelectionText = "チャット欄をドラッグで囲んでください。";
        TestResultText = string.Empty;
    }

    public CaptureProfile? Result { get; private set; }

    public event EventHandler? CloseRequested;

    public string TargetName { get; }

    [ObservableProperty]
    public partial WriteableBitmap? Preview { get; set; }

    [ObservableProperty]
    public partial int FrameWidth { get; set; }

    [ObservableProperty]
    public partial int FrameHeight { get; set; }

    /// <summary>プレビュー上の選択矩形（取得画像のピクセル）。</summary>
    [ObservableProperty]
    public partial PixelRect? SelectionPixels { get; set; }

    [ObservableProperty]
    public partial string SelectionText { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial string TestResultText { get; set; }

    /// <summary>チャットが消える前に画面を止めて選択しやすくする。</summary>
    [ObservableProperty]
    public partial bool IsPreviewFrozen { get; set; }

    partial void OnIsPreviewFrozenChanged(bool value) => _frozen = value;

    public void StartPreview()
    {
        _source = _target.CreateSource();
        _previewTask = Task.Run(() => PreviewLoopAsync(_source, _cts.Token));
    }

    /// <summary>ドラッグの2点（表示中の取得画像のピクセル座標）を選択に反映する。UI スレッドで呼ぶ。</summary>
    public void SetSelection(double x1, double y1, double x2, double y2)
    {
        if (_displayedSize.IsEmpty)
        {
            return;
        }

        PixelRect rect = PixelRect.FromCorners(x1, y1, x2, y2, _displayedSize);
        if (rect.IsEmpty)
        {
            return;
        }

        _selection = NormalizedRect.FromPixels(rect, _displayedSize);
        UpdateSelectionDisplay();
    }

    [RelayCommand]
    private async Task TestReadAsync()
    {
        if (_ocr is null)
        {
            TestResultText = OcrUnavailableMessage;
            return;
        }

        if (!TryCropSelection(out RoiFrame? frame, out string? problem))
        {
            TestResultText = problem;
            return;
        }

        using (frame)
        {
            try
            {
                OcrObservation observation = await _ocr.RecognizeAsync(frame, _cts.Token);
                if (observation.Lines.Count == 0)
                {
                    TestResultText = "（文字を認識できませんでした）";
                    return;
                }

                IReadOnlyList<ChatCandidate> candidates = _parser.Parse(observation.Lines, observation.RoiBounds);
                string rows = string.Join(Environment.NewLine, observation.Lines.Select(l => l.Text));
                string messages = candidates.Count == 0
                    ? "（発言として解析できる行はありません）"
                    : string.Join(Environment.NewLine, candidates.Select(Describe));
                TestResultText =
                    $"■ 読み取った行（{observation.Lines.Count} 行、{observation.OcrDuration.TotalMilliseconds:0} ms）{Environment.NewLine}{rows}" +
                    $"{Environment.NewLine}{Environment.NewLine}■ 発言としての解析（入力欄は除外）{Environment.NewLine}{messages}";
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static string Describe(ChatCandidate candidate)
    {
        string who = candidate.Kind switch
        {
            ChatLineKind.Own => $"自分 → {candidate.ChannelLabel}",
            ChatLineKind.Player => candidate.Speaker ?? "不明",
            _ => "システム",
        };
        return $"{who}{(candidate.IsUncertain ? "（要確認）" : string.Empty)}: {candidate.Body}";
    }

    [RelayCommand]
    private void Save()
    {
        if (_selection is not NormalizedRect selection || _displayedSize.IsEmpty)
        {
            StatusText = "保存する前に ROI を選択してください。";
            return;
        }

        var profile = new CaptureProfile
        {
            Id = _profileId,
            TargetDisplayName = _target.DisplayName,
            Roi = selection,
            FrameSize = _displayedSize,
            UpdatedAt = _time.GetUtcNow(),
        };

        RoiCheck check = RoiRules.Check(profile, _displayedSize, _ocr?.MaxImageDimension);
        if (!check.IsOk)
        {
            StatusText = $"保存できません: {StatusLabels.Of(check.Status)}";
            return;
        }

        Result = profile;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private async Task PreviewLoopAsync(ICaptureSource source, CancellationToken ct)
    {
        // 全体を ROI として低頻度で取得する。保存用のプロファイルではない
        var previewProfile = new CaptureProfile { Id = Guid.Empty, Roi = NormalizedRect.Full, FrameSize = _target.CurrentSize };
        try
        {
            await foreach (RoiFrame frame in source.CaptureAsync(new CaptureRequest(previewProfile, PreviewFramesPerSecond), ct))
            {
                using (frame)
                {
                    if (_frozen)
                    {
                        continue;
                    }

                    byte[] copy = frame.Pixels.ToArray();
                    PixelSize size = frame.Size;
                    lock (_frameGate)
                    {
                        _latestFrame = copy;
                        _latestSize = size;
                    }

                    await _dispatcher.InvokeAsync(() => ShowFrame(copy, size));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (CaptureException ex)
        {
            await _dispatcher.InvokeAsync(() => StatusText = StatusLabels.Of(ex.Code));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // GPU デバイスを作れない等。例外全文は表示しない
            await _dispatcher.InvokeAsync(() => StatusText = $"プレビューを取得できません（{ex.GetType().Name}）。");
        }
    }

    private void ShowFrame(byte[] pixels, PixelSize size)
    {
        if (Preview is null || _displayedSize != size)
        {
            Preview = BgraBitmap.Create(size);
            FrameWidth = size.Width;
            FrameHeight = size.Height;
            _displayedSize = size;
            UpdateSelectionDisplay();
        }

        BgraBitmap.Write(Preview, pixels, size);
        StatusText = $"プレビュー中（{size}、{PreviewFramesPerSecond:0} 回毎秒）";
    }

    private void UpdateSelectionDisplay()
    {
        if (_selection is not NormalizedRect selection || _displayedSize.IsEmpty)
        {
            SelectionPixels = null;
            return;
        }

        PixelRect pixels = selection.ToPixels(_displayedSize);
        SelectionPixels = pixels;
        SelectionText = $"ROI: {pixels}（正規化 {selection}）";
    }

    private bool TryCropSelection([NotNullWhen(true)] out RoiFrame? frame, [NotNullWhen(false)] out string? problem)
    {
        frame = null;
        problem = null;
        byte[]? source;
        PixelSize size;
        lock (_frameGate)
        {
            source = _latestFrame;
            size = _latestSize;
        }

        if (source is null || _selection is not NormalizedRect selection)
        {
            problem = "プレビューと ROI の選択が必要です。";
            return false;
        }

        var profile = new CaptureProfile { Id = _profileId, Roi = selection, FrameSize = size };
        RoiCheck check = RoiRules.Check(profile, size, _ocr?.MaxImageDimension);
        if (!check.IsOk)
        {
            problem = StatusLabels.Of(check.Status);
            return false;
        }

        PixelRect region = check.PixelRoi!.Value;
        frame = RoiFrame.Create(region.Size, region, size, 0, _time.GetUtcNow(), _time.GetTimestamp(),
            (span, _) => BgraBitmap.CopyRegion(source, size, region, span));
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_previewTask is not null)
        {
            await _previewTask.ConfigureAwait(false);
        }

        if (_source is not null)
        {
            await _source.DisposeAsync();
        }

        _cts.Dispose();
        lock (_frameGate)
        {
            _latestFrame = null;
        }
    }
}
