[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory, [Parameter(Mandatory)][string]$PublishDirectory,
    [switch]$LayoutTest)
$ErrorActionPreference = 'Stop'
$pkg = (Resolve-Path -LiteralPath $PackageDirectory).Path
$target = (New-Item -ItemType Directory -Force -Path "$PublishDirectory/drivers/microphone").FullName
$files = 'PiuLinkMic.inf','PiuLinkMic.sys','PiuLinkMic.cat'
foreach ($file in $files) { if (!(Test-Path -LiteralPath "$pkg/$file" -PathType Leaf)) { throw "Missing native microphone component: $file" } }
if (!(Select-String -LiteralPath "$pkg/PiuLinkMic.inf" -SimpleMatch 'ROOT\PiuLinkMic' -Quiet)) { throw 'Unexpected microphone hardware ID.' }
if (!$LayoutTest) {
    $signTool = Get-ChildItem -Path "${env:ProgramFiles(x86)}/Windows Kits/10/bin/*/x64/signtool.exe" |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
    if (!$signTool) { throw 'Install Windows SDK SignTool on the build computer.' }
    $catalog = "$pkg/PiuLinkMic.cat"
    $sig = Get-AuthenticodeSignature -FilePath $catalog
    if ($sig.Status -ne 'Valid' -or !$sig.SignerCertificate -or
        $sig.SignerCertificate.Subject -notmatch '(?:^|, )O=Microsoft Corporation(?:,|$)') {
        throw 'This microphone package requires the catalog returned by the Microsoft Hardware dashboard.'
    }
    foreach ($member in 'PiuLinkMic.sys','PiuLinkMic.inf') {
        & $signTool.FullName verify /q /kp /c $catalog "$pkg/$member"
        if ($LASTEXITCODE) { throw "Kernel catalog verification failed for $member." }
    }
}
foreach ($file in $files) { Copy-Item -LiteralPath "$pkg/$file" -Destination $target -Force }
Copy-Item -LiteralPath "$PSScriptRoot/Microsoft-Public-License.txt" -Destination $target -Force
