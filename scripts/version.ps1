function Get-AppVersion {
    $root = Split-Path $PSScriptRoot
    $version = [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION dosyası geçersiz.' }
    return $version
}
