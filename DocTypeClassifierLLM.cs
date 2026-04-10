using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CiteCheck
{
   internal class DocTypeClassifierLLM
   {
      public const string DefaultModel = "mistral:latest";
      public const string DefaultOllamaGenerateUrl = "https://redsox.uoa.auckland.ac.nz/ollama/api/generate";
      public const string DefaultPromptPath = "doc_type_classifier.txt";

      public const double StrictConfidenceThreshold = 0.90;

      private static readonly HttpClient _http = new HttpClient
      {
         Timeout = TimeSpan.FromSeconds(20)
      };

      private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
      {
         PropertyNameCaseInsensitive = true
      };

      internal sealed class Result
      {
         public string Type { get; init; } = "unknown";
         public double Confidence { get; init; } = 0.0;
         public string Reason { get; init; } = "";
      }

      public static async Task<Result?> ClassifyAsync(
          VerifyInput input,
          string? ollamaGenerateUrl = null,
          string? model = null,
          string? promptPath = null,
          CancellationToken ct = default)
      {
         ollamaGenerateUrl ??= DefaultOllamaGenerateUrl;
         model ??= DefaultModel;
         promptPath ??= DefaultPromptPath;

         string prompt = BuildPromptOrThrow(input, promptPath);

         var req = new
         {
            model = model,
            prompt = prompt,
            stream = false,
            options = new { temperature = 0.0 }
         };

         using var res = await _http.PostAsJsonAsync(ollamaGenerateUrl, req, ct);
         if (!res.IsSuccessStatusCode) return null;

         string body = await res.Content.ReadAsStringAsync(ct);

         using var doc = JsonDocument.Parse(body);
         if (!doc.RootElement.TryGetProperty("response", out var responseElem))
            return null;

         var raw = (responseElem.GetString() ?? "").Trim();
         raw = StripJsonFences(raw);

         Temp? parsed;
         try
         {
            parsed = JsonSerializer.Deserialize<Temp>(raw, _jsonOpts);
         }
         catch
         {
            return null;
         }

         if (parsed == null || string.IsNullOrWhiteSpace(parsed.Type))
            return null;

         string normalizedType = NormalizeType(parsed.Type);
         double conf = Clamp01(parsed.Confidence);
         string reason = (parsed.Reason ?? "").Trim();

         if (!IsAllowedType(normalizedType) || conf < StrictConfidenceThreshold)
         {
            return new Result
            {
               Type = "unknown",
               Confidence = conf,
               Reason = string.IsNullOrWhiteSpace(reason)
                  ? $"Rejected: type='{normalizedType}', conf={conf:F2} (need >= {StrictConfidenceThreshold:F2})"
                  : $"Rejected: {reason}"
            };
         }

         return new Result
         {
            Type = normalizedType,
            Confidence = conf,
            Reason = reason
         };
      }

      private static string BuildPromptOrThrow(VerifyInput v, string promptPath)
      {
         string? template = TryReadPromptTemplate(promptPath);

         if (string.IsNullOrWhiteSpace(template))
            throw new FileNotFoundException(
                $"Prompt file not found: '{promptPath}'. " +
                $"CWD='{Directory.GetCurrentDirectory()}', BaseDir='{AppContext.BaseDirectory}'");

         string authors = (v.Authors == null || v.Authors.Count == 0)
             ? ""
             : string.Join("; ", v.Authors.Where(a => !string.IsNullOrWhiteSpace(a)).Take(10));

         return template
             .Replace("{{title}}", v.Title ?? "", StringComparison.Ordinal)
             .Replace("{{authors}}", authors, StringComparison.Ordinal)
             .Replace("{{year}}", v.Year ?? "", StringComparison.Ordinal)
             .Replace("{{doi}}", v.DOI ?? "", StringComparison.Ordinal)
             .Replace("{{url}}", v.Url ?? "", StringComparison.Ordinal);
      }

      private static string? TryReadPromptTemplate(string promptPath)
      {
         try
         {
            if (File.Exists(promptPath))
               return File.ReadAllText(promptPath, Encoding.UTF8);

            var alt = Path.Combine(AppContext.BaseDirectory, promptPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(alt))
               return File.ReadAllText(alt, Encoding.UTF8);
         }
         catch
         {
         }
         return null;
      }

      private static string StripJsonFences(string s)
      {
         s = s.Trim();
         if (!s.StartsWith("```", StringComparison.Ordinal)) return s;

         int firstNewline = s.IndexOf('\n');
         if (firstNewline >= 0) s = s[(firstNewline + 1)..];

         int lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
         if (lastFence >= 0) s = s[..lastFence];

         return s.Trim();
      }

      private static string NormalizeType(string t)
      {
         t = (t ?? "").Trim().ToLowerInvariant();
         t = t.Replace('-', '_').Replace(' ', '_');

         return t switch
         {
            "thesis" => "thesis",
            "technical_report" => "technical_report",
            "working_paper" => "working_paper",
            "software" => "software",
            "unknown" => "unknown",
            _ => "unknown"
         };
      }

      private static bool IsAllowedType(string t) =>
         t == "thesis" ||
         t == "technical_report" ||
         t == "working_paper" ||
         t == "software";

      private static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);

      private sealed class Temp
      {
         public string? Type { get; set; }
         public double Confidence { get; set; }
         public string? Reason { get; set; }
      }
   }
}
