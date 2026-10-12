$ErrorActionPreference = 'Stop'

Write-Host 'Muhabbet Kuşu build' -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host '.NET 8 SDK bulunamadı. Kuruluyor...' -ForegroundColor Yellow
    winget install Microsoft.DotNet.SDK.8
}

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw 'Python PATH içinde bulunamadı.'
}

python -c "import ema_lightning, antalia_mini; print('ema-lightning and antalia-mini OK')"
if ($LASTEXITCODE -ne 0) { throw 'Python bağımlılıkları bulunamadı.' }
if (-not (Test-Path (Join-Path $PSScriptRoot 'artifacts/stt-runtime/mihu_stt'))) {
    throw 'STT paketlerini önce kurun: python -m pip install --target artifacts/stt-runtime -r requirements-stt.txt'
}

& (Join-Path $PSScriptRoot 'scripts/clean-portable-builds.ps1')

dotnet publish (Join-Path $PSScriptRoot 'MuhabbetKusu.csproj') -c Release -r win-x64 -f net8.0-windows10.0.19041.0 -p:WindowsAppSDKSelfContained=true --self-contained true -p:PublishSingleFile=false -p:UseSharedCompilation=false -o (Join-Path $PSScriptRoot 'publish')
if ($LASTEXITCODE -ne 0) { throw 'Derleme başarısız.' }
foreach ($component in @('python','ffmpeg')) {
    $runtimeSource = Join-Path $PSScriptRoot "artifacts/payload/runtime/$component"
    if (-not (Test-Path $runtimeSource)) { throw "Taşınabilir sürüm için $component çalışma ortamı eksik." }
    $runtimeTarget = Join-Path $PSScriptRoot "publish/runtime/$component"
    New-Item -ItemType Directory -Force $runtimeTarget | Out-Null
    Copy-Item -Path (Join-Path $runtimeSource '*') -Destination $runtimeTarget -Recurse -Force
}
if (Test-Path (Join-Path $PSScriptRoot 'artifacts/stt-runtime/mihu_stt')) {
    $sttPackages = Join-Path $PSScriptRoot 'publish/runtime/stt-packages'
    New-Item -ItemType Directory -Force $sttPackages | Out-Null
    Copy-Item -Path (Join-Path $PSScriptRoot 'artifacts/stt-runtime/*') -Destination $sttPackages -Recurse -Force
}

Write-Host ''
Write-Host 'Hazır:' -ForegroundColor Green
Write-Host (Resolve-Path '.\publish\MuhabbetKusu.exe')
