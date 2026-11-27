[CmdletBinding()]
param(
    [string]$ManifestPath = (Join-Path $PSScriptRoot 'manifests\sunshine-windows-x64.json'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'source-output'),
    [string]$TemporaryDirectory = (Join-Path ([IO.Path]::GetPathRoot([IO.Path]::GetTempPath())) 'piulink-src')
)

$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$temporaryRoot = [IO.Path]::GetFullPath($TemporaryDirectory)
$null = New-Item -ItemType Directory -Path $temporaryRoot -Force
$checkoutPath = [IO.Path]::GetFullPath((Join-Path $temporaryRoot "piulink-sunshine-source-$([Guid]::NewGuid().ToString('N'))"))

if ($checkoutPath -eq $temporaryRoot -or
    -not $checkoutPath.StartsWith(($temporaryRoot.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to create a source checkout outside the Windows temporary directory.'
}

$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$archivePath = Join-Path $OutputDirectory "Sunshine-$($manifest.version)-complete-source.zip"

try {
    # Sunshine has deeply nested submodules whose test fixtures exceed the legacy MAX_PATH limit.
    # A short checkout root plus Git's long-path support is required for a complete archive on Windows.
    & git -c core.longpaths=true clone --depth 1 --branch $manifest.sourceTag --recurse-submodules --shallow-submodules `
        'https://github.com/LizardByte/Sunshine.git' $checkoutPath
    if ($LASTEXITCODE -ne 0) {
        throw "Sunshine source clone failed with exit code $LASTEXITCODE."
    }

    $actualCommit = (& git -c core.longpaths=true -C $checkoutPath rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $manifest.sourceCommit) {
        throw "Sunshine source commit mismatch. Expected $($manifest.sourceCommit), got $actualCommit."
    }

    $submoduleStatus = & git -c core.longpaths=true -C $checkoutPath submodule status --recursive
    $unexpectedSubmodules = @($submoduleStatus | Where-Object { $_ -match '^[+-]' })
    if ($LASTEXITCODE -ne 0 -or $unexpectedSubmodules.Count -gt 0) {
        throw 'One or more Sunshine submodules are missing or checked out at an unexpected commit.'
    }

    & tar.exe -a -c -f $archivePath --exclude='.git' --exclude='*/.git' -C $checkoutPath .
    if ($LASTEXITCODE -ne 0) {
        throw "Sunshine source archive failed with exit code $LASTEXITCODE."
    }

    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    [pscustomobject]@{
        Path = (Resolve-Path -LiteralPath $archivePath).Path
        Version = $manifest.version
        Commit = $actualCommit
        SHA256 = $archiveHash
        SubmoduleCount = @($submoduleStatus).Count
    }
}
finally {
    if ((Test-Path -LiteralPath $checkoutPath) -and
        $checkoutPath.StartsWith(($temporaryRoot.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $checkoutPath -Recurse -Force
    }
}
