using PeisSelfPrint.Core;

var tests = new (string Name, Action Run)[]
{
    ("自助机会话隔离、报告多选、打印防重与异常恢复", KioskTests.Run),
    ("受控跨端口PDF跳转及越界循环限次内容检查", PdfReaderTests.Run),
    ("接受前导零且不改写体检号", () => Equal("000123-A", ReportRules.ValidateExamNumber("000123-A"))),
    ("身份证后六位只接受六位数字或X并统一大写", () =>
    {
        Equal("12345X", ReportRules.ValidateIdCardSuffix("12345x"));
        Equal("000000", ReportRules.ValidateIdCardSuffix("000000"));
        foreach (var value in new string?[] { null, "", "12345", "1234567", "12345Y", "1234 56", "１２３４５６" })
            Reject("INVALID_ID_SUFFIX", () => ReportRules.ValidateIdCardSuffix(value));
    }),
    ("身份证后六位比对忽略空格且不匹配即拒绝", () =>
    {
        Equal(true, ReportRules.MatchesIdCardSuffix("65010219900101123X", "01123X"));
        Equal(true, ReportRules.MatchesIdCardSuffix("110101 19900101 1234", "011234"));
        Equal(false, ReportRules.MatchesIdCardSuffix("650102199001011234", "01123X"));
        Equal(false, ReportRules.MatchesIdCardSuffix("12345", "12345"));
        Equal(false, ReportRules.MatchesIdCardSuffix(null, "011234"));
    }),
    ("拒绝空输入、空白、SQL 字符与超长输入", () =>
    {
        foreach (var value in new string?[] { null, "", " ", "123\n", "' OR 1=1--", "中文", new('1', 65) })
            Reject("INVALID_EXAM_NUMBER", () => ReportRules.ValidateExamNumber(value));
    }),
    ("精确匹配且不取返回列表首项", () => Equal("right", ReportRules.SelectExact("000123", [Report("123", "wrong"), Report("000123", "right")]).ReportId)),
    ("拒绝大小写和前导零混淆", () => { Reject("REPORT_NOT_FOUND", () => ReportRules.SelectExact("ABC", [Report("abc", "x")])); Reject("REPORT_NOT_FOUND", () => ReportRules.SelectExact("000123", [Report("123", "x")])); }),
    ("同一体检号多报告必须拒绝", () => Reject("AMBIGUOUS_REPORT", () => ReportRules.SelectExact("123", [Report("123", "one"), Report("123", "two")]))),
    ("重复行也必须拒绝而不隐式去重", () => Reject("AMBIGUOUS_REPORT", () => ReportRules.SelectExact("123", [Report("123", "one"), Report("123", "one")]))),
    ("缺失报告标识必须拒绝", () => Reject("INVALID_REPORT", () => ReportRules.SelectExact("123", [Report("123", "")]))),
    ("PDF 可信域名精确匹配", () => Equal("reports.example.invalid", ReportRules.ValidatePdfAddress("https://reports.example.invalid/a.pdf", ["https://reports.example.invalid"]).Host)),
    ("拒绝常见 SSRF 与相似域名", () =>
    {
        foreach (var address in new[] { "file:///etc/passwd", "http://reports.example.invalid/a.pdf", "https://127.0.0.1/a.pdf", "https://reports.example.invalid.evil.invalid/a.pdf", "https://reports.example.invalid@evil.invalid/a.pdf", "https://reports.example.invalid:8443/a.pdf", "https://reports.example.invalid/a.pdf#x", "/a.pdf" })
            Reject("PDF_ADDRESS_DENIED", () => ReportRules.ValidatePdfAddress(address, ["https://reports.example.invalid"]));
    }),
    ("空 PDF 白名单关闭链路", () => Reject("PDF_ADDRESS_DENIED", () => ReportRules.ValidatePdfAddress("https://reports.example.invalid/a.pdf", []))),
    ("并发只允许一个任务预留", () =>
    {
        using var storage = new TestLedger(); var ledger = storage.Ledger; var accepted = 0;
        Parallel.For(0, 200, _ => { if (ledger.TryReserve("r1")) Interlocked.Increment(ref accepted); });
        Equal(1, accepted);
    }),
    ("未知状态不允许重试", () =>
    {
        using var storage = new TestLedger(); var ledger = storage.Ledger; ledger.TryReserve("r1"); ledger.BeginDispatch("r1"); ledger.MarkUnknown("r1");
        Equal(PrintState.Unknown, ledger.Get("r1")!.Value); Equal(false, ledger.TryReserve("r1"));
        Reject("INVALID_PRINT_TRANSITION", () => ledger.BeginDispatch("r1"));
    }),
    ("提交不等于完成且禁止重复提交", () =>
    {
        using var storage = new TestLedger(); var ledger = storage.Ledger; ledger.TryReserve("r1");
        Reject("INVALID_PRINT_TRANSITION", () => ledger.MarkSubmitted("r1"));
        ledger.BeginDispatch("r1"); ledger.MarkSubmitted("r1"); Equal(PrintState.Submitted, ledger.Get("r1")!.Value);
        Equal(false, ledger.TryReserve("r1")); Reject("INVALID_PRINT_TRANSITION", () => ledger.MarkUnknown("r1"));
    }),
    ("外部引擎参数不经过 shell 且打印机固定为单独参数", () =>
    {
        var root = Path.GetPathRoot(Path.GetFullPath("."))!;
        var command = ExternalPdfEngine.CreateSumatraCommand(Path.Combine(root, "Program Files", "SumatraPDF.exe"), "院内打印机 & other", Path.Combine(root, "stage dir", "test.pdf"));
        Equal(false, command.UseShellExecute); Equal(6, command.ArgumentList.Count);
        Equal("院内打印机 & other", command.ArgumentList[1]); Equal("-print-to", command.ArgumentList[0]);
    }),
    ("缺少打印引擎配置拒绝生成命令", () => Reject("INVALID_ENGINE_CONFIGURATION", () => ExternalPdfEngine.CreateSumatraCommand("", "printer", Path.GetFullPath("test.pdf")))),
    ("HTTP 必须显式允许且 origin 完全一致", () =>
    {
        const string address = "http://192.0.2.10:8080/viewer/model?_type=pdf&id=synthetic&pageOffset=-2";
        string[] origins = ["http://192.0.2.10:8080"];
        Reject("PDF_ADDRESS_DENIED", () => ReportRules.ValidatePdfAddress(address, origins));
        Equal(address, ReportRules.ValidatePdfAddress(address, origins, true).AbsoluteUri);
        Reject("PDF_ADDRESS_DENIED", () => ReportRules.ValidatePdfAddress(address, ["http://192.0.2.10:8081"], true));
        Reject("PDF_ADDRESS_DENIED", () => ReportRules.ValidatePdfAddress(address, ["https://192.0.2.10:8080"], true));
        Reject("PDF_ADDRESS_DENIED", () => ReportRules.ValidatePdfAddress(address, ["http://192.0.2.10:8080/viewer"], true));
    }),
    ("支持裸数组与成功包装且保留 PDF query", () =>
    {
        const string rows = "[{\"peisNo\":\"0001\",\"peisPersonalReportId\":17,\"peisReportAdress\":\"http://192.0.2.10:8080/viewer/x?_type=pdf&pageOffset=-2\"}]";
        foreach (var json in new[] { rows, "{\"code\":200,\"data\":" + rows + "}" })
        {
            var selected = ReportRules.SelectExact("0001", ReportRules.ParseReportList(System.Text.Encoding.UTF8.GetBytes(json)));
            Equal("17", selected.ReportId); Equal(true, selected.Address.EndsWith("&_type=pdf&pageOffset=-2") || selected.Address.EndsWith("?_type=pdf&pageOffset=-2"));
        }
        foreach (var json in new[] { "{\"code\":500,\"data\":[]}", "{\"data\":[]}", "[null]", "{}" })
            Reject("UPSTREAM_INVALID_RESPONSE", () => ReportRules.ParseReportList(System.Text.Encoding.UTF8.GetBytes(json)));
    }),
    ("重启将调度中恢复为未知且不重打", () =>
    {
        using var storage = new TestLedger();
        storage.Ledger.TryReserve("r1"); storage.Ledger.BeginDispatch("r1"); storage.Ledger.Dispose();
        using (var reopened = new PrintLedger(storage.Path))
        {
            Equal(PrintState.Unknown, reopened.Get("r1")!.Value); Equal(false, reopened.TryReserve("r1"));
            Reject("INVALID_PRINT_TRANSITION", () => reopened.BeginDispatch("r1"));
        }
        using var again = new PrintLedger(storage.Path);
        Equal(PrintState.Unknown, again.Get("r1")!.Value);
    }),
    ("重启保留预留与已提交状态", () =>
    {
        using var storage = new TestLedger(); storage.Ledger.TryReserve("reserved"); storage.Ledger.TryReserve("submitted");
        storage.Ledger.BeginDispatch("submitted"); storage.Ledger.MarkSubmitted("submitted"); storage.Ledger.Dispose();
        using var reopened = new PrintLedger(storage.Path);
        Equal(false, reopened.TryReserve("reserved")); Equal(false, reopened.TryReserve("submitted"));
        Equal(PrintState.Reserved, reopened.Get("reserved")!.Value); Equal(PrintState.Submitted, reopened.Get("submitted")!.Value);
    }),
    ("同一账本只能有一个活动实例", () =>
    {
        using var storage = new TestLedger();
        try { using var duplicate = new PrintLedger(storage.Path); }
        catch (IOException) { return; }
        throw new Exception("账本没有取得独占文件锁");
    }),
    ("损坏账本失败关闭且不重置历史", () =>
    {
        using var storage = new TestLedger(); storage.Ledger.Dispose(); File.WriteAllText(storage.Path, "corrupt");
        try { using var reopened = new PrintLedger(storage.Path); }
        catch (System.Text.Json.JsonException) { Equal("corrupt", File.ReadAllText(storage.Path)); return; }
        throw new Exception("损坏账本被静默接受");
    }),
    ("持久化失败后整个账本禁止继续调度", () =>
    {
        using var storage = new TestLedger(); storage.Ledger.TryReserve("r1");
        Directory.CreateDirectory(storage.Path + ".tmp");
        try { storage.Ledger.BeginDispatch("r1"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Reject("LEDGER_UNAVAILABLE", () => storage.Ledger.TryReserve("r2"));
            return;
        }
        throw new Exception("预期持久化失败");
    })
};
var failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"通过：{name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"失败：{name}；{ex.Message}"); }
}
Console.WriteLine($"共 {tests.Length} 项，通过 {tests.Length - failures} 项，失败 {failures} 项。测试均使用合成标识，不查询患者、不出纸。");
return failures == 0 ? 0 : 1;

static ReportCandidate Report(string number, string id) => new(number, id, "https://reports.example.invalid/a.pdf", "测试类型", "测试时间");
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"预期 {expected}，实际 {actual}"); }
static void Reject(string code, Action action)
{
    try { action(); }
    catch (RuleException ex) when (ex.Message == code) { return; }
    throw new Exception($"预期拒绝代码：{code}");
}

sealed class TestLedger : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PeisSelfPrintTests-" + Guid.NewGuid().ToString("N"));
    public string Path { get; }
    public PrintLedger Ledger { get; }
    public TestLedger() { Path = System.IO.Path.Combine(directory, "ledger.json"); Ledger = new PrintLedger(Path); }
    public void Dispose() { Ledger.Dispose(); Directory.Delete(directory, recursive: true); }
}
