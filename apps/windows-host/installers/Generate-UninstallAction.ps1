param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))) | Out-Null
$payload=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Remove-StandaloneSunshine.ps1'))
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($payload))
# The synchronous commit action only starts an independent worker. It must not
# wait for that worker, which waits for this MSI transaction to finish.
$launcher="`$p=Start-Process -FilePath (Join-Path `$env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList '-NoProfile -NonInteractive -EncodedCommand $encoded' -WindowStyle Hidden -PassThru; if (!`$p) { exit 1 }"
$launchEncoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($launcher))
[IO.File]::WriteAllText($OutputPath, '<Include xmlns="http://wixtoolset.org/schemas/v4/wxs"><?define SunshineRemovalCommand="'+$launchEncoded+'" ?></Include>')
