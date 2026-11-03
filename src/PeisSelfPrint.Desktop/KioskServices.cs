using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Printing;
using System.Text.Json;
using PeisSelfPrint.Core;

namespace PeisSelfPrint.Desktop;

public sealed class KioskSettings
{
    public string Mode { get; set; } = "Demo";
    public string HospitalName { get; set; } = "体检中心";
    public string SupportText { get; set; } = "需要帮助，请联系体检中心服务台";
    public int IdleSeconds { get; set; } = 60;
    public bool FullScreen { get; set; } = true;
    public string PrinterName { get; set; } = "";
    public string PdfEnginePath { get; set; } = "";
    public bool PhysicalPrintingEnabled { get; set; }
    public bool IsDemo => string.Equals(Mode, "Demo", StringComparison.OrdinalIgnoreCase);
    public static KioskSettings Load()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "kiosk.settings.json");
        if (!File.Exists(file)) return new() { Mode = "Live" };
        return JsonSerializer.Deserialize<KioskSettings>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("无法读取自助机配置。");
    }
}

public sealed class KioskReportClient(KioskSettings settings) : IDisposable
{
    private readonly HttpClient client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(50) };
    private Uri BaseUri()
    {
        var value = Environment.GetEnvironmentVariable("PEIS_API_BASE_URL") ?? "http://127.0.0.1:5088/";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http" || !uri.AbsolutePath.EndsWith('/'))
            throw new RuleException("CONFIGURATION_REQUIRED");
        return uri;
    }
    public async Task<bool> HealthAsync(CancellationToken ct)
    {
        if (settings.IsDemo) return true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(3000);
        try { using var response = await client.GetAsync(new Uri(BaseUri(), "health"), timeout.Token); return response.IsSuccessStatusCode; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or RuleException or ObjectDisposedException) { return false; }
    }
    public async Task<KioskLookup> LookupAsync(string number, CancellationToken ct)
    {
        number = ReportRules.ValidateExamNumber(number);
        if (settings.IsDemo)
        {
            await Task.Delay(1100, ct);
            if (number == "DEMO002") throw new RuleException("REPORT_PENDING");
            if (number == "DEMO003") throw new RuleException("REGISTRATION_NOT_FOUND");
            if (number == "DEMO005") throw new RuleException("NETWORK_UNAVAILABLE");
            if (number is not ("DEMO001" or "DEMO004")) throw new RuleException("REGISTRATION_NOT_FOUND");
            return new(number, "演示体验者", [
                new("demo-general", "综合体检报告", "2026-11-03 09:30", 6),
                new("demo-lab", "检验结果报告", "2026-11-03 09:20", 2),
                new("demo-pending", "专项检查报告", "审核中", null, false, "报告审核中，暂不可领取")], true, true);
        }
        var key = Environment.GetEnvironmentVariable("PEIS_API_KEY");
        if (string.IsNullOrEmpty(key) || key.Length < 32) throw new RuleException("CONFIGURATION_REQUIRED");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri(), "api/kiosk/reports/lookup"));
        request.Headers.Add("X-Api-Key", key); request.Content = JsonContent.Create(new { examNumber = number });
        using var response = await client.SendAsync(request, ct);
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!response.IsSuccessStatusCode) throw new RuleException(json.RootElement.TryGetProperty("code", out var code) ? code.GetString() ?? "INTEGRATION_FAILED" : "INTEGRATION_FAILED");
        var result = json.RootElement.Deserialize<KioskLookup>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new RuleException("UPSTREAM_INVALID_RESPONSE");
        if (result.ExamNumber != number || result.IsDemo) throw new RuleException("UPSTREAM_INVALID_RESPONSE");
        return result;
    }
    public async Task<KioskLookup> PrepareForPrintAsync(KioskLookup lookup, IReadOnlyList<KioskReport> selected, CancellationToken ct)
    {
        if (lookup.IsDemo) return lookup;
        if (!lookup.PrintReleaseAuthorized || string.IsNullOrWhiteSpace(lookup.SessionId)) throw new RuleException("IDENTITY_REQUIRED");
        if (selected.Count is 0 or > 10 || selected.Any(r => !lookup.Reports.Contains(r))) throw new RuleException("NO_PRINTABLE_REPORT");
        var key = Environment.GetEnvironmentVariable("PEIS_API_KEY") ?? "";
        var prepared = new List<KioskReport>(); var total = 0;
        foreach (var report in selected)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri(), "api/kiosk/sessions/" + Uri.EscapeDataString(lookup.SessionId) + "/reports/" + Uri.EscapeDataString(report.Id) + "/pdf"));
            request.Headers.Add("X-Api-Key", key);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken: ct);
                throw new RuleException(error?.GetValueOrDefault("code") ?? "PDF_FETCH_FAILED");
            }
            if (response.Content.Headers.ContentLength > 20 * 1024 * 1024) throw new RuleException("RESPONSE_TOO_LARGE");
            await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (output.Length + count > 20 * 1024 * 1024 || total + count > 40 * 1024 * 1024) throw new RuleException("RESPONSE_TOO_LARGE");
                output.Write(buffer, 0, count); total += count;
            }
            var pdf = output.ToArray();
            if (pdf.Length < 5 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new RuleException("INVALID_PDF_CONTENT");
            prepared.Add(report with { Pdf = pdf });
        }
        return lookup with { Reports = prepared };
    }
    public async Task EndSessionAsync(KioskLookup? lookup)
    {
        if (lookup is null || lookup.IsDemo || string.IsNullOrWhiteSpace(lookup.SessionId)) return;
        try
        {
            using var timeout = new CancellationTokenSource(3000);
            using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(BaseUri(), "api/kiosk/sessions/" + Uri.EscapeDataString(lookup.SessionId)));
            request.Headers.Add("X-Api-Key", Environment.GetEnvironmentVariable("PEIS_API_KEY") ?? "");
            using var response = await client.SendAsync(request, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ObjectDisposedException or RuleException) { }
    }
    public void Dispose() => client.Dispose();
}

public sealed class WindowsPrinterAdapter(KioskSettings settings) : IKioskPrintAdapter
{
    // 解析生效打印机：配置名有效则用配置名；配置名无效或为空则回退系统默认打印机。
    // 返回 (生效打印机名, 预检附加说明)；名称为 null 表示系统中完全没有可用打印机。
    private (string? Name, string Note) ResolvePrinter()
    {
        if (!string.IsNullOrWhiteSpace(settings.PrinterName))
        {
            try
            {
                using var server = new LocalPrintServer();
                using var queue = server.GetPrintQueue(settings.PrinterName);
                queue.Refresh();
                return (settings.PrinterName, string.Empty);
            }
            catch (PrintSystemException) { }
        }
        try
        {
            using var server = new LocalPrintServer();
            using var fallback = server.DefaultPrintQueue;
            fallback.Refresh();
            var note = string.IsNullOrWhiteSpace(settings.PrinterName)
                ? string.Empty
                : "（配置的打印机「" + settings.PrinterName + "」未找到，已回退到系统默认打印机）";
            return (fallback.Name, note);
        }
        catch (PrintSystemException)
        {
            return (null, string.IsNullOrWhiteSpace(settings.PrinterName) ? "设备尚未配置打印机。" : "无法连接指定打印机，请联系工作人员。");
        }
    }

    public Task<PrinterReadiness> CheckAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!settings.PhysicalPrintingEnabled) return new PrinterReadiness(false, "设备尚未启用纸质报告领取，请联系服务台。");
        if (string.IsNullOrWhiteSpace(settings.PdfEnginePath) || !File.Exists(settings.PdfEnginePath) || !Path.IsPathFullyQualified(settings.PdfEnginePath))
            return new PrinterReadiness(false, "打印组件需要工作人员配置。");
        var (name, note) = ResolvePrinter();
        if (name is null) return new PrinterReadiness(false, note);
        try
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(name); queue.Refresh();
            var status = queue.QueueStatus;
            if ((status & PrintQueueStatus.PaperOut) != 0) return new PrinterReadiness(false, "打印机缺纸，请联系工作人员加纸。");
            if ((status & PrintQueueStatus.PaperJam) != 0) return new PrinterReadiness(false, "打印机发生卡纸，请联系工作人员处理。");
            if ((status & (PrintQueueStatus.Offline | PrintQueueStatus.Paused | PrintQueueStatus.Error | PrintQueueStatus.NotAvailable | PrintQueueStatus.UserIntervention | PrintQueueStatus.DoorOpen)) != 0)
                return new PrinterReadiness(false, "打印机暂时不可用，请联系工作人员。");
            return new PrinterReadiness(true, "打印机就绪：" + name + note);
        }
        catch (PrintSystemException) { return new PrinterReadiness(false, "无法连接指定打印机，请联系工作人员。"); }
    }, ct);

    public async Task SubmitAsync(string path, CancellationToken ct)
    {
        var readiness = await CheckAsync(ct); if (!readiness.Ready) throw new RuleException("PRINTER_NOT_READY");
        var (name, _) = ResolvePrinter();
        var info = ExternalPdfEngine.CreateSumatraCommand(settings.PdfEnginePath, name ?? string.Empty, path);
        using var process = Process.Start(info) ?? throw new RuleException("PRINT_ENGINE_START_FAILED");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        if (process.ExitCode != 0) throw new RuleException("PRINT_ENGINE_FAILED");
    }
}
