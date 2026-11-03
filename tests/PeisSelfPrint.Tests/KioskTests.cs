using PeisSelfPrint.Core;
using System.Text;

static class KioskTests
{
    public static void Run()
    {
        var generation = new KioskSessionClock(); var old = generation.Begin(); generation.Reset();
        Check(!generation.IsCurrent(old), "idle reset must invalidate an in-flight lookup");
        var fresh = generation.Begin(); Check(generation.IsCurrent(fresh), "new lookup generation must remain valid");
        ReportCandidate R(string number, string id) => new(number, id, "https://example.invalid/a.pdf", "报告", "2026-11-03");
        var selected = KioskReportSelection.ForExam("0001", [R("001", "other"), R("0001", "a"), R("0001", "b")]);
        Check(selected.Length == 2 && selected[0].ReportId == "a", "multi-report selection must preserve exact exam identity");
        Expect("AMBIGUOUS_REPORT", () => KioskReportSelection.ForExam("0001", [R("0001", "a"), R("0001", "a")]));
        Expect("REPORT_NOT_FOUND", () => KioskReportSelection.ForExam("0001", [R("001", "other")]));
        Expect("TOO_MANY_REPORTS", () => KioskReportSelection.ForExam("0001", Enumerable.Range(0, 51).Select(i => R("0001", i.ToString()))));
        var dir = Path.Combine(Path.GetTempPath(), "kiosk-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            using var ledger = new PrintLedger(Path.Combine(dir, "ledger.json"));
            var adapter = new FakePrinter(); var coordinator = new KioskPrintCoordinator(ledger, adapter, Path.Combine(dir, "stage"));
            var report = new KioskReport("r1", "合成报告", "2026-11-03", 1, Pdf: Encoding.ASCII.GetBytes("%PDF-synthetic"));
            var live = new KioskLookup("DEMO-TEST", "测试用户", [report], false, false, PrintReleaseAuthorized: true);
            Expect("IDENTITY_REQUIRED", () => coordinator.PrintAsync(live with { PrintReleaseAuthorized = false }, [report], null, default).GetAwaiter().GetResult());
            Check(adapter.Checks == 0 && adapter.Submits == 0, "release authorization must be checked before printer access");
            Expect("NO_PRINTABLE_REPORT", () => coordinator.PrintAsync(live, [report, report], null, default).GetAwaiter().GetResult());
            Expect("NO_PRINTABLE_REPORT", () => coordinator.PrintAsync(live, [report with { Title = "injected" }], null, default).GetAwaiter().GetResult());
            var bad = report with { Id = "bad", Pdf = Encoding.ASCII.GetBytes("<html>not pdf</html>") };
            Expect("INVALID_PDF_CONTENT", () => coordinator.PrintAsync(live with { Reports = [report, bad] }, [report, bad], null, default).GetAwaiter().GetResult());
            Check(adapter.Submits == 0, "validate entire batch before printing its first report");
            adapter.Ready = false;
            Expect("PRINTER_NOT_READY", () => coordinator.PrintAsync(live, [report], null, default).GetAwaiter().GetResult());
            adapter.Ready = true;
            var result = coordinator.PrintAsync(live, [report], null, default).GetAwaiter().GetResult();
            Check(result == PrintOutcome.Submitted && adapter.Submits == 1, "engine return is submitted, never completed");
            Check(Directory.GetFiles(Path.Combine(dir, "stage")).Length == 0, "staged PDF must be deleted after sending");
            Expect("PRINT_ALREADY_RESERVED", () => coordinator.PrintAsync(live, [report], null, default).GetAwaiter().GetResult());
            Check(adapter.Submits == 1, "double click cannot submit twice");
            var uncertain = report with { Id = "uncertain" };
            var uncertainLookup = live with { Reports = [uncertain] };
            adapter.FailSubmit = true;
            Expect("PRINT_RESULT_UNKNOWN", () => coordinator.PrintAsync(uncertainLookup, [uncertain], null, default).GetAwaiter().GetResult());
            adapter.FailSubmit = false;
            Expect("PRINT_ALREADY_RESERVED", () => coordinator.PrintAsync(uncertainLookup, [uncertain], null, default).GetAwaiter().GetResult());
            Check(adapter.Submits == 2, "uncertain dispatch cannot retry");
            var pair = new[] { report with { Id = "batch-first" }, report with { Id = "batch-second" } };
            adapter.FailAt = adapter.Submits + 2;
            try { coordinator.PrintAsync(live with { Reports = pair }, pair, null, default).GetAwaiter().GetResult(); throw new Exception("Expected partial print failure"); }
            catch (PrintBatchException ex) { Check(ex.SubmittedCount == 1, "partial dispatch must retain the exact number already sent"); }
            adapter.FailAt = 0;
            Expect("PRINT_ALREADY_RESERVED", () => coordinator.PrintAsync(live with { Reports = pair }, pair, null, default).GetAwaiter().GetResult());
            var before = adapter.Submits;
            var demo = live with { IsDemo = true, IdentityVerified = false };
            using var cancel = new CancellationTokenSource();
            var pending = coordinator.PrintAsync(demo, [report], null, cancel.Token);
            Expect("PRINT_ALREADY_RESERVED", () => coordinator.PrintAsync(demo, [report], null, default).GetAwaiter().GetResult());
            cancel.Cancel();
            try { pending.GetAwaiter().GetResult(); throw new Exception("demo cancellation ignored"); } catch (OperationCanceledException) { }
            Check(adapter.Submits == before, "demo must never call physical print engine");
            Check(KioskMessages.For("CONFIGURATION_REQUIRED").Contains("服务台"), "patient-facing message must offer an action");
            Check(KioskMessages.For("PRINT_RESULT_UNKNOWN").Contains("请勿"), "unknown print outcome must discourage automatic retry");
            var enginePath = Path.Combine(dir, "engine.exe"); File.WriteAllText(enginePath, "stub");
            var pdfPath = Path.Combine(dir, "doc.pdf"); File.WriteAllText(pdfPath, "%PDF-x");
            var defaultCmd = ExternalPdfEngine.CreateSumatraCommand(enginePath, string.Empty, pdfPath);
            Check(defaultCmd.ArgumentList.Contains("-print-to-default") && !defaultCmd.ArgumentList.Contains("-print-to"), "empty printer name must follow the Windows default printer");
            var namedCmd = ExternalPdfEngine.CreateSumatraCommand(enginePath, "EPSON L300", pdfPath);
            Check(namedCmd.ArgumentList.Contains("-print-to") && namedCmd.ArgumentList.Contains("EPSON L300") && !namedCmd.ArgumentList.Contains("-print-to-default"), "configured printer name must be pinned to the print command");
            Expect("INVALID_PRINTER_CONFIGURATION", () => ExternalPdfEngine.CreateSumatraCommand(enginePath, "bad\nname", pdfPath));
            Expect("INVALID_ENGINE_CONFIGURATION", () => ExternalPdfEngine.CreateSumatraCommand("relative-engine.exe", "printer", pdfPath));
        }
        finally { Directory.Delete(dir, true); }
    }
    private sealed class FakePrinter : IKioskPrintAdapter
    {
        public bool Ready = true; public bool FailSubmit; public int FailAt; public int Checks; public int Submits;
        public Task<PrinterReadiness> CheckAsync(CancellationToken ct) { Checks++; ct.ThrowIfCancellationRequested(); return Task.FromResult(new PrinterReadiness(Ready, "fixture")); }
        public Task SubmitAsync(string file, CancellationToken ct)
        {
            Check(File.Exists(file) && File.ReadAllText(file).StartsWith("%PDF-"), "engine must receive a staged PDF");
            Submits++; if (FailSubmit || (FailAt > 0 && Submits == FailAt)) throw new IOException("simulated printer communication failure");
            return Task.CompletedTask;
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect(string code, Action action) { try { action(); } catch (RuleException e) when (e.Message == code) { return; } throw new Exception("Expected " + code); }
}
