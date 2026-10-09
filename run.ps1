$ErrorActionPreference = 'Stop'
if (Test-Path '.\publish\MuhabbetKusu.exe') {
    Start-Process '.\publish\MuhabbetKusu.exe'
} else {
    Write-Host 'Önce .\build.ps1 çalıştırın.' -ForegroundColor Yellow
}
