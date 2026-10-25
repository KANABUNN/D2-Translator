# 配布、署名、更新

仕様書 6.2、17.3、A12、T18 に対応する手順。署名の方針は決定 D30。

## 配布物

`scripts/package-msix.ps1` が `artifacts/msix/` に作る。

| ファイル | 内容 |
| --- | --- |
| `D2ChatTranslator_<版>_x64.msix` | 自己完結型の .NET 10（win-x64）を含む MSIX。中に `LICENSE.txt`、`CHANGELOG.md`（変更点、対応構成、未解決事項）、`THIRD-PARTY-NOTICES.md`、`sbom.cdx.json` を同梱する |
| `D2ChatTranslator_<版>_x64.msix.sha256` | パッケージの SHA-256 |
| `sbom.cdx.json` | SBOM（CycloneDX 1.5）。同梱する NuGet パッケージとランタイムの版、SHA-512、ライセンス |
| `THIRD-PARTY-NOTICES.md` | 第三者のライセンス表示。リポジトリ直下にも同じものを置く |

API キー、利用者の設定、診断記録は配布物に含めない。

## 作り方

必要なもの: Windows x64、`global.json` の .NET SDK、Windows SDK（`makeappx.exe` と `signtool.exe`、10.0.19041 以降）。

未署名で作る（インストールはできない。中身の確認や CI 用）:

```bash
powershell -ExecutionPolicy Bypass -File scripts/package-msix.ps1
```

発行は `ContinuousIntegrationBuild=true` で行い、ソースのパスを埋め込まない。依存物の版は `packages.lock.json` で固定している（復元ロック）。

版は `Directory.Build.props` の `<Version>`（例: `0.1.1`）だけで決め、MSIX の版はそれに `.0` を付けたもの（`0.1.1.0`）になる。`-Version` で上書きもできる。版を上げたら [CHANGELOG.md](../CHANGELOG.md) に変更点を書く。

## 署名

MSIX はマニフェストの `Publisher` と署名証明書の `Subject` が一致しないとインストールできない。スクリプトは証明書を渡すと Subject を読み取って `Publisher` に使う。スクリプトは証明書を作らず、信頼の設定も変えない。

### 個人で使う場合（自己署名）

証明書を作り、自分の PC で信頼する。証明書の信頼は Windows のセキュリティ設定の変更なので、内容を理解したうえで自分で実行する。

1. コード署名用の証明書を作る（現在のユーザーの個人ストアに入る）

```powershell
New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=D2ChatTranslator Personal' -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature -NotAfter (Get-Date).AddYears(2)
```

2. 表示された拇印（Thumbprint）で署名する

```powershell
.\scripts\package-msix.ps1 -CertificateThumbprint <拇印>
```

3. 公開鍵だけを書き出し、管理者として開いた PowerShell で「信頼されたユーザー」に入れる

```powershell
Export-Certificate -Cert Cert:\CurrentUser\My\<拇印> -FilePath .\d2ct-signing.cer
```

```powershell
# 管理者として開いた PowerShell で実行する
Import-Certificate -FilePath .\d2ct-signing.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

自分で作った証明書以外は信頼しない。不要になったら「証明書の管理（certlm.msc）」の「信頼されたユーザー」から削除する。

PFX ファイルで署名する場合は `-PfxPath` と `-PfxPassword` を使う。このとき `signtool` は秘密鍵を現在のユーザーの鍵ストア（`%APPDATA%\Microsoft\Crypto\Keys`）に残すため、繰り返し使うなら証明書ストアと拇印を使う方法を勧める。

### 他の人へ配る場合

自己署名の配布物は、受け取った人が証明書を信頼しないとインストールできず、その操作を求めることは勧められない。公的に信頼されるコード署名証明書（認証局のコード署名証明書、Azure Trusted Signing、オープンソース向けの署名サービス等）で署名し、時刻印（既定は `http://timestamp.digicert.com`、`-TimestampUrl` で変更）を付ける。公開配布用の証明書は未取得（決定 D30）。

### 署名の確認

```powershell
Get-AuthenticodeSignature .\artifacts\msix\D2ChatTranslator_0.1.1.0_x64.msix | Format-List Status, StatusMessage, SignerCertificate
```

`Status` が `Valid` ならインストールできる。自己署名の証明書を信頼していない場合は `UnknownError`（信頼されていないルート）になる。

## インストール

1. 配布元の SHA-256 と一致することを確かめる

```powershell
(Get-FileHash .\D2ChatTranslator_0.1.1.0_x64.msix -Algorithm SHA256).Hash
```

2. MSIX をダブルクリックするか、次を実行する

```powershell
Add-AppxPackage .\D2ChatTranslator_0.1.1.0_x64.msix
```

3. 初回は「設定…」の「翻訳」タブで API キーを登録し直す。開発実行（`dotnet run`）とインストール版では保存先が異なり（開発実行は `%LOCALAPPDATA%\D2ChatTranslator`、インストール版はパッケージの LocalState）、設定と API キーは引き継がれない

Windows の OCR は、英語（米国）と日本語の言語機能が入っている必要がある（「設定」→「時刻と言語」→「言語」）。

## 更新

1. アプリを閉じる（ゲームのプレイ中に更新しない。アプリは自動更新しない。起動中は更新が失敗する）
2. 版の大きい MSIX を同じ方法でインストールする。変更点は同梱の `CHANGELOG.md` を見る

Windows は新しい版を準備できてから入れ替えるため、失敗した時は元の版が残る。設定、ROI、API キー、利用者辞書は LocalState にあり、更新では消えない。更新の前に、戻す時に使う古い版の MSIX を残しておく（同じ証明書で署名したもの）。

## 元の版へ戻す

```powershell
Add-AppxPackage -ForceUpdateFromAnyVersion .\D2ChatTranslator_<古い版>_x64.msix
```

新しい版が保存した設定を古い版が読めない場合、古い版は既定値で起動し、DATA01 を表示する。設定ファイルは書き換えないため、新しい版に戻せば設定も戻る（試験 `Settings_from_a_newer_version_are_rejected_and_kept_for_recovery`）。保存時に一世代前の設定を `settings.json.bak` に残す。0.1.0 と 0.1.1 は設定の形式が同じため、どちらへ移っても設定を引き継ぐ。

## 更新と元の版へ戻す確認（0.1.0 → 0.1.1 → 0.1.0 → 0.1.1）

T18、A12 の確認手順。PowerShell をリポジトリのフォルダーで開いて行う。

1. 今入っている版を確かめ、0.1.0.0 の MSIX（同じ証明書で署名したもの）が残っていることを確かめる

```powershell
Get-AppxPackage KANABUN.D2ChatTranslator | Select-Object Version
Get-AuthenticodeSignature .\artifacts\msix\D2ChatTranslator_0.1.0.0_x64.msix | Select-Object Status
```

2. 0.1.1.0 を作り、自分の証明書で署名する（0.1.0.0 の MSIX は上書きされない）

```powershell
.\scripts\package-msix.ps1 -CertificateThumbprint <拇印>
```

3. アプリを閉じてから 0.1.1.0 へ更新する

```powershell
Add-AppxPackage .\artifacts\msix\D2ChatTranslator_0.1.1.0_x64.msix
Get-AppxPackage KANABUN.D2ChatTranslator | Select-Object Version
```

4. アプリを起動し、診断欄の「版」が 0.1.1 であること、設定、ROI、API キーを入れ直さずに取得と翻訳が動くことを確かめる
5. アプリを閉じてから 0.1.0.0 へ戻す。0.1.0 には「版」の表示がないため、版は `Get-AppxPackage` で確かめる。設定、ROI、API キーが残っていることを確かめる

```powershell
Add-AppxPackage -ForceUpdateFromAnyVersion .\artifacts\msix\D2ChatTranslator_0.1.0.0_x64.msix
Get-AppxPackage KANABUN.D2ChatTranslator | Select-Object Version
```

6. もう一度 3 の手順で 0.1.1.0 へ更新する
7. 起動ごとの版は診断記録に残る。次で実行（セッション）ごとの版を一覧できる

```powershell
$family = (Get-AppxPackage KANABUN.D2ChatTranslator).PackageFamilyName
dotnet run --project tools/D2Translator.Eval -- diagnostics "$env:LOCALAPPDATA\Packages\$family\LocalState\diagnostics" --all --out update-check.md
```

## アンインストール

「設定」→「アプリ」から削除するか、次を実行する。LocalState（設定、API キー、診断記録）も削除される。

```powershell
Get-AppxPackage KANABUN.D2ChatTranslator | Remove-AppxPackage
```

開発実行のデータ（`%LOCALAPPDATA%\D2ChatTranslator`）は自動では消えないため、不要なら手で削除する。

## 再現手順

```bash
dotnet restore D2Translator.slnx --locked-mode
```

```bash
dotnet publish src/D2Translator.App/D2Translator.App.csproj -c Release -r win-x64 --self-contained true -o out -p:ContinuousIntegrationBuild=true
```

2026年10月24日、開発機でクリーン後に 2 回発行し、出力の 288 ファイルすべてがバイト単位で一致した（Phase 5 記録）。
