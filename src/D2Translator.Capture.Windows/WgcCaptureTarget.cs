using D2Translator.Application.Capture;
using D2Translator.Domain.Geometry;
using Windows.Graphics.Capture;
using WinRT.Interop;

namespace D2Translator.Capture.Windows;

/// <summary>
/// 利用者が <see cref="GraphicsCapturePicker"/> で選んだウィンドウまたはモニター（仕様書 7.1）。
/// ウィンドウの自動列挙や、選択 UI・通知枠の回避は行わない。
/// </summary>
public sealed class WgcCaptureTarget : ICaptureTarget
{
    private readonly TimeProvider _time;

    private WgcCaptureTarget(GraphicsCaptureItem item, TimeProvider time)
    {
        Item = item;
        _time = time;
        Item.Closed += OnItemClosed;
    }

    internal GraphicsCaptureItem Item { get; }

    public string DisplayName => Item.DisplayName;

    public PixelSize CurrentSize => new(Item.Size.Width, Item.Size.Height);

    public bool IsClosed { get; private set; }

    internal event EventHandler? Closed;

    public static bool IsSupported() => GraphicsCaptureSession.IsSupported();

    /// <summary>システムの選択 UI を開く。取り消されたら null。</summary>
    public static async Task<WgcCaptureTarget?> PickAsync(nint ownerWindow, TimeProvider time)
    {
        if (!IsSupported())
        {
            throw new CaptureException(CaptureFailureKind.Unsupported, "この環境は Windows Graphics Capture に対応していません。");
        }

        var picker = new GraphicsCapturePicker();
        InitializeWithWindow.Initialize(picker, ownerWindow);
        GraphicsCaptureItem? item = await picker.PickSingleItemAsync();
        return item is null ? null : new WgcCaptureTarget(item, time);
    }

    public ICaptureSource CreateSource() => new WgcCaptureSource(this, _time);

    public void Dispose()
    {
        Item.Closed -= OnItemClosed;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        IsClosed = true;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
