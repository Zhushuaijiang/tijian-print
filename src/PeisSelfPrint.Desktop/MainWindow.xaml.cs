using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PeisSelfPrint.Core;

namespace PeisSelfPrint.Desktop;

public sealed class ReportChoice(KioskReport report) : INotifyPropertyChanged
{
    public KioskReport Report { get; } = report;
    public string Title => Report.Title;
    public string Detail => Report.CanPrint ? $"生成时间：{Report.ProducedAt}　·　{(Report.PageCount is > 0 ? Report.PageCount + " 页" : "页数以报告为准")}" : Report.UnavailableReason;
    public bool CanPrint => Report.CanPrint;
    public string StatusText => CanPrint ? "可领取" : "审核中";
    public Brush StatusColor => CanPrint ? Brushes.SeaGreen : Brushes.DarkGoldenrod;
    private bool selected;
    public bool Selected { get => selected; set { if (selected == value) return; selected = value && CanPrint; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private readonly KioskSettings settings;
    private readonly KioskReportClient reports;
    private readonly WindowsPrinterAdapter printer;
    private readonly PrintLedger ledger;
    private readonly KioskPrintCoordinator printing;
    private readonly KioskSessionClock session = new();
    private readonly ObservableCollection<ReportChoice> choices = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource operation = new();
    private KioskStep step;
    private KioskLookup? lookup;
    private DateTime lastActivity = DateTime.UtcNow;
    private DateTime resultAt;
    private int ticks;
    private bool healthChecking;
    private bool printerReady;
    private bool closed;
    private readonly bool smokeTest = Environment.GetCommandLineArgs().Contains("--demo-smoke-test", StringComparer.Ordinal);
    private readonly Brush active = new SolidColorBrush(Color.FromRgb(8, 126, 139));
    private readonly Brush muted = new SolidColorBrush(Color.FromRgb(123, 145, 157));

    public MainWindow()
    {
        InitializeComponent();
        settings = KioskSettings.Load();
        if (Environment.GetCommandLineArgs().Contains("--demo", StringComparer.Ordinal)) settings.Mode = "Demo";
        settings.IdleSeconds = Math.Clamp(settings.IdleSeconds, 30, 180);
        reports = new(settings); printer = new(settings);
        if (smokeTest && !settings.IsDemo) throw new InvalidOperationException("Demo smoke tests require Demo mode.");
        var state = smokeTest ? Path.Combine(Path.GetTempPath(), "PeisKioskSmoke-" + Guid.NewGuid().ToString("N")) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeisSelfPrint", settings.IsDemo ? "demo" : "live");
        ledger = new PrintLedger(Path.Combine(state, "print-ledger.json"));
        printing = new(ledger, printer, Path.Combine(state, "pdf-staging"));
        ClockText.Text = DateTime.Now.ToString("MM月dd日  HH:mm");
        HospitalLabel.Text = settings.HospitalName; SupportLabel.Text = settings.SupportText;
        if (settings.FullScreen) { WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
        ModeBadge.Visibility = settings.IsDemo ? Visibility.Visible : Visibility.Collapsed;
        DemoScenarios.Visibility = settings.IsDemo ? Visibility.Visible : Visibility.Collapsed;
        foreach (var key in "1234567890") AddKey(NumberKeys, key.ToString());
        foreach (var key in "ABCDEFGHIJKLMNOPQRSTUVWXYZ-") AddKey(LetterKeys, key.ToString());
        ReportsList.ItemsSource = choices;
        SizeChanged += (_, _) => ApplyCompactLayout();
        timer.Tick += Timer_Tick;
        Loaded += async (_, _) => { ApplyCompactLayout(); Reset(); timer.Start(); await CheckHealth(); if (smokeTest) await RunDemoSmokeTest(); };
        Closing += (_, e) => { if (step == KioskStep.Printing) { e.Cancel = true; return; } closed = true; operation.Cancel(); timer.Stop(); };
        Closed += (_, _) => { operation.Dispose(); reports.Dispose(); ledger.Dispose(); };
    }
    private void ApplyCompactLayout()
    {
        var compact = ActualHeight <= 820;
        ShellGrid.Margin = compact ? new Thickness(24, 16, 24, 16) : new Thickness(32, 22, 32, 22);
        HeaderRow.Height = new GridLength(compact ? 60 : 76); StepsRow.Height = new GridLength(compact ? 52 : 68);
        FooterRow.Height = new GridLength(compact ? 72 : 78); FooterGrid.Margin = new Thickness(0, compact ? 12 : 18, 0, 0);
        WelcomeCard.Padding = new Thickness(compact ? 20 : 32);
        WelcomeTitle.FontSize = compact ? 28 : 32; WelcomeSubtitle.FontSize = compact ? 17 : 19;
        WelcomeSubtitle.Margin = new Thickness(0, 0, 0, compact ? 16 : 24);
        InputLabel.FontSize = compact ? 17 : 19; ExamInput.Height = compact ? 56 : 64; ExamInput.FontSize = compact ? 26 : 29;
        InputHint.FontSize = compact ? 15 : 16; InputHint.Margin = new Thickness(0, compact ? 8 : 10, 0, compact ? 10 : 16);
        InputActions.Margin = new Thickness(0, 0, 0, compact ? 8 : 18);
        WelcomeInputScroll.Margin = new Thickness(0, 0, 0, compact ? 10 : 16);
        LookupButton.MinHeight = compact ? 60 : 68;
        foreach (var key in NumberKeys.Children.OfType<Button>()) { key.MinHeight = compact ? 44 : 50; key.Margin = new Thickness(0, 0, 8, compact ? 6 : 8); }
        foreach (var button in InputActions.Children.OfType<Button>()) { button.MinHeight = compact ? 44 : 56; button.FontSize = compact ? 18 : 21; }
    }
    private void AddKey(Panel panel, string text)
    {
        var key = new Button { Content = text, Tag = text, FontSize = 22, MinWidth = 40, MinHeight = 50, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(6), Style = (Style)Resources["SecondaryButton"] };
        key.Click += (_, _) => { InsertIntoFocused(text); };
        panel.Children.Add(key);
    }
    // The on-screen keypad feeds the exam-number field on the home page.
    private void InsertIntoFocused(string text)
    {
        var indexHome = Math.Min(ExamInput.CaretIndex, ExamInput.Text.Length);
        if (ExamInput.Text.Length < ExamInput.MaxLength) { ExamInput.Text = ExamInput.Text.Insert(indexHome, text); ExamInput.CaretIndex = indexHome + 1; }
        ExamInput.Focus();
    }
    private void Render(KioskStep next)
    {
        step = next;
        var panels = new Dictionary<KioskStep, UIElement> { [KioskStep.Welcome] = WelcomePanel, [KioskStep.Loading] = LoadingPanel,
            [KioskStep.Reports] = ReportsPanel, [KioskStep.Confirm] = ConfirmPanel, [KioskStep.Printing] = PrintingPanel,
            [KioskStep.Result] = ResultPanel, [KioskStep.Error] = ErrorPanel };
        foreach (var panel in panels) panel.Value.Visibility = panel.Key == next ? Visibility.Visible : Visibility.Collapsed;
        var n = next switch { KioskStep.Reports => 2, KioskStep.Confirm => 3, KioskStep.Printing or KioskStep.Result => 4, _ => 1 };
        var labels = new[] { Step1, Step2, Step3, Step4 };
        for (var i = 0; i < labels.Length; i++) { labels[i].Foreground = i < n ? active : muted; labels[i].FontWeight = i + 1 == n ? FontWeights.Bold : FontWeights.Normal; }
        HomeButton.IsEnabled = next != KioskStep.Printing;
        IdleOverlay.Visibility = Visibility.Collapsed;
        lastActivity = DateTime.UtcNow;
    }
    private void Reset()
    {
        if (step == KioskStep.Printing) return;
        session.Reset(); operation.Cancel(); operation.Dispose(); operation = new();
        _ = reports.EndSessionAsync(lookup);
        lookup = null; choices.Clear(); ExamInput.Clear(); ConfirmReports.Text = ""; ConfirmIdentity.Text = ""; PersonSummary.Text = "";
        Acknowledge.IsChecked = true; printerReady = false; HelpOverlay.Visibility = Visibility.Collapsed; LetterKeys.Visibility = Visibility.Collapsed;
        InputHint.Text = "号码开头的 0 请保留，输入完成后点击查询"; InputHint.Foreground = muted;
        Render(KioskStep.Welcome); ExamInput.Focus();
    }
    private async void Lookup_Click(object sender, RoutedEventArgs e) => await Lookup();
    private async Task Lookup()
    {
        if (step is KioskStep.Loading or KioskStep.Printing) return;
        string number;
        try { number = ReportRules.ValidateExamNumber(ExamInput.Text); }
        catch (RuleException) { InputHint.Text = KioskMessages.For("INVALID_EXAM_NUMBER"); InputHint.Foreground = Brushes.Firebrick; ExamInput.Focus(); return; }
        var version = session.Begin(); operation.Cancel(); operation.Dispose(); operation = new(); var ct = operation.Token;
        lookup = null; choices.Clear(); HelpOverlay.Visibility = Visibility.Collapsed; Render(KioskStep.Loading);
        try
        {
            var response = await reports.LookupAsync(number, ct);
            if (closed || !session.IsCurrent(version) || ct.IsCancellationRequested) return;
            if (response.Reports.Count == 0) throw new RuleException("REPORT_NOT_FOUND");
            lookup = response;
            foreach (var report in response.Reports) choices.Add(new(report));
            PersonSummary.Text = $"{response.DisplayName}　|　体检号：{response.ExamNumber}";
            ReportNotice.Text = response.IsDemo ? "当前为模拟报告，用于体验查询和打印流程，不会实际出纸。" : "请核对体检号。审核中的报告暂不可领取。";
            Render(KioskStep.Reports); UpdateSelection();
        }
        catch (OperationCanceledException) { if (!ct.IsCancellationRequested && session.IsCurrent(version)) Error("NETWORK_UNAVAILABLE"); }
        catch (RuleException ex) { if (session.IsCurrent(version)) Error(ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or IOException) { if (session.IsCurrent(version)) Error("NETWORK_UNAVAILABLE"); }
    }
    private void Error(string code, string? message = null)
    {
        ErrorTitle.Text = code == "REPORT_PENDING" ? "报告正在审核" : code is "REGISTRATION_NOT_FOUND" or "REPORT_NOT_FOUND" ? "暂未找到可领取报告" : code.StartsWith("PRINT", StringComparison.Ordinal) ? "打印需要工作人员协助" : "暂时无法完成查询";
        ErrorMessage.Text = message ?? KioskMessages.For(code); ErrorCodeText.Text = "服务提示：" + code;
        RetryButton.Visibility = code.StartsWith("PRINT", StringComparison.Ordinal) || code == "IDENTITY_REQUIRED" ? Visibility.Collapsed : Visibility.Visible;
        Render(KioskStep.Error);
    }
    private void UpdateSelection()
    {
        var selected = choices.Where(c => c.Selected).ToArray();
        var pages = selected.All(c => c.Report.PageCount is > 0) ? $" · 约 {selected.Sum(c => c.Report.PageCount ?? 0)} 页" : "";
        SelectionText.Text = $"已选 {selected.Length} 份" + (selected.Length > 0 ? pages : "");
        ContinueButton.IsEnabled = selected.Length is > 0 and <= 10;
        if (selected.Length > 10) SelectionText.Text = "每次最多选择 10 份，请减少选择";
    }
    private void Selection_Changed(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(new Action(UpdateSelection));
    private void SelectAll_Click(object sender, RoutedEventArgs e) { var all = choices.Where(c => c.CanPrint).All(c => c.Selected); foreach (var c in choices) c.Selected = !all && c.CanPrint && choices.Where(x => x.CanPrint).Take(10).Contains(c); UpdateSelection(); }
    private async void Confirm_Click(object sender, RoutedEventArgs e) => await ConfirmSelection();
    private async Task ConfirmSelection()
    {
        if (lookup is null || !choices.Any(c => c.Selected)) return;
        var version = session.Current;
        ConfirmIdentity.Text = $"体检号：{lookup.ExamNumber}　　{lookup.DisplayName}";
        ConfirmReports.Text = string.Join("\n", choices.Where(c => c.Selected).Select((c, i) => $"{i + 1}. {c.Title}　{(c.Report.PageCount is > 0 ? c.Report.PageCount + " 页" : "")}\n    {c.Report.ProducedAt}"));
        PrintButton.Content = lookup.IsDemo ? "确认模拟打印" : "确认打印";
        Acknowledge.IsChecked = true; printerReady = false; PrintButton.IsEnabled = false;
        PrinterStatus.Text = "正在检查打印设备…"; ConfirmHint.Text = lookup.IsDemo ? "演示仅展示处理过程，不连接真实打印机。" : "请确认领取本人报告；打印发送后请勿重复点击。";
        Render(KioskStep.Confirm);
        if (lookup.IsDemo)
        {
            printerReady = lookup.ExamNumber != "DEMO004";
            PrinterStatus.Text = printerReady ? "● 模拟打印设备已就绪" : "● 模拟故障：打印机缺纸，请联系工作人员加纸。";
        }
        else
        {
            try
            {
                var readiness = await printer.CheckAsync(operation.Token);
                if (!session.IsCurrent(version) || step != KioskStep.Confirm || lookup is null) return;
                printerReady = readiness.Ready;
                PrinterStatus.Text = readiness.Message;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { if (!session.IsCurrent(version)) return; PrinterStatus.Text = KioskMessages.For("PRINTER_NOT_READY"); }
        }
        UpdatePrintButton();
    }
    private void UpdatePrintButton() => PrintButton.IsEnabled = step == KioskStep.Confirm && printerReady && Acknowledge.IsChecked == true;
    private void Acknowledge_Changed(object sender, RoutedEventArgs e) { if (PrintButton is not null) UpdatePrintButton(); }
    private void BackToReports(object sender, RoutedEventArgs e) { Render(KioskStep.Reports); UpdateSelection(); }
    private async void Print_Click(object sender, RoutedEventArgs e) => await SubmitSelection();
    private async Task SubmitSelection()
    {
        if (lookup is null || step != KioskStep.Confirm || !printerReady || Acknowledge.IsChecked != true) return;
        var current = lookup; var selected = choices.Where(c => c.Selected).Select(c => c.Report).ToArray();
        Render(KioskStep.Printing); PrintProgressBar.Value = 0;
        PrintingTitle.Text = current.IsDemo ? "正在演示报告处理过程" : "正在发送报告";
        PrintingDetail.Text = "请稍候";
        var progress = new Progress<PrintProgress>(p => { if (closed || step != KioskStep.Printing) return; PrintProgressBar.Value = 100.0 * p.Completed / p.Total; PrintingDetail.Text = p.Message; });
        try
        {
            var prepared = await reports.PrepareForPrintAsync(current, selected, operation.Token);
            var outcome = await printing.PrintAsync(prepared, prepared.Reports.Where(r => selected.Any(s => s.Id == r.Id)).ToArray(), progress, operation.Token);
            if (closed) return;
            ResultTitle.Text = outcome == PrintOutcome.Simulated ? "演示流程已完成" : "报告已发送到打印机";
            ResultMessage.Text = outcome == PrintOutcome.Simulated ? $"已模拟处理 {selected.Length} 份报告。\n本次没有实际打印，也没有使用真实体检数据。" : $"已提交 {selected.Length} 份报告，请在出纸口领取。\n如未出纸，请联系工作人员，不要重复打印。";
            Render(KioskStep.Result); resultAt = DateTime.UtcNow;
        }
        catch (PrintBatchException ex) { Error("PRINT_PARTIAL", $"已有 {ex.SubmittedCount} 份报告发送到打印机，其余报告未能确认完成。请联系工作人员核对出纸情况，不要整批重打。"); }
        catch (RuleException ex) { Error(ex.Message); }
        catch (Exception) { Error("PRINT_RESULT_UNKNOWN"); }
    }
    private async Task CheckHealth()
    {
        if (healthChecking || closed) return; healthChecking = true;
        try { var ready = await reports.HealthAsync(CancellationToken.None); if (closed) return; ServiceStatus.Text = settings.IsDemo ? "● 演示环境 · 所有数据均为模拟" : ready ? "● 报告服务已连接" : "● 报告服务暂不可用"; ServiceStatus.Foreground = ready ? Brushes.SeaGreen : Brushes.DarkGoldenrod; }
        finally { healthChecking = false; }
    }
    private async void Timer_Tick(object? sender, EventArgs e)
    {
        ClockText.Text = DateTime.Now.ToString("MM月dd日  HH:mm");
        if (++ticks % 20 == 0 && step is KioskStep.Welcome or KioskStep.Error) await CheckHealth();
        if (step == KioskStep.Printing) return;
        if (step == KioskStep.Result)
        {
            var remain = Math.Max(0, 20 - (int)(DateTime.UtcNow - resultAt).TotalSeconds);
            ResultCountdown.Text = $"{remain} 秒后自动返回首页并清除本次信息";
            if (remain == 0) Reset(); return;
        }
        var left = settings.IdleSeconds - (int)(DateTime.UtcNow - lastActivity).TotalSeconds;
        if (left <= 0) { Reset(); return; }
        if (step != KioskStep.Welcome || ExamInput.Text.Length > 0)
        {
            IdleOverlay.Visibility = left <= 15 ? Visibility.Visible : Visibility.Collapsed;
            IdleMessage.Text = $"为保护您的信息，{left} 秒后将自动清屏。";
        }
    }
    private void UserActivity(object sender, InputEventArgs e) { lastActivity = DateTime.UtcNow; }
    // 运维暗门：右上角时间栏连续点击 5 次（间隔不超过 2 秒）打开配置窗口。
    private int gateClicks;
    private DateTime gateLast = DateTime.MinValue;
    private bool RegisterGateClick()
    {
        var now = DateTime.UtcNow;
        gateClicks = now - gateLast <= TimeSpan.FromSeconds(2) ? gateClicks + 1 : 1;
        gateLast = now;
        if (gateClicks < 5) return false;
        gateClicks = 0;
        return true;
    }
    private void ClockGate_Click(object sender, RoutedEventArgs e) { if (RegisterGateClick()) OpenConfig(); }
    private async void OpenConfig()
    {
        lastActivity = DateTime.UtcNow;
        var version = session.Current;
        var config = new KioskConfigWindow(settings, printer) { Owner = this };
        config.ShowDialog();
        if (closed) return;
        if (step == KioskStep.Confirm && lookup is not null && !lookup.IsDemo)
        {
            printerReady = false; PrintButton.IsEnabled = false; PrinterStatus.Text = "正在检查打印设备…";
            try
            {
                var readiness = await printer.CheckAsync(CancellationToken.None);
                if (!closed && step == KioskStep.Confirm && session.IsCurrent(version)) { printerReady = readiness.Ready; PrinterStatus.Text = readiness.Message; }
            }
            catch (OperationCanceledException) { }
            UpdatePrintButton();
        }
    }
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        lastActivity = DateTime.UtcNow;
        if (e.Key == Key.F11) { WindowStyle = WindowStyle == WindowStyle.None ? WindowStyle.SingleBorderWindow : WindowStyle.None; WindowState = WindowStyle == WindowStyle.None ? WindowState.Maximized : WindowState.Normal; e.Handled = true; }
        else if (e.Key == Key.F1) { HelpOverlay.Visibility = Visibility.Visible; e.Handled = true; }
        else if (e.Key == Key.Escape && step != KioskStep.Printing) { Reset(); e.Handled = true; }
        else if (e.Key == Key.Enter && step == KioskStep.Welcome) { e.Handled = true; await Lookup(); }    }
    private void ExamInput_Changed(object sender, TextChangedEventArgs e) { lastActivity = DateTime.UtcNow; if (InputHint is not null) { InputHint.Text = "号码开头的 0 请保留，输入完成后点击查询"; InputHint.Foreground = muted; } }
    private void ToggleLetters(object sender, RoutedEventArgs e) => LetterKeys.Visibility = LetterKeys.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    private void Backspace(object sender, RoutedEventArgs e) { var index = Math.Min(ExamInput.CaretIndex, ExamInput.Text.Length); if (index > 0) { ExamInput.Text = ExamInput.Text.Remove(index - 1, 1); ExamInput.CaretIndex = index - 1; } ExamInput.Focus(); }
    private void ClearInput(object sender, RoutedEventArgs e) { ExamInput.Clear(); ExamInput.Focus(); }
    private async void Demo_Click(object sender, RoutedEventArgs e) { ExamInput.Text = ((Button)sender).Tag.ToString() ?? ""; await Lookup(); }
    private void Home_Click(object sender, RoutedEventArgs e) => Reset();
    private void Help_Click(object sender, RoutedEventArgs e) { SupportLabel.Text = settings.SupportText; HelpOverlay.Visibility = Visibility.Visible; }
    private void CloseHelp_Click(object sender, RoutedEventArgs e) { HelpOverlay.Visibility = Visibility.Collapsed; lastActivity = DateTime.UtcNow; }
    private void ContinueSession_Click(object sender, RoutedEventArgs e) { lastActivity = DateTime.UtcNow; IdleOverlay.Visibility = Visibility.Collapsed; }
}
