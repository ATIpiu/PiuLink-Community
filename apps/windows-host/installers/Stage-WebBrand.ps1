[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WebRoot,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $WebRoot).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$descriptions = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'web-descriptions.json')) | ConvertFrom-Json

# The standalone engine brands its staged directory in place; Host publish uses a separate output.
function Write-StagedText([string]$RelativePath, [string]$Text) {
    $path = Join-Path $output $RelativePath
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    if (![IO.File]::Exists($path) -or [IO.File]::ReadAllText($path) -cne $Text) {
        [IO.File]::WriteAllText($path, $Text, $utf8)
    }
}
foreach ($page in Get-ChildItem -LiteralPath $root -Filter '*.html' -File) {
    $html = [IO.File]::ReadAllText($page.FullName)
    $html = $html.Replace('<title>Sunshine</title>', '<title>PiuLink Host</title>').Replace('alt="Sunshine"', 'alt="PiuLink Host"')
    Write-StagedText $page.Name $html
}
foreach ($bundle in Get-ChildItem -LiteralPath "$root/assets" -Filter 'Navbar-*.js' -File) {
    $js = [IO.File]::ReadAllText($bundle.FullName)
    $js = $js.Replace('title:"Sunshine"', 'title:"PiuLink Host"').Replace('alt:"Sunshine"', 'alt:"PiuLink Host"')
    Write-StagedText "assets/$($bundle.Name)" $js
}
# The pinned frontend embeds the default English dictionary and skips en.json at runtime.
$english = [IO.File]::ReadAllText("$root/assets/locale/en.json") | ConvertFrom-Json
$defaults = @(
    @{ Key='welcome'; Before=$english.index.welcome; After='PiuLink Host' }
    @{ Key='description'; Before=$english.index.description; After=$descriptions.en }
    @{ Key='greeting'; Before=$english.welcome.greeting; After='PiuLink Host' }
    @{ Key='create_creds_alert'; Before=$english.welcome.create_creds_alert; After=$english.welcome.create_creds_alert.Replace('Sunshine', 'PiuLink Host') }
)
$bundles = @(Get-ChildItem -LiteralPath "$root/assets" -Filter 'createLucideIcon-*.js' -File)
if ($bundles.Count -ne 1) { throw 'Expected one frontend bundle containing the default English dictionary.' }
$js = [IO.File]::ReadAllText($bundles[0].FullName)
foreach ($entry in $defaults) {
    $key = $entry.Key + ':"'
    $original = $key + $entry.Before + '"'
    if (!$js.Contains($original)) { throw "Embedded English text is missing for $($entry.Key)." }
    $js = $js.Replace($original, $key + $entry.After + '"')
}
Write-StagedText "assets/$($bundles[0].Name)" $js
foreach ($bundle in Get-ChildItem -LiteralPath "$root/assets" -Filter 'welcome-*.js' -File) {
    $js = [IO.File]::ReadAllText($bundle.FullName)
    Write-StagedText "assets/$($bundle.Name)" ($js.Replace('newUsername:"sunshine"', 'newUsername:"piulink"'))
}
foreach ($file in Get-ChildItem -LiteralPath "$root/assets/locale" -Filter '*.json' -File) {
    $locale = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
    $description = $descriptions.($file.BaseName)
    if (!$description) { $description = $descriptions.($file.BaseName.Split('_')[0]) }
    if (!$description) { $description = $descriptions.en }
    $locale.index.welcome = 'PiuLink Host'
    $locale.index.description = $description
    $locale.welcome.greeting = 'PiuLink Host'
    $locale.welcome.create_creds_alert = $locale.welcome.create_creds_alert.Replace('Sunshine', 'PiuLink Host')
    Write-StagedText "assets/locale/$($file.Name)" ($locale | ConvertTo-Json -Depth 32 -Compress)
}
