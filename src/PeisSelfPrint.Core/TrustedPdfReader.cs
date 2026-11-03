using System.Net;

namespace PeisSelfPrint.Core;

public static class TrustedPdfReader
{
    // The supplied client must disable automatic redirects and must not carry credentials.
    public static async Task<byte[]> ReadAsync(HttpClient client, Uri initial, string[] origins,
        bool allowHttp, CancellationToken ct, int maximumBytes = 20 * 1024 * 1024)
    {
        var current = initial;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var hop = 0; hop <= 3; hop++)
        {
            current = ReportRules.ValidatePdfAddress(current.AbsoluteUri, origins, allowHttp);
            if (!visited.Add(current.AbsoluteUri)) throw new RuleException("PDF_REDIRECT_LOOP");
            using var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (hop == 3 || response.Headers.Location is null) throw new RuleException("PDF_REDIRECT_LIMIT_OR_INVALID");
                current = new Uri(current, response.Headers.Location);
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK) throw new RuleException("PDF_FETCH_FAILED");
            if (response.Content.Headers.ContentLength > maximumBytes) throw new RuleException("RESPONSE_TOO_LARGE");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) != 0)
            {
                if (output.Length + read > maximumBytes) throw new RuleException("RESPONSE_TOO_LARGE");
                output.Write(buffer, 0, read);
            }
            var bytes = output.ToArray();
            if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new RuleException("INVALID_PDF_CONTENT");
            return bytes;
        }
        throw new RuleException("PDF_REDIRECT_LIMIT_OR_INVALID");
    }
}
