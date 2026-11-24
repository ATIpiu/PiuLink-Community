[CmdletBinding()]
param([Parameter(Mandatory)][string]$ApkPath)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/..").Path
$apk = Get-Item -LiteralPath $ApkPath
$release = "$repo/piulink-android/app/build/outputs/apk/nonRoot/release"
$metadata = Get-Content -LiteralPath "$release/output-metadata.json" -Raw | ConvertFrom-Json
if ($metadata.variantName -cne 'nonRootRelease' -or $metadata.applicationId -cne 'labs.atipiu.piulink') {
    throw 'Build the signed assembleNonRootRelease variant before packaging.'
}
$entries = @($metadata.elements | Where-Object { $_.outputFile -ceq $apk.Name -and !$_.filters.Count })
if ($entries.Count -ne 1) { throw 'Expected the universal APK from the nonRootRelease output.' }
$entry = $entries[0]
if ($entry.versionName -notmatch '^\d+\.\d+\.\d+$' -or $entry.versionCode -lt 1 -or
    $apk.Name -cne "PiuLink-Android-$($entry.versionName)-nonRoot-universal-release.apk" -or
    (Get-FileHash -LiteralPath $apk.FullName).Hash -ne (Get-FileHash -LiteralPath "$release/$($entry.outputFile)").Hash) {
    throw 'APK filename, version or digest differs from the Gradle release output.'
}
$properties = "$repo/piulink-android/local.properties"
$sdk = $env:ANDROID_HOME
if (Test-Path $properties) {
    $line = Get-Content $properties | Where-Object { $_ -like 'sdk.dir=*' } | Select-Object -First 1
    if ($line) { $sdk = $line.Substring(8).Replace('\:', ':').Replace('\\','\') }
}
if (!$sdk) { $sdk = "$env:LOCALAPPDATA/Android/Sdk" }
$tools = Get-ChildItem "$sdk/build-tools" -Directory | Where-Object { $_.Name -match '^\d+(\.\d+)+$' } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$cert = & "$($tools.FullName)/apksigner.bat" verify --print-certs $apk.FullName
if ($LASTEXITCODE -or @($cert | Select-String 'certificate SHA-256 digest:').Count -ne 1 -or
    (($cert -join [Environment]::NewLine) -notmatch '(?m)^Signer #1 certificate SHA-256 digest: 87fc3422cb020674abe14a693ba5629d92e9e753ab93bee12cac7828b4805dab\s*$')) {
    throw 'APK release signer verification failed.'
}
$badging = & "$($tools.FullName)/aapt.exe" dump badging $apk.FullName
if ($LASTEXITCODE -or $badging[0] -notmatch "^package: name='labs\.atipiu\.piulink' versionCode='$($entry.versionCode)' versionName='$([regex]::Escape($entry.versionName))'" -or
    $badging -match '^application-debuggable') { throw 'APK manifest does not match the non-debug release.' }
$apk
