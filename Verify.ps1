$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot 'PeisSelfPrint.sln'
$tests = Join-Path $PSScriptRoot 'tests\PeisSelfPrint.Tests\PeisSelfPrint.Tests.csproj'
& dotnet build $solution -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& dotnet run --project $tests -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
Write-Host 'Build and logic tests passed. No patient system contacted and no printing performed.'
