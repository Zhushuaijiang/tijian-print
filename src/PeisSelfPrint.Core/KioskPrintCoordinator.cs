using System.Security.Cryptography;
using System.Text;

namespace PeisSelfPrint.Core;

public sealed class KioskPrintCoordinator(PrintLedger ledger, IKioskPrintAdapter adapter, string stagingDirectory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<PrintOutcome> PrintAsync(KioskLookup lookup, IReadOnlyList<KioskReport> selected,
        IProgress<PrintProgress>? progress, CancellationToken ct)
    {
        if (selected.Count == 0 || selected.Count > 10 || selected.Any(r => !r.CanPrint) ||
            selected.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != selected.Count ||
            selected.Any(r => !lookup.Reports.Contains(r))) throw new RuleException("NO_PRINTABLE_REPORT");
        if (!await gate.WaitAsync(0, ct)) throw new RuleException("PRINT_ALREADY_RESERVED");
        try
        {
            if (lookup.IsDemo)
            {
                for (var i = 0; i < selected.Count; i++)
                {
                    progress?.Report(new(i, selected.Count, $"正在模拟处理第 {i + 1} / {selected.Count} 份报告"));
                    await Task.Delay(900, ct);
                    progress?.Report(new(i + 1, selected.Count, "演示进度已更新"));
                }
                return PrintOutcome.Simulated;
            }
            if (!lookup.PrintReleaseAuthorized) throw new RuleException("IDENTITY_REQUIRED");
            // Validate every payload before any physical dispatch. A bad later report must not cause partial accidental printing.
            foreach (var report in selected)
                if (report.Pdf is not { Length: >= 5 and <= 20971520 } || !report.Pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
                    throw new RuleException("INVALID_PDF_CONTENT");
            if (!Path.IsPathFullyQualified(stagingDirectory)) throw new RuleException("INVALID_STAGED_PDF");
            var readiness = await adapter.CheckAsync(ct);
            if (!readiness.Ready) throw new RuleException("PRINTER_NOT_READY");
            var keys = selected.Select(r => Key(lookup.ExamNumber, r)).ToArray();
            if (keys.Any(k => ledger.Get(k) is not null)) throw new RuleException("PRINT_ALREADY_RESERVED");
            Directory.CreateDirectory(stagingDirectory);
            for (var i = 0; i < selected.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".pdf");
                var dispatching = false;
                try
                {
                    await File.WriteAllBytesAsync(file, selected[i].Pdf!, ct);
                    readiness = await adapter.CheckAsync(ct);
                    if (!readiness.Ready) throw new RuleException("PRINTER_NOT_READY");
                    if (!ledger.TryReserve(keys[i])) throw new RuleException("PRINT_ALREADY_RESERVED");
                    ct.ThrowIfCancellationRequested();
                    ledger.BeginDispatch(keys[i]); dispatching = true;
                    progress?.Report(new(i, selected.Count, $"正在发送第 {i + 1} / {selected.Count} 份报告"));
                    await adapter.SubmitAsync(file, ct);
                    ledger.MarkSubmitted(keys[i]); dispatching = false;
                    progress?.Report(new(i + 1, selected.Count, "已发送到打印机"));
                }
                catch (Exception ex)
                {
                    if (dispatching)
                    {
                        ledger.MarkUnknown(keys[i]);
                        if (i > 0) throw new PrintBatchException(i, "PRINT_RESULT_UNKNOWN");
                        throw new RuleException("PRINT_RESULT_UNKNOWN");
                    }
                    if (i > 0) throw new PrintBatchException(i, ex is RuleException rule ? rule.Message : "PRINT_RESULT_UNKNOWN");
                    throw;
                }
                finally { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
            // Successful engine exit only proves submission, never physical paper output.
            return PrintOutcome.Submitted;
        }
        finally { gate.Release(); }
    }
    private static string Key(string examNumber, KioskReport report) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(examNumber + "\0" + report.Id + "\0" + report.ProducedAt + "\0" +
            Convert.ToHexString(SHA256.HashData(report.Pdf!)))));
}
