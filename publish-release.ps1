<#
.SYNOPSIS
    リリース用パッケージ（ソースコードを含まない、自己完結型の配布フォルダ）を作成する。

.DESCRIPTION
    通常の `dotnet build` は開発用の bin/Debug 配下に出力するだけで、このスクリプトの
    対象ではない。リリースを作るときだけ、このスクリプトを個別に実行する。

    - CLI（SkyrimJPStringPatcher.csproj）とGUI（SkyrimJPStringPatcherGui.csproj）を、
      どちらも自己完結型（--self-contained true、win-x64。.NETランタイムの事前
      インストール不要）で publish する。
    - CLIは出力先フォルダ直下、GUIはその中の `SkyrimJPStringPatcherGui` サブフォルダに
      分けて publish する（開発版のフォルダ名規約と同じ形——CliLocator.
      TryGetProductRoot が「SkyrimJPStringPatcherGui」という名前の祖先フォルダを
      探して親をルートとみなす、という既存ロジックをそのまま利用できる）。
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
    出力先フォルダ。省略時は ..\Releases\<このフォルダ名> （例:
    SkyrimJPTranslationSupporter_v0.54.0 なら ..\Releases\SkyrimJPTranslationSupporter_v0.54.0）。

.EXAMPLE
    .\publish-release.ps1
    既定の出力先（../Releases/<バージョンフォルダ名>）にリリースパッケージを作成する。

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
    $versionFolderName = Split-Path -Leaf $root
    $OutputDir = Join-Path (Join-Path (Split-Path -Parent $root) "Releases") $versionFolderName
}
$guiOutputDir = Join-Path $OutputDir "SkyrimJPStringPatcherGui"

Write-Host "出力先: $OutputDir"
if (Test-Path $OutputDir) {
    Write-Host "既存の出力先を削除しています（前回分の残骸によるファイル混在を防ぐため）..."
    Remove-Item -Recurse -Force $OutputDir
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $guiOutputDir | Out-Null

Write-Host "--- CLI (SkyrimJPStringPatcher) を publish ---"
dotnet publish (Join-Path $root "SkyrimJPStringPatcher.csproj") `
    -c Release -r win-x64 --self-contained true `
    -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "CLIのpublishに失敗しました（exit code $LASTEXITCODE）" }

Write-Host "--- GUI (SkyrimJPStringPatcherGui) を publish ---"
dotnet publish (Join-Path $root "SkyrimJPStringPatcherGui\SkyrimJPStringPatcherGui.csproj") `
    -c Release -r win-x64 --self-contained true `
    -o $guiOutputDir
if ($LASTEXITCODE -ne 0) { throw "GUIのpublishに失敗しました（exit code $LASTEXITCODE）" }

$importDir = Join-Path $OutputDir "Translation\import"
New-Item -ItemType Directory -Force -Path $importDir | Out-Null

# v0.54.0: 謝辞・クレジット表記。エンドユーザーの目に触れる配布物に必ず含める。
Copy-Item -Path (Join-Path $root "CREDITS.md") -Destination $OutputDir -Force

# v0.54.0: GUIはサブフォルダの中にあるため、フォルダ直下からすぐ起動できるように
# ショートカットを1つ置いておく——毎回サブフォルダへ潜る必要をなくすため。
Write-Host "--- ショートカットを作成 ---"
$guiExe = Join-Path $guiOutputDir "SkyrimJPStringPatcherGui.exe"
$shortcutPath = Join-Path $OutputDir "Skyrim JP Translation Supporter.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $guiExe
$shortcut.WorkingDirectory = $guiOutputDir
$shortcut.Description = "Skyrim JP Translation Supporter"
$shortcut.Save()
[System.Runtime.Interopservices.Marshal]::ReleaseComObject($shell) | Out-Null

Write-Host ""
Write-Host "完了: $OutputDir"
Write-Host "  SkyrimJPStringPatcher.exe（直下） / SkyrimJPStringPatcherGui\SkyrimJPStringPatcherGui.exe（サブフォルダ） / Data/ / Translation/import/ を含む"
Write-Host "  ソースコード・開発用ドキュメント（DESIGN_NOTES.md等）は含まれない"
Write-Host "  起動は直下の「Skyrim JP Translation Supporter.lnk」から（CLIは通常直接使わない）"
