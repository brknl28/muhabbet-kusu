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

dotnet restore .\MuhabbetKusu.csproj
dotnet publish .\MuhabbetKusu.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o .\publish

Write-Host ''
Write-Host 'Hazır:' -ForegroundColor Green
Write-Host (Resolve-Path '.\publish\MuhabbetKusu.exe')
