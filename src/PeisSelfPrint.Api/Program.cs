using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using PeisSelfPrint.Core;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("integration.local.json", optional: true, reloadOnChange: false);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
var settings = builder.Configuration.GetSection("Integration").Get<IntegrationSettings>() ?? new();
builder.Services.AddSingleton(settings);
builder.Services.AddHttpClient("upstream", c => c.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false });
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("lookup", limiter =>
{
    limiter.PermitLimit = 30; limiter.Window = TimeSpan.FromMinutes(1); limiter.QueueLimit = 0;
    limiter.AutoReplenishment = true; limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
}));
var app = builder.Build();
app.UseRateLimiter();
app.MapKioskReports(settings);
app.MapGet("/health", () => Results.Ok(new { status = "running", mode = "validation-only", realPrintingEnabled = false }));
app.MapPost("/api/reports/resolve", async (LookupRequest request, HttpContext context, IHttpClientFactory factory) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!settings.IsConfigured()) return Results.Json(new { code = "CONFIGURATION_REQUIRED" }, statusCode: 503);
    var supplied = context.Request.Headers["X-Api-Key"].ToString();
    if (supplied.Length > 512 || !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(settings.ApiKey))))
        return Results.Json(new { code = "UNAUTHORIZED" }, statusCode: 401);
    if (!settings.PatientIdMappingVerified) return Results.Json(new { code = "PATIENT_ID_MAPPING_UNVERIFIED" }, statusCode: 503);
    try
    {
        var examNumber = ReportRules.ValidateExamNumber(request.ExamNumber);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        var patientId = await PatientRegistration.FindPatientIdAsync(settings.ConnectionString, examNumber, ct);
        var endpoint = new Uri(new Uri(settings.ReportApiBaseUrl), "api/PeisReport/ViewPeisPersonReportListByZLCDRView?PatientId=" +
            Uri.EscapeDataString(patientId) + "&HospitalAreaId=" + Uri.EscapeDataString(settings.HospitalAreaId) + "&PeisCenterId=" + Uri.EscapeDataString(settings.PeisCenterId));
        var client = factory.CreateClient("upstream");
        using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK) throw new RuleException("UPSTREAM_FAILED");
        var jsonBytes = await BoundedRead.ReadAsync(response.Content, 1024 * 1024, ct);
        var selected = ReportRules.SelectExact(examNumber, ReportRules.ParseReportList(jsonBytes));
        var pdfUri = ReportRules.ValidatePdfAddress(selected.Address, settings.AllowedPdfOrigins, settings.AllowIntranetHttp);
        // 可选验证只检查响应内容，不把报告内容或地址传给终端，也不发起打印。
        if (settings.ValidatePdfContent)
        {
            _ = await TrustedPdfReader.ReadAsync(client, pdfUri, settings.AllowedPdfOrigins,
                settings.AllowIntranetHttp, ct);
        }
        return Results.Ok(new { code = "VALIDATED_FOR_INTEGRATION_ONLY", examNumber, reportType = selected.TypeName,
            producedAt = selected.ProducedAt, pdfContentValidated = settings.ValidatePdfContent,
            identityVerified = false, realPrintingEnabled = false });
    }
    catch (RuleException ex) { return Results.Json(new { code = ex.Message }, statusCode: 422); }
    catch (OperationCanceledException) { return Results.Json(new { code = "INTEGRATION_TIMEOUT_OR_CANCELLED" }, statusCode: 504); }
    catch (Exception) { return Results.Json(new { code = "INTEGRATION_FAILED" }, statusCode: 502); }
}).RequireRateLimiting("lookup");
app.Run();

public sealed record LookupRequest(string? ExamNumber);
public sealed class IntegrationSettings
{
    public string ApiKey { get; set; } = "";
    public string ConnectionString { get; set; } = "";
    public string ReportApiBaseUrl { get; set; } = "";
    public string HospitalAreaId { get; set; } = "";
    public string PeisCenterId { get; set; } = "";
    public string[] AllowedPdfOrigins { get; set; } = [];
    public string[] AllowedReportApiOrigins { get; set; } = [];
    public bool AllowIntranetHttp { get; set; }
    public bool BarcodeReleaseEnabled { get; set; }
    public bool PatientIdMappingVerified { get; set; }
    public bool ValidatePdfContent { get; set; }
    public bool IsConfigured() => ApiKey.Length is >= 32 and <= 512 && !string.IsNullOrWhiteSpace(ConnectionString) &&
        AllowedPdfOrigins.Length > 0 && AllowedPdfOrigins.All(origin => ReportRules.IsValidOrigin(origin, AllowIntranetHttp)) &&
        AllowedReportApiOrigins.Length > 0 && AllowedReportApiOrigins.All(origin => ReportRules.IsValidOrigin(origin, AllowIntranetHttp)) &&
        Uri.TryCreate(ReportApiBaseUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "https" || (AllowIntranetHttp && uri.Scheme == "http")) &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath.EndsWith('/') &&
        AllowedReportApiOrigins.Any(origin => string.Equals(new Uri(origin).GetLeftPart(UriPartial.Authority), uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));
}
public static class PatientRegistration
{
    public static async Task<string> FindPatientIdAsync(string connectionString, string examNumber, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT \"病人id\" FROM public.zkyszzj_view WHERE \"体检号\" = @examNumber LIMIT 2", connection);
        command.Parameters.AddWithValue("examNumber", examNumber);
        command.CommandTimeout = 15;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new RuleException("REGISTRATION_NOT_FOUND");
        if (reader.IsDBNull(0)) throw new RuleException("INVALID_REGISTRATION");
        var patientId = reader.GetString(0);
        if (string.IsNullOrWhiteSpace(patientId)) throw new RuleException("INVALID_REGISTRATION");
        if (await reader.ReadAsync(ct)) throw new RuleException("AMBIGUOUS_REGISTRATION");
        return patientId;
    }
}
public static class BoundedRead
{
    public static async Task<byte[]> ReadAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) throw new RuleException("RESPONSE_TOO_LARGE");
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int length;
        while ((length = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + length > maximum) throw new RuleException("RESPONSE_TOO_LARGE");
            output.Write(buffer, 0, length);
        }
        return output.ToArray();
    }
}
