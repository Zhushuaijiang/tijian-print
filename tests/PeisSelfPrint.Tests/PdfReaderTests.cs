using System.Net;
using PeisSelfPrint.Core;

static class PdfReaderTests
{
    public static void Run()
    {
        var initial = new Uri("http://reports.example.invalid:8230/viewer/test?_type=pdf&pageOffset=-2");
        string[] allowed = ["http://reports.example.invalid:8230", "http://reports.example.invalid:8240"];
        using var handler = new Fake(request => request.RequestUri!.Port == 8230
            ? Redirect("http://reports.example.invalid:8240/home/getpdf/test") : Pdf());
        using var client = new HttpClient(handler);
        var bytes = TrustedPdfReader.ReadAsync(client, initial, allowed, true, CancellationToken.None).GetAwaiter().GetResult();
        if (bytes.Length < 5 || handler.Calls != 2) throw new Exception("Expected validated cross-port redirect");
        using var deniedHandler = new Fake(_ => Redirect("http://not-allowed.example.invalid/secret"));
        using var denied = new HttpClient(deniedHandler);
        Reject("PDF_ADDRESS_DENIED", () => TrustedPdfReader.ReadAsync(denied, initial, allowed, true, CancellationToken.None).GetAwaiter().GetResult());
        if (deniedHandler.Calls != 1) throw new Exception("Forbidden redirect was requested");
        using var loop = new HttpClient(new Fake(_ => Redirect(initial.AbsoluteUri)));
        Reject("PDF_REDIRECT_LOOP", () => TrustedPdfReader.ReadAsync(loop, initial, allowed, true, CancellationToken.None).GetAwaiter().GetResult());
        using var endless = new HttpClient(new Fake(r => Redirect("http://reports.example.invalid:8230/" + Guid.NewGuid())));
        Reject("PDF_REDIRECT_LIMIT_OR_INVALID", () => TrustedPdfReader.ReadAsync(endless, initial, allowed, true, CancellationToken.None).GetAwaiter().GetResult());
        using var html = new HttpClient(new Fake(_ => new(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") }));
        Reject("INVALID_PDF_CONTENT", () => TrustedPdfReader.ReadAsync(html, initial, allowed, true, CancellationToken.None).GetAwaiter().GetResult());
        using var large = new HttpClient(new Fake(_ => Pdf()));
        Reject("RESPONSE_TOO_LARGE", () => TrustedPdfReader.ReadAsync(large, initial, allowed, true, CancellationToken.None, 4).GetAwaiter().GetResult());
    }
    private static HttpResponseMessage Pdf() => new(HttpStatusCode.OK) { Content = new StringContent("%PDF-test") };
    private static HttpResponseMessage Redirect(string location)
    {
        var result = new HttpResponseMessage(HttpStatusCode.Found);
        result.Headers.Location = new Uri(location);
        return result;
    }
    private static void Reject(string expected, Action action)
    {
        try { action(); } catch (RuleException e) when (e.Message == expected) { return; }
        throw new Exception("Expected " + expected);
    }
    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            return Task.FromResult(reply(request));
        }
    }
}
