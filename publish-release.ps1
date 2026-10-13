<#
.SYNOPSIS
    リリース用パッケージ（ソースコードを含まない配布フォルダ）を作成する。

.DESCRIPTION
    通常の `dotnet build` は開発用の bin/Debug 配下に出力するだけで、このスクリプトの
    対象ではない。リリースを作るときだけ、このスクリプトを個別に実行する。

    【配布形態（v0.63.0〜）】
    GUI・SJPTS_InGameText・SJPTS_InterfaceText の3つを、すべて「フレームワーク依存・
    多数ファイル」で publish し、.NETランタイムは配布フォルダ内の `dotnet\` フォルダに
    1つだけ同梱する。各EXE（apphost）に「ランタイムは自分からの相対パスにある」ことを
    埋め込む（-p:AppHostDotNetSearch=AppRelative / -p:AppHostRelativeDotNet=...）。
    - 利用者は.NETを事前に導入する必要がない（自己完結型と同じ利点）。
    - フォルダのトップは、GUIのEXE（と、その設定ファイル数個）・各フォルダ・CREDITS.md
      だけになる。ランタイム（約470ファイル）は `dotnet\` の中、CLIの依存DLLは各CLIの
      フォルダの中に収まる。ランタイムは3つのEXEで共有する（コピーは1つ）。

    【なぜ単一ファイル・自己完結型をやめたか】
    Nexus Modsへのアップロードが隔離されるたびに、VirusTotalでBkav Proだけが汎用判定
    （W32.Malware.…）で検出していた。最小のHello WorldのEXEでも再現し、「.NETランタイムを
    1つのEXEに丸ごと埋め込む形」（自己完結型・単一ファイル）そのものが原因と判断した
    （2026-10-02検証。多数ファイル版・フレームワーク依存の単一ファイル版は検出なし）。
    - 自己完結型・多数ファイル（A案）はトップにDLLが約210個並ぶ。管理されたDLLを `lib\` に
      移す案は、hostpolicyが自己完結型のランタイムのDLLを「アプリのフォルダ＋ファイル名」
      だけで探すため、deps.jsonの書き換えでは移せない（実験で確認、2026-10-13）。
    - 今回の形（フレームワーク依存＋相対パスのランタイム）は、トップがすっきりし、ランタイムの
      コピーも1つで済む。Bkav Proで検出されないかは、リリース前にVirusTotalで確認すること。
    - トリミング（-p:PublishTrimmed=true）は、Mutagenがリフレクションを使用しており安全性が
      不明なため、あえて含めていない。

    【ランタイムの入手元】
    このPCにインストールされている .NET 9（`dotnet` コマンドのあるフォルダ）から、
    host\fxr・shared\Microsoft.NETCore.App・shared\Microsoft.WindowsDesktop.App（WinForms用）の
    それぞれ最新の9.x.xをコピーする（SDKのインストーラに含まれる）。言語別のリソースは
    日本語（ja）だけ残す。LICENSE.txt・ThirdPartyNotices.txt も同梱する（再配布の条件）。

    - 出力の構成: GUIは出力先フォルダ直下、CLIはそれぞれ `SJPTS_InGameText`・
      `SJPTS_InterfaceText` サブフォルダ（CliLocator / InterfaceTextCliLocator が期待する配置。
      CLIを別フォルダに分けるのは、利用者が誤って直接実行する混乱を避けるため）。
    - Data/ フォルダは各csprojのContent項目により publish 時に自動でコピーされる。
    - import\plugin・import\interface（xTranslator等のインポート用）・out（出力先）は空のまま作成する。
    - ソースコード・開発用ドキュメントは含まれない（dotnet publishはビルド成果物のみを出力する）。
      pdbも含めない。
    - 配布フォルダと同じ場所に、フォルダ名と同名のzipも作成する（Nexus Modsへの配布はzip単位）。
    - 最後に、配布フォルダの構成の確認（ランタイムの有無）と、CLIの起動確認を行う。

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

Push-Location $root
$gitVersion = (git describe --tags 2>$null)
Pop-Location
if ([string]::IsNullOrWhiteSpace($gitVersion)) { $gitVersion = "unversioned" }

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path (Join-Path (Split-Path -Parent $root) "Releases") "SkyrimJPTranslationSupporter-$gitVersion"
}

# v0.60.0: exe内に埋め込むバージョン（ウィンドウタイトル表示用）。MSBuildの
# Versionプロパティは "Major.Minor.Patch[-prerelease]" 形式を要求するため、
# タグが無い("unversioned")場合や、gitが使えない場合はそのまま渡すとpublish
# 自体が失敗する（実機で確認済み: 'unversioned'はNuGetのバージョンパーサーに
# 拒否される）。gitタグから何コミットか進んだ状態（例: v0.59.4-3-gabc1234）は
# 問題なく通ることも確認済みなので、ここでは「vX.Y.Zで始まっているか」だけを
# 緩くチェックし、それ以外は-p:Versionを渡さず.NET SDKの既定値（1.0.0）に
# フォールバックする（表示が味気なくなるだけで、publish自体は失敗しない）。
$assemblyVersionArgs = @()
if ($gitVersion -match '^v(\d+\.\d+\.\d+)') {
    $assemblyVersionArgs = @("-p:Version=$($gitVersion.TrimStart('v'))")
} else {
    Write-Host "警告: gitタグの形式が想定と異なるため（'$gitVersion'）、exeへのバージョン埋め込みをスキップします。"
}
$cliOutputDir = Join-Path $OutputDir "SJPTS_InGameText"
$interfaceTextOutputDir = Join-Path $OutputDir "SJPTS_InterfaceText"
$runtimeDir = Join-Path $OutputDir "dotnet"

# 1つのEXEをフレームワーク依存で publish する。$RelativeDotnet は、そのEXEから見た
# ランタイムのフォルダの相対パス（GUI: ./dotnet、CLI: ../dotnet）。
function Publish-App {
    param([string]$Project, [string]$Output, [string]$RelativeDotnet, [string[]]$ExtraArgs = @())
    dotnet publish $Project `
        -c Release -r win-x64 --self-contained false `
        -p:AppHostDotNetSearch=AppRelative "-p:AppHostRelativeDotNet=$RelativeDotnet" `
        -p:DebugType=none -p:DebugSymbols=false `
        @ExtraArgs `
        -o $Output
    if ($LASTEXITCODE -ne 0) { throw "publishに失敗しました: $Project（exit code $LASTEXITCODE）" }
}

# インストール済みの .NET から、ランタイムを配布フォルダの dotnet\ に、host\fxr / shared の
# レイアウトのままコピーする（apphostがこの形で探す）。
function Copy-DotnetRuntime {
    param([string]$Destination)

    $dotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
    function Get-Latest9 {
        param([string]$Dir)
        $found = Get-ChildItem -LiteralPath $Dir -Directory -ErrorAction Stop |
            Where-Object { $_.Name -match '^9\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } | Select-Object -Last 1
        if (-not $found) { throw "9.x.xが見つかりません: $Dir（.NET 9 SDKをインストールしてください）" }
        return $found
    }

    $fxr = Get-Latest9 (Join-Path $dotnetRoot "host\fxr")
    $core = Get-Latest9 (Join-Path $dotnetRoot "shared\Microsoft.NETCore.App")
    $desktop = Get-Latest9 (Join-Path $dotnetRoot "shared\Microsoft.WindowsDesktop.App")
    Write-Host "ランタイム: hostfxr $($fxr.Name) / NETCore.App $($core.Name) / WindowsDesktop.App $($desktop.Name)（$dotnetRoot）"
    if ($core.Name -ne $desktop.Name) {
        Write-Host "警告: NETCore.AppとWindowsDesktop.Appのバージョンが異なります（$($core.Name) / $($desktop.Name)）。"
    }

    foreach ($item in @(
            @{ From = $fxr.FullName; To = (Join-Path $Destination "host\fxr\$($fxr.Name)") },
            @{ From = $core.FullName; To = (Join-Path $Destination "shared\Microsoft.NETCore.App\$($core.Name)") },
            @{ From = $desktop.FullName; To = (Join-Path $Destination "shared\Microsoft.WindowsDesktop.App\$($desktop.Name)") })) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $item.To) | Out-Null
        Copy-Item -LiteralPath $item.From -Destination $item.To -Recurse
        # 言語別のリソース（cs・de・…）は、日本語（ja）以外を除く。
        Get-ChildItem -LiteralPath $item.To -Directory |
            Where-Object { $_.Name -match '^[a-z]{2,3}(-[A-Za-z]+)?$' -and $_.Name -ne 'ja' } |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
    }

    # WPF関連は同梱しない（GUIはWinFormsで、WPFは使わない。2026-10-13の検証で、これらを除いても
    # GUI・CLIが起動し、残るアセンブリにWPFへの参照がないことを確認済み。zipが約19MB小さくなる）。
    # deps.jsonには残るが、ファイルがなくてもホストはエラーにしない。
    $wpfNames = @(
        "PresentationCore", "PresentationFramework", "PresentationUI", "ReachFramework",
        "System.Printing", "System.Windows.Controls.Ribbon", "System.Windows.Input.Manipulations",
        "System.Windows.Presentation", "System.Xaml",
        "UIAutomationClient", "UIAutomationClientSideProviders", "UIAutomationProvider", "UIAutomationTypes",
        "WindowsBase", "WindowsFormsIntegration", "DirectWriteForwarder",
        "PresentationNative_cor3", "PenImc_cor3", "wpfgfx_cor3", "D3DCompiler_47_cor3")
    $desktopDest = Join-Path $Destination "shared\Microsoft.WindowsDesktop.App\$($desktop.Name)"
    Get-ChildItem -LiteralPath $desktopDest -File -Filter *.dll |
        Where-Object { $wpfNames -contains $_.BaseName -or $_.BaseName -like "PresentationFramework*" } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

    foreach ($notice in @("LICENSE.txt", "ThirdPartyNotices.txt")) {
        $path = Join-Path $dotnetRoot $notice
        if (Test-Path $path) { Copy-Item -LiteralPath $path -Destination $Destination }
    }
}

Write-Host "出力先: $OutputDir"
if (Test-Path $OutputDir) {
    Write-Host "既存の出力先を削除しています（前回分の残骸によるファイル混在を防ぐため）..."
    Remove-Item -Recurse -Force $OutputDir
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host "--- CLI (SkyrimJPStringPatcher) を publish ---"
Publish-App (Join-Path $root "SkyrimJPStringPatcher.csproj") $cliOutputDir "../dotnet"

Write-Host "--- CLI (SJPTS_InterfaceText) を publish ---"
Publish-App (Join-Path $root "SJPTS_InterfaceText\SJPTS_InterfaceText.csproj") $interfaceTextOutputDir "../dotnet"

Write-Host "--- GUI (SkyrimJPStringPatcherGui) を publish ---"
Publish-App (Join-Path $root "SkyrimJPStringPatcherGui\SkyrimJPStringPatcherGui.csproj") $OutputDir "./dotnet" $assemblyVersionArgs

Write-Host "--- .NETランタイムを同梱 ---"
Copy-DotnetRuntime $runtimeDir

# 2026-09-17: プラグイン翻訳用・Interface翻訳用のimportフォルダを、製品ルート
# 直下の"import"フォルダの下に集約（旧Translation\import・InterfaceText\
# Translation\importから移動）。実際のMODデータベースでは両方の翻訳データが
# まとめて配布されるケースが多く、共通の親フォルダの下にあった方が分かりやすい
# ため。
$pluginImportDir = Join-Path $OutputDir "import\plugin"
New-Item -ItemType Directory -Force -Path $pluginImportDir | Out-Null

$interfaceImportDir = Join-Path $OutputDir "import\interface"
New-Item -ItemType Directory -Force -Path $interfaceImportDir | Out-Null

# 2026-09-16: 出力先の"out"フォルダも、import等と同様に配布物へ空フォルダとして
# 同梱する。CLI（DsdWriter）は生成成功時に自動作成するが、初回生成前や生成失敗後に
# GUIの「出力フォルダを開く」を押すとフォルダが存在せず「フォルダがまだありません」と
# 誤って警告されるため。
$outDir = Join-Path $OutputDir "out"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# v0.54.0: 謝辞・クレジット表記。エンドユーザーの目に触れる配布物に必ず含める。
Copy-Item -Path (Join-Path $root "CREDITS.md") -Destination $OutputDir -Force

# 配布フォルダの構成の確認。ランタイムが欠けていると、利用者の環境で起動できない。
$required = @(
    "Skyrim_JP_Translation_Supporter.exe",
    "SJPTS_InGameText\SJPTS_InGameText.exe",
    "SJPTS_InterfaceText\SJPTS_InterfaceText.exe",
    "dotnet\host\fxr",
    "dotnet\shared\Microsoft.NETCore.App",
    "dotnet\shared\Microsoft.WindowsDesktop.App",
    "CREDITS.md"
)
foreach ($rel in $required) {
    if (-not (Test-Path (Join-Path $OutputDir $rel))) { throw "配布フォルダに必要なものがありません: $rel" }
}

# CLIの起動確認: 引数なしで実行すると使い方を出して終了コード1で終わる（クラッシュしないこと）。
# 同梱したランタイムで起動できることの確認になる。
Write-Host "--- CLIの起動確認 ---"
$smoke = & (Join-Path $cliOutputDir "SJPTS_InGameText.exe") 2>&1 | Out-String
if ($LASTEXITCODE -ne 1 -or $smoke -notmatch "Usage") {
    throw "CLIの起動確認に失敗しました（exit code $LASTEXITCODE）。同梱したランタイムで起動できていません:`n$smoke"
}

# 構成の概要（トップの様子・ファイル数）。
$topFiles = @(Get-ChildItem -LiteralPath $OutputDir -File)
$allFiles = @(Get-ChildItem -LiteralPath $OutputDir -File -Recurse)
$peCount = @($allFiles | Where-Object { $_.Extension -in ".dll", ".exe" }).Count
Write-Host ("構成: トップのファイル {0}個 / 全ファイル {1}個（うちDLL・EXE {2}個）" -f $topFiles.Count, $allFiles.Count, $peCount)

# v0.59.0: 配布フォルダと同じ場所に、フォルダ名と同名のzipを作成する。
$zipPath = "$OutputDir.zip"
Write-Host "--- zip圧縮 ---"
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
# 2026-10-14: Compress-Archive（Windows PowerShell 5.1）は、zip内のパスの区切りを「\」で書く
# （ZIPの仕様は「/」）。Windowsのツールは許容するが、NexusのようにLinux側でzipを解析・プレビュー
# するところでは、フォルダ階層を認識できず、「プレビュー生成に失敗」として検疫される恐れがある。
# そのため、区切りを「/」にして自分で書く。空のフォルダ（import・out）もエントリとして残す。
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipBase = [IO.Path]::GetFullPath($OutputDir).TrimEnd('\')
$zipParent = Split-Path -Parent $zipBase
$zipStream = [IO.File]::Create($zipPath)
try {
    $archive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $rootName = (Split-Path -Leaf $zipBase)
        [void]$archive.CreateEntry("$rootName/")
        foreach ($item in (Get-ChildItem -LiteralPath $zipBase -Recurse | Sort-Object FullName)) {
            $entryName = $item.FullName.Substring($zipParent.Length + 1).Replace('\', '/')
            if ($item.PSIsContainer) {
                [void]$archive.CreateEntry("$entryName/")
            } else {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $item.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal)
            }
        }
    } finally { $archive.Dispose() }
} finally { $zipStream.Dispose() }
if (-not (Test-Path $zipPath)) { throw "zipの作成に失敗しました: $zipPath" }

# zipの確認: パスの区切りが「/」で、フォルダ名のエントリから始まり、フォルダの中身（ファイル数）が揃っていること。
$check = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $badNames = @($check.Entries | Where-Object { $_.FullName.Contains('\') })
    if ($badNames.Count -gt 0) { throw "zip内のパスに「\」が含まれています（例: $($badNames[0].FullName)）" }
    $zipFileCount = @($check.Entries | Where-Object { -not $_.FullName.EndsWith('/') }).Count
    if ($zipFileCount -ne $allFiles.Count) { throw "zipのファイル数が配布フォルダと一致しません（zip $zipFileCount / フォルダ $($allFiles.Count)）" }
} finally { $check.Dispose() }

Write-Host ""
Write-Host "完了: $OutputDir"
Write-Host "  Skyrim_JP_Translation_Supporter.exe（直下） / SJPTS_InGameText\SJPTS_InGameText.exe（サブフォルダ） /"
Write-Host "  SJPTS_InterfaceText\SJPTS_InterfaceText.exe（サブフォルダ） / dotnet\（.NETランタイム） / Data/ / import/plugin/ / import/interface/ / out/ を含む"
Write-Host "  ソースコード・開発用ドキュメント（DESIGN_NOTES.md等）は含まれない"
Write-Host "  起動は直下の「Skyrim_JP_Translation_Supporter.exe」から（CLIは通常直接使わない）"
Write-Host ("  zip: {0}（{1:N1} MB）" -f $zipPath, ((Get-Item $zipPath).Length / 1MB))
Write-Host "  【リリース前に】zipをVirusTotalに上げて、Bkav Proなどの検出が無いことを確認してください。"

# 直前のネイティブコマンド（CLIの起動確認は終了コード1が正常）の終了コードを引きずらない。
exit 0
