param(
    [string]$InputFolder = $PSScriptRoot,
    [string]$ResultsFolder,
    [switch]$ConfirmDisposableEnvironment
)
$ErrorActionPreference = 'Stop'
if (-not $ConfirmDisposableEnvironment) { throw 'Bu test yalnızca temiz, geçici Windows ortamında çalıştırılmalıdır.' }
if (-not $ResultsFolder) { throw 'Test sonuç klasörü gerekir.' }
New-Item -ItemType Directory -Force $ResultsFolder | Out-Null
$report = [ordered]@{ startedUtc = [DateTime]::UtcNow.ToString('o'); passed = $false; checks = @() }
$appRoot = Join-Path $env:LOCALAPPDATA 'MuhabbetKusu'
$gui = $null
Start-Transcript -Path (Join-Path $ResultsFolder 'test.log') -Force | Out-Null
try {
    $report.os = (Get-CimInstance Win32_OperatingSystem).Caption
    $report.build = [Environment]::OSVersion.Version.ToString()
    if (Test-Path -LiteralPath $appRoot) { throw 'Uygulama klasörü zaten var. Temiz ortam gerekir.' }
    foreach ($name in @('python.exe','dotnet.exe','ffmpeg.exe')) {
        if (Get-Command $name -ErrorAction SilentlyContinue) { throw "Temiz ortamda dış bağımlılık bulundu: $name" }
    }
    $report.checks += 'No preinstalled app or external runtime on PATH'
    $setup = Join-Path $InputFolder 'setup.exe'
    $report.setupSha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    $report.signature = (Get-AuthenticodeSignature -LiteralPath $setup).Status.ToString()
    $install = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/TASKS=downloadmodels',"/LOG=`"$(Join-Path $ResultsFolder 'install.log')`"") -WindowStyle Hidden -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Kurulum hata kodu: $($install.ExitCode)" }
    if (-not (Test-Path (Join-Path $appRoot 'models/ready.json'))) { throw 'Kurulum sırasında modeller indirilemedi.' }
    $report.checks += 'Setup and model download'
    & (Join-Path $appRoot 'runtime/python/python.exe') -I (Join-Path $InputFolder 'smoke_setup.py') $appRoot
    if ($LASTEXITCODE -ne 0) { throw 'Çevrimdışı ses testi başarısız.' }
    Copy-Item -LiteralPath (Join-Path $appRoot 'smoke-results.json') -Destination $ResultsFolder
    $report.checks += 'Both TTS models, WAV and MP3 with offline model cache'
    $gui = Start-Process -FilePath (Join-Path $appRoot 'MuhabbetKusu.exe') -PassThru
    $deadline = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 500
        $gui.Refresh()
        if ($gui.HasExited) { throw 'Uygulama açılırken kapandı.' }
    } while ($gui.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
    if ($gui.MainWindowHandle -eq 0) { throw 'Uygulama penceresi açılmadı.' }
    $report.checks += 'Native application window opened'
    $null = $gui.CloseMainWindow()
    if (-not $gui.WaitForExit(15000)) { throw 'Uygulama kapanmadı.' }
    $uninstall = Start-Process -FilePath (Join-Path $appRoot 'unins000.exe') -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -WindowStyle Hidden -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw 'Kaldırma işlemi başarısız.' }
    if (Test-Path (Join-Path $appRoot 'MuhabbetKusu.exe')) { throw 'Uygulama kaldırılamadı.' }
    if (-not (Test-Path (Join-Path $appRoot 'models/ready.json')) -or -not (Test-Path (Join-Path $appRoot 'outputs/setup-smoke.mp3'))) { throw 'Kullanıcı dosyaları korunmadı.' }
    $report.checks += 'Uninstall preserves models and outputs'
    $report.passed = $true
} catch {
    $report.error = $_.Exception.Message
} finally {
    $report.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ResultsFolder 'report.json') -Encoding utf8
    Stop-Transcript | Out-Null
}
if (-not $report.passed) { exit 1 }
