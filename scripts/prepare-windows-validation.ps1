param([string]$SetupPath = (Join-Path $PSScriptRoot '../artifacts/setup/MuhabbetKusu-Setup-1.4.0-x64.exe'))
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '../artifacts/windows-validation'
$inputFolder = Join-Path $root 'input'
$resultsFolder = Join-Path $root 'results'
New-Item -ItemType Directory -Force $inputFolder,$resultsFolder | Out-Null
Copy-Item -LiteralPath $SetupPath -Destination (Join-Path $inputFolder 'setup.exe') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'test-clean-windows.ps1'),(Join-Path $PSScriptRoot 'smoke_setup.py') -Destination $inputFolder -Force
$inputXml = [System.Security.SecurityElement]::Escape((Resolve-Path $inputFolder).Path)
$resultsXml = [System.Security.SecurityElement]::Escape((Resolve-Path $resultsFolder).Path)
@"
<Configuration>
  <Networking>Enable</Networking>
  <MappedFolders>
    <MappedFolder><HostFolder>$inputXml</HostFolder><SandboxFolder>C:\ValidationInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$resultsXml</HostFolder><SandboxFolder>C:\ValidationResults</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\ValidationInput\test-clean-windows.ps1 -InputFolder C:\ValidationInput -ResultsFolder C:\ValidationResults -ConfirmDisposableEnvironment</Command></LogonCommand>
</Configuration>
"@ | Set-Content (Join-Path $root 'CleanWindows.wsb') -Encoding utf8
Write-Host "Test paketi: $((Resolve-Path $root).Path)"
