using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using PeisSelfPrint.Core;

public static class KioskEndpoints
{
    private sealed record Session(string ExamNumber, string DisplayName, DateTimeOffset Expires, ReportCandidate[] Reports, bool ReleaseAuthorized);
    private static readonly ConcurrentDictionary<string, Session> Sessions = new(StringComparer.Ordinal);
    private static bool Authenticated(HttpContext context, IntegrationSettings settings)
    {
        var key = context.Request.Headers["X-Api-Key"].ToString();
        return settings.ApiKey.Length >= 32 && key.Length <= 512 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)), SHA256.HashData(Encoding.UTF8.GetBytes(settings.ApiKey)));
    }
    private static string MaskName(string value) => string.IsNullOrWhiteSpace(value) ? "体检用户" : value[..1] + new string('*', Math.Clamp(value.Length - 1, 1, 3));
    public static void MapKioskReports(this WebApplication app, IntegrationSettings settings)
    {
        app.MapPost("/api/kiosk/reports/lookup", async (LookupRequest request, HttpContext context, IHttpClientFactory factory) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!Authenticated(context, settings)) return Results.Json(new { code = "UNAUTHORIZED" }, statusCode: 401);
            if (!settings.IsConfigured()) return Results.Json(new { code = "CONFIGURATION_REQUIRED" }, statusCode: 503);
            try
            {
                var number = ReportRules.ValidateExamNumber(request.ExamNumber);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                deadline.CancelAfter(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
                await using var connection = new NpgsqlConnection(settings.ConnectionString); await connection.OpenAsync(ct);
                await using var tx = await connection.BeginTransactionAsync(ct);
                await using (var command = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, tx)) await command.ExecuteNonQueryAsync(ct);
                await using var query = new NpgsqlCommand("SELECT \"病人id\", \"姓名\" FROM public.zkyszzj_view WHERE \"体检号\" = @examNumber LIMIT 2", connection, tx);
                query.Parameters.AddWithValue("examNumber", number); query.CommandTimeout = 15;
                string patient; string displayName;
                await using (var reader = await query.ExecuteReaderAsync(ct))
                {
                    if (!await reader.ReadAsync(ct)) throw new RuleException("REGISTRATION_NOT_FOUND");
                    if (reader.IsDBNull(0) || string.IsNullOrWhiteSpace(reader.GetString(0))) throw new RuleException("INVALID_REGISTRATION");
                    patient = reader.GetString(0); displayName = reader.IsDBNull(1) ? "体检用户" : MaskName(reader.GetString(1));
                    if (await reader.ReadAsync(ct)) throw new RuleException("AMBIGUOUS_REGISTRATION");
                }
                await tx.RollbackAsync(ct);
                var endpoint = new Uri(new Uri(settings.ReportApiBaseUrl), "api/PeisReport/ViewPeisPersonReportListByZLCDRView?PatientId=" + Uri.EscapeDataString(patient) + "&HospitalAreaId=" + Uri.EscapeDataString(settings.HospitalAreaId) + "&PeisCenterId=" + Uri.EscapeDataString(settings.PeisCenterId));
                using var response = await factory.CreateClient("upstream").GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode != HttpStatusCode.OK) throw new RuleException("UPSTREAM_FAILED");
                var all = ReportRules.ParseReportList(await BoundedRead.ReadAsync(response.Content, 1024 * 1024, ct));
                if (all.Length == 0) throw new RuleException("REPORT_PENDING");
                var matches = KioskReportSelection.ForExam(number, all);
                foreach (var report in matches) ReportRules.ValidatePdfAddress(report.Address, settings.AllowedPdfOrigins, settings.AllowIntranetHttp);
                foreach (var expired in Sessions.Where(s => s.Value.Expires <= DateTimeOffset.UtcNow)) Sessions.TryRemove(expired.Key, out _);
                if (Sessions.Count >= 100) return Results.Json(new { code = "SERVICE_BUSY" }, statusCode: 429);
                ct.ThrowIfCancellationRequested();
                var id = Guid.NewGuid().ToString("N");
                // Per hospital's decision: possession of the exam number authorizes release; no ID suffix check on site.
                var session = new Session(number, displayName, DateTimeOffset.UtcNow.AddMinutes(5), matches, true);
                Sessions[id] = session;
                var rows = matches.Select(r => new KioskReport(r.ReportId, string.IsNullOrWhiteSpace(r.TypeName) ? "体检报告" : r.TypeName, r.ProducedAt, null)).ToArray();
                // Release follows the hospital's exam-number policy; printing is still guarded by the per-report ledger.
                return Results.Ok(new KioskLookup(number, displayName, rows, false, true, id, true));
            }
            catch (RuleException ex) { return Results.Json(new { code = ex.Message }, statusCode: 422); }
            catch (OperationCanceledException) { return Results.Json(new { code = "INTEGRATION_TIMEOUT_OR_CANCELLED" }, statusCode: 504); }
            catch (Exception) { return Results.Json(new { code = "INTEGRATION_FAILED" }, statusCode: 502); }
        }).RequireRateLimiting("lookup");

        app.MapGet("/api/kiosk/sessions/{sessionId}/reports/{reportId}/pdf", async (string sessionId, string reportId, HttpContext context, IHttpClientFactory factory) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!Authenticated(context, settings)) return Results.Json(new { code = "UNAUTHORIZED" }, statusCode: 401);
            if (!Sessions.TryGetValue(sessionId, out var session) || session.Expires <= DateTimeOffset.UtcNow)
                return Results.Json(new { code = "SESSION_EXPIRED" }, statusCode: 410);
            if (!session.ReleaseAuthorized) return Results.Json(new { code = "IDENTITY_REQUIRED" }, statusCode: 403);
            var report = session.Reports.SingleOrDefault(r => string.Equals(r.ReportId, reportId, StringComparison.Ordinal));
            if (report is null) return Results.NotFound();
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); deadline.CancelAfter(TimeSpan.FromSeconds(45));
                var uri = ReportRules.ValidatePdfAddress(report.Address, settings.AllowedPdfOrigins, settings.AllowIntranetHttp);
                var bytes = await TrustedPdfReader.ReadAsync(factory.CreateClient("upstream"), uri, settings.AllowedPdfOrigins, settings.AllowIntranetHttp, deadline.Token);
                return Results.File(bytes, "application/pdf");
            }
            catch (RuleException ex) { return Results.Json(new { code = ex.Message }, statusCode: 422); }
            catch (OperationCanceledException) { return Results.Json(new { code = "INTEGRATION_TIMEOUT_OR_CANCELLED" }, statusCode: 504); }
            catch (Exception) { return Results.Json(new { code = "PDF_FETCH_FAILED" }, statusCode: 502); }
        }).RequireRateLimiting("lookup");
        app.MapDelete("/api/kiosk/sessions/{sessionId}", (string sessionId, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!Authenticated(context, settings)) return Results.Unauthorized();
            Sessions.TryRemove(sessionId, out _); return Results.NoContent();
        });
    }
}
