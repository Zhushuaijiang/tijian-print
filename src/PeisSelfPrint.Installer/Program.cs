using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
namespace PeisSelfPrint.Installer;

// 体检报告自助打印 - 一键安装器
// 双击运行（管理员）：解压内嵌离线包 -> 修复配置路径 -> 桌面快捷方式 -> 开机自启 -> 可选立即启动。
// 支持静默模式便于自动化验收：--silent --dir "D:\体检自助机" [--no-shortcut] [--no-autostart] [--no-launch]
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var silent = args.Contains("--silent", StringComparer.OrdinalIgnoreCase);
        if (silent) return RunSilent(args);
        ApplicationConfiguration.Initialize();
        Application.Run(new InstallForm());
        return 0;
    }

    private static int RunSilent(string[] args)
    {
        string? dir = null;
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], "--dir", StringComparison.OrdinalIgnoreCase)) dir = args[i + 1];
        var target = dir ?? DefaultTarget();
        var shortcut = !HasFlag(args, "--no-shortcut");
        var autostart = !HasFlag(args, "--no-autostart");
        var launch = !HasFlag(args, "--no-launch");
        try
        {
            var report = InstallerCore.Install(target, shortcut, autostart, launch, _ => { });
            foreach (var line in report) Console.WriteLine(line);
            Console.WriteLine("RESULT=OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR=" + ex.Message);
            return 1;
        }
    }

    private static bool HasFlag(string[] args, string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

    internal static string DefaultTarget()
    {
        var d = Path.GetPathRoot(Path.GetFullPath("D:\\")) ?? "C:\\";
        return Directory.Exists(Path.GetFullPath("D:\\")) ? Path.Combine("D:\\", "体检自助机") : Path.Combine("C:\\", "体检自助机");
    }
}

// 全部安装逻辑（GUI 与静默共用），返回逐行结果便于验收。
internal static class InstallerCore
{
    private const string ShortcutName = "体检报告自助打印.lnk";
    private const string TaskName = "PeisKioskAutoStart";

    internal static List<string> Install(string target, bool shortcut, bool autostart, bool launch, Action<(int done, int total)> progress)
    {
        var report = new List<string>();
        target = Path.GetFullPath(target);
        Directory.CreateDirectory(target);

        // 1) 停止可能在运行的程序（覆盖部署需要）
        foreach (var name in new[] { "PeisSelfPrint.Desktop", "PeisSelfPrint.Api" })
            foreach (var p in Process.GetProcessesByName(name)) { try { p.Kill(); p.WaitForExit(5000); } catch (Win32Exception) { } catch (InvalidOperationException) { } }

        // 2) 解压内嵌离线包（含 .NET 运行时，目标机零依赖）
        using (var stream = typeof(InstallerCore).Assembly.GetManifestResourceStream("payload.zip") ?? throw new InvalidOperationException("安装包内嵌数据缺失"))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            var entries = zip.Entries;
            var done = 0;
            foreach (var entry in entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrEmpty(name)) continue;
                var dest = Path.GetFullPath(Path.Combine(target, name));
                if (!dest.StartsWith(target, StringComparison.OrdinalIgnoreCase)) continue; // 防路径穿越
                if (name.EndsWith("/")) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                done++;
                if (done % 20 == 0) progress((done, entries.Count));
            }
            progress((entries.Count, entries.Count));
            report.Add("EXTRACTED=" + entries.Count + " -> " + target);
        }

        // 3) 修复打印引擎路径（换机部署绝对路径失效问题）
        var settingsPath = Path.Combine(target, "Desktop", "kiosk.settings.json");
        if (File.Exists(settingsPath))
        {
            var engine = Path.Combine(target, "Tools", "SumatraPDF.exe");
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject();
            if (node is not null)
            {
                node["PdfEnginePath"] = engine;
                File.WriteAllText(settingsPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                report.Add("PDF_ENGINE=" + engine);
            }
        }

        var startScript = Path.Combine(target, "Start-Debug.ps1");
        if (!File.Exists(startScript)) throw new InvalidOperationException("安装包不完整：缺少 Start-Debug.ps1");

        // 4) 桌面快捷方式（公共桌面，所有账户可见）
        if (shortcut)
        {
            var publicDesktop = Environment.GetEnvironmentVariable("PUBLIC") is { Length: > 0 } pub
                ? Path.Combine(pub, "Desktop")
                : Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            var lnk = Path.Combine(publicDesktop, ShortcutName);
            var ps = "$ws = New-Object -ComObject WScript.Shell; " +
                     "$s = $ws.CreateShortcut('" + lnk.Replace("'", "''") + "'); " +
                     "$s.TargetPath = 'powershell.exe'; " +
                     "$s.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"' + '" + startScript.Replace("'", "''") + "' + '\"'; " +
                     "$s.WorkingDirectory = '" + target.Replace("'", "''") + "'; " +
                     "$s.IconLocation = '" + Path.Combine(target, "Desktop", "PeisSelfPrint.Desktop.exe").Replace("'", "''") + ",0'; " +
                     "$s.Description = '体检报告自助打印'; " +
                     "$s.Save()";
            RunPowerShell(ps);
            report.Add("SHORTCUT=" + lnk);
        }

        // 5) 开机自启（登录即启动，计划任务）
        if (autostart)
        {
            var run = "powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + startScript + "\"";
            var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "/create", "/tn", TaskName, "/tr", run, "/sc", "onlogon", "/rl", "highest", "/f" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("schtasks 启动失败");
            p.WaitForExit(20000);
            report.Add(p.ExitCode == 0 ? "AUTOSTART=" + TaskName : "AUTOSTART=FAILED(" + p.ExitCode + ")，可手动创建计划任务");
        }

        // 6) 立即启动
        if (launch)
        {
            RunPowerShell("-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + startScript + "\"", waitForExit: false);
            report.Add("LAUNCH=start issued (Start-Debug.ps1)");
        }
        return report;
    }

    private static void RunPowerShell(string arguments, bool waitForExit = true)
    {
        // arguments 以 -File / -Command 形式给出时拆分为 ArgumentList，避免嵌套引号转义问题
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        if (waitForExit)
        {
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(arguments);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("powershell 启动失败");
            if (!p.WaitForExit(30000)) { try { p.Kill(); } catch (InvalidOperationException) { } throw new InvalidOperationException("powershell 执行超时"); }
            if (p.ExitCode != 0) throw new InvalidOperationException("powershell 执行失败（退出码 " + p.ExitCode + "）");
        }
        else
        {
            foreach (var part in SplitArguments(arguments)) psi.ArgumentList.Add(part);
            Process.Start(psi);
        }
    }

    private static IEnumerable<string> SplitArguments(string command)
    {
        // 仅用于本安装器固定形态的命令行拆分（含双引号段）
        var parts = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        foreach (var ch in command)
        {
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (ch == ' ' && !inQuote) { if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); } continue; }
            sb.Append(ch);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }
}

internal sealed class InstallForm : Form
{
    private readonly TextBox dirBox = new() { Font = new Font("Microsoft YaHei UI", 12F), Height = 34, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly CheckBox shortcutCheck = new() { Text = "创建桌面快捷方式", Checked = true, Font = new Font("Microsoft YaHei UI", 11F), AutoSize = true };
    private readonly CheckBox autostartCheck = new() { Text = "开机自动启动（登录后自动运行）", Checked = true, Font = new Font("Microsoft YaHei UI", 11F), AutoSize = true };
    private readonly CheckBox launchCheck = new() { Text = "安装完成后立即启动", Checked = true, Font = new Font("Microsoft YaHei UI", 11F), AutoSize = true };
    private readonly Button browseButton = new() { Text = "浏览…", Font = new Font("Microsoft YaHei UI", 11F), AutoSize = true };
    private readonly Button installButton = new() { Text = "一键安装", Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold), Height = 52, BackColor = Color.FromArgb(8, 126, 139), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly ProgressBar bar = new() { Height = 26, Anchor = AnchorStyles.Left | AnchorStyles.Right, Minimum = 0, Maximum = 100 };
    private readonly Label statusLabel = new() { Text = "准备就绪。目标机无需安装任何运行环境，直接点击「一键安装」。", Font = new Font("Microsoft YaHei UI", 10.5F), ForeColor = Color.FromArgb(84, 113, 127), AutoSize = false, Height = 60, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };

    public InstallForm()
    {
        Text = "体检报告自助打印 - 一键安装";
        Font = new Font("Microsoft YaHei UI", 11F);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 470);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        var title = new Label { Text = "体检报告自助打印系统", Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold), AutoSize = true, Location = new Point(24, 22) };
        var subtitle = new Label { Text = "含全部程序、.NET 运行环境与打印引擎（约 120MB），安装后开箱即用。", Font = new Font("Microsoft YaHei UI", 10.5F), ForeColor = Color.FromArgb(90, 110, 122), AutoSize = true, Location = new Point(24, 62) };
        var dirLabel = new Label { Text = "安装目录：", AutoSize = true, Location = new Point(24, 108) };
        dirBox.Text = Program.DefaultTarget();
        dirBox.Location = new Point(24, 132); dirBox.Width = 560;
        browseButton.Location = new Point(596, 130);
        var options = new FlowLayoutPanel { Location = new Point(24, 190), AutoSize = true, FlowDirection = FlowDirection.TopDown };
        options.Controls.AddRange(new Control[] { shortcutCheck, autostartCheck, launchCheck });
        installButton.Location = new Point(24, 300); installButton.Width = 632;
        bar.Location = new Point(24, 372); bar.Width = 632;
        statusLabel.Location = new Point(24, 404); statusLabel.Width = 632;

        Controls.AddRange(new Control[] { title, subtitle, dirLabel, dirBox, browseButton, options, installButton, bar, statusLabel });

        browseButton.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = dirBox.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) dirBox.Text = dialog.SelectedPath;
        };
        installButton.Click += async (_, _) => await InstallAsync();
    }

    private async Task InstallAsync()
    {
        var target = dirBox.Text.Trim();
        if (target.Length == 0 || target.IndexOfAny(Path.GetInvalidPathChars()) >= 0) { MessageBox.Show(this, "安装目录无效。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (Directory.Exists(Path.Combine(target, "Desktop")) && MessageBox.Show(this, "目录已存在安装内容，将覆盖安装（正在运行的程序会被停止）。继续？", "覆盖确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        installButton.Enabled = false; browseButton.Enabled = false; dirBox.Enabled = false;
        statusLabel.Text = "正在安装，请稍候（解压约需一两分钟）…";
        bar.Value = 2;
        try
        {
            var progress = new Progress<(int done, int total)>(v => { if (v.total > 0 && IsHandleCreated) bar.Value = Math.Min(98, 2 + v.done * 96 / v.total); });
            var report = await Task.Run(() => InstallerCore.Install(target, shortcutCheck.Checked, autostartCheck.Checked, launchCheck.Checked, ((IProgress<(int, int)>)progress).Report));
            bar.Value = 100;
            var lines = string.Join(Environment.NewLine, report);
            statusLabel.Text = "安装完成！" + Environment.NewLine + "打印机与数据服务配置：启动程序后，点击主界面右上角时间处连续 5 次调出配置窗口。";
            MessageBox.Show(this, "安装完成。" + Environment.NewLine + Environment.NewLine + lines + Environment.NewLine + Environment.NewLine + "双击桌面「体检报告自助打印」即可启动。" + Environment.NewLine + "打印机/数据库等配置：启动后在主界面右上角时间处连续点击 5 次打开配置窗口。", "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            bar.Value = 0;
            statusLabel.Text = "安装失败：" + ex.Message;
            MessageBox.Show(this, "安装失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { installButton.Enabled = true; browseButton.Enabled = true; dirBox.Enabled = true; }
    }
}
