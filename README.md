# D2 Chat Translator

Destiny 2 専用のリアルタイム双方向チャット翻訳ソフト（Windows / C# / .NET 10 / WPF）。

ゲーム画面に表示された英語チャットを OS の画面取得と端末内 OCR で読み取り、日本語で表示します。日本語の返信を英訳し、利用者が確認してコピーできます。ゲームへの貼り付けと送信は利用者自身が行います。

> **状態: 開発中（Phase 1 実装済み・実機検証待ち）** — 画面取得、ROI 調整、OCR の技術検証版です。翻訳はまだ行いません。

## 仕様と記録

- [日本語仕様書 v1.1](docs/D2_Chat_Translator_Specification_v1.1_ja.md)
- [決定記録（D09 以降）](docs/decisions.md)
- [Phase 0 版固定表](docs/phase0/version-pinning.md)
- [Phase 1 技術検証記録](docs/phase1/verification.md)

主な設計方針:

| 項目 | 内容 |
| --- | --- |
| 表示 | C方式（非侵入型オーバーレイと独立ウィンドウの切替） |
| 画面取得 | Windows Graphics Capture。利用者がシステムの選択 UI で選んだウィンドウまたはモニターのみ |
| OCR | Windows.Media.Ocr（端末内） |
| AI 翻訳 | OpenAI Responses API、モデル `gpt-6-luna` 固定、`reasoning.effort = none` 固定 |
| ゲームへの介入 | なし（コード挿入、メモリ読み書き、入力生成、パケット解析を行わない） |

## 構成

| プロジェクト | 責務（仕様書 5.2） |
| --- | --- |
| `src/D2Translator.App` | WPF の画面（メイン独立ウィンドウ、ROI 調整）、ViewModel、起動処理 |
| `src/D2Translator.Domain` | 座標、ROI 規則、状態遷移、設定と検証、エラーコード、固定 AI プロファイル。外部依存なし |
| `src/D2Translator.Application` | 取得・OCR の境界定義、受信パイプライン、取得の状態管理 |
| `src/D2Translator.Capture.Windows` | Windows Graphics Capture と D3D11 による ROI 切出し |
| `src/D2Translator.Ocr.Windows` | Windows.Media.Ocr の呼出しと結果の変換 |
| `src/D2Translator.Infrastructure` | 保存先の解決、検証付き JSON の原子的な保存 |

## ビルドと試験

必要なもの: Windows x64、.NET 10 SDK（`global.json` の版）

```bash
dotnet build D2Translator.slnx
```

```bash
dotnet test D2Translator.slnx
```

OCR の結合試験は OS の英語 OCR 言語機能を使います。インストールされていない環境ではスキップされます。画面取得の結合試験は自プロセスの試験ウィンドウだけを取得し、対話デスクトップのない CI ではスキップされます。

アプリの起動:

```bash
dotnet run --project src/D2Translator.App
```

MSIX パッケージの作成（署名は証明書の拇印を渡した場合のみ。スクリプトは証明書の作成や OS 設定の変更を行いません）:

```bash
powershell -ExecutionPolicy Bypass -File scripts/package-msix.ps1
```

## 非侵入性について

本アプリはゲームのプロセスに一切介入しません。ただし、これは Bungie やアンチチートによる許可、検知されないこと、アカウント制限が起きないことを保証するものではありません。詳細は仕様書 19 章を参照してください。

## 開発計画

仕様書 22 章の段階（Phase 0〜6）に沿って進めます。

| 段階 | 状態 |
| --- | --- |
| Phase 0 基盤、版固定、WGC と OCR | 完了（WPF と MSIX の試作は Phase 1 で実施、D11） |
| Phase 1 キャプチャ、ROI、独立表示 | 実装済み。D2 実機で取得を確認。日本語クライアントの行頭表示は Phase 2 で対応（[記録](docs/phase1/verification.md)） |
| Phase 2 OCR、行追跡、辞書注釈 | 未着手 |
| Phase 3〜6 | 未着手 |
