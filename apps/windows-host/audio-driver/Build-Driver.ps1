[CmdletBinding()]
param([string]$OutputDirectory, [string]$UpstreamRepository)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../../..").Path
$commit = 'd49bb7d72f3651952b2b6e4ad28d14fe2247eb69'
$version = '10.0.26100.6584'
$kit = '10.0.26100.0'
if (!$OutputDirectory) { $OutputDirectory = "$root/artifacts/PiuLinkMic-x64" }
$out = (New-Item -Force -ItemType Directory -Path $OutputDirectory).FullName
$build = (New-Item -ItemType Directory -Path "$root/work/piulink-mic-build-$([guid]::NewGuid().ToString('N'))").FullName
if (!$UpstreamRepository) {
    $UpstreamRepository = "$root/work/piulink-microphone-samples"
    if (!(Test-Path -LiteralPath "$UpstreamRepository/.git")) {
        git clone --quiet --filter=blob:none --no-checkout https://github.com/microsoft/Windows-driver-samples.git $UpstreamRepository
        if ($LASTEXITCODE) { throw 'Could not fetch the Microsoft audio driver sample.' }
    }
}
git -C $UpstreamRepository archive "--output=$build/source.tar" $commit audio/simpleaudiosample
if ($LASTEXITCODE) { throw 'Pinned driver sources are unavailable.' }
tar -xf "$build/source.tar" -C $build
if ($LASTEXITCODE) { throw 'Could not extract the pinned sources.' }
git init -q $build
git -C $build apply --ignore-space-change "$PSScriptRoot/loopback.patch"
if ($LASTEXITCODE) { throw 'PiuLink driver patch did not apply.' }

$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { "$env:USERPROFILE/.nuget/packages" }
$wdk = "$packages/microsoft.windows.wdk.x64/$version/c"
if (!(Test-Path "$wdk/Include/$kit/km/portcls.h")) {
    $restoreProject = "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageDownload Include='Microsoft.Windows.WDK.x64' Version='[$version]' /></ItemGroup></Project>"
    Set-Content -LiteralPath "$build/WDK.csproj" -Value $restoreProject
    dotnet restore "$build/WDK.csproj"
    if ($LASTEXITCODE) { throw 'Could not restore the pinned Windows Driver Kit.' }
}
$vs = & "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'MSVC x64 build tools are required.' }
$vcVersion = (Get-Content "$vs/VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt").Trim()
$vc = "$vs/VC/Tools/MSVC/$vcVersion"
$sdk = "${env:ProgramFiles(x86)}/Windows Kits/10"
$src = "$build/audio/simpleaudiosample/Source"
Copy-Item -LiteralPath "$PSScriptRoot/PiuLinkPcmQueue.h" -Destination "$src/Inc"
$inc = @("$wdk/Include/$kit/km", "$wdk/Include/$kit/km/crt", "$wdk/Include/wdf/kmdf/1.15",
    "$sdk/Include/$kit/shared", "$sdk/Include/$kit/ucrt", "$vc/include", "$src/Inc", "$src/Filters", "$src/Main", "$src/Utilities")
$definitions = @('_AMD64_', 'AMD64', '_WIN64', '_WIN32', '_NEW_DELETE_OPERATORS_', 'UNICODE', '_UNICODE', 'PC_IMPLEMENTATION',
    'WINVER=0x0A00', '_WIN32_WINNT=0x0A00', 'NTDDI_VERSION=0x0A000008',
    'KMDF_VERSION_MAJOR=1', 'KMDF_VERSION_MINOR=15', 'DEPRECATE_DDK_FUNCTIONS=1')
$clArgs = @('/nologo','/c','/O2','/GS','/Gy','/Zi','/Zl','/kernel','/GR-','/W3','/guard:cf') +
    @($inc | ForEach-Object { "/I$_" }) + @($definitions | ForEach-Object { "/D$_" })
$sources = Get-ChildItem -LiteralPath "$src/Filters","$src/Main","$src/Utilities" -Filter '*.cpp' -File
Push-Location $build
try {
    & "$vc/bin/Hostx64/x64/cl.exe" @clArgs @($sources.FullName)
    if ($LASTEXITCODE) { throw 'Driver compilation failed.' }
    $km = "$wdk/Lib/$kit/km/x64"
    $wdf = "$wdk/Lib/wdf/kmdf/x64/1.15"
    & "$sdk/bin/$kit/x64/rc.exe" /nologo /I "$sdk/Include/$kit/um" /I "$sdk/Include/$kit/shared" /fo PiuLinkMic.res "$PSScriptRoot/PiuLinkMic.rc"
    if ($LASTEXITCODE) { throw 'Driver resource compilation failed.' }
    & "$vc/bin/Hostx64/x64/link.exe" /nologo /driver /subsystem:native /machine:x64 /nodefaultlib /entry:FxDriverEntry /integritycheck /dynamicbase /nxcompat /guard:cf /opt:ref /debug:full /Brepro '/PDBALTPATH:%_PDB%' "/pdb:$build/PiuLinkMic.pdb" "/out:$build/PiuLinkMic.sys" "/libpath:$km" "/libpath:$wdf" @((Get-ChildItem -Filter '*.obj').FullName) PiuLinkMic.res ntoskrnl.lib hal.lib portcls.lib stdunk.lib libcntpr.lib ks.lib wmilib.lib WdfLdr.lib WdfDriverEntry.lib bufferoverflowfastfailk.lib
    if ($LASTEXITCODE) { throw 'Driver link failed.' }
} finally { Pop-Location }
$pkg = (New-Item -ItemType Directory -Path "$build/package").FullName
Copy-Item -LiteralPath "$build/PiuLinkMic.sys","$PSScriptRoot/PiuLinkMic.inf" -Destination $pkg
& "$wdk/tools/$kit/x64/InfVerif.exe" /w "$pkg/PiuLinkMic.inf"
if ($LASTEXITCODE) { throw 'INF verification failed.' }
& "$wdk/bin/$kit/x86/Inf2Cat.exe" "/driver:$pkg" /os:10_X64 /uselocaltime
if ($LASTEXITCODE) { throw 'Driver package or catalog validation failed.' }
Copy-Item -LiteralPath "$pkg/PiuLinkMic.sys","$pkg/PiuLinkMic.inf","$pkg/PiuLinkMic.cat","$build/PiuLinkMic.pdb","$PSScriptRoot/Microsoft-Public-License.txt" -Destination $out -Force
$ddf = @('.OPTION EXPLICIT','.Set Cabinet=on','.Set Compress=on','.Set CompressionType=MSZIP',
    '.Set CabinetNameTemplate=PiuLinkMic-submission.cab', ".Set DiskDirectoryTemplate=`"$out`"", '.Set DestinationDir=PiuLinkMic')
$ddf += @('PiuLinkMic.inf','PiuLinkMic.sys','PiuLinkMic.cat','PiuLinkMic.pdb') | ForEach-Object { "`"$out\$_`" $_" }
Set-Content -LiteralPath "$build/submit.ddf" -Value $ddf -Encoding ASCII
Push-Location $build
try {
    & "$env:SystemRoot/System32/makecab.exe" /F "$build/submit.ddf" | Out-Null
    if ($LASTEXITCODE) { throw 'Submission CAB generation failed.' }
} finally { Pop-Location }
Write-Output "Built unsigned driver package in $out."
