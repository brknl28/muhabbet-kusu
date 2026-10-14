param(
    [string]$CompilerPath,
    [string]$FfmpegPath,
    [switch]$SkipPublish,
    [string]$SigningCertificateThumbprint,
    [string]$SignToolPath,
    [string]$TimestampUrl = 'https://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
. (Join-Path $projectRoot 'scripts/version.ps1')
$appVersion = Get-AppVersion
$artifactRoot = Join-Path $projectRoot 'artifacts'
$payload = Join-Path $artifactRoot 'payload'
$downloads = Join-Path $artifactRoot 'downloads'
$signCommand = $null
if ($SigningCertificateThumbprint) {
    . (Join-Path $projectRoot 'scripts/signing.ps1')
    $signCommand = Get-ReleaseSignCommand -Thumbprint $SigningCertificateThumbprint -SignToolPath $SignToolPath -TimestampUrl $TimestampUrl
}
$sttSource = Join-Path $artifactRoot 'stt-runtime'
if (-not (Test-Path (Join-Path $sttSource 'mihu_stt'))) {
    throw 'STT paketleri eksik. python -m pip install --target artifacts/stt-runtime -r requirements-stt.txt çalıştırın.'
}
New-Item -ItemType Directory -Force $downloads | Out-Null

if (-not $SkipPublish) {
    $absolutePayload = [IO.Path]::GetFullPath($payload)
    if (-not $absolutePayload.StartsWith([IO.Path]::GetFullPath($artifactRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload yolu artifacts dışında.' }
    if (Test-Path -LiteralPath $absolutePayload) {
        if ((Get-Item -LiteralPath $absolutePayload).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Bağlantılı payload klasörü temizlenemez.' }
        Remove-Item -LiteralPath $absolutePayload -Recurse -Force
    }
    New-Item -ItemType Directory -Force $payload,$downloads | Out-Null
    dotnet publish (Join-Path $projectRoot 'MuhabbetKusu.csproj') -c Release -r win-x64 -f net8.0-windows10.0.19041.0 -p:WindowsAppSDKSelfContained=true --self-contained true -p:PublishSingleFile=false -p:UseSharedCompilation=false -o $payload
    if ($LASTEXITCODE -ne 0) { throw 'Uygulama publish işlemi başarısız.' }
}
if ((Get-Item (Join-Path $payload 'MuhabbetKusu.exe')).VersionInfo.ProductVersion -ne $appVersion) {
    throw 'Payload sürümü VERSION ile aynı değil. SkipPublish kullanmadan yeniden derleyin.'
}
New-Item -ItemType Directory -Force (Join-Path $payload 'bridge') | Out-Null
Copy-Item -Path (Join-Path $projectRoot 'bridge/*.py') -Destination (Join-Path $payload 'bridge') -Force

$pythonVersion = '3.12.10'
$pythonArchive = Join-Path $downloads "python-$pythonVersion-embed-amd64.zip"
if (-not (Test-Path -LiteralPath $pythonArchive)) {
    Invoke-WebRequest "https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-embed-amd64.zip" -OutFile $pythonArchive
}
$pythonRoot = Join-Path $payload 'runtime/python'
New-Item -ItemType Directory -Force $pythonRoot | Out-Null
Expand-Archive -LiteralPath $pythonArchive -DestinationPath $pythonRoot -Force
@'
python312.zip
.
Lib/site-packages
../../bridge
import site
'@ | Set-Content (Join-Path $pythonRoot 'python312._pth') -Encoding ascii

# Use the same CPython ABI as the private runtime and copy its tested dependency closure.
python -c "import sys; assert sys.version_info[:2] == (3, 12), 'Python 3.12 gerekir'"
if ($LASTEXITCODE -ne 0) { throw 'Paketleme için Python 3.12 gerekir.' }
python (Join-Path $projectRoot 'scripts/vendor_python.py') (Join-Path $pythonRoot 'Lib/site-packages')
if ($LASTEXITCODE -ne 0) { throw 'Python bağımlılıkları paketlenemedi.' }

# App-local Microsoft C/C++ runtime DLLs: no administrator installation on user PCs.
$vcDlls = @('msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','msvcp140_atomic_wait.dll','msvcp140_codecvt_ids.dll','vcruntime140.dll','vcruntime140_1.dll','concrt140.dll')
foreach ($name in $vcDlls) {
    $source = Join-Path $env:WINDIR "System32/$name"
    if (-not (Test-Path -LiteralPath $source)) { throw "Paketleme makinesinde $name eksik." }
    Copy-Item -LiteralPath $source -Destination (Join-Path $pythonRoot $name) -Force
}

if (-not $FfmpegPath) { $FfmpegPath = (Get-Command ffmpeg.exe -ErrorAction Stop).Source }
$ffmpegRoot = Join-Path $payload 'runtime/ffmpeg'
New-Item -ItemType Directory -Force $ffmpegRoot | Out-Null
Copy-Item -LiteralPath $FfmpegPath -Destination (Join-Path $ffmpegRoot 'ffmpeg.exe') -Force
$ffmpegLicense = Join-Path (Split-Path (Split-Path $FfmpegPath)) 'LICENSE'
if (-not (Test-Path -LiteralPath $ffmpegLicense)) { throw 'FFmpeg LICENSE dosyası bulunamadı.' }
Copy-Item -LiteralPath $ffmpegLicense -Destination (Join-Path $ffmpegRoot 'LICENSE') -Force
'FFmpeg kaynak kodu ve derleme bilgileri: https://www.gyan.dev/ffmpeg/builds/' | Set-Content (Join-Path $ffmpegRoot 'SOURCE.txt') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $payload -Force

& (Join-Path $pythonRoot 'python.exe') -I -c "import torch, ema_lightning, antalia_mini, soundfile; print('Private Python imports OK')"
if ($LASTEXITCODE -ne 0) { throw 'Özel Python ortamı kontrolü başarısız.' }
$sttTarget = Join-Path $payload 'runtime/stt-packages'
New-Item -ItemType Directory -Force $sttTarget | Out-Null
Copy-Item -Path (Join-Path $sttSource '*') -Destination $sttTarget -Recurse -Force
& (Join-Path $pythonRoot 'python.exe') -I -c "import sys; sys.path[:0]=sys.argv[1:]; import mihu_stt,sherpa_onnx,faster_whisper,onnxruntime,sentencepiece,duyu_small; print('Private STT imports OK')" $sttTarget (Join-Path $payload 'bridge')
if ($LASTEXITCODE -ne 0) { throw 'STT çalışma ortamı kontrolü başarısız.' }

if (-not $CompilerPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $CompilerPath = if ($compiler) { $compiler.Source } else { Join-Path $artifactRoot 'tools/inno/ISCC.exe' }
}
if (-not (Test-Path -LiteralPath $CompilerPath)) { throw 'Inno Setup ISCC.exe bulunamadı. -CompilerPath ile yolunu verin.' }
$compilerArguments = @('/Q', "/DAppVersion=$appVersion", "/DPayloadDir=$payload", "/DSetupOutputDir=$(Join-Path $artifactRoot 'setup')")
if ($signCommand) {
    Invoke-ReleaseSign -Path (Join-Path $payload 'MuhabbetKusu.exe') -Command $signCommand
    $compilerArguments += '/DSignEnabled=1', "/Sappsign=$signCommand"
}
& $CompilerPath @compilerArguments (Join-Path $projectRoot 'installer/MuhabbetKusu.iss')
if ($LASTEXITCODE -ne 0) { throw 'Setup oluşturulamadı.' }
$setupFile = Join-Path $artifactRoot "setup/MuhabbetKusu-Setup-$appVersion-x64.exe"
if ($signCommand -and (Get-AuthenticodeSignature -LiteralPath $setupFile).Status -ne 'Valid') { throw 'Setup imzası doğrulanamadı.' }
$checksum = (Get-FileHash -LiteralPath $setupFile -Algorithm SHA256).Hash.ToLowerInvariant()
"$checksum  $(Split-Path $setupFile -Leaf)" | Set-Content "$setupFile.sha256" -Encoding ascii
# Keep only the verified current installer and its checksum.
$setupRoot = [IO.Path]::GetFullPath((Join-Path $artifactRoot 'setup'))
foreach ($oldFile in @(Get-ChildItem -LiteralPath $setupRoot -File | Where-Object {
    $_.Name -like 'MuhabbetKusu-Setup-*-x64.exe*' -and $_.FullName -notin @($setupFile, "$setupFile.sha256")
})) {
    $absolute = [IO.Path]::GetFullPath($oldFile.FullName)
    if (-not $absolute.StartsWith($setupRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Eski setup yolu beklenen klasör dışında.'
    }
    Remove-Item -LiteralPath $absolute -Force
}
Write-Host $setupFile
