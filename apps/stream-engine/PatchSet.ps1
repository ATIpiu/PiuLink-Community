function Sync-PiuLinkPatchSet {
    param([string]$Source, [string[]]$Patches, [switch]$VerifyOnly)
    if (!$Patches.Count) { return }
    $previousIndex = $env:GIT_INDEX_FILE
    $indexPath = Join-Path ([IO.Path]::GetTempPath()) ("piulink-index-" + [guid]::NewGuid())
    try {
        $env:GIT_INDEX_FILE = $indexPath
        & git -C $Source read-tree HEAD
        if ($LASTEXITCODE) { throw 'Cannot create source verification index.' }
        foreach ($patch in $Patches) {
            & git -C $Source apply --cached --whitespace=nowarn $patch
            if ($LASTEXITCODE) { throw "Patch sequence does not reproduce the pinned source: $patch" }
        }
        $files = @(& git -C $Source diff --cached --name-only --diff-filter=ACDM)
        & git -C $Source diff --quiet -- $files
        if ($LASTEXITCODE -eq 0) { return }
        if ($VerifyOnly) { throw 'Source differs from the complete reviewed patch sequence.' }
        & git -C $Source diff HEAD --quiet -- $files
        if ($LASTEXITCODE) { throw 'Source has additional or partial changes; use a clean pinned checkout. Existing work was preserved.' }
        foreach ($patch in $Patches) {
            & git -C $Source apply --whitespace=nowarn $patch
            if ($LASTEXITCODE) { throw "Patch application failed: $patch" }
        }
        & git -C $Source diff --quiet -- $files
        if ($LASTEXITCODE) { throw 'Patched source verification failed.' }
    } finally {
        [Environment]::SetEnvironmentVariable('GIT_INDEX_FILE', $previousIndex, 'Process')
        if (Test-Path -LiteralPath $indexPath) { Remove-Item -LiteralPath $indexPath -Force }
    }
}
