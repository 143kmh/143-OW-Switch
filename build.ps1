param([string]$Output = "$PSScriptRoot\artifacts")
$ErrorActionPreference = 'Stop'
dotnet run --project "$PSScriptRoot\tests\Excluder.Tests" -c Release
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
dotnet publish "$PSScriptRoot\src\Excluder.App" -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath "$Output\143OWSwitch.exe").Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText("$Output\143OWSwitch.exe.sha256", "$hash  143OWSwitch.exe`n", [System.Text.UTF8Encoding]::new($false))
