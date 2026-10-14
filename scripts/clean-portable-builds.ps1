$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'publish'))
if (-not $publishRoot.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Temizleme yolu proje dışında.' }
if (-not (Test-Path -LiteralPath $publishRoot)) { return }
if ((Get-Item -LiteralPath $publishRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Bağlantılı klasör temizlenemez.' }
foreach ($process in @(Get-Process MuhabbetKusu -ErrorAction SilentlyContinue)) {
    if ($process.Path -and $process.Path.StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Yeni derleme için açık Muhabbet Kuşu penceresini kapatın.'
    }
}
# Preserve the application-local model cache and user audio.
foreach ($item in @(Get-ChildItem -LiteralPath $publishRoot -Force | Where-Object { $_.Name -notin @('models','outputs') })) {
    $absolute = [IO.Path]::GetFullPath($item.FullName)
    if (-not $absolute.StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Silme yolu publish dışında.' }
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
