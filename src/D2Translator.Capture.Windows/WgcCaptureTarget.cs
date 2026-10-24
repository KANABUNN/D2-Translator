using D2Translator.Application.Capture;
using D2Translator.Domain.Geometry;
using Windows.Graphics.Capture;
using WinRT.Interop;

namespace D2Translator.Capture.Windows;

/// <summary>
/// 利用者が <see cref="GraphicsCapturePicker"/> で選んだウィンドウまたはモニター（仕様書 7.1）。
/// ウィンドウの自動列挙や、選択 UI・通知枠の回避は行わない。
/// </summary>
/// <remarks>
/// UI スレッド（STA）で作られた <see cref="GraphicsCaptureItem"/> は他スレッドから呼ぶと RPC_E_WRONG_THREAD になる。
/// 対象への操作は作成時の同期コンテキストで実行する。
/// </remarks>
public sealed class WgcCaptureTarget : ICaptureTarget
{
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _ownerContext;
    private readonly int _ownerThreadId;
    private int _disposed;

    private WgcCaptureTarget(GraphicsCaptureItem item, TimeProvider time)
    {
        Item = item;
        _time = time;
        _ownerContext = SynchronizationContext.Current;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        DisplayName = item.DisplayName;
        Item.Closed += OnItemClosed;
    }

    internal GraphicsCaptureItem Item { get; }

    public string DisplayName { get; }

    public PixelSize CurrentSize => RunOnOwner(() => new PixelSize(Item.Size.Width, Item.Size.Height));

    public bool IsClosed { get; private set; }

    internal event EventHandler? Closed;

    public static bool IsSupported() => GraphicsCaptureSession.IsSupported();

    /// <summary>システムの選択 UI を開く。UI スレッドから呼ぶ。取り消されたら null。</summary>
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

    /// <summary>
    /// HWND から取得対象を作る（仕様書 7.1 の代替経路）。結合試験で自プロセスのウィンドウを取得するためだけに使い、
    /// 他アプリのウィンドウを選択 UI なしに取得する用途には使わない。
    /// </summary>
    internal static WgcCaptureTarget CreateForWindow(nint hwnd, TimeProvider time) =>
        new(Direct3D11Interop.CreateItemForWindow(hwnd), time);

    public ICaptureSource CreateSource() => new WgcCaptureSource(this, _time);

    /// <summary>作成元のスレッドで実行する。作成元に同期コンテキストがなければその場で実行する。</summary>
    internal Task<T> RunOnOwnerAsync<T>(Func<T> func)
    {
        if (_ownerContext is null || Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            try
            {
                return Task.FromResult(func());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ownerContext.Post(_ =>
        {
            try
            {
                completion.SetResult(func());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }, null);
        return completion.Task;
    }

    internal Task RunOnOwnerAsync(Action action) => RunOnOwnerAsync(() =>
    {
        action();
        return true;
    });

    private T RunOnOwner<T>(Func<T> func)
    {
        if (_ownerContext is null || Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            return func();
        }

        T result = default!;
        Exception? error = null;
        _ownerContext.Send(_ =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, null);
        return error is null ? result : throw error;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 失敗時は対象がすでに失われているだけなので無視する
        _ = RunOnOwnerAsync(() => Item.Closed -= OnItemClosed);
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        IsClosed = true;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
