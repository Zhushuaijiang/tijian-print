namespace PeisSelfPrint.Core;

public enum KioskStep { Welcome, Loading, Reports, Confirm, Printing, Result, Error }
public sealed record KioskReport(string Id, string Title, string ProducedAt, int? PageCount,
    bool CanPrint = true, string UnavailableReason = "", byte[]? Pdf = null);
public sealed record KioskLookup(string ExamNumber, string DisplayName, IReadOnlyList<KioskReport> Reports,
    bool IsDemo, bool IdentityVerified, string SessionId = "", bool PrintReleaseAuthorized = false);
public sealed record PrinterReadiness(bool Ready, string Message);
public enum PrintOutcome { Simulated, Submitted, AlreadySubmitted, NeedsStaff }
public sealed record PrintProgress(int Completed, int Total, string Message);
public interface IKioskPrintAdapter
{
    Task<PrinterReadiness> CheckAsync(CancellationToken cancellationToken);
    Task SubmitAsync(string stagedPdf, CancellationToken cancellationToken);
}

// A generation separates users: a cancelled or expired lookup cannot repopulate the next visitor's screen.
public sealed class KioskSessionClock
{
    private long revision;
    public long Current => Interlocked.Read(ref revision);
    public long Begin() => Interlocked.Increment(ref revision);
    public bool IsCurrent(long value) => Current == value;
    public void Reset() => Interlocked.Increment(ref revision);
}

public static class KioskMessages
{
    public static string For(string? code) => code switch
    {
        "INVALID_EXAM_NUMBER" => "体检号格式不正确，请扫描体检单条码，或输入 1–64 位字母、数字和短横线。",
        "INVALID_ID_SUFFIX" => "请输入身份证号后 6 位（含字母 X 也可以）。",
        "IDENTITY_MISMATCH" => "体检号与身份证后 6 位不一致，请核对后重试。",
        "IDENTITY_NOT_AVAILABLE" => "该记录缺少身份证信息，无法自助领取，请联系服务台。",
        "TOO_MANY_ATTEMPTS" => "验证次数过多，请稍后再试，或到服务台领取报告。",
        "REGISTRATION_NOT_FOUND" or "REPORT_NOT_FOUND" => "暂未找到可领取的报告。请核对体检号；如体检尚未完成，请稍后再来。",
        "REPORT_PENDING" => "您的报告正在审核，完成后即可领取。请稍后再来或咨询服务台。",
        "AMBIGUOUS_REGISTRATION" or "AMBIGUOUS_REPORT" => "查询到多条无法确认归属的记录，请工作人员核对后领取。",
        "CONFIGURATION_REQUIRED" or "PATIENT_ID_MAPPING_UNVERIFIED" => "设备正在配置中，暂时无法查询。请到服务台领取报告。",
        "UNAUTHORIZED" => "设备连接需要工作人员维护，请到服务台领取报告。",
        "IDENTITY_REQUIRED" => "该报告暂不支持自助领取，请联系工作人员。",
        "INTEGRATION_TIMEOUT_OR_CANCELLED" or "NETWORK_UNAVAILABLE" => "暂时无法连接报告服务。请稍后重试，或联系服务台。",
        "PRINTER_NOT_READY" => "打印机暂时不可用，请联系工作人员处理。您的报告不会重复提交。",
        "PRINT_ALREADY_RESERVED" => "这份报告已有打印记录，请勿重复操作。未取到报告时请联系工作人员。",
        "PRINT_RESULT_UNKNOWN" => "打印结果需要工作人员确认。请勿再次打印，以免重复出纸。",
        "SESSION_EXPIRED" => "本次查询已过期，请重新扫码查询。",
        "SERVICE_BUSY" => "当前设备繁忙，请稍后重试。",
        "PDF_FETCH_FAILED" or "INVALID_PDF_CONTENT" or "PDF_ADDRESS_DENIED" => "报告文件暂时无法获取，请联系服务台。",
        "NO_PRINTABLE_REPORT" => "请至少选择一份可领取的报告。",
        _ => "暂时无法完成本次操作，请稍后重试或联系服务台。"
    };
}

public static class KioskReportSelection
{
    public static ReportCandidate[] ForExam(string number, IEnumerable<ReportCandidate> reports)
    {
        ReportRules.ValidateExamNumber(number);
        var rows = reports.Where(r => string.Equals(r.PeisNo, number, StringComparison.Ordinal)).Take(51).ToArray();
        if (rows.Length == 0) throw new RuleException("REPORT_NOT_FOUND");
        if (rows.Length > 50) throw new RuleException("TOO_MANY_REPORTS");
        if (rows.Any(r => string.IsNullOrWhiteSpace(r.ReportId))) throw new RuleException("INVALID_REPORT");
        if (rows.Select(r => r.ReportId).Distinct(StringComparer.Ordinal).Count() != rows.Length) throw new RuleException("AMBIGUOUS_REPORT");
        return rows;
    }
}

public sealed class PrintBatchException(int submittedCount, string code) : Exception(code)
{
    public int SubmittedCount { get; } = submittedCount;
}
