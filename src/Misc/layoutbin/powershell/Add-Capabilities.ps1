[CmdletBinding()]
param()

Import-Module -Name 'Microsoft.PowerShell.Management'
Import-Module -Name 'Microsoft.PowerShell.Utility'
$ErrorActionPreference = 'Stop'
Import-Module -Name $PSScriptRoot\CapabilityHelpers

$scanners = @()
$scanners += (Get-ChildItem -LiteralPath "${PSScriptRoot}" -Filter "Add-*Capabilities.ps1")

# Handling of user specific scanner
$customScannerPath = "${PSScriptRoot}\..\..\user\scanners"
if (Test-Path "${customScannerPath}" -PathType Container) {
    $scanners += Get-ChildItem -LiteralPath "${customScannerPath}" -Filter "Add-*Capabilities.ps1"
}

# Run each capability script.
foreach ($item in $scanners) {
    if ($item.Name -eq ([System.IO.Path]::GetFileName($PSCommandPath))) {
        continue;
    }

    Write-Host "& $($item.FullName)"
    try {
        & $item.FullName
    } catch {
        Write-Host ($_ | Out-String)
    }
}
