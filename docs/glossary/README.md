# 既定辞書の出典と検証

同梱の既定辞書 `src/D2Translator.DictionaryContext/Data/glossary-default.json`（辞書版 `d2-chat-2026-10-24-v1`、9,547 語義）の出典、検証結果、作成時の記録。2026年10月25日、利用者が作成した辞書パッケージ `D2-Translator-Glossary-2026-10-24.zip` から取り込んだ（決定 D33）。

| ファイル | 内容 |
| --- | --- |
| [PACKAGE-README.md](PACKAGE-README.md) | パッケージの説明（収録内容、品質と対応範囲、出典）。パッケージ内の名前は README.md |
| [VALIDATION.md](VALIDATION.md) | 作成時の検証内容と限界 |
| [CURATION_NOTES.md](CURATION_NOTES.md) | ネットスラングと D2 用語の収集メモ、確認の水準、多義語 |
| `glossary-sources.json` | 出典、Bungie Manifest の版（`245114.26.10.13.2000-1-bnet.66137`）、取得 URL と SHA-256、項目ごとの対応 ID |
| `ambiguity-audit.json` | 同じ照合表記に複数の語義がある項目 |
| `reference-only-common-names.json` | 一般語との衝突が大きく、自動照合から外した公式名称 |
| `validation-result.json` | 作成時の検証結果と測定値 |
| `SHA256SUMS.txt` | パッケージ内のファイルの SHA-256 |

パッケージの `glossary-example.json` を `glossary-default.json` という名前で取り込んだ。内容は同一で、SHA-256 は `SHA256SUMS.txt` の `glossary-example.json` の値（`c522104815e6…`）と一致する。追加分だけの `glossary-user.json`（全件版と重ねて使わない）と、取り込み作業の依頼文 `CLAUDE_HANDOFF.md` は取り込んでいない。

## 扱い

- 全件が未確認（`pending`）。アプリは注釈に「未確認の辞書」と表示する。D2 を理解する日英評価者による確認は行っていない
- 英日（受信の翻訳）だけに使う。日英の返信には使っていない
- 公式名称は Bungie.net API（Destiny 2 Manifest）から取得した英語と日本語の表示名。Destiny 2 の名称は Bungie, Inc. に帰属する。本ソフトは Bungie と提携していない。配布の可否は Bungie の API 利用規約の確認が必要（未検証事項 U09）
- アプリでの検査は `tests/D2Translator.DictionaryContext.Tests/DefaultGlossaryTests.cs`
