$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'version.ps1')
$version = Get-AppVersion
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publish = Join-Path $root 'publish'
if (-not (Test-Path (Join-Path $publish 'MuhabbetKusu.exe'))) { throw 'Önce uygulamayı derleyin.' }
if ((Get-Item (Join-Path $publish 'MuhabbetKusu.exe')).VersionInfo.ProductVersion -ne $version) { throw 'Önce VERSION ile aynı sürümde uygulama derleyin.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$portableRoot = Join-Path $root 'artifacts/portable'
New-Item -ItemType Directory -Force $portableRoot | Out-Null
$zipPath = Join-Path $portableRoot "MuhabbetKusu-Portable-$version-x64.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $publish -File -Recurse) {
        $relative = $file.FullName.Substring($publish.Length + 1).Replace('\','/')
        if ($relative -match '^(models|outputs)/' -or $relative -match '(^|/)__pycache__/') { continue }
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative)
    }
} finally { $zip.Dispose() }
Get-ChildItem -LiteralPath $portableRoot -File -Filter 'MuhabbetKusu-Portable-*-x64.zip' | Where-Object FullName -ne $zipPath | ForEach-Object {
    $absolute = [IO.Path]::GetFullPath($_.FullName)
    if (-not $absolute.StartsWith([IO.Path]::GetFullPath($portableRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP yolu portable dışında.' }
    Remove-Item -LiteralPath $absolute -Force
}
Write-Host $zipPath
