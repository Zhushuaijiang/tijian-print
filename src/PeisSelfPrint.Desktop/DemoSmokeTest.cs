using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PeisSelfPrint.Core;

namespace PeisSelfPrint.Desktop;

public partial class MainWindow
{
    // Development-only rendering test of this application's own visual tree; no system input automation.
    private async Task RunDemoSmokeTest()
    {
        var directory = Environment.GetEnvironmentVariable("PEIS_KIOSK_TEST_OUTPUT");
        if (string.IsNullOrWhiteSpace(directory)) directory = Path.Combine(Path.GetTempPath(), "PeisKioskSmokeOutput");
        Directory.CreateDirectory(directory);
        var passed = new List<string>();
        void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); passed.Add(name); }
        async Task Capture(string name)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var content = (FrameworkElement)Content; content.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory!, name + ".png")); encoder.Save(file);
        }
        try
        {
            WindowStyle = WindowStyle.SingleBorderWindow; WindowState = WindowState.Normal; Width = 1366; Height = 900;
            bool gateOpened = false;
            for (var i = 0; i < 5; i++) gateOpened = RegisterGateClick();
            Require(gateOpened, "five consecutive clock taps open the service configuration gate");
            Require(!RegisterGateClick(), "gate resets after being opened to prevent accidental reopening");

            // 服务配置（数据库/报告服务）纯逻辑断言
            Require(KioskConfigWindow.ValidateServiceInputs("", "", Array.Empty<string>()).Count == 0, "empty service inputs are valid (means no override)");
            Require(KioskConfigWindow.ValidateServiceInputs("Host=db;Port=5432", "http://10.0.0.1:8080/", new[] { "http://10.0.0.1:8230" }).Count == 0, "well-formed service inputs pass validation");
            Require(KioskConfigWindow.ValidateServiceInputs("no-host-here", "notaurl", new[] { "ftp://bad" }).Count == 3, "malformed service inputs are all rejected");
            var integration = new JsonObject();
            KioskConfigWindow.ApplyServiceOverrides(integration, "Host=db;Port=5432", "http://10.0.0.1:8080/", new[] { " http://10.0.0.1:8230 ", "" }, "A1", "");
            Require((string?)integration["ConnectionString"] == "Host=db;Port=5432" && (string?)integration["PeisCenterId"] is null, "service overrides set values and drop blanks");
            Require(((JsonArray?)integration["AllowedPdfOrigins"])!.Count == 1, "origin list trims and skips blank lines");
            KioskConfigWindow.ApplyServiceOverrides(integration, "", "", Array.Empty<string>(), "", "");
            Require(!integration.ContainsKey("ConnectionString") && !integration.ContainsKey("AllowedPdfOrigins"), "blank overrides fall back to bundled defaults");
            passed.Add("service config override merge verified");
            var readiness = await printer.CheckAsync(CancellationToken.None);
            passed.Add("configured printer preflight completed: " + readiness.Ready + " / " + readiness.Message);
            await Capture("01-home");
            ExamInput.Text = "DEMO001"; await Lookup();
            Require(step == KioskStep.Reports && choices.Count == 3 && choices.Count(x => x.CanPrint) == 2, "normal lookup offers two ready reports and one pending report");
            SelectAll_Click(this, new RoutedEventArgs());
            Require(choices.Count(x => x.Selected) == 2 && !choices.Last().Selected, "select-all excludes pending report");
            await Capture("02-report-selection");
            await ConfirmSelection();
            Require(PrintButton.IsEnabled, "preset acknowledgement keeps print action ready after confirm");
            Require(!Acknowledge.IsEnabled && Acknowledge.IsChecked == true, "acknowledgement is preset and not user-editable");
            await Capture("03-confirm");
            await SubmitSelection();
            Require(step == KioskStep.Result && ResultTitle.Text.Contains("演示") && ResultMessage.Text.Contains("没有实际打印"), "demo completion never claims paper output");
            await Capture("04-result");
            Reset(); ExamInput.Text = "DEMO001"; var pending = Lookup(); await Task.Delay(50); Reset(); await pending;
            Require(step == KioskStep.Welcome && choices.Count == 0 && ExamInput.Text == "", "late lookup response cannot repopulate a cleared session");
            ExamInput.Text = "DEMO001"; await Lookup(); lastActivity = DateTime.UtcNow.AddSeconds(-settings.IdleSeconds - 1); Timer_Tick(this, EventArgs.Empty);
            Require(step == KioskStep.Welcome && lookup is null && choices.Count == 0 && PersonSummary.Text == "", "idle expiry clears all visitor state");
            ExamInput.Text = "DEMO002"; await Lookup();
            Require(step == KioskStep.Error && ErrorTitle.Text.Contains("审核"), "pending report has a human-readable state");
            await Capture("05-pending");
            Reset(); ExamInput.Text = "DEMO004"; await Lookup(); SelectAll_Click(this, new RoutedEventArgs()); await ConfirmSelection();
            Require(!PrintButton.IsEnabled && PrinterStatus.Text.Contains("缺纸"), "paper-out disables submission even with preset acknowledgement");
            await Capture("06-paper-out");
            Reset(); ExamInput.Text = "DEMO005"; await Lookup();
            Require(step == KioskStep.Error && ErrorMessage.Text.Contains("重试"), "network error offers recovery guidance");
            await Capture("07-network-error");
            Reset(); Width = 1024; Height = 768;
            await Capture("08-small-screen");
            var buttonBounds = LookupButton.TransformToAncestor(WelcomePanel).TransformBounds(new Rect(0, 0, LookupButton.ActualWidth, LookupButton.ActualHeight));
            Require(buttonBounds.Top >= 0 && buttonBounds.Bottom <= WelcomePanel.ActualHeight + 1, "primary lookup button stays visible on a 1024x768 screen");
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { success = true, checks = passed, realPrintingPerformed = false }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { success = false, checks = passed, error = ex.Message, realPrintingPerformed = false }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(1);
        }
    }
}
