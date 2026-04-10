using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace CiteCheck.Grobid
{
   public sealed class GrobidClient : IDisposable
   {
      private readonly HttpClient _http;

      public Uri BaseUri { get; }

      public GrobidClient(string baseUrl, TimeSpan? timeout = null)
      {
         if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("baseUrl is empty", nameof(baseUrl));

         BaseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);

         _http = new HttpClient
         {
            BaseAddress = BaseUri,
            Timeout = timeout ?? TimeSpan.FromMinutes(5)
         };
      }

      public void Dispose() => _http.Dispose();

      public async Task<bool> IsAliveAsync(CancellationToken ct = default)
      {
         using var req = new HttpRequestMessage(HttpMethod.Get, "api/isalive");
         using var res = await _http.SendAsync(req, ct);
         return res.IsSuccessStatusCode;
      }

      public async Task<string> ProcessReferencesAsync(
         string pdfPath,
         int consolidateCitations = 0,
         bool includeRawCitations = false,
         CancellationToken ct = default)
      {
         if (string.IsNullOrWhiteSpace(pdfPath))
            throw new ArgumentException("pdfPath is empty", nameof(pdfPath));
         if (!File.Exists(pdfPath))
            throw new FileNotFoundException("PDF not found", pdfPath);

         TimeSpan delay = TimeSpan.FromSeconds(2);

         for (int attempt = 1; attempt <= 6; attempt++)
         {
            using var content = new MultipartFormDataContent();

            var pdfBytes = await File.ReadAllBytesAsync(pdfPath, ct);
            var fileContent = new ByteArrayContent(pdfBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            content.Add(fileContent, "input", Path.GetFileName(pdfPath));

            content.Add(new StringContent(consolidateCitations.ToString()), "consolidateCitations");
            content.Add(new StringContent(includeRawCitations ? "1" : "0"), "includeRawCitations");

            using var req = new HttpRequestMessage(HttpMethod.Post, "api/processReferences")
            {
               Content = content
            };

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (res.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
               if (attempt == 6)
               {
                  var body = await res.Content.ReadAsStringAsync(ct);
                  throw new HttpRequestException(
                     $"Grobid is busy (503) after retries. Last response: {body}",
                     null,
                     res.StatusCode);
               }

               await Task.Delay(delay, ct);
               delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 12));
               continue;
            }

            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync(ct);
         }

         throw new InvalidOperationException("Unexpected retry loop exit.");
      }
   }
}
