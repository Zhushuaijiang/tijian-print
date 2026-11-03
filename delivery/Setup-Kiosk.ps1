# ============================================================
# 体检报告自助机 - 环境检测与一键配置
# 适用于 Windows 7 SP1+ / PowerShell 5.1（系统自带，无需安装）
# 用法：双击「一键安装检测.cmd」或
#       powershell -ExecutionPolicy Bypass -File Setup-Kiosk.ps1
# ============================================================
param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path ($MyInvocation.MyCommand.Path) -Parent }
$apiDir = Join-Path $root 'Api'
$desktopDir = Join-Path $root 'Desktop'
$toolsDir = Join-Path $root 'Tools'
$apiExe = Join-Path $apiDir 'PeisSelfPrint.Api.exe'
$desktopExe = Join-Path $desktopDir 'PeisSelfPrint.Desktop.exe'
$sumatra = Join-Path $toolsDir 'SumatraPDF.exe'
$appsettings = Join-Path $apiDir 'appsettings.json'
$localConfig = Join-Path $apiDir 'integration.local.json'
$kioskSettings = Join-Path $desktopDir 'kiosk.settings.json'

# ---------- 工具函数 ----------
function Read-JsonFile([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
}
function Save-JsonFile([object]$obj, [string]$path) {
    $json = $obj | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding($true)))
}
function Test-TcpPort([string]$hostName, [int]$port) {
    try {
        $client = New-Object Net.Sockets.TcpClient
        $async = $client.BeginConnect($hostName, $port, $null, $null)
        $ok = $async.AsyncWaitHandle.WaitOne(2500, $false)
        if ($ok -and $client.Connected) { $client.Close(); return $true }
        $client.Close(); return $false
    } catch { return $false }
}
function Get-InstalledPrinters {
    try { return @([Drawing.Printing.PrinterSettings]::InstalledPrinters) } catch { return @() }
}

# ---------- 检测逻辑 ----------
$script:results = New-Object System.Collections.ArrayList
function Add-Check([string]$name, [Nullable[bool]]$ok, [string]$detail) {
    [void]$script:results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
}

function Invoke-AllChecks {
    $script:results.Clear()

    # 1. 离线包完整性
    $missing = @()
    foreach ($f in @($apiExe, (Join-Path $apiDir 'hostfxr.dll'), (Join-Path $apiDir 'coreclr.dll'),
                    $desktopExe, (Join-Path $desktopDir 'hostfxr.dll'), (Join-Path $desktopDir 'coreclr.dll'), $sumatra)) {
        if (-not (Test-Path -LiteralPath $f)) { $missing += (Split-Path $f -Leaf) + '(' + (Split-Path (Split-Path $f) -Leaf) + ')' }
    }
    Add-Check '离线包完整性（运行时/程序/打印引擎）' ($missing.Count -eq 0) $(if ($missing.Count -eq 0) { '全部关键文件就位' } else { '缺少: ' + ($missing -join ', ') })

    # 2. 配置文件
    $cfg = Read-JsonFile $appsettings
    $local = Read-JsonFile $localConfig
    $kiosk = Read-JsonFile $kioskSettings
    $cfgOk = ($null -ne $cfg) -and ($null -ne $cfg.Integration)
    Add-Check '主配置 appsettings.json' $cfgOk $(if ($cfgOk) { '可正常解析' } else { '缺失或损坏' })
    Add-Check '私有配置 integration.local.json' ($null -ne $local) $(if ($null -ne $local) { '可正常解析' } else { '缺失（首次部署请先在下方填写数据库连接）' })
    Add-Check '界面配置 kiosk.settings.json' ($null -ne $kiosk) $(if ($null -ne $kiosk) { '模式=' + $kiosk.Mode + '，打印机=' + $kiosk.PrinterName } else { '缺失或损坏' })

    # 3. 本机 API 服务
    $listener = $null; $portFree = $true; $apiRunning = $false
    if (Test-Path -LiteralPath $apiExe) {
        $listener = New-Object Net.Sockets.TcpClient
        try { $l = $listener.BeginConnect('127.0.0.1', 5088, $null, $null); $portFree = -not $l.AsyncWaitHandle.WaitOne(800, $false) } catch { $portFree = $true }
        try { $listener.Close() } catch {}
        if (-not $portFree) {
            try {
                $req = [Net.HttpWebRequest]::Create('http://127.0.0.1:5088/health'); $req.Proxy = $null; $req.Timeout = 3000; $req.ReadWriteTimeout = 3000
                $resp = $req.GetResponse(); $reader = New-Object IO.StreamReader($resp.GetResponseStream())
                $health = $reader.ReadToEnd() | ConvertFrom-Json; $reader.Close(); $resp.Close()
                $apiRunning = ($health.status -eq 'running')
                Add-Check '本机 API 服务 (127.0.0.1:5088)' $apiRunning $(if ($apiRunning) { '运行中，模式=' + $health.mode } else { '端口被占用但健康检查未通过' })
            } catch { Add-Check '本机 API 服务 (127.0.0.1:5088)' $false '端口被其他程序占用，且非本系统 API' }
        } else {
            Add-Check '本机 API 服务 (127.0.0.1:5088)' $true '未运行（端口空闲，可一键启动）'
        }
    }

    # 4. 数据库连通
    $connStr = $null
    if ($null -ne $local -and $local.Integration.ConnectionString) { $connStr = [string]$local.Integration.ConnectionString }
    elseif ($null -ne $cfg -and $cfg.Integration.ConnectionString) { $connStr = [string]$cfg.Integration.ConnectionString }
    if ($connStr) {
        $dbHost = $null; $dbPort = 5432
        if ($connStr -match 'Host=([^;]+)') { $dbHost = $Matches[1].Trim() }
        if ($connStr -match 'Port=(\d+)') { $dbPort = [int]$Matches[1] }
        if ($dbHost) {
            $dbOk = Test-TcpPort $dbHost $dbPort
            Add-Check "数据库连通 ($dbHost`:$dbPort)" $dbOk $(if ($dbOk) { '端口可达（账号密码由程序登录验证）' } else { '无法连接，请检查网络/防火墙/账号' })
        } else { Add-Check '数据库连通' $false '连接串中未找到 Host=' }
    } else { Add-Check '数据库连通' $false '未配置连接串' }

    # 5. 报告服务 + PDF 源
    if ($null -ne $cfg) {
        $reportBase = [string]$cfg.Integration.ReportApiBaseUrl
        if ($reportBase) {
            try { $u = [Uri]$reportBase; $ok = Test-TcpPort $u.Host $u.Port; Add-Check "报告服务 ($($u.Host):$($u.Port))" $ok $(if ($ok) { '端口可达' } else { '无法连接' }) } catch { Add-Check '报告服务' $false '地址无法解析: ' + $reportBase }
        }
        foreach ($origin in @($cfg.Integration.AllowedPdfOrigins)) {
            try { $u = [Uri]$origin; $ok = Test-TcpPort $u.Host $u.Port; Add-Check "PDF 报告源 ($($u.Host):$($u.Port))" $ok $(if ($ok) { '端口可达' } else { '无法连接，会影响报告打印' }) } catch {}
        }
    }

    # 6. 打印机
    $printers = Get-InstalledPrinters
    Add-Check '系统打印机' ($printers.Count -gt 0) $(if ($printers.Count -gt 0) { $printers -join ', ' } else { '未检测到任何打印机' })
    if ($null -ne $kiosk -and $kiosk.PrinterName) {
        $match = $printers -contains $kiosk.PrinterName
        Add-Check '配置的打印机已安装' $match $(if ($match) { $kiosk.PrinterName + ' 就绪' } else { '未找到「' + $kiosk.PrinterName + '」，请在下方选择后保存' })
    }

    # 7. 打印引擎路径
    if ($null -ne $kiosk) {
        $engine = [string]$kiosk.PdfEnginePath
        if ($engine) {
            $engineOk = Test-Path -LiteralPath $engine
            $sameDir = ($engine -ieq $sumatra)
            Add-Check '打印引擎 (SumatraPDF) 路径' ($engineOk -and $sameDir) $(if ($sameDir) { '指向本包 Tools\SumatraPDF.exe' } elseif ($engineOk) { '存在但指向其他位置，建议点「修复打印引擎路径」' } else { '路径无效：' + $engine })
        }
    }

    # 8. 开机自启
    $taskExists = $false
    try { $t = Get-ScheduledTask -TaskName 'PeisKioskStart' -ErrorAction SilentlyContinue; $taskExists = ($null -ne $t) } catch { $taskExists = $false }
    Add-Check '开机自启（计划任务 PeisKioskStart）' $taskExists $(if ($taskExists) { '已安装，登录后自动启动自助机' } else { '未安装，点「安装开机自启」后每次开机自动运行' })
}

# ---------- 界面 ----------
$form = New-Object Windows.Forms.Form
$form.Text = '体检报告自助机 - 环境检测与配置'
$form.Size = New-Object Drawing.Size(880, 660)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object Drawing.Font('Microsoft YaHei UI', 9)
$form.MaximizeBox = $false

$title = New-Object Windows.Forms.Label
$title.Text = '体检报告自助机 · 环境检测与一键配置'
$title.Font = New-Object Drawing.Font('Microsoft YaHei UI', 14, [Drawing.FontStyle]::Bold)
$title.ForeColor = [Drawing.Color]::FromArgb(30, 60, 120)
$title.Location = New-Object Drawing.Point(20, 12); $title.Size = New-Object Drawing.Size(600, 32)
$form.Controls.Add($title)

$statusLabel = New-Object Windows.Forms.Label
$statusLabel.Text = '点击「开始检测」检查部署环境'
$statusLabel.Location = New-Object Drawing.Point(24, 48); $statusLabel.Size = New-Object Drawing.Size(820, 22)
$form.Controls.Add($statusLabel)

$listView = New-Object Windows.Forms.ListView
$listView.View = 'Details'; $listView.FullRowSelect = $true; $listView.GridLines = $true
$listView.Location = New-Object Drawing.Point(20, 78); $listView.Size = New-Object Drawing.Size(824, 250)
$listView.Columns.Add('结果', 52) | Out-Null
$listView.Columns.Add('检测项', 320) | Out-Null
$listView.Columns.Add('详情', 440) | Out-Null
$form.Controls.Add($listView)

# 配置编辑区
$gbConfig = New-Object Windows.Forms.GroupBox
$gbConfig.Text = '配置'
$gbConfig.Location = New-Object Drawing.Point(20, 336); $gbConfig.Size = New-Object Drawing.Size(824, 150)
$form.Controls.Add($gbConfig)

$lblPrinter = New-Object Windows.Forms.Label
$lblPrinter.Text = '打印机：'; $lblPrinter.Location = New-Object Drawing.Point(16, 30); $lblPrinter.Size = New-Object Drawing.Size(70, 22)
$gbConfig.Controls.Add($lblPrinter)
$cmbPrinter = New-Object Windows.Forms.ComboBox
$cmbPrinter.Location = New-Object Drawing.Point(90, 26); $cmbPrinter.Size = New-Object Drawing.Size(320, 24)
$cmbPrinter.DropDownStyle = 'DropDownList'
$gbConfig.Controls.Add($cmbPrinter)
$btnSavePrinter = New-Object Windows.Forms.Button
$btnSavePrinter.Text = '保存打印机'; $btnSavePrinter.Location = New-Object Drawing.Point(420, 24); $btnSavePrinter.Size = New-Object Drawing.Size(110, 28)
$gbConfig.Controls.Add($btnSavePrinter)
$btnFixEngine = New-Object Windows.Forms.Button
$btnFixEngine.Text = '修复打印引擎路径'; $btnFixEngine.Location = New-Object Drawing.Point(540, 24); $btnFixEngine.Size = New-Object Drawing.Size(150, 28)
$gbConfig.Controls.Add($btnFixEngine)

$lblDb = New-Object Windows.Forms.Label
$lblDb.Text = '数据库连接串：'; $lblDb.Location = New-Object Drawing.Point(16, 64); $lblDb.Size = New-Object Drawing.Size(100, 22)
$gbConfig.Controls.Add($lblDb)
$txtDb = New-Object Windows.Forms.TextBox
$txtDb.Location = New-Object Drawing.Point(120, 60); $txtDb.Size = New-Object Drawing.Size(570, 24)
$gbConfig.Controls.Add($txtDb)
$btnSaveDb = New-Object Windows.Forms.Button
$btnSaveDb.Text = '保存'; $btnSaveDb.Location = New-Object Drawing.Point(700, 58); $btnSaveDb.Size = New-Object Drawing.Size(80, 28)
$gbConfig.Controls.Add($btnSaveDb)

$chkPhysical = New-Object Windows.Forms.CheckBox
$chkPhysical.Text = '启用真实出纸（取消勾选则为演示模式，不实际打印）'
$chkPhysical.Location = New-Object Drawing.Point(16, 96); $chkPhysical.Size = New-Object Drawing.Size(420, 24)
$gbConfig.Controls.Add($chkPhysical)
$chkFullscreen = New-Object Windows.Forms.CheckBox
$chkFullscreen.Text = '全屏运行'
$chkFullscreen.Location = New-Object Drawing.Point(450, 96); $chkFullscreen.Size = New-Object Drawing.Size(120, 24)
$gbConfig.Controls.Add($chkFullscreen)
$btnSaveUi = New-Object Windows.Forms.Button
$btnSaveUi.Text = '保存界面设置'; $btnSaveUi.Location = New-Object Drawing.Point(590, 94); $btnSaveUi.Size = New-Object Drawing.Size(120, 28)
$gbConfig.Controls.Add($btnSaveUi)

# 操作区
$gbOps = New-Object Windows.Forms.GroupBox
$gbOps.Text = '操作'
$gbOps.Location = New-Object Drawing.Point(20, 494); $gbOps.Size = New-Object Drawing.Size(824, 78)
$form.Controls.Add($gbOps)

$btnCheck = New-Object Windows.Forms.Button
$btnCheck.Text = '开始检测'; $btnCheck.Location = New-Object Drawing.Point(16, 28); $btnCheck.Size = New-Object Drawing.Size(120, 34)
$btnCheck.BackColor = [Drawing.Color]::FromArgb(230, 240, 255)
$gbOps.Controls.Add($btnCheck)
$btnStart = New-Object Windows.Forms.Button
$btnStart.Text = '一键启动/重启'; $btnStart.Location = New-Object Drawing.Point(150, 28); $btnStart.Size = New-Object Drawing.Size(130, 34)
$btnStart.BackColor = [Drawing.Color]::FromArgb(220, 245, 225)
$gbOps.Controls.Add($btnStart)
$btnAutostart = New-Object Windows.Forms.Button
$btnAutostart.Text = '安装开机自启'; $btnAutostart.Location = New-Object Drawing.Point(294, 28); $btnAutostart.Size = New-Object Drawing.Size(130, 34)
$gbOps.Controls.Add($btnAutostart)
$btnSmoke = New-Object Windows.Forms.Button
$btnSmoke.Text = '运行自检测试'; $btnSmoke.Location = New-Object Drawing.Point(438, 28); $btnSmoke.Size = New-Object Drawing.Size(120, 34)
$gbOps.Controls.Add($btnSmoke)
$btnShortcut = New-Object Windows.Forms.Button
$btnShortcut.Text = '创建桌面快捷方式'; $btnShortcut.Location = New-Object Drawing.Point(572, 28); $btnShortcut.Size = New-Object Drawing.Size(150, 34)
$gbOps.Controls.Add($btnShortcut)

function Refresh-ConfigPanel {
    $kiosk = Read-JsonFile $kioskSettings
    $local = Read-JsonFile $localConfig
    $cmbPrinter.Items.Clear()
    foreach ($p in (Get-InstalledPrinters)) { [void]$cmbPrinter.Items.Add($p) }
    if ($null -ne $kiosk -and $kiosk.PrinterName) {
        $idx = $cmbPrinter.Items.IndexOf([string]$kiosk.PrinterName)
        if ($idx -ge 0) { $cmbPrinter.SelectedIndex = $idx }
    }
    $conn = $null
    if ($null -ne $local -and $local.Integration -and $local.Integration.ConnectionString) { $conn = [string]$local.Integration.ConnectionString }
    elseif ($null -ne (Read-JsonFile $appsettings)) { $c = (Read-JsonFile $appsettings).Integration; if ($c -and $c.ConnectionString) { $conn = [string]$c.ConnectionString } }
    $txtDb.Text = $conn
    if ($null -ne $kiosk) {
        $chkPhysical.Checked = [bool]$kiosk.PhysicalPrintingEnabled
        $chkFullscreen.Checked = [bool]$kiosk.FullScreen
    }
}

function Show-Results {
    $listView.Items.Clear()
    $failCount = 0
    foreach ($r in $script:results) {
        $item = New-Object Windows.Forms.ListViewItem($(if ($null -eq $r.Ok) { '—' } elseif ($r.Ok) { '✓' } else { '✗' }))
        $item.SubItems.Add($r.Name) | Out-Null
        $item.SubItems.Add($r.Detail) | Out-Null
        if ($r.Ok -eq $true) { $item.ForeColor = [Drawing.Color]::FromArgb(0, 128, 64) }
        elseif ($r.Ok -eq $false) { $item.ForeColor = [Drawing.Color]::FromArgb(200, 40, 40); $failCount++ }
        else { $item.ForeColor = [Drawing.Color]::FromArgb(160, 120, 0) }
        [void]$listView.Items.Add($item)
    }
    $script:failCount = $failCount
}

# ---------- 事件 ----------
$btnCheck.Add_Click({
    $statusLabel.Text = '正在检测，请稍候…'
    $form.Refresh()
    try { Invoke-AllChecks; Show-Results; Refresh-ConfigPanel }
    catch { [Windows.Forms.MessageBox]::Show('检测出错: ' + $_.Exception.Message) | Out-Null }
    $f = $script:failCount
    $statusLabel.Text = $(if ($f -eq 0) { '检测完成：全部通过 ✓' } else { "检测完成：有 $f 项未通过（红色行），请处理后重新检测" })
    $statusLabel.ForeColor = $(if ($f -eq 0) { [Drawing.Color]::FromArgb(0, 128, 64) } else { [Drawing.Color]::FromArgb(200, 40, 40) })
})

$btnSavePrinter.Add_Click({
    if (-not (Test-Path -LiteralPath $kioskSettings)) { [Windows.Forms.MessageBox]::Show('kiosk.settings.json 不存在') | Out-Null; return }
    if ($cmbPrinter.SelectedItem) {
        $kiosk = Read-JsonFile $kioskSettings
        $kiosk.PrinterName = [string]$cmbPrinter.SelectedItem
        Save-JsonFile $kiosk $kioskSettings
        [Windows.Forms.MessageBox]::Show('已保存打印机：' + $cmbPrinter.SelectedItem) | Out-Null
    }
})

$btnFixEngine.Add_Click({
    if (-not (Test-Path -LiteralPath $kioskSettings)) { return }
    if (-not (Test-Path -LiteralPath $sumatra)) { [Windows.Forms.MessageBox]::Show('本包 Tools 下未找到 SumatraPDF.exe') | Out-Null; return }
    $kiosk = Read-JsonFile $kioskSettings
    $kiosk.PdfEnginePath = $sumatra
    Save-JsonFile $kiosk $kioskSettings
    [Windows.Forms.MessageBox]::Show('已将打印引擎路径修复为本包位置。') | Out-Null
})

$btnSaveDb.Add_Click({
    $text = $txtDb.Text.Trim()
    if (-not $text) { return }
    if (-not ($text -match 'Host=' -and $text -match 'Database=')) {
        [Windows.Forms.MessageBox]::Show('连接串需包含 Host= 和 Database=，例如：Host=192.168.x.x;Port=5432;Database=XXX;Username=xx;Password=xx') | Out-Null
        return
    }
    if (Test-Path -LiteralPath $localConfig) { $local = Read-JsonFile $localConfig } else { $local = New-Object psobject -Property @{ Integration = New-Object psobject } }
    if (-not $local.Integration) { $local | Add-Member -MemberType NoteProperty -Name 'Integration' -Value (New-Object psobject) -Force }
    $local.Integration | Add-Member -MemberType NoteProperty -Name 'ConnectionString' -Value $text -Force
    Save-JsonFile $local $localConfig
    [Windows.Forms.MessageBox]::Show('数据库连接串已保存。') | Out-Null
})

$btnSaveUi.Add_Click({
    if (-not (Test-Path -LiteralPath $kioskSettings)) { return }
    $kiosk = Read-JsonFile $kioskSettings
    $kiosk.PhysicalPrintingEnabled = [bool]$chkPhysical.Checked
    $kiosk.FullScreen = [bool]$chkFullscreen.Checked
    Save-JsonFile $kiosk $kioskSettings
    [Windows.Forms.MessageBox]::Show('界面设置已保存。') | Out-Null
})

function Start-Kiosk([switch]$restart) {
    $script = Join-Path $root 'Start-Debug.ps1'
    if (-not (Test-Path -LiteralPath $script)) { [Windows.Forms.MessageBox]::Show('缺少 Start-Debug.ps1') | Out-Null; return }
    try {
        $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $script + '"' + $(if ($restart) { ' -Restart' }))
        Register-ScheduledTask -TaskName 'PeisKioskRunOnce' -Action $action -Principal $principal -Force | Out-Null
        Start-ScheduledTask -TaskName 'PeisKioskRunOnce'
        Start-Sleep -Seconds 3
        Unregister-ScheduledTask -TaskName 'PeisKioskRunOnce' -Confirm:$false -ErrorAction SilentlyContinue
        $statusLabel.Text = '启动指令已发出，自助机窗口将在本机屏幕上出现。'
        $statusLabel.ForeColor = [Drawing.Color]::FromArgb(0, 128, 64)
    } catch {
        # 计划任务失败时退回直接启动（远程会话下窗口不可见，仅本机操作时可用）
        try { Start-Process powershell.exe -ArgumentList ('-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $script + '"' + $(if ($restart) { ' -Restart' })) } catch {}
        $statusLabel.Text = '已尝试直接启动（如无窗口出现，请在本机屏幕上操作）。'
    }
}

$btnStart.Add_Click({
    $running = @(Get-Process -Name 'PeisSelfPrint.Api', 'PeisSelfPrint.Desktop' -ErrorAction SilentlyContinue)
    Start-Kiosk -restart:($running.Count -gt 0)
})

$btnAutostart.Add_Click({
    try {
        $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $root 'Start-Debug.ps1') + '"')
        Register-ScheduledTask -TaskName 'PeisKioskStart' -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
        [Windows.Forms.MessageBox]::Show('开机自启已安装：登录 Windows 后自动启动自助机。', '完成') | Out-Null
    } catch { [Windows.Forms.MessageBox]::Show('安装失败: ' + $_.Exception.Message) | Out-Null }
})

$btnSmoke.Add_Click({
    if (-not (Test-Path -LiteralPath $desktopExe)) { [Windows.Forms.MessageBox]::Show('Desktop 程序不存在') | Out-Null; return }
    $out = Join-Path $env:TEMP ('PeisKioskSmoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    $env:PEIS_KIOSK_TEST_OUTPUT = $out
    $p = Start-Process -FilePath $desktopExe -ArgumentList '--demo-smoke-test', '--demo' -WorkingDirectory $desktopDir -PassThru
    if ($p.WaitForExit(70000)) {
        $resultsFile = Join-Path $out 'results.json'
        if ((Test-Path -LiteralPath $resultsFile) -and ((Get-Content -LiteralPath $resultsFile -Raw | ConvertFrom-Json).success)) {
            [Windows.Forms.MessageBox]::Show('自检通过：界面、报告选择、确认、打印调度逻辑均正常（未实际出纸）。', '自检结果') | Out-Null
        } else { [Windows.Forms.MessageBox]::Show('自检未通过，详情见: ' + $out, '自检结果') | Out-Null }
    } else { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; [Windows.Forms.MessageBox]::Show('自检超时') | Out-Null }
})

$btnShortcut.Add_Click({
    try {
        $ws = New-Object -ComObject WScript.Shell
        $lnk = $ws.CreateShortcut([IO.Path]::Combine([Environment]::GetFolderPath('Desktop'), '体检报告自助机.lnk'))
        $lnk.TargetPath = 'powershell.exe'
        $lnk.Arguments = ('-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $root 'Start-Debug.ps1') + '"')
        $lnk.WorkingDirectory = $root
        $lnk.Save()
        [Windows.Forms.MessageBox]::Show('桌面快捷方式已创建。', '完成') | Out-Null
    } catch { [Windows.Forms.MessageBox]::Show('创建失败: ' + $_.Exception.Message) | Out-Null }
})

# 启动时自动检测一次
Invoke-AllChecks
Show-Results
Refresh-ConfigPanel
$f = $script:failCount
$statusLabel.Text = $(if ($f -eq 0) { '打开时已自动检测：全部通过 ✓' } else { "打开时已自动检测：有 $f 项未通过（红色行）" })
$statusLabel.ForeColor = $(if ($f -eq 0) { [Drawing.Color]::FromArgb(0, 128, 64) } else { [Drawing.Color]::FromArgb(200, 40, 40) })

if ($CheckOnly) {
    Invoke-AllChecks
    $failCount = 0
    foreach ($r in $script:results) {
        $mark = $(if ($null -eq $r.Ok) { 'INFO' } elseif ($r.Ok) { 'PASS' } else { 'FAIL' })
        Write-Output ("[{0}] {1} :: {2}" -f $mark, $r.Name, $r.Detail)
        if ($r.Ok -eq $false) { $failCount++ }
    }
    Write-Output ("RESULT: {0} checks, {1} failed" -f $script:results.Count, $failCount)
    exit $(if ($failCount -gt 0) { 1 } else { 0 })
}

[void]$form.ShowDialog()
