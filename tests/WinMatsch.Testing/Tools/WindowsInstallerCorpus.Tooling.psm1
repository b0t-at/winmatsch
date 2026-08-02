Set-StrictMode -Version Latest

function Get-WindowsInstallerCorpusToolManifest {
    [CmdletBinding()]
    param()

    [pscustomobject]@{
        InnoVersion = '6.4.0'
        InnoInstallerUris = @(
            'https://github.com/jrsoftware/issrc/releases/download/is-6_4_0/innosetup-6.4.0.exe'
        )
        InnoInstallerSha256 = 'A360DB165CFB1D42D195B020700181E7EAF5DB45C1249A24EDB51C3C33E9D659'
        NsisVersion = '3.10'
        NsisInstallerUris = @(
            'https://downloads.sourceforge.net/project/nsis/NSIS%203/3.10/nsis-3.10-setup.exe',
            'https://sourceforge.net/projects/nsis/files/NSIS%203/3.10/nsis-3.10-setup.exe/download',
            'https://pilotfiber.dl.sourceforge.net/project/nsis/NSIS%203/3.10/nsis-3.10-setup.exe'
        )
        NsisInstallerSha256 = '4313D352E0DAFD1F22B6517126A655CAE3B444FA758D2845EDDFBE72F24F7BDD'
        WixVersion = '5.0.2'
        WixPackageUris = @(
            'https://api.nuget.org/v3-flatcontainer/wix/5.0.2/wix.5.0.2.nupkg'
        )
        WixPackageSha256 = 'F30EF0C74E2A986126539C5780BE93AC24E8136EAF723B1937B26272703AE173'
        BalPackageUris = @(
            'https://api.nuget.org/v3-flatcontainer/wixtoolset.bal.wixext/5.0.2/wixtoolset.bal.wixext.5.0.2.nupkg'
        )
        BalPackageSha256 = '22422B50A925477E33C2B5F78D2965A9A09C6622D17BA5AA5365B20EC662B7C8'
        WindowsSdkVersion = '10.0.26100.0'
        MakeAppxMinimumFileVersion = '10.0.26100.0'
        MakeAppxSignerOrganization = 'Microsoft Corporation'
    }
}

function Assert-FileHash {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $ExpectedSha256
    )

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -cne $ExpectedSha256) {
        throw "File '$Path' has SHA-256 '$actual', expected '$ExpectedSha256'."
    }

    $actual
}

function Invoke-PinnedDownload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Uri,

        [Parameter(Mandatory)]
        [string] $Path
    )

    & curl.exe --fail --location --silent --show-error --proto '=https' --tlsv1.2 `
        --output $Path $Uri
    if ($LASTEXITCODE -ne 0) {
        throw "HTTPS download failed with curl exit code $LASTEXITCODE."
    }
}

function Get-VerifiedFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]] $Uris,

        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $ExpectedSha256,

        [Parameter(Mandatory)]
        [bool] $AllowDownload,

        [scriptblock] $DownloadFile = {
            param([string] $Uri, [string] $Destination)
            Invoke-PinnedDownload -Uri $Uri -Path $Destination
        }
    )

    if ($Uris.Count -eq 0) {
        throw 'At least one pinned acquisition URI is required.'
    }

    foreach ($uri in $Uris) {
        if (-not [Uri]::IsWellFormedUriString($uri, [UriKind]::Absolute) -or
            ([Uri]$uri).Scheme -cne 'https') {
            throw "Pinned acquisition URI '$uri' is not an absolute HTTPS URI."
        }
    }

    if (Test-Path -LiteralPath $Path) {
        $hash = Assert-FileHash -Path $Path -ExpectedSha256 $ExpectedSha256
        return [pscustomobject]@{
            Path = (Resolve-Path -LiteralPath $Path).Path
            Sha256 = $hash
            Source = 'verified-local-cache'
        }
    }

    if (-not $AllowDownload) {
        throw "Pinned package '$Path' is absent. Rerun with -AcquireTools while online, or pre-populate that exact path with SHA-256 '$ExpectedSha256'. No unpinned fallback is permitted."
    }

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $failures = [Collections.Generic.List[string]]::new()
    foreach ($uri in $Uris) {
        $temporaryPath = Join-Path $directory ".download-$([Guid]::NewGuid().ToString('N')).tmp"
        try {
            try {
                & $DownloadFile $uri $temporaryPath
            } catch {
                $failures.Add("$uri -> $($_.Exception.Message)")
                continue
            }

            $hash = Assert-FileHash -Path $temporaryPath -ExpectedSha256 $ExpectedSha256
            Move-Item -LiteralPath $temporaryPath -Destination $Path
            return [pscustomobject]@{
                Path = (Resolve-Path -LiteralPath $Path).Path
                Sha256 = $hash
                Source = $uri
            }
        } finally {
            if (Test-Path -LiteralPath $temporaryPath) {
                Remove-Item -LiteralPath $temporaryPath -Force
            }
        }
    }

    $details = $failures -join '; '
    throw "All pinned package sources failed for '$Path': $details. Verify outbound HTTPS access or pre-populate the exact verified package. No unpinned fallback is permitted."
}

function Assert-AuthenticodeSignature {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [scriptblock] $SignatureInspector = {
            param([string] $Candidate)
            Get-AuthenticodeSignature -LiteralPath $Candidate
        }
    )

    $signature = & $SignatureInspector $Path
    if ([string]$signature.Status -cne 'Valid') {
        throw "Tool '$Path' does not have a valid Authenticode signature: $($signature.Status)."
    }

    if ($null -eq $signature.SignerCertificate -or
        [string]::IsNullOrWhiteSpace([string]$signature.SignerCertificate.Subject)) {
        throw "Tool '$Path' has no Authenticode signer identity."
    }

    [string]$signature.SignerCertificate.Subject
}

function Get-MakeAppxCandidatePaths {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $SdkVersion,

        [Parameter(Mandatory)]
        [string] $DefaultSdkRoot
    )

    $candidates = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($env:WindowsSdkVerBinPath)) {
        $candidates.Add((Join-Path $env:WindowsSdkVerBinPath 'x64\makeappx.exe'))
    }

    if (-not [string]::IsNullOrWhiteSpace($env:WindowsSdkBinPath)) {
        $candidates.Add((Join-Path $env:WindowsSdkBinPath "$SdkVersion\x64\makeappx.exe"))
    }

    $candidates.Add((Join-Path $DefaultSdkRoot "$SdkVersion\x64\makeappx.exe"))
    @($candidates | Select-Object -Unique)
}

function Resolve-ApprovedMakeAppx {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]] $CandidatePaths,

        [Parameter(Mandatory)]
        [string] $SdkVersion,

        [Parameter(Mandatory)]
        [Version] $MinimumFileVersion,

        [Parameter(Mandatory)]
        [string] $ApprovedSignerOrganization,

        [scriptblock] $SignatureInspector = {
            param([string] $Candidate)
            Get-AuthenticodeSignature -LiteralPath $Candidate
        },

        [scriptblock] $FileVersionReader = {
            param([string] $Candidate)
            (Get-Item -LiteralPath $Candidate).VersionInfo.FileVersion.Split(' ', 2)[0]
        }
    )

    $sdk = [Version]$SdkVersion
    foreach ($candidate in $CandidatePaths | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        $signer = Assert-AuthenticodeSignature `
            -Path $candidate `
            -SignatureInspector $SignatureInspector
        $organizationPattern = '(^|,\s*)O=' +
            [Regex]::Escape($ApprovedSignerOrganization) +
            '(,|$)'
        if ($signer -notmatch $organizationPattern) {
            throw "MakeAppx '$candidate' is signed by '$signer', not approved organization '$ApprovedSignerOrganization'."
        }

        $actualVersion = [Version](& $FileVersionReader $candidate)
        if ($actualVersion.Major -ne $sdk.Major -or
            $actualVersion.Minor -ne $sdk.Minor -or
            $actualVersion.Build -ne $sdk.Build -or
            $actualVersion -lt $MinimumFileVersion) {
            throw "MakeAppx '$candidate' has file version '$actualVersion'; expected signed Windows SDK $SdkVersion tooling with version at least '$MinimumFileVersion' and build '$($sdk.Build)'."
        }

        return [pscustomobject]@{
            Path = (Resolve-Path -LiteralPath $candidate).Path
            SdkVersion = $SdkVersion
            FileVersion = $actualVersion.ToString()
            Sha256 = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
            Signer = $signer
            Source = (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "Approved MakeAppx for Windows SDK '$SdkVersion' was not found. Install that exact SDK, or set WindowsSdkVerBinPath/WindowsSdkBinPath to its bin directory. No latest-SDK fallback is permitted."
}

Export-ModuleMember -Function @(
    'Assert-AuthenticodeSignature',
    'Assert-FileHash',
    'Get-MakeAppxCandidatePaths',
    'Get-VerifiedFile',
    'Get-WindowsInstallerCorpusToolManifest',
    'Invoke-PinnedDownload',
    'Resolve-ApprovedMakeAppx'
)
