using System.Diagnostics;

namespace PeisSelfPrint.Core;

// Build a fixed-printer, single-copy command. The coordinator owns dispatch and durable state.
public static class ExternalPdfEngine
{
    public static ProcessStartInfo CreateSumatraCommand(string configuredExecutable, string configuredPrinter, string stagedPdfPath)
    {
        if (string.IsNullOrWhiteSpace(configuredExecutable) || !Path.IsPathFullyQualified(configuredExecutable) ||
            !string.Equals(Path.GetExtension(configuredExecutable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new RuleException("INVALID_ENGINE_CONFIGURATION");
        if (string.IsNullOrWhiteSpace(configuredPrinter) || configuredPrinter.Any(char.IsControl))
            throw new RuleException("INVALID_PRINTER_CONFIGURATION");
        if (!Path.IsPathFullyQualified(stagedPdfPath) || !string.Equals(Path.GetExtension(stagedPdfPath), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new RuleException("INVALID_STAGED_PDF");
        var start = new ProcessStartInfo(configuredExecutable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-print-to");
        start.ArgumentList.Add(configuredPrinter);
        start.ArgumentList.Add("-silent");
        start.ArgumentList.Add("-print-settings");
        start.ArgumentList.Add("fit,1x");
        start.ArgumentList.Add(stagedPdfPath);
        return start;
    }
}
