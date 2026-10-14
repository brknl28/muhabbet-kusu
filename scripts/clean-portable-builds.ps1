$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = Join-Path $projectRoot 'publish'
$oldBuilds = @('artifacts/player-fixed','artifacts/player-publish','artifacts/final-app')
$targets = @($publishRoot) + @($oldBuilds | ForEach-Object { Join-Path $projectRoot $_ })
foreach ($target in $targets) {
    $absolute = [IO.Path]::GetFullPath($target)
    if (-not $absolute.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Temizleme yolu proje dışında.' }
    if (Test-Path -LiteralPath $absolute) {
        if ((Get-Item -LiteralPath $absolute).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Bağlantılı klasör temizlenemez: $absolute" }
    }
}
foreach ($process in @(Get-Process MuhabbetKusu -ErrorAction SilentlyContinue)) {
    if ($process.Path -and @($targets | Where-Object { $process.Path.StartsWith($_ + '\', [StringComparison]::OrdinalIgnoreCase) }).Count) {
        throw 'Yeni derleme için açık Muhabbet Kuşu penceresini kapatın.'
    }
}
# Preserve user data from earlier portable copies before removing build files.
foreach ($target in $targets) {
    if (-not (Test-Path -LiteralPath $target)) { continue }
    foreach ($folder in @(Get-ChildItem -LiteralPath $target -Directory | Where-Object { $_.Name -in @('models','outputs') })) {
        if ($folder.FullName -in @((Join-Path $publishRoot 'models'),(Join-Path $publishRoot 'outputs'))) { continue }
        $backup = Join-Path $projectRoot ('artifacts/preserved-data/' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $backup | Out-Null
        $resolved = [IO.Path]::GetFullPath($folder.FullName)
        if (-not $resolved.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Veri yolu beklenen klasör dışında.' }
        Move-Item -LiteralPath $resolved -Destination $backup
    }
    if ($target -eq $publishRoot) {
        foreach ($item in @(Get-ChildItem -LiteralPath $target -Force | Where-Object { $_.Name -notin @('models','outputs') })) {
            $absolute = [IO.Path]::GetFullPath($item.FullName)
            if (-not $absolute.StartsWith($publishRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Silme yolu beklenen klasör dışında.' }
            Remove-Item -LiteralPath $absolute -Recurse -Force
        }
    } else {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}
