# Skyrim JP Translation Supporter

英語版Skyrim SEのMODを、Mod Organizer 2 (MO2) のロードオーダー全体でまとめて日本語化する
作業を支援するツールです。[DSD (Dynamic String Distributor)](https://www.nexusmods.com/skyrimspecialedition/mods/107676)
向けの翻訳ファイルを、ESP/ESMを一切改変せずに生成します。

配布版（コンパイル済み、ソースコード不要ですぐ使えます）は Nexus Mods で公開しています。
https://www.nexusmods.com/skyrimspecialedition/mods/189369

このリポジトリは、[GPL-3.0](./LICENSE) でライセンスされている
[Mutagen](https://github.com/Mutagen-Modding/Mutagen) を利用しているため、
本ツール自体のソースコードもGPL-3.0で公開しています。

## 構成

- `Core/` `PickUpTarget/` `Translation/` `GenerateDsdFile/` `Program.cs` — CLI本体
  （`SkyrimJPStringPatcher.csproj`）。①MO2のロードオーダーから翻訳候補を抽出
  （PickUpTarget）→②各種手法で自動翻訳（Translation）→③DSD形式のJSONを生成
  （GenerateDsdFile）、の3ステージ構成
- `SkyrimJPStringPatcherGui/` — GUI本体（`SkyrimJPStringPatcherGui.csproj`）。
  CLIをサブプロセス起動するだけの薄い層
- `Data/` — コーパス・用語集等の同梱データ
- `CREDITS.md` — 依拠している技術・データへの謝辞
- `publish-release.ps1` — 配布用パッケージ（自己完結型、ソース非同梱）を作成するスクリプト

## ビルド方法

.NET 9 SDK が必要です。

```powershell
dotnet build SkyrimJPStringPatcher.csproj
dotnet build SkyrimJPStringPatcherGui\SkyrimJPStringPatcherGui.csproj
```

配布用パッケージ（自己完結型、win-x64）を作る場合:

```powershell
.\publish-release.ps1
```

## ライセンス

[GPL-3.0](./LICENSE)

## 謝辞

[CREDITS.md](./CREDITS.md) を参照してください。
