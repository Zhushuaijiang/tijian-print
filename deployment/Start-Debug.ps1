#Requires -Version 5.1
[CmdletBinding()]
param([switch]$Restart, [switch]$Demo)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=[Text.Encoding]::UTF8
$root=$PSScriptRoot
$stateDir=Join-Path $root '.debug'
New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
$logFile=Join-Path $stateDir 'start-last.log'
$lock=$null
function Note([string]$text){$line=(Get-Date -Format 'HH:mm:ss')+' '+$text;Write-Host $line;Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8}
function OwnProcesses([string]$name,[string]$path){return @(Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object {try{$_.Path -ieq $path}catch{$false}})}
function Health {
 $request=[Net.HttpWebRequest]::Create('http://127.0.0.1:5088/health');$request.Proxy=$null;$request.Timeout=1500;$request.ReadWriteTimeout=1500
 $response=$null;$reader=$null
 try{$response=$request.GetResponse();$reader=[IO.StreamReader]::new($response.GetResponseStream());return ($reader.ReadToEnd()|ConvertFrom-Json)}
 finally{if($reader){$reader.Dispose()};if($response){$response.Close()}}
}
try {
 $lock=[IO.File]::Open((Join-Path $stateDir 'start.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 $apiDir=Join-Path $root 'Api';$desktopDir=Join-Path $root 'Desktop'
 $apiExe=Join-Path $apiDir 'PeisSelfPrint.Api.exe';$desktopExe=Join-Path $desktopDir 'PeisSelfPrint.Desktop.exe'
 foreach($f in @($apiExe,$desktopExe,(Join-Path $apiDir 'hostfxr.dll'),(Join-Path $desktopDir 'hostfxr.dll'))){if(-not(Test-Path -LiteralPath $f)){throw "离线包不完整：$f"}}
 $config=Get-Content -LiteralPath (Join-Path $apiDir 'appsettings.json') -Raw -Encoding UTF8|ConvertFrom-Json
 $localConfig=Join-Path $apiDir 'integration.local.json'
 if(Test-Path -LiteralPath $localConfig){$private=Get-Content -LiteralPath $localConfig -Raw -Encoding UTF8|ConvertFrom-Json;foreach($property in $private.Integration.PSObject.Properties){$config.Integration|Add-Member -MemberType NoteProperty -Name $property.Name -Value $property.Value -Force}}
 $key=[string]$env:Integration__ApiKey
 if([string]::IsNullOrWhiteSpace($key)){$key=[string]$config.Integration.ApiKey}
 if([string]::IsNullOrWhiteSpace($key)){
  Add-Type -AssemblyName System.Security
  $keyFile=Join-Path $stateDir 'api-key.dpapi'
  if(Test-Path -LiteralPath $keyFile){
   $key=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyFile),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
  }else{
   $bytes=New-Object byte[] 32;$rng=[Security.Cryptography.RandomNumberGenerator]::Create()
   try{$rng.GetBytes($bytes)}finally{$rng.Dispose()}
   $key=[Convert]::ToBase64String($bytes)
   $cipher=[Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($key),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
   [IO.File]::WriteAllBytes($keyFile,$cipher)
  }
 }
 if($key.Length -lt 32 -or $key.Length -gt 512){throw 'API 密钥长度无效；请检查现有配置。'}
 $env:Integration__ApiKey=$key
 $env:PEIS_API_KEY=$key
 $env:PEIS_API_BASE_URL='http://127.0.0.1:5088/'
 $apis=@(OwnProcesses 'PeisSelfPrint.Api' $apiExe)
 $desktops=@(OwnProcesses 'PeisSelfPrint.Desktop' $desktopExe)
 if($Restart){
  Note '按需重启本目录的调试程序。'
  foreach($p in @($desktops)+@($apis)){Stop-Process -Id $p.Id -ErrorAction Stop;$p.WaitForExit(5000)|Out-Null}
  $apis=@();$desktops=@()
 }
 if($apis.Count -eq 0){
  $testListener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,5088)
  try{$testListener.Start()}catch{throw '5088 端口已被其他程序占用，未启动第二个 API。'}finally{$testListener.Stop()}
  $stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
  $api=Start-Process -FilePath $apiExe -ArgumentList ('--urls http://127.0.0.1:5088 --contentRoot "'+$apiDir+'"') -WorkingDirectory $apiDir -WindowStyle Hidden -RedirectStandardOutput (Join-Path $stateDir ('api-'+$stamp+'.stdout.log')) -RedirectStandardError (Join-Path $stateDir ('api-'+$stamp+'.stderr.log')) -PassThru
  Note ("API 已启动，PID="+$api.Id)
 }else{$api=$apis[0];Note ("复用本目录 API，PID="+$api.Id)}
 $health=$null
 for($i=0;$i -lt 25;$i++){
  $api.Refresh();if($api.HasExited){throw 'API 已退出，请查看 .debug 目录的 API 日志。'}
  try{$health=Health;if($health.status -eq 'running'){break}}catch{}
  Start-Sleep -Milliseconds 400
 }
 if(-not $health -or $health.status -ne 'running'){throw 'API 健康检查未通过，请查看 .debug 日志。'}
 Note 'API 健康检查通过：http://127.0.0.1:5088/health'
 $session=[Diagnostics.Process]::GetCurrentProcess().SessionId
 $desktops=@($desktops | Where-Object {$_.SessionId -eq $session})
 if($desktops.Count -eq 0){
  $desktopStart=@{FilePath=$desktopExe;WorkingDirectory=$desktopDir;PassThru=$true}
  if($Demo){$desktopStart.ArgumentList='--demo'}
  $desktop=Start-Process @desktopStart
  if(-not $desktop.WaitForInputIdle(15000)){throw '桌面程序启动超时。'}
  $desktop.Refresh();if($desktop.HasExited){throw '桌面程序已退出。'}
  Note ("桌面程序已启动，PID="+$desktop.Id+'，会话='+$desktop.SessionId)
 }else{$desktop=$desktops[0];Note ("复用桌面程序，PID="+$desktop.Id)}
 for($i=0;$i -lt 30;$i++){$desktop.Refresh();if($desktop.HasExited){throw '桌面程序已退出。'};if($desktop.MainWindowHandle -ne [IntPtr]::Zero){break};Start-Sleep -Milliseconds 300}
 if($desktop.MainWindowHandle -eq [IntPtr]::Zero){throw '桌面进程存在但窗口尚未就绪，请检查应用日志。'}
 Note ('窗口已就绪：'+$desktop.MainWindowTitle)
 $connectionSet=[bool]$config.Integration.ConnectionString -or [bool]$env:Integration__ConnectionString
 $reportSet=[bool]$config.Integration.ReportApiBaseUrl -or [bool]$env:Integration__ReportApiBaseUrl
 if(-not($connectionSet -and $reportSet)){Note '界面和本机 API 已就绪；真实查询仍需配置数据库与报告服务。'}
 [pscustomobject]@{Time=(Get-Date).ToString('o');Status='ready';ApiPid=$api.Id;DesktopPid=$desktop.Id;SessionId=$desktop.SessionId;WindowTitle=$desktop.MainWindowTitle;WindowReady=($desktop.MainWindowHandle -ne [IntPtr]::Zero);Health=$health;DatabaseConfigured=$connectionSet;ReportServiceConfigured=$reportSet;ApiBaseUrl=$env:PEIS_API_BASE_URL}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $stateDir 'status.json') -Encoding UTF8
 Note '调试启动完成。修改连接配置后可运行 Restart-Debug.cmd。'
}catch{Note ('启动失败：'+$_.Exception.Message);throw}
finally{if($lock){$lock.Dispose()}}
