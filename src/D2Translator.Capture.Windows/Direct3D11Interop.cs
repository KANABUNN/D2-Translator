using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace D2Translator.Capture.Windows;

/// <summary>D3D11 と WinRT の Direct3D 型の相互変換。</summary>
internal static unsafe partial class Direct3D11Interop
{
    // IDirect3DDxgiInterfaceAccess (windows.graphics.directx.direct3d11.interop.h)
    private static readonly Guid IDirect3DDxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out nint inspectable));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        nint surfaceAbi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfaceAbi, in IDirect3DDxgiInterfaceAccessIid, out nint access));
            try
            {
                // IDirect3DDxgiInterfaceAccess::GetInterface は IUnknown の3メソッドの次
                var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(void***)access)[3];
                Guid textureIid = typeof(ID3D11Texture2D).GUID;
                nint texture;
                Marshal.ThrowExceptionForHR(getInterface(access, &textureIid, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            MarshalInterface<IDirect3DSurface>.DisposeAbi(surfaceAbi);
        }
    }
}
