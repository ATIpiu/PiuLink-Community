[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Bundle,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Python = 'python',
    [string]$SevenZip = "$env:ProgramFiles\7-Zip\7z.exe",
    [string]$Iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$ErrorActionPreference = 'Stop'
$bundleFile = Get-Item -LiteralPath $Bundle
if ($bundleFile.Name -notmatch '^PiuLink-Windows-(\d+\.\d+\.\d+)-win-x64\.exe$') {
    throw 'Select the canonical public Windows suite EXE.'
}
$version = $Matches[1]
$work = (New-Item -ItemType Directory -Path $WorkDirectory).FullName
$out = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
$targets = & dotnet msbuild "$PSScriptRoot/../PiuLink.Windows.wixproj" -nologo -getProperty:WixTargetsPath
if ($LASTEXITCODE) { throw 'Cannot locate the pinned WiX SDK.' }
$native = (Resolve-Path (Join-Path (Split-Path $targets) 'net472/x64/wixnative.exe')).Path
$payload = Join-Path $work 'media'
& $Python "$PSScriptRoot/prepare.py" $bundleFile.FullName $native $payload
if ($LASTEXITCODE) { throw 'Media preparation failed.' }
Copy-Item -LiteralPath $native, "$PSScriptRoot/WiX-LICENSE.txt" -Destination $payload
Push-Location $PSScriptRoot
try {
    & "$env:SystemRoot/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /optimize+ /target:exe "/out:$payload\RestoreBundle.exe" RestoreBundle.cs
    if ($LASTEXITCODE) { throw 'Could not build the restore utility.' }
} finally { Pop-Location }
$archive = Join-Path $work 'media.7z'
Push-Location $payload
try {
    & $SevenZip a -t7z -mx=9 -md=192m -mmt=4 -ms=1g -mqs=on $archive .
    if ($LASTEXITCODE) { throw 'Solid compression failed.' }
} finally { Pop-Location }
& $Iscc /Q "/DSuiteVersion=$version" "/DArchive=$archive" "/O$out" "$PSScriptRoot/first-install.iss"
if ($LASTEXITCODE) { throw 'First-install packaging failed.' }
