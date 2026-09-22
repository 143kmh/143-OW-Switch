param([string]$Output = "$PSScriptRoot\artifacts")
$ErrorActionPreference = 'Stop'
dotnet run --project "$PSScriptRoot\tests\Excluder.Tests" -c Release
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
dotnet publish "$PSScriptRoot\src\Excluder.App" -c Release -r win-x64 --self-contained true -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Get-FileHash -Algorithm SHA256 -LiteralPath "$Output\143 Overwatch Finlad Server Excluder.exe"
