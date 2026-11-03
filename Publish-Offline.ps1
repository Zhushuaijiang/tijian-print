param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\offline'))
$ErrorActionPreference = 'Stop'
foreach ($component in @('Desktop','Api')) {
    $project = Join-Path $PSScriptRoot ('src\PeisSelfPrint.' + $component + '\PeisSelfPrint.' + $component + '.csproj')
    $destination = Join-Path $OutputDirectory $component
    & dotnet publish $project -c Release -r win-x64 --self-contained true -o $destination --nologo
    if ($LASTEXITCODE -ne 0) { throw ('Publish failed: ' + $component) }
    foreach ($runtimeFile in @('hostfxr.dll','coreclr.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $destination $runtimeFile))) { throw ('Missing bundled runtime: ' + $component + '/' + $runtimeFile) }
    }
}
Write-Host 'Self-contained win-x64 binaries ready. Keep all files in each component directory together.'

foreach ($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'deployment') -File) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $OutputDirectory $file.Name) -Force }
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'delivery') -File) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $OutputDirectory $file.Name) -Force }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Tools') -Destination $OutputDirectory -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'KIOSK-DEV.md') -Destination $OutputDirectory -Force
Write-Host 'Kiosk launchers and portable print engine copied. Site credentials are never included.'
