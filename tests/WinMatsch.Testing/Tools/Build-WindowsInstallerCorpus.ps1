[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [switch] $AcquireTools
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$innoVersion = '6.4.0.1'
$nsisVersion = '3.10'
$wixVersion = '5.0.2'
$windowsSdkVersion = '10.0.26100.0'
$toolRoot = Join-Path $PSScriptRoot '.tools'
$sourceRoot = Join-Path $PSScriptRoot 'Sources'

if ($AcquireTools) {
    winget install --id JRSoftware.InnoSetup --version $innoVersion --exact --source winget `
        --accept-package-agreements --accept-source-agreements --disable-interactivity
    if ($LASTEXITCODE -ne 0) { throw "Pinned Inno Setup acquisition failed." }

    winget install --id NSIS.NSIS --version $nsisVersion --exact --source winget `
        --accept-package-agreements --accept-source-agreements --disable-interactivity
    if ($LASTEXITCODE -ne 0) { throw "Pinned NSIS acquisition failed." }

    New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null
    dotnet tool install wix --version $wixVersion --tool-path $toolRoot
    if ($LASTEXITCODE -ne 0) { throw "Pinned WiX acquisition failed." }
}

$inno = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$nsis = Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe'
$wix = Join-Path $toolRoot 'wix.exe'
$makeAppx = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\$windowsSdkVersion\x64\makeappx.exe"

if (-not $inno) { throw "Pinned Inno Setup $innoVersion is not installed." }
if (-not (Test-Path -LiteralPath $nsis)) { throw "Pinned NSIS $nsisVersion is not installed." }
if (-not (Test-Path -LiteralPath $wix)) { throw "Pinned WiX $wixVersion is not installed under '$toolRoot'." }
if (-not (Test-Path -LiteralPath $makeAppx)) {
    throw "Pinned Windows SDK $windowsSdkVersion MakeAppx was not found."
}

function Assert-SignedTool([string] $Path) {
    $item = Get-Item -LiteralPath $Path
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Tool '$Path' does not have a valid Authenticode signature: $($signature.Status)."
    }
}

Assert-SignedTool $inno
Assert-SignedTool $nsis
Assert-SignedTool $makeAppx

$actualInnoVersion = [Version](Get-Item -LiteralPath $inno).VersionInfo.FileVersion
if ($actualInnoVersion -ne [Version]$innoVersion) {
    throw "Inno Setup has version '$actualInnoVersion', expected exactly '$innoVersion'."
}

$actualNsisVersion = [Version](Get-Item -LiteralPath $nsis).VersionInfo.FileVersion
if ($actualNsisVersion -ne [Version]'3.10.0.0') {
    throw "NSIS has file version '$actualNsisVersion', expected exactly '3.10.0.0'."
}

$actualSdkVersion = [Version](Get-Item -LiteralPath $makeAppx).VersionInfo.FileVersion
if ($actualSdkVersion.Major -ne 10 -or $actualSdkVersion.Minor -ne 0 -or $actualSdkVersion.Build -ne 26100) {
    throw "MakeAppx has serviced version '$actualSdkVersion', expected SDK build '10.0.26100'."
}

$actualWixVersion = (& $wix --version | Select-Object -First 1).Trim()
$normalizedWixVersion = ($actualWixVersion -split '\+', 2)[0]
if ($normalizedWixVersion -ne $wixVersion) {
    throw "WiX has version '$actualWixVersion', expected '$wixVersion'."
}

if ($AcquireTools) {
    & $wix extension add "WixToolset.Bal.wixext/$wixVersion"
    if ($LASTEXITCODE -ne 0) { throw "Pinned WiX Bal extension acquisition failed." }
}

$installedExtensions = @(& $wix extension list)
if (-not ($installedExtensions | Where-Object {
    $extension = @($_.Trim() -split '\s+', 2)
    $extension.Count -eq 2 `
        -and $extension[0] -ceq 'WixToolset.Bal.wixext' `
        -and $extension[1] -ceq $wixVersion
})) {
    throw "Pinned WiX Bal extension $wixVersion is absent; rerun with -AcquireTools."
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$resolvedOutput = (Resolve-Path -LiteralPath $OutputDirectory).Path

& $inno "/O$resolvedOutput" '/Ffixture-inno' (Join-Path $sourceRoot 'fixture.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno fixture compilation failed." }

& $nsis "/DOUTFILE=$(Join-Path $resolvedOutput 'fixture-nsis.exe')" `
    (Join-Path $sourceRoot 'fixture.nsi')
if ($LASTEXITCODE -ne 0) { throw "NSIS fixture compilation failed." }

$wixMsi = Join-Path $resolvedOutput 'fixture-wix.msi'
& $wix build (Join-Path $sourceRoot 'fixture.wxs') -o $wixMsi
if ($LASTEXITCODE -ne 0) { throw "WiX MSI fixture compilation failed." }
Copy-Item -LiteralPath $wixMsi -Destination (Join-Path $resolvedOutput 'fixture-msi.msi')

$bundleTemplate = Get-Content -LiteralPath (Join-Path $sourceRoot 'fixture-bundle.wxs') -Raw
$bundleSource = Join-Path $resolvedOutput 'fixture-bundle.generated.wxs'
$escapedMsi = [Security.SecurityElement]::Escape($wixMsi)
Set-Content -LiteralPath $bundleSource -Value $bundleTemplate.Replace('__MSI_PATH__', $escapedMsi) `
    -Encoding utf8NoBOM
& $wix build $bundleSource -ext WixToolset.Bal.wixext -o (Join-Path $resolvedOutput 'fixture-burn.exe')
if ($LASTEXITCODE -ne 0) { throw "WiX Burn fixture compilation failed." }
Remove-Item -LiteralPath $bundleSource

$layout = Join-Path $resolvedOutput 'msix-layout'
New-Item -ItemType Directory -Force -Path (Join-Path $layout 'Assets') | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceRoot 'AppxManifest.xml') -Destination $layout
Copy-Item -LiteralPath (Join-Path $resolvedOutput 'fixture-inno.exe') `
    -Destination (Join-Path $layout 'fixture.exe')
$logo = [Convert]::FromBase64String(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=')
[IO.File]::WriteAllBytes((Join-Path $layout 'Assets\logo.png'), $logo)
& $makeAppx pack /d $layout /p (Join-Path $resolvedOutput 'fixture.msix') /o
if ($LASTEXITCODE -ne 0) { throw "MSIX fixture compilation failed." }
Remove-Item -LiteralPath $layout -Recurse

Get-ChildItem -LiteralPath $resolvedOutput -File |
    Where-Object Extension -In '.exe', '.msi', '.msix' |
    Sort-Object Name |
    ForEach-Object {
        $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
        "$($_.Name) $($hash.Hash)"
    }
