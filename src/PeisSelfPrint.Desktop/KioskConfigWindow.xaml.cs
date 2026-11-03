using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Printing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PeisSelfPrint.Core;

namespace PeisSelfPrint.Desktop;

// 运维配置窗口（暗门入口：主界面时间栏连续点击 5 次）。
// 职责：环境一键检测、打印机获取/配置/测试、运行方式调整。不承载业务打印逻辑。
public partial class KioskConfigWindow : Window
{
    private const string FollowDefaultLabel = "（跟随系统默认打印机）";
    private readonly KioskSettings settings;
    private readonly WindowsPrinterAdapter printer;
    private static readonly string settingsPath = Path.Combine(AppContext.BaseDirectory, "kiosk.settings.json");
    private static readonly string apiDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Api"));
    private static readonly string localIntegrationPath = Path.Combine(apiDir, "integration.local.json");
    private static readonly string enginePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Tools", "SumatraPDF.exe"));

    public KioskConfigWindow(KioskSettings current, WindowsPrinterAdapter adapter)
    {
        InitializeComponent();
        settings = current;
        printer = adapter;
        SettingsPathText.Text = settingsPath;
        LoadUiFromSettings();
        LoadServiceUi();
    }

    // ---------- 初始化 ----------
    private void LoadUiFromSettings()
    {
        PrinterCombo.Items.Clear();
        PrinterCombo.Items.Add(FollowDefaultLabel);
        try
        {
            using var server = new LocalPrintServer();
            foreach (var queue in server.GetPrintQueues())
            {
                var name = queue.Name;
                if (!string.IsNullOrEmpty(name)) PrinterCombo.Items.Add(name);
            }
        }
        catch (PrintSystemException) { }
        var configured = settings.PrinterName ?? string.Empty;
        var index = PrinterCombo.Items.IndexOf(configured);
        PrinterCombo.SelectedIndex = index >= 0 ? index : 0;
        RefreshDefaultPrinterLabel();
        PhysicalPrintingCheck.IsChecked = settings.PhysicalPrintingEnabled;
        FullScreenCheck.IsChecked = settings.FullScreen;
        HospitalBox.Text = settings.HospitalName;
        foreach (var item in ModeCombo.Items)
            if (item is ComboBoxItem { Tag: string tag } && string.Equals(tag, settings.Mode, StringComparison.OrdinalIgnoreCase))
            { ModeCombo.SelectedItem = item; break; }
        if (ModeCombo.SelectedItem is null) ModeCombo.SelectedIndex = 0;
    }

    private void RefreshDefaultPrinterLabel()
    {
        var system = GetSystemDefaultPrinter();
        DefaultPrinterText.Text = "Windows 系统默认打印机：" + (system ?? "（未知）");
    }

    private static string? GetSystemDefaultPrinter()
    {
        try { using var server = new LocalPrintServer(); using var queue = server.DefaultPrintQueue; queue.Refresh(); return queue.Name; }
        catch (PrintSystemException) { return null; }
    }

    // ---------- 暗门状态机（供冒烟测试直接调用） ----------
    private int gateClicks;
    private DateTime gateLast = DateTime.MinValue;
    public bool RegisterGateClick()
    {
        var now = DateTime.UtcNow;
        gateClicks = now - gateLast <= TimeSpan.FromSeconds(2) ? gateClicks + 1 : 1;
        gateLast = now;
        if (gateClicks < 5) return false;
        gateClicks = 0;
        return true;
    }

    // ---------- 事件 ----------
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Printer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PrinterHint.Text = string.Empty;
    }

    private string? SelectedPrinterName()
    {
        if (PrinterCombo.SelectedItem is not string name) return null;
        return name == FollowDefaultLabel ? string.Empty : name;
    }

    private void SavePrinter_Click(object sender, RoutedEventArgs e)
    {
        var name = SelectedPrinterName();
        if (name is null) { PrinterHint.Text = "请先选择打印机。"; return; }
        settings.PrinterName = name;
        SaveSettings();
        PrinterHint.Text = name.Length == 0
            ? "已保存：打印时跟随 Windows 系统默认打印机。"
            : "已保存：打印将固定使用「" + name + "」。";
    }

    private void SetDefaultPrinter_Click(object sender, RoutedEventArgs e)
    {
        var name = SelectedPrinterName();
        if (string.IsNullOrEmpty(name)) { PrinterHint.Text = "请先在下拉框选择一台具体打印机。"; return; }
        try
        {
            // PrintQueue 未提供公开的“设为默认”API，走 Windows 原生 printui（Win7+ 均可用）
            var start = Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = "printui.dll,PrintUIEntry /y /n \"" + name + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("rundll32 启动失败");
            if (!start.WaitForExit(8000)) { try { start.Kill(); } catch (InvalidOperationException) { } throw new InvalidOperationException("Windows 打印机设置未响应"); }
            RefreshDefaultPrinterLabel();
            var now = GetSystemDefaultPrinter();
            PrinterHint.Text = string.Equals(now, name, StringComparison.OrdinalIgnoreCase)
                ? "系统默认打印机已设为「" + name + "」。"
                : "设置命令已发出，但系统默认仍是「" + (now ?? "未知") + "」，请在 Windows 打印机设置中确认。";
        }
        catch (Exception ex)
        {
            PrinterHint.Text = "设置系统默认打印机失败：" + ex.Message + "。也可以在 Windows 打印机设置中手动设为默认。";
        }
    }

    private async void PrintTest_Click(object sender, RoutedEventArgs e)
    {
        PrinterHint.Text = "正在准备打印测试…";
        try
        {
            var readiness = await printer.CheckAsync(CancellationToken.None);
            if (!readiness.Ready) { PrinterHint.Text = "打印测试未开始：" + readiness.Message; return; }
            var pdf = BuildTestPdf();
            var staging = Path.Combine(Path.GetTempPath(), "peis-print-test-" + Guid.NewGuid().ToString("N") + ".pdf");
            File.WriteAllBytes(staging, pdf);
            try { await printer.SubmitAsync(staging, CancellationToken.None); }
            finally { try { File.Delete(staging); } catch (IOException) { } }
            PrinterHint.Text = "打印测试已提交，请确认打印机出纸。";
        }
        catch (RuleException ex)
        {
            PrinterHint.Text = "打印测试失败：" + KioskMessages.For(ex.Message);
        }
        catch (Exception ex)
        {
            PrinterHint.Text = "打印测试失败：" + ex.Message;
        }
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        ApplyUiToSettings();
        SaveSettings();
        PrinterHint.Text = "全部设置已保存（部分设置需重启程序生效）。";
    }

    private void SaveRestart_Click(object sender, RoutedEventArgs e)
    {
        ApplyUiToSettings();
        SaveSettings();
        // 服务配置一并保存（输入非法则中止重启，先提示修正）
        var conn = DbConnBox.Text.Trim();
        var reportUrl = ReportUrlBox.Text.Trim();
        var origins = OriginLines();
        var errors = ValidateServiceInputs(conn, reportUrl, origins);
        if (errors.Count > 0) { ServiceHint.Text = "服务配置未保存：" + string.Join("；", errors); return; }
        if (!TryWriteServiceConfig(conn, reportUrl, origins, AreaIdBox.Text.Trim(), CenterIdBox.Text.Trim())) return;
        var answer = MessageBox.Show(this, "设置已保存。重启程序与后台服务后生效，是否立即重启？", "保存并重启", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        // 优先走部署包的一键重启脚本：同时重启 Desktop 与 Api 后台服务，保证服务配置生效
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Start-Debug.ps1"));
        if (File.Exists(script))
        {
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Restart") { UseShellExecute = false, CreateNoWindow = true });
            Application.Current.Shutdown();
            return;
        }
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
        Application.Current.Shutdown();
    }

    private void OpenTools_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(enginePath);
        if (dir is not null && Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", '"' + dir + '"') { UseShellExecute = true });
        else MessageBox.Show(this, "未找到打印引擎目录：" + enginePath, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ApplyUiToSettings()
    {
        var name = SelectedPrinterName() ?? string.Empty;
        settings.PrinterName = name;
        if (ModeCombo.SelectedItem is ComboBoxItem { Tag: string tag }) settings.Mode = tag;
        settings.PhysicalPrintingEnabled = PhysicalPrintingCheck.IsChecked == true;
        settings.FullScreen = FullScreenCheck.IsChecked == true;
        settings.HospitalName = string.IsNullOrWhiteSpace(HospitalBox.Text) ? "体检中心" : HospitalBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(settings.PdfEnginePath) && File.Exists(enginePath)) settings.PdfEnginePath = enginePath;
    }

    private void SaveSettings()
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(settingsPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    // ---------- 数据服务配置（数据库 / 报告服务，覆盖层写入 Api/integration.local.json） ----------

    private void LoadServiceUi()
    {
        DbConnBox.Text = ReadApiConfigString("ConnectionString") ?? string.Empty;
        ReportUrlBox.Text = ReadApiConfigString("ReportApiBaseUrl") ?? string.Empty;
        OriginsBox.Text = string.Join(Environment.NewLine, ReadApiConfigOrigins());
        AreaIdBox.Text = ReadApiConfigString("HospitalAreaId") ?? string.Empty;
        CenterIdBox.Text = ReadApiConfigString("PeisCenterId") ?? string.Empty;
    }

    // 输入校验：返回错误列表（空 = 通过）。可独立于 UI 测试。
    internal static List<string> ValidateServiceInputs(string conn, string reportUrl, string[] origins)
    {
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(conn) && !System.Text.RegularExpressions.Regex.IsMatch(conn, @"(^|;)\s*Host\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            errors.Add("连接串必须包含 Host=（格式：Host=…;Port=…;Database=…;Username=…;Password=…）");
        if (!string.IsNullOrWhiteSpace(reportUrl) && (!Uri.TryCreate(reportUrl.Trim(), UriKind.Absolute, out var uri0) || uri0.Scheme is not ("http" or "https")))
            errors.Add("报告服务地址必须是完整 URL（含 http:// 或 https://）");
        foreach (var line in origins)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!Uri.TryCreate(line.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                errors.Add("PDF 允许来源每行必须是完整 http/https URL：" + line.Trim());
        }
        return errors;
    }

    // 把界面值合并进覆盖层 JSON：空值 = 删除覆盖（回退 appsettings.json 出厂值）；保留界面之外的既有字段。
    internal static JsonObject ApplyServiceOverrides(JsonObject integration, string conn, string reportUrl, string[] origins, string areaId, string centerId)
    {
        SetOrRemove(integration, "ConnectionString", conn);
        SetOrRemove(integration, "ReportApiBaseUrl", reportUrl);
        if (origins.Length > 0)
        {
            var array = new JsonArray();
            foreach (var line in origins)
                if (!string.IsNullOrWhiteSpace(line)) array.Add(line.Trim());
            if (array.Count > 0) integration["AllowedPdfOrigins"] = array; else integration.Remove("AllowedPdfOrigins");
        }
        else integration.Remove("AllowedPdfOrigins");
        SetOrRemove(integration, "HospitalAreaId", areaId);
        SetOrRemove(integration, "PeisCenterId", centerId);
        return integration;
    }

    private static void SetOrRemove(JsonObject obj, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) obj.Remove(key);
        else obj[key] = value.Trim();
    }

    private string[] OriginLines() => OriginsBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

    private void SaveService_Click(object sender, RoutedEventArgs e)
    {
        var conn = DbConnBox.Text.Trim();
        var reportUrl = ReportUrlBox.Text.Trim();
        var origins = OriginLines();
        var errors = ValidateServiceInputs(conn, reportUrl, origins);
        if (errors.Count > 0) { ServiceHint.Text = "未保存：" + string.Join("；", errors); return; }
        if (TryWriteServiceConfig(conn, reportUrl, origins, AreaIdBox.Text.Trim(), CenterIdBox.Text.Trim()))
            ServiceHint.Text = "已保存到 Api/integration.local.json。数据库等后台服务配置需重启后台服务后生效（点「保存并重启程序」即可）。";
    }

    private bool TryWriteServiceConfig(string conn, string reportUrl, string[] origins, string areaId, string centerId)
    {
        JsonObject root;
        try
        {
            root = File.Exists(localIntegrationPath) && JsonNode.Parse(File.ReadAllText(localIntegrationPath)) is JsonObject existing ? existing : new JsonObject();
        }
        catch (JsonException ex) { ServiceHint.Text = "integration.local.json 已损坏（" + ex.Message + "），请先点「清除本机覆盖」恢复默认。"; return false; }

        if (root["Integration"] is not JsonObject integration) { integration = new JsonObject(); root["Integration"] = integration; }
        ApplyServiceOverrides(integration, conn, reportUrl, origins, areaId, centerId);

        try
        {
            Directory.CreateDirectory(apiDir);
            File.WriteAllText(localIntegrationPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return true;
        }
        catch (Exception ex) { ServiceHint.Text = "写入失败：" + ex.Message; return false; }
    }

    private void ClearServiceOverride_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(localIntegrationPath))
            {
                var answer = MessageBox.Show(this, "将删除本机覆盖文件 integration.local.json，数据服务配置恢复为随包默认值。确定？", "清除本机覆盖", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (answer != MessageBoxResult.OK) return;
                File.Delete(localIntegrationPath);
            }
            LoadServiceUi();
            ServiceHint.Text = "本机覆盖已清除，数据服务配置恢复为随包默认值（重启后台服务后生效）。";
        }
        catch (Exception ex) { ServiceHint.Text = "清除失败：" + ex.Message; }
    }

    private void TestService_Click(object sender, RoutedEventArgs e)
    {
        var conn = DbConnBox.Text.Trim();
        var reportUrl = ReportUrlBox.Text.Trim();
        var origins = OriginLines();
        var errors = ValidateServiceInputs(conn, reportUrl, origins);
        if (errors.Count > 0) { ServiceHint.Text = "输入有误，无法测试：" + string.Join("；", errors); return; }
        var results = new List<string>();
        if (!string.IsNullOrWhiteSpace(conn))
        {
            var (host, port) = ParseEndpoint(conn, "Host", 5432);
            if (host is null) results.Add("数据库：连接串中未找到 Host=");
            else results.Add("数据库 " + host + ":" + port + " → " + (ProbeTcp(host, port) ? "可达" : "无法连接"));
        }
        if (!string.IsNullOrWhiteSpace(reportUrl))
        {
            var (host, port) = ParseEndpoint(reportUrl, null, 80);
            if (host is not null) results.Add("报告服务 " + host + ":" + port + " → " + (ProbeTcp(host, port) ? "可达" : "无法连接"));
        }
        foreach (var line in origins)
        {
            var (host, port) = ParseEndpoint(line.Trim(), null, 80);
            if (host is not null) results.Add("PDF 源 " + host + ":" + port + " → " + (ProbeTcp(host, port) ? "可达" : "无法连接"));
        }
        if (results.Count == 0) results.Add("未填写任何数据服务地址。");
        ServiceHint.Text = string.Join("；", results) + "。（仅探测端口连通性，账号密码是否正确以实际查询为准）";
    }

    // ---------- 环境检测 ----------
    private sealed record CheckItem(string Name, bool Ok, string Detail);
    private readonly List<CheckItem> checkItems = new();

    private void CheckFix_Click(object sender, RoutedEventArgs e)
    {
        var note = string.Empty;
        if (!File.Exists(enginePath))
        {
            note = "本包 Tools 下未找到 SumatraPDF.exe，无法自动修复。";
        }
        else if (!string.Equals(settings.PdfEnginePath, enginePath, StringComparison.OrdinalIgnoreCase))
        {
            settings.PdfEnginePath = enginePath;
            SaveSettings();
            note = "打印引擎路径已修复为：" + enginePath;
        }
        else note = "打印引擎路径已正确，无需修复。";
        MessageBox.Show(this, note, "修复打印引擎路径", MessageBoxButton.OK, MessageBoxImage.Information);
        RunChecks_Click(sender, e);
    }

    private bool isChecking;
    private async void RunChecks_Click(object sender, RoutedEventArgs e)
    {
        if (isChecking) return;
        isChecking = true;
        try
        {
            checkItems.Clear();
            RenderChecks();
            await Task.Yield();

            var missing = new List<string>();
            if (!File.Exists(enginePath)) missing.Add("SumatraPDF.exe(Tools)");
            if (!File.Exists(settingsPath)) missing.Add("kiosk.settings.json");
            checkItems.Add(new CheckItem("程序与打印引擎文件", missing.Count == 0, missing.Count == 0 ? "就位：" + settingsPath : "缺少 " + string.Join(", ", missing)));
            RenderChecks();

            var readiness = await printer.CheckAsync(CancellationToken.None);
            checkItems.Add(new CheckItem("打印机预检", readiness.Ready, readiness.Message));
            RenderChecks();

            var system = GetSystemDefaultPrinter();
            var configured = settings.PrinterName;
            checkItems.Add(new CheckItem("打印机配置来源", true,
                string.IsNullOrWhiteSpace(configured) ? "跟随系统默认打印机（当前：" + (system ?? "未知") + "）" : "固定打印机：" + configured));

            var api = await ProbeHttpAsync("http://127.0.0.1:5088/health");
            checkItems.Add(new CheckItem("本机 API 服务", api.ok, api.detail));
            RenderChecks();

            var conn = ReadApiConfigString("ConnectionString");
            var report = ReadApiConfigString("ReportApiBaseUrl");
            if (conn is not null)
            {
                var (host, port) = ParseEndpoint(conn, "Host", 5432);
                if (host is not null)
                {
                    var ok = ProbeTcp(host, port);
                    checkItems.Add(new CheckItem("数据库 " + host + ":" + port, ok, ok ? "端口可达" : "无法连接"));
                }
                else checkItems.Add(new CheckItem("数据库", false, "连接串中未找到 Host="));
            }
            else checkItems.Add(new CheckItem("数据库", false, "未配置连接串（Api/integration.local.json）"));
            if (report is not null)
            {
                var (host, port) = ParseEndpoint(report, null, 80);
                if (host is not null)
                {
                    var ok = ProbeTcp(host, port);
                    checkItems.Add(new CheckItem("报告服务 " + host + ":" + port, ok, ok ? "端口可达" : "无法连接，报告打印不可用"));
                }
            }
            else checkItems.Add(new CheckItem("报告服务", false, "未配置 ReportApiBaseUrl"));
            foreach (var origin in ReadApiConfigOrigins())
            {
                var (host, port) = ParseEndpoint(origin, null, 80);
                if (host is null) continue;
                var ok = ProbeTcp(host, port);
                checkItems.Add(new CheckItem("PDF 报告源 " + host + ":" + port, ok, ok ? "端口可达" : "无法连接"));
            }
            var failures = checkItems.Count(x => !x.Ok);
            checkItems.Add(new CheckItem("检测完成", failures == 0, failures == 0 ? "全部通过" : failures + " 项未通过，请处理后重新检测"));
            RenderChecks();
        }
        finally { isChecking = false; }
    }

    private void RenderChecks()
    {
        CheckList.Items.Clear();
        foreach (var item in checkItems)
        {
            var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 3, 4, 3) };
            block.Inlines.Add(new System.Windows.Documents.Run((item.Ok ? "✓ " : "✗ ") + item.Name + "　") { Foreground = new SolidColorBrush(item.Ok ? Color.FromRgb(0x16, 0x83, 0x65) : Color.FromRgb(0xC8, 0x28, 0x28)), FontWeight = FontWeights.SemiBold });
            block.Inlines.Add(new System.Windows.Documents.Run(item.Detail) { Foreground = new SolidColorBrush(Color.FromRgb(0x54, 0x71, 0x7F)) });
            CheckList.Items.Add(block);
        }
    }

    private static (string? host, int port) ParseEndpoint(string text, string? key, int fallbackPort)
    {
        var value = text;
        if (key is not null)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, key + @"=(""([^""]*)""|[^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) return (null, fallbackPort);
            value = match.Groups[2].Success ? match.Groups[2].Value.Trim() : match.Groups[1].Value.Trim();
            // 连接串场景：端口在独立的 Port= 键里，不能被 Uri 默认端口覆盖
            var portMatch = System.Text.RegularExpressions.Regex.Match(text, @"Port=(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (portMatch.Success && int.TryParse(portMatch.Groups[1].Value, out var parsed)) fallbackPort = parsed;
        }
        if (!Uri.TryCreate(value.Contains("://") ? value : "http://" + value, UriKind.Absolute, out var uri)) return (null, fallbackPort);
        return (uri.Host, uri.IsDefaultPort ? fallbackPort : uri.Port);
    }

    private static bool ProbeTcp(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var async = client.BeginConnect(host, port, null, null);
            var ok = async.AsyncWaitHandle.WaitOne(2500, false);
            if (ok && client.Connected) { client.Close(); return true; }
            client.Close();
            return false;
        }
        catch (SocketException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task<(bool ok, string detail)> ProbeHttpAsync(string url)
    {
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode && body.Contains("\"running\"")) return (true, "运行中");
            return (response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "已响应但状态异常" : "HTTP " + (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, "未运行或无响应");
        }
    }

    // 合并读取 Api 配置：appsettings.json 为兜底，integration.local.json 同名字段优先覆盖。
    // （现场部署习惯：连接串放 integration.local.json，报告服务地址等放 appsettings.json）
    private static Dictionary<string, JsonElement>? LoadApiConfig()
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var any = false;
        foreach (var name in new[] { "appsettings.json", "integration.local.json" })
        {
            var file = Path.Combine(apiDir, name);
            if (!File.Exists(file)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("Integration", out var integration) || integration.ValueKind != JsonValueKind.Object) continue;
                foreach (var prop in integration.EnumerateObject()) merged[prop.Name] = prop.Value.Clone();
                any = true;
            }
            catch (JsonException) { }
        }
        return any ? merged : null;
    }

    private static string? ReadApiConfigString(string property)
    {
        var root = LoadApiConfig();
        if (root is null || !root.TryGetValue(property, out var value)) return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static IEnumerable<string> ReadApiConfigOrigins()
    {
        var root = LoadApiConfig();
        if (root is null || !root.TryGetValue("AllowedPdfOrigins", out var array) || array.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in array.EnumerateArray())
        {
            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text)) yield return text;
        }
    }

    // ---------- 打印测试页 PDF（单页、英文，避免内嵌字体） ----------
    private static byte[] BuildTestPdf()
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var lines = new[]
        {
            "PeisSelfPrint Print Test",
            "Kiosk: " + Environment.MachineName,
            "Time: " + stamp,
            "If you can read this page, printing works.",
            "(60 char line for engine width check: "
                + new string('#', 34) + ")"
        };
        var content = new StringBuilder("BT\n/F1 20 Tf\n50 760 Td\n18 TL\n");
        foreach (var line in lines) content.Append('(').Append(line.Replace("(", "[").Replace(")", "]")).Append(") Tj T*\n");
        content.Append("ET\n");
        var stream = content.ToString();
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length " + Encoding.ASCII.GetByteCount(stream) + " >>\nstream\n" + stream + "endstream"
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xrefStart = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xrefStart).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
