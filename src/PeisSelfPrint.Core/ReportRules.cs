using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace PeisSelfPrint.Core;

public sealed record ReportCandidate(string PeisNo, string ReportId, string Address, string TypeName, string ProducedAt);
public sealed class RuleException(string code) : Exception(code) { }

public static partial class ReportRules
{
    [GeneratedRegex("\\A[A-Za-z0-9-]{1,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExamNumberPattern();

    public static string ValidateExamNumber(string? value)
    {
        // 扫码器的回车由 UI 处理，业务层不悄悄更正标识符。
        if (value is null || !ExamNumberPattern().IsMatch(value)) throw new RuleException("INVALID_EXAM_NUMBER");
        return value;
    }

    [GeneratedRegex("\\A[0-9Xx]{6}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdCardSuffixPattern();

    public static string ValidateIdCardSuffix(string? value)
    {
        if (value is null || !IdCardSuffixPattern().IsMatch(value)) throw new RuleException("INVALID_ID_SUFFIX");
        return value.ToUpperInvariant();
    }

    // Constant-time comparison so repeated attempts cannot learn the suffix character by character.
    public static bool MatchesIdCardSuffix(string? idCard, string normalizedSuffix)
    {
        if (string.IsNullOrWhiteSpace(idCard)) return false;
        var digits = new string(idCard.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (digits.Length < 6) return false;
        var tail = digits[^6..].ToUpperInvariant();
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(tail), System.Text.Encoding.ASCII.GetBytes(normalizedSuffix));
    }

    public static ReportCandidate SelectExact(string examNumber, IEnumerable<ReportCandidate> reports)
    {
        ValidateExamNumber(examNumber);
        var matches = reports.Where(r => string.Equals(r.PeisNo, examNumber, StringComparison.Ordinal)).Take(2).ToArray();
        if (matches.Length == 0) throw new RuleException("REPORT_NOT_FOUND");
        if (matches.Length != 1) throw new RuleException("AMBIGUOUS_REPORT");
        if (string.IsNullOrWhiteSpace(matches[0].ReportId)) throw new RuleException("INVALID_REPORT");
        return matches[0];
    }

    public static Uri ValidatePdfAddress(string address, IEnumerable<string> allowedOrigins, bool allowIntranetHttp = false)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(allowIntranetHttp && uri.Scheme == Uri.UriSchemeHttp)) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !allowedOrigins.Any(origin => IsValidOrigin(origin, allowIntranetHttp) &&
                string.Equals(new Uri(origin).GetLeftPart(UriPartial.Authority), uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)))
            throw new RuleException("PDF_ADDRESS_DENIED");
        // 保留原始 query，包括 viewer 的 _type、报告参数和 pageOffset。
        return uri;
    }

    public static bool IsValidOrigin(string origin, bool allowIntranetHttp) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || (allowIntranetHttp && uri.Scheme == Uri.UriSchemeHttp)) &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.Query.Length == 0 && uri.AbsolutePath == "/";

    public static ReportCandidate[] ParseReportList(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement;
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (!data.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number ||
                !code.TryGetInt32(out var numericCode) || numericCode != 200 || !data.TryGetProperty("data", out data))
                throw new RuleException("UPSTREAM_INVALID_RESPONSE");
        }
        if (data.ValueKind != JsonValueKind.Array) throw new RuleException("UPSTREAM_INVALID_RESPONSE");
        return data.EnumerateArray().Select(row =>
        {
            if (row.ValueKind != JsonValueKind.Object) throw new RuleException("UPSTREAM_INVALID_RESPONSE");
            return new ReportCandidate(Text(row, "peisNo"), Text(row, "peisPersonalReportId"), Text(row, "peisReportAdress"),
                Text(row, "peisTypeName"), Text(row, "peisPersonalReportProduceTime"));
        }).ToArray();
    }

    private static string Text(JsonElement row, string name) => row.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";
}

public enum PrintState { Reserved, Dispatching, Submitted, Unknown }

// 单机本地磁盘账本。独占锁跨进程有效；不得放在网络共享盘。本阶段仍不开放打印入口。
public sealed class PrintLedger : IDisposable
{
    private readonly object gate = new();
    private readonly string path;
    private readonly FileStream ownership;
    private Dictionary<string, PrintState> jobs;
    private bool unavailable;
    private bool disposed;

    public PrintLedger(string ledgerPath)
    {
        if (!Path.IsPathFullyQualified(ledgerPath)) throw new RuleException("INVALID_LEDGER_PATH");
        path = ledgerPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ownership = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            jobs = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, PrintState>>(File.ReadAllBytes(path))
                ?? throw new RuleException("INVALID_LEDGER_CONTENT") : new(StringComparer.Ordinal);
            if (jobs.Any(job => string.IsNullOrWhiteSpace(job.Key) || !Enum.IsDefined(job.Value))) throw new RuleException("INVALID_LEDGER_CONTENT");
            if (jobs.Any(job => job.Value == PrintState.Dispatching))
            {
                var recovered = new Dictionary<string, PrintState>(jobs, StringComparer.Ordinal);
                foreach (var key in recovered.Keys.ToArray())
                    if (recovered[key] == PrintState.Dispatching) recovered[key] = PrintState.Unknown;
                Commit(recovered);
            }
        }
        catch { ownership.Dispose(); throw; }
    }

    public bool TryReserve(string reportId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);
        lock (gate)
        {
            EnsureAvailable();
            if (jobs.ContainsKey(reportId)) return false;
            var next = new Dictionary<string, PrintState>(jobs, StringComparer.Ordinal) { [reportId] = PrintState.Reserved };
            Commit(next);
            return true;
        }
    }

    public void BeginDispatch(string reportId) => Transition(reportId, PrintState.Reserved, PrintState.Dispatching);
    public void MarkSubmitted(string reportId) => Transition(reportId, PrintState.Dispatching, PrintState.Submitted);
    public void MarkUnknown(string reportId) => Transition(reportId, PrintState.Dispatching, PrintState.Unknown);
    public PrintState? Get(string reportId)
    {
        lock (gate) { EnsureAvailable(); return jobs.TryGetValue(reportId, out var state) ? state : null; }
    }

    private void Transition(string reportId, PrintState from, PrintState to)
    {
        lock (gate)
        {
            EnsureAvailable();
            if (!jobs.TryGetValue(reportId, out var state) || state != from) throw new RuleException("INVALID_PRINT_TRANSITION");
            var next = new Dictionary<string, PrintState>(jobs, StringComparer.Ordinal) { [reportId] = to };
            Commit(next);
        }
    }

    private void Commit(Dictionary<string, PrintState> next)
    {
        try
        {
            // 同目录暂存，刷新后原子替换。只有持久化成功才能返回允许调度。
            using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(file, next);
                file.Flush(flushToDisk: true);
            }
            File.Move(path + ".tmp", path, overwrite: true);
            jobs = next;
        }
        catch { unavailable = true; throw; }
    }

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (unavailable) throw new RuleException("LEDGER_UNAVAILABLE");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ownership.Dispose();
        }
    }
}
