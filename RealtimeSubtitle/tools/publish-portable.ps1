# Portable zip: App + selected models (no installer).
# Usage:  powershell -File tools\publish-portable.ps1
# Output: dist\RealtimeSubtitle-portable.zip  and dist\RealtimeSubtitle-portable\

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $repo "RealtimeSubtitle.slnx"))) {
    # script lives in RealtimeSubtitle/tools
    $repo = $PSScriptRoot
    if (-not (Test-Path (Join-Path $repo "RealtimeSubtitle.slnx"))) {
        $repo = Split-Path -Parent $PSScriptRoot
    }
}

# Resolve solution root (folder containing RealtimeSubtitle.slnx)
$sln = Get-ChildItem -Path $repo -Filter "RealtimeSubtitle.slnx" -Recurse -Depth 2 -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $sln) { throw "RealtimeSubtitle.slnx not found from $repo" }
$root = $sln.Directory.FullName

$env:TMP = $env:TEMP = Join-Path $root ".tmp"
$env:NUGET_PACKAGES = Join-Path $root ".nuget-packages"
Remove-Item Env:SSL_CERT_FILE -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $env:TMP | Out-Null

$stage = Join-Path $root "dist\RealtimeSubtitle-portable"
$zip = Join-Path $root "dist\RealtimeSubtitle-portable.zip"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Write-Host "=== publish App (folder, self-contained WinAppSDK) ==="
$proj = Join-Path $root "RealtimeSubtitle.App\RealtimeSubtitle.App.csproj"
dotnet publish $proj -c Release -r win-x64 `
    -p:WindowsPackageType=None `
    -p:SelfContained=true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    --output (Join-Path $stage "app") `
    --no-restore
if ($LASTEXITCODE -ne 0) {
    # restore then retry once
    dotnet publish $proj -c Release -r win-x64 `
        -p:WindowsPackageType=None `
        -p:SelfContained=true `
        -p:PublishSingleFile=false `
        --output (Join-Path $stage "app")
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

# Flatten: users double-click exe at package root
$appOut = Join-Path $stage "app"
Get-ChildItem $appOut -Force | ForEach-Object {
    Move-Item $_.FullName -Destination $stage -Force
}
Remove-Item $appOut -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "=== copy models ==="
$modelSrc = Join-Path $root "tools\model-convert\out"
$modelDst = Join-Path $stage "models"
New-Item -ItemType Directory -Force -Path $modelDst | Out-Null

# All translation models + English small + Chinese + Japanese ASR.
# Other English whisper sizes (tiny/base) intentionally omitted.
$models = @(
    "opus-mt-en-zh-int8",
    "opus-mt-ja-zh-int8",
    "whisper-en-small-int8",
    "qwen3-asr-1.7b-int8",
    "sensevoice-small-int8"
)

$total = 0
foreach ($id in $models) {
    $src = Join-Path $modelSrc $id
    if (-not (Test-Path $src)) { throw "Model missing: $src" }
    Write-Host "  $id ..."
    Copy-Item $src -Destination (Join-Path $modelDst $id) -Recurse -Force
    $sz = (Get-ChildItem (Join-Path $modelDst $id) -Recurse -File | Measure-Object Length -Sum).Sum
    $total += $sz
    Write-Host ("    {0:N0} MB" -f ($sz / 1MB))
}
Write-Host ("Models total: {0:N0} MB" -f ($total / 1MB))

$readme = @"
RealtimeSubtitle 便携版
======================

解压后双击 RealtimeSubtitle.App.exe（无需安装）。

模型已放在 models\ 目录（应用会自动识别）：
  - whisper-en-small-int8   英语识别
  - sensevoice-small-int8   中文识别（默认，快速可 NPU）/ 日语识别
  - qwen3-asr-1.7b-int8     中文高精度（可选：Asr.ZhBackend=qwen3 或 GUI「中文引擎」）
  - opus-mt-en-zh-int8      英→中翻译
  - opus-mt-ja-zh-int8      日→中翻译

首次使用：
  1. 选择运行模式「真声直播」
  2. 选识别语言（自动/中文/英语/日语）
  3. 点「开始」，播放带语音的内容

说明：
  - 中文识别默认 SenseVoice；中文模式下翻译自动关闭（原文即字幕）
  - 配置与日志： %LOCALAPPDATA%\RealtimeSubtitle\
"@
Set-Content -Path (Join-Path $stage "README-便携说明.txt") -Value $readme -Encoding UTF8

Write-Host "=== zip ==="
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
$zipMb = (Get-Item $zip).Length / 1MB
Write-Host ("DONE: {0}  ({1:N0} MB)" -f $zip, $zipMb)
