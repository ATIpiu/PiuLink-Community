[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceDirectory, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'upstream.json') -Raw | ConvertFrom-Json
$sourcePath = [IO.Path]::GetFullPath($SourceDirectory)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if ((& git -C $sourcePath rev-parse HEAD) -ne $pin.commit) { throw 'Source commit mismatch.' }
. (Join-Path $PSScriptRoot "PatchSet.ps1")
Sync-PiuLinkPatchSet -Source $sourcePath -Patches @($pin.patches | ForEach-Object { Join-Path $PSScriptRoot $_ }) -VerifyOnly
foreach ($group in ($pin.dependencyPatches | Group-Object directory)) {
    Sync-PiuLinkPatchSet -Source (Join-Path $sourcePath $group.Name) -Patches @($group.Group | ForEach-Object { Join-Path $PSScriptRoot $_.patch }) -VerifyOnly
}
$submodules = & git -C $sourcePath submodule status --recursive
if ($LASTEXITCODE -or @($submodules | Where-Object { $_ -match '^[+\-U]' }).Count) {
    throw 'Incomplete or mismatched source dependencies.'
}
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$archive = Join-Path $outputPath "PiuLink-Engine-$($pin.engineVersion)-source.zip"
if (Test-Path -LiteralPath $archive) { throw 'Choose a new output directory; do not append to an earlier source archive.' }
$sevenZip = Join-Path $env:ProgramFiles '7-Zip/7z.exe'
if (!(Test-Path -LiteralPath $sevenZip)) { throw 'Install 7-Zip to create and integrity-test the complete source archive.' }
Push-Location -LiteralPath $sourcePath
try {
    & $sevenZip a -tzip $archive . '-xr!.git' '-xr!node_modules' '-xr!build-piulink' '-x!config' -mx=3
    if ($LASTEXITCODE) { throw 'Source archive failed.' }
} finally { Pop-Location }
& $sevenZip t $archive
if ($LASTEXITCODE) { throw 'Source archive integrity verification failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'upstream.json') -Destination $outputPath
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $outputPath 'ENGINE-BUILD.md')
Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Select-Object Path, Hash
