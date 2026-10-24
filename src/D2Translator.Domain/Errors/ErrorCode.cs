namespace D2Translator.Domain.Errors;

/// <summary>仕様書 14.3 の統一エラーコード。</summary>
public enum ErrorCode
{
    /// <summary>取得非対応または権限拒否。</summary>
    CAP01,

    /// <summary>対象消失、最小化、フレーム停止。</summary>
    CAP02,

    /// <summary>サイズ、DPI、ROI が無効。</summary>
    CAP03,

    /// <summary>OCR 言語機能なし、初期化失敗。</summary>
    OCR01,

    /// <summary>認識不安定、行分離不明。</summary>
    OCR02,

    /// <summary>認証失敗または権限不足。</summary>
    TR01,

    /// <summary>制限到達、429、予算上限。</summary>
    TR02,

    /// <summary>通信失敗、時間超過、5xx。</summary>
    TR03,

    /// <summary>スキーマ不一致、拒否、出力打切り。</summary>
    TR04,

    /// <summary>gpt-6-luna 利用不可、未確認の応答モデル。</summary>
    TR05,

    /// <summary>設定、辞書、保存データの不正。</summary>
    DATA01,

    /// <summary>ホットキー登録、表示方式の失敗。</summary>
    UI01,
}
