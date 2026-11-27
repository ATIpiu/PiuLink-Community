param(
    [string]$SigningCertificateThumbprint,
    [string]$SigningCertificateSubject,
    [string]$OutputDirectory,
    [string]$SelfContained = 'false'
)
$ErrorActionPreference = 'Stop'
$hostRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $hostRoot '..\..\artifacts\windows-share' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$package = Join-Path $OutputDirectory 'package'
New-Item -ItemType Directory -Force (Join-Path $package 'Assets') | Out-Null
& dotnet publish (Join-Path $hostRoot 'src\PiuLink.ShareTarget\PiuLink.ShareTarget.csproj') -c Release -r win-x64 --self-contained $SelfContained -o (Join-Path $OutputDirectory 'ShareTarget')
if ($LASTEXITCODE) { throw 'Share target build failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Destination $package -Force
foreach ($asset in @(@('StoreLogo.scale-100.png','StoreLogo.png'),@('Square150x150Logo.scale-100.png','Logo.png'),@('Square44x44Logo.scale-100.png','SmallLogo.png'))) {
    Copy-Item -LiteralPath (Join-Path $hostRoot ('store-assets\' + $asset[0])) -Destination (Join-Path $package ('Assets\' + $asset[1])) -Force
}
$sdk = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Filter makeappx.exe -Recurse | Where-Object DirectoryName -match '\\x64$' | Sort-Object FullName -Descending | Select-Object -First 1
if (!$sdk) { throw 'Windows SDK MakeAppx is required' }
$msix = Join-Path $OutputDirectory 'PiuLink.Share.msix'
if ($SigningCertificateThumbprint) {
    if ([string]::IsNullOrWhiteSpace($SigningCertificateSubject)) {
        $thumbprint = $SigningCertificateThumbprint.Replace(' ', '').ToUpperInvariant()
        $certificate = Get-Item -Path "Cert:\CurrentUser\My\$thumbprint" -ErrorAction SilentlyContinue
        if (!$certificate) {
            $certificate = Get-Item -Path "Cert:\LocalMachine\My\$thumbprint" -ErrorAction SilentlyContinue
        }
        if (!$certificate) { throw "Share signing certificate was not found: $SigningCertificateThumbprint" }
        if (!$certificate.HasPrivateKey) { throw 'The share signing certificate does not have a private key.' }
        $SigningCertificateSubject = $certificate.Subject
    }
    $manifestPath = Join-Path $package 'AppxManifest.xml'
    [xml]$manifest = [IO.File]::ReadAllText($manifestPath)
    $manifest.Package.Identity.Publisher = $SigningCertificateSubject
    $manifest.Save($manifestPath)
}
& $sdk.FullName pack /d $package /p $msix /o /nv
if ($LASTEXITCODE) { throw 'Share identity packaging failed' }
if ($SigningCertificateThumbprint) {
    & (Join-Path $sdk.DirectoryName 'signtool.exe') sign /fd SHA256 /sha1 $SigningCertificateThumbprint $msix
    if ($LASTEXITCODE) { throw 'Share identity signing failed' }
}
Write-Output $msix
