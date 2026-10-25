# D2 Chat Translator

Destiny 2 専用のリアルタイム双方向チャット翻訳ソフト（Windows / C# / .NET 10 / WPF）。

ゲーム画面に表示された英語チャットを OS の画面取得と端末内 OCR で読み取り、日本語で表示します。日本語の返信を英訳し、利用者が確認してコピーできます。ゲームへの貼り付けと送信は利用者自身が行います。

> **状態: 開発中（Phase 5 実装済み）** — 画面取得、OCR、発言の追跡、D2 用語とネットスラングの辞書（9,547 語義、未確認）による注釈、GPT-6 Luna による受信の英日翻訳と返信の日英翻訳、オーバーレイと独立ウィンドウの切替（C方式）、ホットキー、設定画面、GPU 喪失からの復旧、本文なしの診断記録、署名付き MSIX の作成手順まで揃っています。署名した MSIX のインストール、8 時間の実機試験、100 回の表示切替、IME、翻訳品質の採点は利用者が確認済みです。残りは [受入試験の証跡](docs/phase5/acceptance.md) と [残存制限一覧](docs/limitations.md) を参照してください。

## 使い方と記録

- [操作説明](docs/user-guide.md)
- [変更履歴](CHANGELOG.md)
- [既定辞書の出典と検証](docs/glossary/README.md)
- [残存制限一覧](docs/limitations.md)
- [配布、署名、更新](docs/distribution.md)
- [日本語仕様書 v1.1](docs/D2_Chat_Translator_Specification_v1.1_ja.md)
- [決定記録（D09 以降）](docs/decisions.md)
- [Phase 0 版固定表](docs/phase0/version-pinning.md)
- [Phase 1 技術検証記録](docs/phase1/verification.md)
- [Phase 2 実施記録](docs/phase2/report.md)
- [Phase 3 実施記録](docs/phase3/report.md)
- [Phase 4 実施記録](docs/phase4/report.md)
- [Phase 5 実施記録](docs/phase5/report.md)、[受入試験の証跡（A01〜A12）](docs/phase5/acceptance.md)

主な設計方針:

| 項目 | 内容 |
| --- | --- |
| 表示 | C方式（非侵入型オーバーレイと独立ウィンドウの切替） |
| 画面取得 | Windows Graphics Capture。利用者がシステムの選択 UI で選んだウィンドウまたはモニターのみ |
| OCR | Windows.Media.Ocr（端末内）。英語と日本語（ゲーム表示）の2言語で認識して行を組み立てる |
| AI 翻訳 | OpenAI Responses API、モデル `gpt-6-luna` 固定、`reasoning.effort = none` 固定 |
| ゲームへの介入 | なし（コード挿入、メモリ読み書き、入力生成、パケット解析を行わない） |

## 構成

| プロジェクト | 責務（仕様書 5.2） |
| --- | --- |
| `src/D2Translator.App` | WPF の画面（メイン独立ウィンドウ、ROI 調整）、ViewModel、起動処理 |
| `src/D2Translator.Domain` | 座標、ROI 規則、状態遷移、設定と検証、エラーコード、固定 AI プロファイル。外部依存なし |
| `src/D2Translator.Application` | 取得・OCR の境界定義、受信パイプライン、取得の状態管理 |
| `src/D2Translator.Capture.Windows` | Windows Graphics Capture と D3D11 による ROI 切出し |
| `src/D2Translator.Ocr.Windows` | Windows.Media.Ocr の呼出し、前処理、2言語の結果の合成 |
| `src/D2Translator.TextTracking` | 行の組み立て、行の解析、発言の追跡、話者の一時 ID、文字誤り率 |
| `src/D2Translator.DictionaryContext` | D2 辞書の検証付き読み込みと照合 |
| `src/D2Translator.Translation.OpenAI` | OpenAI Responses API（gpt-6-luna 固定）の要求、応答の検証、再試行 |
| `src/D2Translator.Infrastructure` | 保存先の解決、検証付き JSON の原子的な保存、API キーの DPAPI 保護、診断メタデータの保存 |
| `tests/D2Translator.Compliance.Tests` | 非侵入性の設計点検（ネイティブ関数の承認一覧、入力生成や他プロセスへの接続の不在、送信先の固定） |
| `tools/D2Translator.Eval` | OCR の文字誤り率、翻訳品質の採点用 TSV、診断記録の集計、ゲームなしの耐久試験を行う開発用ツール |

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

OCR の評価（画像と同名の .txt に正解を書く。評価画像は他プレイヤーの名前が写るため `eval-data/` に置き、コミットしない）:

```bash
dotnet run --project tools/D2Translator.Eval -- eval-data
```

翻訳品質の採点用 TSV の作成（原文を 1 行 1 例で用意する。返信の日英は `--ja-en` を付ける。`--yes` を付けると実際に送信し、API 利用料が生じる）:

```bash
dotnet run --project tools/D2Translator.Eval -- translate eval-data/translation.txt eval-data/translation.tsv
```

診断記録から性能と安定性の判定表（P01〜P04、P10〜P14）を作る（フォルダーを省くとアプリの保存先を読む）:

```bash
dotnet run --project tools/D2Translator.Eval -- diagnostics --out report.md
```

ゲームなしの耐久試験（自プロセスの模擬チャット画面を取得し、偽の API で翻訳まで回す。外部送信なし）:

```bash
dotnet run --project tools/D2Translator.Eval -- soak --minutes 60
```

MSIX パッケージの作成（LICENSE、第三者通知、SBOM を同梱。署名は証明書を渡した場合のみ。スクリプトは証明書の作成や OS 設定の変更を行いません。手順は [配布、署名、更新](docs/distribution.md)）:

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
| Phase 2 OCR、行追跡、辞書注釈 | 実装済み。受入評価用の画像の収集と、実機での追跡確認が残る（[記録](docs/phase2/report.md)） |
| Phase 3 辞書、文脈、GPT-6 Luna 接続 | 実装済み。実 API での接続確認と翻訳品質の評価が残る（[記録](docs/phase3/report.md)） |
| Phase 4 C方式、返信入力、コピー、設定の統合 | 実装済み。ゲーム上でオーバーレイ、返信、コピー、ホットキーを確認（[記録](docs/phase4/report.md)） |
| Phase 5 安定性、セキュリティ、実機共存、配布検証 | 実装済み。利用者の実機確認（署名した MSIX、8 時間、100 回切替、IME、翻訳品質）済み（[記録](docs/phase5/report.md)、[証跡](docs/phase5/acceptance.md)） |
| Phase 6 | 未着手 |

## 操作

| 操作 | 方法 |
| --- | --- |
| 表示方式の切替（独立ウィンドウ／オーバーレイ） | 「表示切替」または Ctrl+Alt+O |
| オーバーレイの移動 | 「オーバーレイ配置…」→ ドラッグ → Esc かもう一度ボタン |
| オーバーレイを隠す・表示（取得は続く） | Ctrl+Alt+H |
| 取得と翻訳の一時停止・再開 | 「開始」「停止」または Ctrl+Alt+T |
| 返信 | 「返信…」または Ctrl+Alt+J → 日本語を入力して Ctrl+Enter → 英訳を確認・編集 →「コピー」→ ゲームのチャット欄に貼り付けて自分で送信 |
| 設定 | 「設定…」（ホットキーの変更もここで行う） |

ボタンには Alt と組み合わせるアクセスキーがあります（例: 開始は Alt+S）。オーバーレイはボーダーレスかウィンドウ表示のゲームの上に表示します。排他フルスクリーンには対応しません。詳しくは [操作説明](docs/user-guide.md)。

## クラウド翻訳の使い方

1. 「設定…」の「翻訳」タブを開き、外部へ送信する内容を確認して「上記の内容を確認しました」を選ぶ。
2. 「クラウド翻訳を有効にする」を選ぶ。
3. 自分の OpenAI API キーを登録する（Windows のユーザー単位で暗号化して保存し、値は再表示しない）。
4. 「接続確認」で 1 回だけ翻訳を試す（API 利用料が生じ得る）。

OpenAI 側でも予算上限を設定してください。アプリの推計額は請求額を保証しません。

## ライセンス

[MIT License](LICENSE)。配布物に含まれる第三者のソフトウェアのライセンスは [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照してください。
