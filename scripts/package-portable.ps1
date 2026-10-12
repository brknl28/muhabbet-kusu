$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publish = Join-Path $root 'publish'
if (-not (Test-Path (Join-Path $publish 'MuhabbetKusu.exe'))) { throw 'Önce uygulamayı derleyin.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $root 'artifacts/MuhabbetKusu-Portable.zip'
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $publish -File -Recurse) {
        $relative = $file.FullName.Substring($publish.Length + 1).Replace('\','/')
        if ($relative -match '^(models|outputs)/' -or $relative -match '(^|/)__pycache__/') { continue }
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative)
    }
} finally { $zip.Dispose() }
Write-Host $zipPath
