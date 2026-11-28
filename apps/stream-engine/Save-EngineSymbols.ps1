[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EngineDirectory,
    [Parameter(Mandatory)][string]$MsysRoot
)
$ErrorActionPreference = 'Stop'
$engine = (Resolve-Path -LiteralPath $EngineDirectory).Path
$strip = (Resolve-Path -LiteralPath (Join-Path $MsysRoot 'ucrt64/bin/strip.exe')).Path
# Keep the complete, symbol-bearing executable indexed by the shipped binary's hash.
# The sibling directory is private build output and is never harvested by the MSI.
$archive = $engine.TrimEnd('\', '/') + '-symbols'
foreach ($relative in 'PiuLink.Engine.exe','tools/audio-info.exe','tools/PiuLink.Engine.Service.exe') {
    $source = Get-Item -LiteralPath (Join-Path $engine $relative)
    if ((Get-AuthenticodeSignature -LiteralPath $source.FullName).Status -ne 'NotSigned') { continue }
    $temporary = $source.FullName + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        & $strip --strip-all --preserve-dates -o $temporary $source.FullName
        if ($LASTEXITCODE) { throw "Could not strip $relative" }
        if ((Get-Item -LiteralPath $temporary).Length -ge $source.Length) { continue }
        $key = (Get-FileHash -LiteralPath $temporary).Hash
        $directory = New-Item -ItemType Directory -Force -Path (Join-Path $archive $key)
        $saved = Join-Path $directory.FullName $source.Name
        if (!(Test-Path -LiteralPath $saved)) { Copy-Item -LiteralPath $source.FullName -Destination $saved }
        if ((Get-FileHash -LiteralPath $saved).Hash -ne (Get-FileHash -LiteralPath $source.FullName).Hash) {
            throw "Symbol archive mismatch for $relative"
        }
        [IO.File]::Replace($temporary, $source.FullName, [System.Management.Automation.Language.NullString]::Value)
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}
