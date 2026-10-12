# No passwords or private keys are read by this script. Use a certificate store.
function Get-ReleaseSignCommand {
    param([string]$Thumbprint, [string]$SignToolPath, [string]$TimestampUrl)
    $Thumbprint = $Thumbprint -replace '\s', ''
    if ($Thumbprint -notmatch '^[a-fA-F0-9]{40}$') { throw 'Geçerli sertifika parmak izi gerekir.' }
    $certificate = Get-Item "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction Stop
    if (-not $certificate.HasPrivateKey) { throw 'Sertifikanın özel anahtarı yok.' }
    if ($certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) { throw 'Sertifika tarihleri geçerli değil.' }
    $eku = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }
    if (-not $eku -or '1.3.6.1.5.5.7.3.3' -notin @($eku.EnhancedKeyUsages | ForEach-Object { $_.Value })) { throw 'Kod imzalama sertifikası gerekir.' }
    $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
    try { if (-not $chain.Build($certificate)) { throw 'Sertifika güven zinciri doğrulanamadı.' } } finally { $chain.Dispose() }
    if (-not $SignToolPath) { $SignToolPath = (Get-Command signtool.exe -ErrorAction Stop).Source }
    if (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) { throw 'SignTool bulunamadı.' }
    $uri = [Uri]$TimestampUrl
    if ($uri.Scheme -ne 'https' -or $TimestampUrl -match '["$\s]') { throw 'Geçerli HTTPS zaman damgası adresi gerekir.' }
    if ($SignToolPath -match '["$]') { throw 'SignTool yolunda desteklenmeyen karakter var.' }
    return "`"$SignToolPath`" sign /sha1 $Thumbprint /s My /fd SHA256 /tr $TimestampUrl /td SHA256 `$f"
}

function Invoke-ReleaseSign {
    param([string]$Path, [string]$Command)
    # Inno expands $f itself. Here invoke the same arguments directly, without a shell.
    if ($Command -notmatch '^"(.+)" sign /sha1 ([A-Fa-f0-9]{40}) /s My /fd SHA256 /tr (\S+) /td SHA256 \$f$') { throw 'İmzalama komutu geçersiz.' }
    & $Matches[1] sign /sha1 $Matches[2] /s My /fd SHA256 /tr $Matches[3] /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw 'Uygulama imzalanamadı.' }
    if ((Get-AuthenticodeSignature -LiteralPath $Path).Status -ne 'Valid') { throw 'Uygulama imzası doğrulanamadı.' }
}
