using System.Threading.Channels;
using D2Translator.Application.Capture;
using D2Translator.Domain.Geometry;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace D2Translator.Capture.Windows;

/// <summary>
/// 1回の取得セッション。D3D11 デバイス、フレームプール、ROI 用ステージングテクスチャを所有する。
/// </summary>
internal sealed unsafe class WgcFrameGrabber : IDisposable
{
    // SDR のみ初期対応（仕様書 7.3）
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private const int BufferCount = 2;

    private static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    ];

    private readonly CaptureRequest _request;
    private readonly ChannelWriter<RoiFrame> _writer;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly Lock _gate = new();

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winRtDevice;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;

    /// <summary>作成元（取得対象を作ったスレッド）の同期コンテキスト。フレームプールの作り直しはここで行う（決定 D12）。</summary>
    private readonly SynchronizationContext? _ownerContext = SynchronizationContext.Current;

    private SizeInt32 _poolSize;
    private ID3D11Texture2D? _staging;
    private PixelSize _stagingSize;
    private long _lastEmitTimestamp;
    private long _sequence;
    private bool _disposed;

    public WgcFrameGrabber(GraphicsCaptureItem item, CaptureRequest request, ChannelWriter<RoiFrame> writer, TimeProvider time)
    {
        _request = request;
        _writer = writer;
        _time = time;
        _interval = TimeSpan.FromSeconds(1 / Math.Clamp(request.FramesPerSecond, 0.5, 30));

        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, FeatureLevels,
            out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        _device = device!;
        _context = context!;
        _winRtDevice = Direct3D11Interop.CreateWinRtDevice(_device);

        _poolSize = item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winRtDevice, PixelFormat, BufferCount, _poolSize);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(item);
        if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsCursorCaptureEnabled)))
        {
            _session.IsCursorCaptureEnabled = false;
        }

        // 取得の通知枠（IsBorderRequired）は変更しない（仕様書 7.1）
        _session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                using Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }

                SizeInt32 contentSize = frame.ContentSize;
                if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
                {
                    // 対象サイズの変更。プールを作り直し、このフレームは使わない
                    _poolSize = contentSize;
                    RecreatePool(contentSize);
                    return;
                }

                long now = _time.GetTimestamp();
                if (_lastEmitTimestamp != 0 && _time.GetElapsedTime(_lastEmitTimestamp, now) < _interval)
                {
                    return;
                }

                var frameSize = new PixelSize(contentSize.Width, contentSize.Height);
                if (frameSize.IsEmpty)
                {
                    return;
                }

                _lastEmitTimestamp = now;
                PixelRect roi = _request.Profile.Roi.ToPixels(frameSize);
                RoiFrame roiFrame = CopyRoi(frame.Surface, roi, frameSize, now);
                if (!_writer.TryWrite(roiFrame))
                {
                    roiFrame.Dispose();
                }
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException)
            {
                _writer.TryComplete(new CaptureException(CaptureFailureKind.DeviceLost, "フレームの取得に失敗しました。", ex));
            }
        }
    }

    /// <summary>
    /// フレームプールを新しい大きさで作り直す。フレームの受信スレッドから呼ぶと RPC_E_WRONG_THREAD になるため、
    /// 作成元のスレッドへ送る。作り直すまでに届いたフレームは古い大きさのまま捨てられる。
    /// </summary>
    private void RecreatePool(SizeInt32 size)
    {
        void Recreate()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    _framePool.Recreate(_winRtDevice, PixelFormat, BufferCount, size);
                }
                catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException)
                {
                    _writer.TryComplete(new CaptureException(CaptureFailureKind.DeviceLost, "取得の作り直しに失敗しました。", ex));
                }
            }
        }

        if (_ownerContext is null)
        {
            Recreate();
        }
        else
        {
            _ownerContext.Post(_ => Recreate(), null);
        }
    }

    private RoiFrame CopyRoi(IDirect3DSurface surface, PixelRect roi, PixelSize frameSize, long timestamp)
    {
        // GPU 側で ROI だけを切り出し、全画面の CPU 転送を避ける（仕様書 7.2）
        using ID3D11Texture2D source = Direct3D11Interop.GetTexture(surface);
        ID3D11Texture2D staging = EnsureStaging(roi.Size);
        var box = new Box(roi.X, roi.Y, 0, roi.Right, roi.Bottom, 1);
        _context.CopySubresourceRegion(staging, 0, 0, 0, 0, source, 0, box);

        MappedSubresource mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            return RoiFrame.Create(roi.Size, roi, frameSize, ++_sequence, _time.GetUtcNow(), timestamp, (span, size) =>
            {
                int rowBytes = size.Width * RoiFrame.BytesPerPixel;
                byte* basePtr = (byte*)mapped.DataPointer;
                for (int y = 0; y < size.Height; y++)
                {
                    new ReadOnlySpan<byte>(basePtr + (long)y * mapped.RowPitch, rowBytes)
                        .CopyTo(span.Slice(y * rowBytes, rowBytes));
                }
            });
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    private ID3D11Texture2D EnsureStaging(PixelSize size)
    {
        if (_staging is not null && _stagingSize == size)
        {
            return _staging;
        }

        _staging?.Dispose();
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)size.Width,
            Height = (uint)size.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
        _stagingSize = size;
        return _staging;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _framePool.FrameArrived -= OnFrameArrived;
            _session.Dispose();
            _framePool.Dispose();
            _staging?.Dispose();
            _winRtDevice.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }
}
