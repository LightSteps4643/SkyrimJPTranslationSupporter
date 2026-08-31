<#
.SYNOPSIS
    リリース用パッケージ（ソースコードを含まない、自己完結型の配布フォルダ）を作成する。

.DESCRIPTION
    通常の `dotnet build` は開発用の bin/Debug 配下に出力するだけで、このスクリプトの
    対象ではない。リリースを作るときだけ、このスクリプトを個別に実行する。

    - CLI（SkyrimJPStringPatcher.csproj）とGUI（SkyrimJPStringPatcherGui.csproj）を、
      どちらも自己完結型（--self-contained true、win-x64。.NETランタイムの事前
      インストール不要）で publish する。
    - v0.54.2: 単一ファイル発行（-p:PublishSingleFile=true）も併用する。Nexus Modsの
      検疫（quarantine）対策——自己完結型配布は大量のランタイムDLL（実測485個の
      Portable Executable）を同梱するため、これ自体がウイルススキャンのヒューリス
      ティック検知（「実行ファイルを大量に含むバンドル」というシグナル）を誘発しやすい
      と判明した。単一ファイル化でZIP内のPEファイル数を大幅に削減し、この要因を
      弱める狙い。トリミング（-p:PublishTrimmed=true）は、Mutagenがリフレクションを
      使用しているため安全性が不明であり、あえて含めていない。
    - v0.58.0: GUIは出力先フォルダ直下、CLIはその中の `SkyrimJPStringPatcher` サブ
      フォルダに分けて publish する（CliLocator.TryGetProductRoot の新レイアウト
      検出に対応、DESIGN_NOTES.md既知の課題25.参照）。GUIをフォルダ直下に置く
      ことで、ランチャー（.bat/.lnk）を挟まず直接ダブルクリックで起動できる。
      CLIを別フォルダへ分けているのは、ユーザーが誤って直接実行してしまう混乱を
      避けるため（CLIは通常GUI経由でのみ使う）。
      【重要】同じフォルダに両方を自己完結型でpublishすると、それぞれが依存する
      ランタイムDLL（例: System.Text.Encoding.CodePages）のバージョンが食い違う場合に
      後から publish した方が前の必須ファイルを上書きし、実行時エラーになることを
      実機で確認済み（Mutagenが要求するv10.0.9を、.NETランタイム同梱のv9.0.xが
      上書きしてしまうケース）。サブフォルダを分けることでこの衝突を根本的に回避する。
    - Data/ フォルダは各csprojのContent項目により publish 時に自動でコピーされる。
    - Translation/import/（xTranslator用インポートフォルダ、ユーザーが自分の
      翻訳ファイルを置く場所）は空のまま作成しておく。
    - ソースコード（*.cs/*.csproj）・DESIGN_NOTES.md等の開発用ドキュメントは
      publish出力に含まれない（dotnet publishはビルド成果物のみを出力するため）。

.PARAMETER OutputDir
    出力先フォルダ。省略時は ..\Releases\SkyrimJPTranslationSupporter-<バージョン>。
    バージョンは `git describe --tags` から取得する（例: v0.54.0、タグ無しなら
    コミットハッシュ等）。gitが使えない場合は "unversioned" になる。

.EXAMPLE
    .\publish-release.ps1
    既定の出力先（現在のgit tagに基づく）にリリースパッケージを作成する。

.EXAMPLE
    .\publish-release.ps1 -OutputDir "D:\Dist\MyRelease"
    出力先を明示的に指定する。
#>
param(
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    Push-Location $root
    $gitVersion = (git describe --tags 2>$null)
    Pop-Location
    if ([string]::IsNullOrWhiteSpace($gitVersion)) { $gitVersion = "unversioned" }
    $OutputDir = Join-Path (Join-Path (Split-Path -Parent $root) "Releases") "SkyrimJPTranslationSupporter-$gitVersion"
}
$cliOutputDir = Join-Path $OutputDir "SkyrimJPStringPatcher"

Write-Host "出力先: $OutputDir"
if (Test-Path $OutputDir) {
    Write-Host "既存の出力先を削除しています（前回分の残骸によるファイル混在を防ぐため）..."
    Remove-Item -Recurse -Force $OutputDir
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $cliOutputDir | Out-Null

Write-Host "--- CLI (SkyrimJPStringPatcher) を publish ---"
dotnet publish (Join-Path $root "SkyrimJPStringPatcher.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $cliOutputDir
if ($LASTEXITCODE -ne 0) { throw "CLIのpublishに失敗しました（exit code $LASTEXITCODE）" }

Write-Host "--- GUI (SkyrimJPStringPatcherGui) を publish ---"
dotnet publish (Join-Path $root "SkyrimJPStringPatcherGui\SkyrimJPStringPatcherGui.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "GUIのpublishに失敗しました（exit code $LASTEXITCODE）" }

$importDir = Join-Path $OutputDir "Translation\import"
New-Item -ItemType Directory -Force -Path $importDir | Out-Null

# v0.54.0: 謝辞・クレジット表記。エンドユーザーの目に触れる配布物に必ず含める。
Copy-Item -Path (Join-Path $root "CREDITS.md") -Destination $OutputDir -Force

Write-Host ""
Write-Host "完了: $OutputDir"
Write-Host "  Skyrim_JP_Translation_Supporter.exe（直下） / SkyrimJPStringPatcher\SkyrimJPStringPatcher.exe（サブフォルダ） / Data/ / Translation/import/ を含む"
Write-Host "  ソースコード・開発用ドキュメント（DESIGN_NOTES.md等）は含まれない"
Write-Host "  起動は直下の「Skyrim_JP_Translation_Supporter.exe」から（CLIは通常直接使わない）"
