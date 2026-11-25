param(
    [ValidateSet('osx-arm64', 'osx-x64')][string[]]$Runtime = @('osx-arm64', 'osx-x64'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../../artifacts/macos-0.1.0')
)
$ErrorActionPreference = 'Stop'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$project = Join-Path $PSScriptRoot '../src/PiuLink.Mac/PiuLink.Mac.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version

# Preserve Unix executable permissions even when packaging on Windows.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.IO.Compression;
public static class MacBundleZip {
    public static void Create(string app, string destination) {
        using (var archive = ZipFile.Open(destination, ZipArchiveMode.Create)) {
            foreach (var file in Directory.EnumerateFiles(app, "*", SearchOption.AllDirectories)) {
                var relative = Path.GetFileName(app) + "/" + Path.GetRelativePath(app, file).Replace('\\', '/');
                var entry = archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                var executable = Path.GetFileName(file) == "PiuLink.Mac" || Path.GetFileName(file) == "piulink-direct" || file.EndsWith(".dylib", StringComparison.Ordinal);
                entry.ExternalAttributes = unchecked((int)((executable ? 0x81EDu : 0x81A4u) << 16));
            }
        }
        // ZipArchive uses the builder OS for "version made by". Mark the central
        // directory Unix so macOS extractors honor the stored POSIX permissions.
        using (var stream = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite))
        using (var reader = new BinaryReader(stream)) {
            stream.Position = stream.Length - 22;
            if (reader.ReadUInt32() != 0x06054b50) throw new InvalidDataException("Unexpected ZIP end record.");
            stream.Position += 6;
            var count = reader.ReadUInt16();
            stream.Position += 4;
            var offset = reader.ReadUInt32();
            long position = offset;
            for (int i = 0; i < count; i++) {
                stream.Position = position;
                if (reader.ReadUInt32() != 0x02014b50) throw new InvalidDataException("Invalid ZIP central record.");
                stream.Position = position + 5; stream.WriteByte(3);
                stream.Position = position + 28;
                var name = reader.ReadUInt16(); var extra = reader.ReadUInt16(); var comment = reader.ReadUInt16();
                position += 46 + name + extra + comment;
            }
        }
    }
}
'@

foreach ($rid in $Runtime) {
    $ridOutput = Join-Path $outputRoot $rid
    $bundle = Join-Path $ridOutput 'PiuLink.app'
    if (Test-Path -LiteralPath $bundle) { throw "Output already exists; choose a fresh directory: $bundle" }
    $binaryDirectory = Join-Path $bundle 'Contents/MacOS'
    New-Item -ItemType Directory -Force -Path $binaryDirectory | Out-Null
    dotnet publish $project -c Release -r $rid --self-contained true -p:UseAppHost=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $binaryDirectory
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $rid" }
    if (!(Test-Path -LiteralPath (Join-Path $binaryDirectory 'PiuLink.Mac'))) { throw 'Missing macOS apphost.' }
    $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>PiuLink.Mac</string>
<key>CFBundleIdentifier</key><string>labs.atipiu.piulink.mac</string>
<key>CFBundleName</key><string>PiuLink</string>
<key>CFBundleDisplayName</key><string>PiuLink</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
<key>CFBundleVersion</key><string>$version</string>
<key>CFBundleShortVersionString</key><string>$version</string>
<key>LSMinimumSystemVersion</key><string>12.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSLocalNetworkUsageDescription</key><string>Connect to your PiuLink hosts on the local network.</string>
</dict></plist>
"@
    [IO.File]::WriteAllText((Join-Path $bundle 'Contents/Info.plist'), $plist)
    $resources = Join-Path $bundle 'Contents/Resources'
    New-Item -ItemType Directory -Force -Path $resources | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../README.md') -Destination $resources
    $zip = Join-Path $outputRoot "PiuLink-Mac-$version-$rid-unsigned.zip"
    [MacBundleZip]::Create($bundle, $zip)
    Get-FileHash -LiteralPath $zip -Algorithm SHA256
}
