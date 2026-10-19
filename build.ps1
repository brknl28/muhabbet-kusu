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
foreach ($package in @('mihu_stt','faster_whisper','sentencepiece','onnxruntime')) {
    if (-not (Test-Path (Join-Path $PSScriptRoot "artifacts/stt-runtime/$package"))) {
        throw 'STT paketlerini önce kurun: python -m pip install --target artifacts/stt-runtime -r requirements-stt.txt'
    }
}

& (Join-Path $PSScriptRoot 'scripts/clean-portable-builds.ps1')

dotnet publish (Join-Path $PSScriptRoot 'MuhabbetKusu.csproj') -c Release -r win-x64 -f net8.0-windows10.0.19041.0 -p:WindowsAppSDKSelfContained=true --self-contained true -p:PublishSingleFile=false -p:UseSharedCompilation=false -o (Join-Path $PSScriptRoot 'publish')
if ($LASTEXITCODE -ne 0) { throw 'Derleme başarısız.' }
foreach ($component in @('python','ffmpeg')) {
    $runtimeSource = Join-Path $PSScriptRoot "artifacts/payload/runtime/$component"
    if (-not (Test-Path $runtimeSource)) { throw "Taşınabilir sürüm için $component çalışma ortamı eksik." }
    $runtimeTarget = Join-Path $PSScriptRoot "publish/runtime/$component"
    New-Item -ItemType Directory -Force $runtimeTarget | Out-Null
    if ($component -eq 'python') {
        Copy-Item -Path (Join-Path $runtimeSource '*') -Destination $runtimeTarget -Exclude 'Lib' -Recurse -Force
        $libTarget = Join-Path $runtimeTarget 'Lib'
        New-Item -ItemType Directory -Force $libTarget | Out-Null
        Copy-Item -Path (Join-Path $runtimeSource 'Lib/*') -Destination $libTarget -Exclude 'site-packages' -Recurse -Force
    } else {
        Copy-Item -Path (Join-Path $runtimeSource '*') -Destination $runtimeTarget -Recurse -Force
    }
}
# Refresh Python dependencies from the pinned development environment. The previous
# setup payload can contain an older EMA runtime during local model development.
$privatePackages = Join-Path $PSScriptRoot 'publish/runtime/python/Lib/site-packages'
$absolutePackages = [IO.Path]::GetFullPath($privatePackages)
$publishRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'publish'))
if (-not $absolutePackages.StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Python paket yolu publish dışında.' }
if (Test-Path -LiteralPath $absolutePackages) {
    if ((Get-Item -LiteralPath $absolutePackages).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Bağlantılı Python paket klasörü temizlenemez.' }
    Remove-Item -LiteralPath $absolutePackages -Recurse -Force
}
python (Join-Path $PSScriptRoot 'scripts/vendor_python.py') $privatePackages
if ($LASTEXITCODE -ne 0) { throw 'Yerel Python çalışma ortamı güncellenemedi.' }
if (Test-Path (Join-Path $PSScriptRoot 'artifacts/stt-runtime/mihu_stt')) {
    $sttPackages = Join-Path $PSScriptRoot 'publish/runtime/stt-packages'
    New-Item -ItemType Directory -Force $sttPackages | Out-Null
    Copy-Item -Path (Join-Path $PSScriptRoot 'artifacts/stt-runtime/*') -Destination $sttPackages -Recurse -Force
}

Write-Host ''
Write-Host 'Hazır:' -ForegroundColor Green
Write-Host (Resolve-Path '.\publish\MuhabbetKusu.exe')
