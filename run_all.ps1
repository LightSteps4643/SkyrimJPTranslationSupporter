Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope Process

# SkyrimJPStringPatcher v0.50.0 - 一括実行スクリプト
# pickuptarget -> translation(--all --llm) -> generatedsdfile を順に実行する。
# いずれかの段階が失敗したら即座に停止する。
#
# TODO: pickuptarget/translationのout_tempは実行のたびに自動クリアされない
# （DESIGN_NOTES.md「既知の課題・次にやるとよいこと」8番参照）。対象プラグインを
# 変えて再実行する場合は、Translation/out_tempの残骸に注意すること。

$ErrorActionPreference = "Stop"

# 必要に応じて書き換える
$Mo2Dir = "D:/Modding/MO2"
$LlmModel = "gemma3:12b"

Write-Host "=== 1/3 pickuptarget ===" -ForegroundColor Cyan
dotnet run -c Debug -- pickuptarget $Mo2Dir
if ($LASTEXITCODE -ne 0) { throw "pickuptarget failed (exit $LASTEXITCODE)" }

Write-Host "=== 2/3 translation --all --llm ===" -ForegroundColor Cyan
dotnet run -c Debug -- translation PickUpTarget/out_temp Translation/out_temp --all --llm --llm-model=$LlmModel
if ($LASTEXITCODE -ne 0) { throw "translation failed (exit $LASTEXITCODE)" }

Write-Host "=== 3/3 generatedsdfile ===" -ForegroundColor Cyan
dotnet run -c Debug -- generatedsdfile
if ($LASTEXITCODE -ne 0) { throw "generatedsdfile failed (exit $LASTEXITCODE)" }

Write-Host "=== 完了 ===" -ForegroundColor Green
