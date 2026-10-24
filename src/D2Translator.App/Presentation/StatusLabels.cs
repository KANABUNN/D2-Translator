using D2Translator.Domain.Capture;
using D2Translator.Domain.Errors;

namespace D2Translator.App.Presentation;

/// <summary>状態とエラーの利用者向け表示（仕様書 14.3、15.3）。色だけに依存しない文言にする。</summary>
internal static class StatusLabels
{
    public static string Of(CaptureState state) => state switch
    {
        CaptureState.Idle => "対象未選択",
        CaptureState.Selecting => "対象を選択中",
        CaptureState.Calibrating => "ROI 調整中",
        CaptureState.Ready => "開始できます",
        CaptureState.Running => "取得中",
        CaptureState.Paused => "停止中",
        CaptureState.ReconfigureRequired => "再調整が必要",
        CaptureState.Recovering => "復旧中",
        CaptureState.Faulted => "停止（エラー）",
        CaptureState.Stopped => "終了",
        _ => state.ToString(),
    };

    public static string Of(ErrorCode code) => code switch
    {
        ErrorCode.CAP01 => "CAP01 取得できません。対象を選び直すか、Windows の設定を確認してください。",
        ErrorCode.CAP02 => "CAP02 対象が閉じられたか、新しいフレームが届いていません。対象を選び直してください。",
        ErrorCode.CAP03 => "CAP03 画面のサイズまたは縦横比が変わりました。ROI を再調整してください。",
        ErrorCode.OCR01 => "OCR01 英語の OCR を使えません。Windows の設定で「英語（米国）」の言語機能を追加してください。",
        ErrorCode.OCR02 => "OCR02 認識が不安定です。",
        ErrorCode.DATA01 => "DATA01 設定ファイルが不正です。",
        ErrorCode.UI01 => "UI01 ホットキーまたは表示方式を設定できません。",
        _ => code.ToString(),
    };

    public static string Of(RoiCheckStatus status) => status switch
    {
        RoiCheckStatus.Ok => "問題ありません",
        RoiCheckStatus.InvalidRoi => "ROI が画像の範囲外か、幅または高さがゼロです",
        RoiCheckStatus.EmptyFrame => "画像を取得できていません",
        RoiCheckStatus.AspectRatioChanged => "調整時から画面の縦横比が変わりました",
        RoiCheckStatus.TooSmall => "ROI が小さすぎます",
        RoiCheckStatus.ExceedsOcrLimit => "ROI が OCR の画像上限を超えています",
        _ => status.ToString(),
    };
}
