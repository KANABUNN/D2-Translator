using Vortice.DXGI;

namespace D2Translator.Capture.Windows;

/// <summary>
/// 自プロセスが使っている GPU メモリ（仕様書 16.2 P11）。DXGI の QueryVideoMemoryInfo は呼出し元プロセスの使用量を返す。
/// 他のプロセス（ゲームを含む）の値は読まない。
/// </summary>
public static class GpuMemoryProbe
{
    /// <returns>専用（Local）と共有（NonLocal）の使用量（バイト、全アダプターの合計）。取得できなければ null。</returns>
    public static (long Local, long NonLocal)? TryQuery()
    {
        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            long local = 0, nonLocal = 0;
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success; i++)
            {
                using (adapter)
                using (IDXGIAdapter3? adapter3 = adapter.QueryInterfaceOrNull<IDXGIAdapter3>())
                {
                    if (adapter3 is null)
                    {
                        continue;
                    }

                    local += (long)adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage;
                    nonLocal += (long)adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.NonLocal).CurrentUsage;
                }
            }

            return (local, nonLocal);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
