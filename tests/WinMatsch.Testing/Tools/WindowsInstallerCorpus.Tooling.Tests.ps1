[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$modulePath = Join-Path $PSScriptRoot 'WindowsInstallerCorpus.Tooling.psm1'
$buildScriptPath = Join-Path $PSScriptRoot 'Build-WindowsInstallerCorpus.ps1'

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Throws([scriptblock] $Action, [string] $MessagePattern) {
    try {
        & $Action
    } catch {
        Assert-True `
            ($_.Exception.Message -match $MessagePattern) `
            "Exception '$($_.Exception.Message)' did not match '$MessagePattern'."
        return
    }

    throw "Expected an exception matching '$MessagePattern'."
}

foreach ($path in @($modulePath, $buildScriptPath, $PSCommandPath)) {
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile(
        $path,
        [ref]$tokens,
        [ref]$errors)
    Assert-True ($errors.Count -eq 0) "PowerShell parser errors in '$path': $errors"
}

Import-Module $modulePath -Force
$root = Join-Path ([IO.Path]::GetTempPath()) "winmatsch-tooling-tests-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $root | Out-Null
try {
    $payload = Join-Path $root 'payload.bin'
    [IO.File]::WriteAllBytes($payload, [byte[]](1, 3, 3, 7))
    $expectedHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
    $calls = [Collections.Generic.List[string]]::new()
    $destination = Join-Path $root 'fallback.bin'
    $fallback = Get-VerifiedFile `
        -Uris @('https://first.example.test/tool-3.10.exe', 'https://second.example.test/tool-3.10.exe') `
        -Path $destination `
        -ExpectedSha256 $expectedHash `
        -AllowDownload $true `
        -DownloadFile {
            param($Uri, $Path)
            $calls.Add($Uri)
            if ($Uri -match 'first') {
                throw 'simulated mirror outage'
            }

            Copy-Item -LiteralPath $payload -Destination $Path
        }
    Assert-True ($calls.Count -eq 2) 'Pinned mirror fallback did not try exactly two sources.'
    Assert-True ($calls[0] -match 'first' -and $calls[1] -match 'second') 'Pinned mirror order changed.'
    Assert-True ($fallback.Source -eq $calls[1]) 'Provenance did not record the successful pinned mirror.'

    $badPayload = Join-Path $root 'bad.bin'
    [IO.File]::WriteAllBytes($badPayload, [byte[]](9, 9, 9))
    $mismatchCalls = [Collections.Generic.List[string]]::new()
    Assert-Throws {
        Get-VerifiedFile `
            -Uris @('https://first.example.test/tool-3.10.exe', 'https://second.example.test/tool-3.10.exe') `
            -Path (Join-Path $root 'mismatch.bin') `
            -ExpectedSha256 $expectedHash `
            -AllowDownload $true `
            -DownloadFile {
                param($Uri, $Path)
                $mismatchCalls.Add($Uri)
                Copy-Item -LiteralPath $badPayload -Destination $Path
            }
    } 'has SHA-256'
    Assert-True ($mismatchCalls.Count -eq 1) 'Checksum mismatch incorrectly fell through to another mirror.'

    Assert-Throws {
        Get-VerifiedFile `
            -Uris @('https://first.example.test/tool-3.10.exe') `
            -Path (Join-Path $root 'offline.bin') `
            -ExpectedSha256 $expectedHash `
            -AllowDownload $false
    } 'Rerun with -AcquireTools.*pre-populate.*No unpinned fallback'

    Assert-Throws {
        Get-VerifiedFile `
            -Uris @('https://first.example.test/tool-3.10.exe', 'https://second.example.test/tool-3.10.exe') `
            -Path (Join-Path $root 'unreachable.bin') `
            -ExpectedSha256 $expectedHash `
            -AllowDownload $true `
            -DownloadFile { param($Uri, $Path) throw "offline: $Uri" }
    } 'All pinned package sources failed.*No unpinned fallback'

    $manifest = Get-WindowsInstallerCorpusToolManifest
    Assert-True ($manifest.NsisInstallerUris.Count -ge 2) 'NSIS must have multiple pinned acquisition routes.'
    foreach ($entry in @(
        @($manifest.InnoInstallerUris, $manifest.InnoVersion),
        @($manifest.NsisInstallerUris, $manifest.NsisVersion),
        @($manifest.WixPackageUris, $manifest.WixVersion),
        @($manifest.BalPackageUris, $manifest.WixVersion)
    )) {
        foreach ($uri in $entry[0]) {
            Assert-True ($uri -match [Regex]::Escape($entry[1])) "URI '$uri' is not version-pinned."
            Assert-True ($uri -notmatch '(?i)latest|releases/latest') "URI '$uri' uses an unpinned latest route."
        }
    }

    $missingCandidate = Join-Path $root 'missing\makeappx.exe'
    $candidate = Join-Path $root 'sdk\10.0.26100.0\x64\makeappx.exe'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $candidate) | Out-Null
    Copy-Item -LiteralPath $payload -Destination $candidate
    $validSignature = {
        param($Path)
        [pscustomobject]@{
            Status = 'Valid'
            SignerCertificate = [pscustomobject]@{
                Subject = 'CN=Microsoft Windows, O=Microsoft Corporation, C=US'
            }
        }
    }
    $resolved = Resolve-ApprovedMakeAppx `
        -CandidatePaths @($missingCandidate, $candidate) `
        -SdkVersion $manifest.WindowsSdkVersion `
        -MinimumFileVersion ([Version]$manifest.MakeAppxMinimumFileVersion) `
        -ApprovedSignerOrganization $manifest.MakeAppxSignerOrganization `
        -SignatureInspector $validSignature `
        -FileVersionReader { param($Path) '10.0.26100.8249' }
    Assert-True ($resolved.Path -eq (Resolve-Path -LiteralPath $candidate).Path) 'MakeAppx discovery order failed.'
    Assert-True (-not [string]::IsNullOrWhiteSpace($resolved.Sha256)) 'MakeAppx provenance omitted its hash.'

    Assert-Throws {
        Resolve-ApprovedMakeAppx `
            -CandidatePaths @($candidate) `
            -SdkVersion $manifest.WindowsSdkVersion `
            -MinimumFileVersion ([Version]$manifest.MakeAppxMinimumFileVersion) `
            -ApprovedSignerOrganization $manifest.MakeAppxSignerOrganization `
            -SignatureInspector {
                param($Path)
                [pscustomobject]@{ Status = 'NotSigned'; SignerCertificate = $null }
            } `
            -FileVersionReader { param($Path) '10.0.26100.8249' }
    } 'does not have a valid Authenticode signature'

    Assert-Throws {
        Resolve-ApprovedMakeAppx `
            -CandidatePaths @($candidate) `
            -SdkVersion $manifest.WindowsSdkVersion `
            -MinimumFileVersion ([Version]$manifest.MakeAppxMinimumFileVersion) `
            -ApprovedSignerOrganization $manifest.MakeAppxSignerOrganization `
            -SignatureInspector {
                param($Path)
                [pscustomobject]@{
                    Status = 'Valid'
                    SignerCertificate = [pscustomobject]@{
                        Subject = 'CN=Unapproved SDK, O=Example Corporation, C=US'
                    }
                }
            } `
            -FileVersionReader { param($Path) '10.0.26100.8249' }
    } 'not approved organization'

    Assert-Throws {
        Resolve-ApprovedMakeAppx `
            -CandidatePaths @($candidate) `
            -SdkVersion $manifest.WindowsSdkVersion `
            -MinimumFileVersion ([Version]$manifest.MakeAppxMinimumFileVersion) `
            -ApprovedSignerOrganization $manifest.MakeAppxSignerOrganization `
            -SignatureInspector $validSignature `
            -FileVersionReader { param($Path) '10.0.22621.1' }
    } 'expected signed Windows SDK'

    'Windows installer corpus tooling tests passed.'
} finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
