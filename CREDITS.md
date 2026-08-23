# 謝辞・クレジット (Credits)

Skyrim JP Translation Supporter は、以下の素晴らしい先人たちの技術・データ・知見の上に成り立っています。深く感謝いたします。

## 前提となっているMOD・ツール

### DSD (Dynamic String Distributor)
本ツールが最終的に出力する翻訳データの配信先です。ESP/ESMを一切改変せず、非破壊的に文字列を差し替えるSKSEプラグインで、本ツールが生成した翻訳JSONはこのMODを通じてゲームに反映されます。

- 作者: SkyHorizon3
- Nexus: https://www.nexusmods.com/skyrimspecialedition/mods/107676
- GitHub: https://github.com/SkyHorizon3/SSE-Dynamic-String-Distributor

### xTranslator
個別MOD翻訳エディタです。本ツールの設計方針（MOD横断でのロードオーダー全体の翻訳状況把握という差別化）を固める際の比較対象とし、また同ツールが出力するSST/XML形式のコミュニティ翻訳ファイルを自動取り込みする機能（xTranslatorインポーター）で活用させていただいています。

- 作者: MGuffin
- Nexus: https://www.nexusmods.com/skyrimspecialedition/mods/134
- GitHub: https://github.com/MGuffin/xTranslator

### Mutagen
Skyrimのプラグインファイル（ESP/ESM）を読み書きするための.NETライブラリです。本ツールのMOD読み込み処理（PickUpTargetステージ）の中核として利用させていただいています。

- プロジェクト: Mutagen
- GitHub: https://github.com/Mutagen-Modding/Mutagen
- ライセンス: GPL-3.0 license

### Skyrim Special Edition Mod データベース、および日本語翻訳を公開してくださるコミュニティの皆様
本ツールは、xTranslator形式で公開されているコミュニティ製の日本語翻訳ファイルを取り込む機能（xTranslatorインポーター）を持っています。そうした翻訳ファイルの多くは、以下のサイトの各MODページを通じて公開されています。サイト運営者様、および各MODページで翻訳を公開してくださっている翻訳者の皆様に感謝いたします。

- サイト: Skyrim Special Edition Mod データベース
- URL: https://skyrimspecialedition.2game.info

## 対訳リファレンス

### スカイリム日英対訳表
Bethesda公式ローカライズ文字列を集約した英日対訳表です。本ツールのコーパスの一部として、翻訳の参考データに活用させていただいています。

- 公開者: 半透明様
- URL: https://ss1.xrea.com/croatoan.s323.xrea.com/Skyrim/taiyaku_.html

## ライセンスについて

上記各プロジェクトのライセンス条件は、それぞれのリポジトリ・配布ページをご確認ください。本ツール自体のライセンスについては別途定めます。
