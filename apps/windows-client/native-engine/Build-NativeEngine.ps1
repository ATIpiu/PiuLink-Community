[CmdletBinding()]
param([string]$Configuration = 'Release', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$cmake = 'C:\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$build = Join-Path $PSScriptRoot 'build'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\windows-engine\PiuLink-Native-0.1.0-x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$repoPrefix = $root.TrimEnd('\') + '\'
if (-not $OutputDirectory.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Native output directory must remain inside the repository: $OutputDirectory"
}
if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
& $cmake -S $PSScriptRoot -B $build -A x64
if ($LASTEXITCODE) { throw "Native engine configure failed: $LASTEXITCODE" }
& $cmake --build $build --config $Configuration --parallel
if ($LASTEXITCODE) { throw "Native engine build failed: $LASTEXITCODE" }
& $cmake --install $build --config $Configuration --prefix $OutputDirectory
if ($LASTEXITCODE) { throw "Native engine install failed: $LASTEXITCODE" }
Write-Output $OutputDirectory
