# Phase 0 版固定表

仕様書 6.1 と 17.3 に基づき、ビルドに使う SDK とパッケージの版を固定する。確認日は 2026年10月24日。

## 選定方針

- 安定版のみを直接参照する。プレビュー版を正式配布へ自動採用しない。
- 公開から14日以上経過した版のうち最新を選ぶ（新しすぎる版による供給網リスクを避けるため）。
- 版は `Directory.Packages.props` で一元管理し、`packages.lock.json` で推移的依存も固定する。CI は `RestoreLockedMode` で復元する。
- パッケージの取得元は `NuGet.config` で nuget.org のみに限定する。

## SDK と実行基盤

| 対象 | 版 | 固定方法 |
| --- | --- | --- |
| .NET SDK | 10.0.204 | `global.json`（rollForward: latestPatch） |
| .NET 実行環境 | 10.0.8 | 自己完結型の MSIX に同梱（Microsoft.NETCore.App.Runtime.win-x64、Microsoft.WindowsDesktop.App.Runtime.win-x64。Phase 5 の SBOM `docs/phase5/sbom-0.1.0.cdx.json` に版と SHA-512 を記録） |
| TFM（Windows API を使う層） | `net10.0-windows10.0.19041.0` | `Directory.Build.props` |
| TFM（Domain と Application） | `net10.0` | WPF と Windows SDK に依存しない |
| WinRT 投影 | Microsoft.Windows.SDK.NET.Ref 10.0.19041.57 | TFM から SDK が自動選択 |
| プラットフォーム | x64 | `PlatformTarget` |

## NuGet パッケージ

| パッケージ | 版 | 公開日 (UTC) | 用途 | 仕様書 |
| --- | --- | --- | --- | --- |
| CommunityToolkit.Mvvm | 8.4.2 | 2026-03-25 | ViewModel 補助 | S10 |
| Vortice.Direct3D11 | 3.8.3 | 2026-03-04 | WGC フレームの ROI 切出し | S12 |
| Vortice.DXGI | 3.8.3 | 2026-03-04 | 同上 | S12 |
| Microsoft.Extensions.DependencyInjection | 10.0.12 | 2026-09-08 | DI | 6.1 |
| Microsoft.Extensions.Logging | 10.0.12 | 2026-09-08 | ログ（本文とキーは記録しない） | 6.1 |
| Microsoft.Extensions.Logging.Abstractions | 10.0.12 | 2026-09-08 | 同上 | 6.1 |
| Microsoft.Extensions.Logging.Debug | 10.0.12 | 2026-09-08 | 開発時のログ出力 | 6.1 |
| System.Security.Cryptography.ProtectedData | 10.0.12 | 2026-09-08 | API キーの DPAPI 保護（Phase 3 で追加） | S20 |

### 試験用

| パッケージ | 版 | 公開日 (UTC) |
| --- | --- | --- |
| xunit.v3 | 3.2.2 | 2026-01-14 |
| xunit.runner.visualstudio | 3.1.5 | 2025-09-27 |
| Microsoft.NET.Test.Sdk | 18.10.1 | 2026-09-15 |

### 見送り

| パッケージ | 理由 |
| --- | --- |
| Microsoft.Windows.CsWin32 | 現時点で必要な Win32 API は `CreateDirect3D11DeviceFromDXGIDevice` の1件のみで、`LibraryImport` で足りる。最新安定版 0.3.367 は公開2日後のため不採用。オーバーレイとホットキー（Phase 4）で Win32 API が増える時点で再評価する |
| Tesseract | 初期版の必須依存にしない（仕様書 6.1） |

## 注意が必要な推移的依存

| パッケージ | 版 | 状況 |
| --- | --- | --- |
| SharpGen.Runtime | 2.4.2-beta | Vortice 3.8.3 が依存するプレビュー版。Vortice の安定版はいずれも SharpGen のベータ版に依存しており、置換できない。正式配布前に、代替（CsWin32 による D3D11 呼出し等）と比較して採否を決める（未検証事項 U07）。Phase 5 の非侵入性の点検では、呼び出すネイティブ関数に禁止事項（他プロセスへの接続、入力生成等）は含まれなかった |
| SharpGen.Runtime.COM | 2.4.2-beta | 同上 |
| Vortice.DirectX | 3.8.3 | Vortice の共通部 |
| Vortice.Mathematics | 2.1.0 | `Box` 型など |

## 開発機の環境（参考記録）

| 項目 | 値 |
| --- | --- |
| OS | Windows 10 Pro 22H2（ビルド 19045） |
| GPU | NVIDIA GeForce RTX 4060 Ti（ドライバー 32.0.15.8180） |
| Windows SDK ツール | 10.0.19041.0（makeappx、signtool） |

開発機は Windows 10 であり、仕様書 2.1 の正式対応 OS（Windows 11 x64）ではない。この環境での動作結果は正式対応の根拠にしない。
