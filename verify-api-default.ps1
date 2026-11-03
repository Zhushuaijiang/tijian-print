param([string]$ProjectRoot = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$apiDir = Join-Path $ProjectRoot 'src\PeisSelfPrint.Api'
$dll = Join-Path $apiDir 'bin\Release\net8.0\PeisSelfPrint.Api.dll'
if (!(Test-Path -LiteralPath $dll)) { throw 'Build Release first.' }
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'dotnet'
$psi.Arguments = '"' + $dll + '" --urls http://127.0.0.1:18973 --contentRoot "' + $apiDir + '" --Logging:LogLevel:Microsoft.Hosting.Lifetime Information'
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.CreateNoWindow = $true
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $psi
try {
    if (!$process.Start()) { throw 'Could not start API.' }
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        $lineTask = $process.StandardOutput.ReadLineAsync()
        if (!$lineTask.Wait(15000)) { throw 'API startup timeout.' }
        $line = $lineTask.Result
        if ($null -eq $line) { throw 'API exited before readiness.' }
        if ($line -match 'Now listening on:') { $ready = $true; break }
    }
    if (!$ready) { throw 'API readiness not confirmed.' }
    $health = Invoke-RestMethod -Uri 'http://127.0.0.1:18973/health' -TimeoutSec 5
    if ($health.realPrintingEnabled -ne $false) { throw 'Real printing must be disabled.' }
    Write-Output 'PASS health reports realPrintingEnabled=false'
    try {
        Invoke-WebRequest -UseBasicParsing -Method Post -Uri 'http://127.0.0.1:18973/api/reports/resolve' -ContentType 'application/json' -Body '{"examNumber":"TEST001"}' -TimeoutSec 5 | Out-Null
        throw 'Unconfigured API unexpectedly accepted lookup.'
    } catch {
        if ($null -eq $_.Exception.Response) { throw }
        $status = [int]$_.Exception.Response.StatusCode
        if ($status -ne 503 -and $status -ne 401) { throw }
        Write-Output ('PASS unconfigured/unauthorized lookup blocked with HTTP ' + $status)
    }
    Write-Output 'PASS no patient data queried and no print job submitted'
} finally {
    if ($process.Id -and !$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $process.Dispose()
}
