[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$Tests,
    [switch]$Android,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}
Push-Location $repo
try {
    $solution = Join-Path $repo 'PiuLink.sln'
    $projects = [regex]::Matches([IO.File]::ReadAllText($solution), '"([^"\r\n]+\.csproj)"') |
        ForEach-Object { [IO.Path]::GetFullPath((Join-Path $repo $_.Groups[1].Value)) }
    if ($projects.Count -eq 0) { throw 'No maintained projects found in the root solution.' }
    foreach ($project in $projects) {
        if (!(Test-Path -LiteralPath $project -PathType Leaf)) { throw "Missing solution project: $project" }
        if ($project.StartsWith((Join-Path $repo 'archive') + [IO.Path]::DirectorySeparatorChar)) { throw "Archived project in active solution: $project" }
        $xml = [xml][IO.File]::ReadAllText($project)
        foreach ($reference in $xml.SelectNodes('//ProjectReference')) {
            $resolved = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $project) $reference.Include))
            if (!(Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Broken project reference in ${project}: $($reference.Include)" }
            if ($resolved.StartsWith((Join-Path $repo 'archive') + [IO.Path]::DirectorySeparatorChar)) { throw "Active project references archive: $resolved" }
        }
    }
    foreach ($path in @('apps/macos-client/installers/Build-Mac.ps1', 'packages/client-core/tests/PiuLink.Client.Core.Tests', 'docs/README.md')) {
        if (!(Test-Path -LiteralPath $path)) { throw "Missing maintained entry: $path" }
    }
    Write-Output "PASS structure: $($projects.Count) active projects and their project references."
    $documents = @(Get-Item -LiteralPath (Join-Path $repo 'README.md')) +
        @(Get-ChildItem -LiteralPath (Join-Path $repo 'docs') -Filter '*.md' -File -Recurse)
    foreach ($document in $documents) {
        $body = [regex]::Replace([IO.File]::ReadAllText($document.FullName), '(?ms)^```.*?^```[^\r\n]*', '')
        foreach ($link in [regex]::Matches($body, '\]\(([^\s\)]+)\)')) {
            $target = $link.Groups[1].Value
            if ($target -match '^([a-zA-Z]+:|/|#)') { continue }
            $path = ($target -split '#', 2)[0]
            $resolved = Join-Path $document.DirectoryName $path
            if (!(Test-Path -LiteralPath $resolved)) { throw "Broken documentation link in $($document.FullName): $target" }
        }
    }
    Write-Output "PASS documentation: $($documents.Count) entry/index documents and relative link targets."
    if ($Build) { Invoke-DotNet @('build', $solution, '-c', $Configuration) }
    if ($Tests) {
        $testProjects = @(
            'packages/client-core/tests/PiuLink.Client.Core.Tests',
            'apps/windows-host/tests/PiuLink.Host.Core.Tests',
            'services/activation/tests/PiuLink.Activation.Tests'
        )
        if ($IsWindows) {
            $testProjects += 'apps/windows-host/tests/PiuLink.Host.Display.Tests'
            $testProjects += 'apps/windows-host/tests/PiuLink.Host.Instance.Tests'
        }
        foreach ($testProject in $testProjects) {
            $arguments = @('run', '--project', $testProject, '-c', $Configuration)
            if ($Build) { $arguments += '--no-build' }
            Invoke-DotNet $arguments
        }
    }
    if ($Android) {
        $androidRoot = Join-Path $repo 'piulink-android'
        if (!(Test-Path -LiteralPath (Join-Path $androidRoot 'gradlew.bat'))) { throw 'The independent piulink-android checkout is missing.' }
        Push-Location $androidRoot
        try {
            $wrapper = if ($IsWindows) { './gradlew.bat' } else { './gradlew' }
            & $wrapper :app:verifyReleaseSigningConfiguration :app:testNonRootReleaseUnitTest :app:lintNonRootRelease :app:assembleNonRootRelease --console=plain
            if ($LASTEXITCODE -ne 0) { throw "Android verification failed ($LASTEXITCODE)." }
        } finally { Pop-Location }
    }
} finally { Pop-Location }
