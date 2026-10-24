using D2Translator.Domain.Capture;
using D2Translator.Domain.Errors;
using D2Translator.Domain.Geometry;

namespace D2Translator.Application.Capture;

/// <param name="FramesPerSecond">処理対象の抽出頻度。OS のフレーム到着頻度とは別。</param>
public sealed record CaptureRequest(CaptureProfile Profile, double FramesPerSecond);

/// <summary>利用者がシステムの選択 UI で選んだ取得対象。</summary>
public interface ICaptureTarget : IDisposable
{
    string DisplayName { get; }

    PixelSize CurrentSize { get; }

    ICaptureSource CreateSource();
}

/// <summary>仕様書 14.1 の取得境界。</summary>
public interface ICaptureSource : IAsyncDisposable
{
    IAsyncEnumerable<RoiFrame> CaptureAsync(CaptureRequest request, CancellationToken ct);
}

public enum CaptureFailureKind
{
    /// <summary>取得非対応または開始の拒否。</summary>
    Unsupported,

    /// <summary>対象ウィンドウが閉じられた。</summary>
    TargetClosed,

    /// <summary>GPU デバイスの喪失等。</summary>
    DeviceLost,
}

public sealed class CaptureException(CaptureFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public CaptureFailureKind Kind { get; } = kind;

    public ErrorCode Code => Kind switch
    {
        CaptureFailureKind.TargetClosed => ErrorCode.CAP02,
        _ => ErrorCode.CAP01,
    };
}
