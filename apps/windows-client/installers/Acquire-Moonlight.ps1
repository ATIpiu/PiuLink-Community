[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$clientRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $PSScriptRoot 'manifests\moonlight-windows-x64.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$thirdPartyRoot = Join-Path $clientRoot 'third_party'
$cacheRoot = Join-Path $thirdPartyRoot 'cache'
$archivePath = Join-Path $cacheRoot "MoonlightPortable-x64-$($manifest.version).zip"
$payloadRoot = Join-Path $thirdPartyRoot 'moonlight'

function Assert-ChildPath([string]$Path) {
    $resolvedRoot = $clientRoot.TrimEnd('\') + '\'
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe path outside Windows client root: $resolvedPath"
    }
}

Assert-ChildPath $thirdPartyRoot
Assert-ChildPath $cacheRoot
Assert-ChildPath $archivePath
Assert-ChildPath $payloadRoot
New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null

if ($Force -or -not (Test-Path -LiteralPath $archivePath)) {
    $temporaryArchive = Join-Path $cacheRoot "moonlight-$([Guid]::NewGuid().ToString('N')).download"
    Assert-ChildPath $temporaryArchive
    try {
        Invoke-WebRequest -Headers @{ 'User-Agent' = 'PiuLink-build' } -Uri $manifest.archiveUrl -OutFile $temporaryArchive
        $downloadHash = (Get-FileHash -LiteralPath $temporaryArchive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($downloadHash -ne $manifest.archiveSha256) {
            throw "Moonlight archive SHA-256 mismatch. Expected $($manifest.archiveSha256), got $downloadHash."
        }

        Move-Item -LiteralPath $temporaryArchive -Destination $archivePath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryArchive) {
            Remove-Item -LiteralPath $temporaryArchive -Force
        }
    }
}

$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($archiveHash -ne $manifest.archiveSha256) {
    throw "Cached Moonlight archive SHA-256 mismatch. Delete it or rerun with -Force."
}

$stagingRoot = Join-Path $thirdPartyRoot "moonlight-staging-$([Guid]::NewGuid().ToString('N'))"
Assert-ChildPath $stagingRoot
New-Item -ItemType Directory -Path $stagingRoot | Out-Null

try {
    Expand-Archive -LiteralPath $archivePath -DestinationPath $stagingRoot
    $moonlightExe = Join-Path $stagingRoot 'Moonlight.exe'
    if (-not (Test-Path -LiteralPath $moonlightExe)) {
        throw 'Moonlight archive did not contain Moonlight.exe.'
    }

    $executableHash = (Get-FileHash -LiteralPath $moonlightExe -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($executableHash -ne $manifest.executableSha256) {
        throw "Moonlight.exe SHA-256 mismatch. Expected $($manifest.executableSha256), got $executableHash."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $moonlightExe
    if ($signature.Status -ne 'Valid' -or
        $signature.SignerCertificate.Subject -ne $manifest.signerSubject -or
        $signature.SignerCertificate.Thumbprint -ne $manifest.signerThumbprint) {
        throw 'Moonlight.exe Authenticode signer did not match the pinned manifest.'
    }

    # The official portable marker makes Moonlight write pairing state beside its executable. That
    # directory is read-only inside MSIX, so use Moonlight's normal per-user AppData profile instead.
    $portableMarker = Join-Path $stagingRoot 'portable.dat'
    if (Test-Path -LiteralPath $portableMarker) {
        Remove-Item -LiteralPath $portableMarker -Force
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses\Moonlight-NOTICE.md') -Destination (Join-Path $stagingRoot 'PiuLink-Moonlight-NOTICE.md')
    Copy-Item -LiteralPath (Join-Path $clientRoot '..\windows-host\installers\licenses\Sunshine-GPL-3.0.txt') -Destination (Join-Path $stagingRoot 'GPL-3.0.txt')

    if (Test-Path -LiteralPath $payloadRoot) {
        if (-not $Force) {
            $installedExe = Join-Path $payloadRoot 'Moonlight.exe'
            if ((Test-Path -LiteralPath $installedExe) -and
                (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash.ToLowerInvariant() -eq $manifest.executableSha256) {
                Write-Output "Moonlight $($manifest.version) is already prepared at $payloadRoot"
                return
            }

            throw 'A different Moonlight payload already exists. Rerun with -Force to replace it.'
        }

        Assert-ChildPath $payloadRoot
        Remove-Item -LiteralPath $payloadRoot -Recurse -Force
    }

    Move-Item -LiteralPath $stagingRoot -Destination $payloadRoot
    Write-Output "Prepared verified Moonlight $($manifest.version) payload at $payloadRoot"
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Assert-ChildPath $stagingRoot
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
