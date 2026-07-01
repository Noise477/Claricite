using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CiteCheck;

public record VerifyInput(string? DOI, string? Title, List<string> Authors, string? Year, string? Url);
public record VerifyHit(string Source, string? DOI, string? Title, List<string> Authors, string? Year, string? Url);
public record VerifyTraceStep(string Source, bool Found, string Detail);
public record VerifyResult(bool Exists, List<VerifyHit> Evidence, List<VerifyTraceStep> Trace);

public record CrossrefResponse(CrossrefMessage? Message);
public record CrossrefMessage(string? DOI, string? URL, string[]? Title, CrossrefAuthor[]? Author, CrossrefDate? Issued, CrossrefMessage[]? Items);
public record CrossrefAuthor(string? Given, string? Family);
public record CrossrefDate(int[][]? DateParts);

public record OpenAlexListResponse(List<OpenAlexWork>? Results);
public record OpenAlexWork(string? Title, int? PublicationYear, OpenAlexIds? Ids, List<OpenAlexAuthorship>? Authorships, OpenAlexPrimaryLocation? PrimaryLocation);
public record OpenAlexIds(string? Doi);
public record OpenAlexAuthorship(OpenAlexAuthor? Author);
public record OpenAlexAuthor(string? DisplayName);
public record OpenAlexPrimaryLocation(OpenAlexSource? Source);
public record OpenAlexSource(string? HomepageUrl);

public record S2SearchResponse(List<S2Paper>? Data);
public record S2Paper(string? Title, int? Year, string? Url, List<S2Author>? Authors, S2ExternalIds? ExternalIds);
public record S2Author(string? Name);
public record S2ExternalIds(string? DOI);

public static class Verifier
{
   private static readonly HttpClient httpApi = CreateApiHttp();
   private static readonly HttpClient httpUrl = CreateUrlProbeHttp();
   private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };
   private static readonly ConcurrentDictionary<string, bool> UrlExistsCache = new(StringComparer.OrdinalIgnoreCase);

   public static async Task<VerifyResult> CheckExistenceAsync(
   VerifyInput q,
   string? openAlexApiKey = null,
   string? s2ApiKey = null,
   CancellationToken ct = default)
   {
      var trace = new List<VerifyTraceStep>();

      if (!string.IsNullOrWhiteSpace(q.DOI))
      {
         var (isResolvable, evidence, doiTrace) = await IsDoiValidAsync(q.DOI, openAlexApiKey, s2ApiKey, ct);
         trace.AddRange(doiTrace);

         if (isResolvable && evidence != null)
         {
            bool needBibliographicCheck =
               !string.IsNullOrWhiteSpace(q.Title) ||
               (q.Authors != null && q.Authors.Count > 0) ||
               !string.IsNullOrWhiteSpace(q.Year);

            if (!needBibliographicCheck)
            {
               return new VerifyResult(true, new List<VerifyHit> { evidence }, trace);
            }

            bool placeholderTitle =
               !string.IsNullOrWhiteSpace(evidence.Title) &&
               evidence.Title.StartsWith("DOI is resolvable", StringComparison.OrdinalIgnoreCase);

            bool hasUsefulMetadata =
               (!string.IsNullOrWhiteSpace(evidence.Title) && !placeholderTitle) ||
               (evidence.Authors != null && evidence.Authors.Count > 0) ||
               !string.IsNullOrWhiteSpace(evidence.Year);

            if (!hasUsefulMetadata)
            {
               trace.Add(new VerifyTraceStep("DOI Metadata Check", false,
                  "DOI is resolvable but no bibliographic metadata returned; continue with URL/Title checks."));
            }
            else
            {
               bool ok;
               string? detail;

               if (!string.IsNullOrWhiteSpace(q.Title) && !string.IsNullOrWhiteSpace(evidence.Title) && !placeholderTitle)
               {
                  double titleSim = TitleSimilarity(q.Title, evidence.Title);
                  double authorSim = (q.Authors != null && q.Authors.Count > 0 && evidence.Authors != null && evidence.Authors.Count > 0)
                     ? AuthorDice(q.Authors, evidence.Authors)
                     : 0.0;
                  double yearSim = YearScore(q.Year, evidence.Year);

                  bool titleOk = titleSim >= 0.60;
                  bool authorOk = authorSim >= 0.30 || (q.Authors == null || q.Authors.Count == 0);
                  bool yearOk = yearSim > 0.0 || string.IsNullOrWhiteSpace(q.Year) || string.IsNullOrWhiteSpace(evidence.Year);

                  ok = titleOk && (authorOk || yearOk);
                  detail = $"t={titleSim:F3}, a={authorSim:F3}, y={yearSim:F3} (DOI metadata check: title>0.60 required) | q.Title={q.Title} | hit={evidence.Title}";
               }
               else
               {
                  ok = AcceptByAuthorsYear(q, evidence, out detail);
               }

               trace.Add(new VerifyTraceStep("DOI Metadata Check", ok, detail ?? "No detail"));

               if (ok)
                  return new VerifyResult(true, new List<VerifyHit> { evidence }, trace);

               trace.Add(new VerifyTraceStep("DOI Ignored", false,
                  "DOI resolved but metadata does not match the citation; continue with URL/Title checks."));
            }
         }
      }

      if (!string.IsNullOrWhiteSpace(q.Url))
      {
         var url = FixedUrl(q.Url.Trim());
         bool urlExists = await CheckUrlExistenceAsync(url, ct);
         trace.Add(new VerifyTraceStep("Direct URL", urlExists, url));

         if (urlExists)
         {
            var evidence = new VerifyHit(
               Source: "Direct URL Check",
               DOI: q.DOI,
               Title: q.Title,
               Authors: q.Authors!,
               Year: q.Year,
               Url: q.Url);

            return new VerifyResult(true, new List<VerifyHit> { evidence }, trace);
         }
      }

      if (string.IsNullOrWhiteSpace(q.Title))
         return new VerifyResult(false, new List<VerifyHit>(), trace);

      {
         var cands = await TryOpenAlexCandidatesByTitleAsync(q.Title, openAlexApiKey, ct);
         var hit = PickBestPassingCandidate(q, cands, out var detail, out var ok);
         trace.Add(new VerifyTraceStep("OpenAlex Title", ok, detail));
         if (ok && hit != null)
            return new VerifyResult(true, new List<VerifyHit> { hit }, trace);
      }

      {
         var cands = await TryCrossrefCandidatesByTitleAsync(q.Title, ct);
         var hit = PickBestPassingCandidate(q, cands, out var detail, out var ok);
         trace.Add(new VerifyTraceStep("Crossref Title", ok, detail));
         if (ok && hit != null)
            return new VerifyResult(true, new List<VerifyHit> { hit }, trace);
      }

      {
         var cands = await TryS2CandidatesByTitleAsync(q.Title, s2ApiKey, ct);
         var hit = PickBestPassingCandidate(q, cands, out var detail, out var ok);
         trace.Add(new VerifyTraceStep("SemanticScholar Title", ok, detail));
         if (ok && hit != null)
            return new VerifyResult(true, new List<VerifyHit> { hit }, trace);
      }

      {
         var arxivHit = await TryArxivByTitleAsync(q.Title, q.Authors!.FirstOrDefault(), ct);
         if (arxivHit == null)
         {
            trace.Add(new VerifyTraceStep("arXiv Title", false, "No match"));
         }
         else
         {
            bool ok = AcceptCandidateAdaptive(q, arxivHit, out var detail);
            trace.Add(new VerifyTraceStep("arXiv Title", ok, detail ?? (arxivHit.Title ?? "No match")));
            if (ok)
               return new VerifyResult(true, new List<VerifyHit> { arxivHit }, trace);
         }
      }

      return new VerifyResult(false, new List<VerifyHit>(), trace);
   }

   private static string FixedUrl(string url)
   {
      if (string.IsNullOrEmpty(url)) return url;

      if (url.IndexOfAny(BadTildes) == -1)
      {
         return url;
      }

      return url
          .Replace('\u223C', '~')
          .Replace('\u02DC', '~')
          .Replace('\uFF5E', '~')
          .Replace('\u223E', '~');
   }
   private static VerifyHit? PickBestPassingCandidate(
      VerifyInput q,
      List<VerifyHit> candidates,
      out string detail,
      out bool ok)
   {
      ok = false;

      if (candidates == null || candidates.Count == 0)
      {
         detail = "No match";
         return null;
      }

      VerifyHit? bestPassing = null;
      double bestPassingScore = double.NegativeInfinity;
      string? bestPassingDetail = null;

      VerifyHit? bestOverall = null;
      double bestOverallScore = double.NegativeInfinity;
      string? bestOverallDetail = null;

      var top = candidates
         .Select(h => new { Hit = h, TitleSim = TitleSimilarity(q.Title ?? "", h.Title ?? "") })
         .OrderByDescending(x => x.TitleSim)
         .Take(12)
         .Select(x => x.Hit)
         .ToList();

      foreach (var hit in top)
      {
         var pass = AcceptCandidateAdaptive(q, hit, out var d);

         double t = TitleSimilarity(q.Title ?? "", hit.Title ?? "");
         double a = (q.Authors?.Count > 0 && hit.Authors?.Count > 0) ? AuthorDice(q.Authors, hit.Authors) : 0.0;
         double y = YearScore(q.Year, hit.Year);
         double score = (0.75 * t) + (0.20 * a) + (0.05 * y);

         if (score > bestOverallScore)
         {
            bestOverallScore = score;
            bestOverall = hit;
            bestOverallDetail = d;
         }

         if (pass && score > bestPassingScore)
         {
            bestPassingScore = score;
            bestPassing = hit;
            bestPassingDetail = d;
         }
      }

      if (bestPassing != null)
      {
         ok = true;
         detail = bestPassingDetail ?? (bestPassing.Title ?? "Match");
         return bestPassing;
      }

      detail = bestOverallDetail ?? (bestOverall?.Title ?? "No match");
      return null;
   }

   private static bool AcceptCandidateAdaptive(VerifyInput q, VerifyHit hit, out string? detail)
   {
      detail = null;
      if (hit == null || string.IsNullOrWhiteSpace(q.Title) || string.IsNullOrWhiteSpace(hit.Title))
         return false;

      double t = TitleSimilarity(q.Title, hit.Title);
      double a = (q.Authors != null && q.Authors.Count > 0 && hit.Authors != null && hit.Authors.Count > 0)
         ? AuthorDice(q.Authors, hit.Authors)
         : 0.0;

      double y = YearScore(q.Year, hit.Year);

      bool hasAuthorInfo = q.Authors != null && q.Authors.Count > 0 && hit.Authors != null && hit.Authors.Count > 0;
      bool hasYearInfo = !string.IsNullOrWhiteSpace(q.Year) && !string.IsNullOrWhiteSpace(hit.Year);

      int qTokens = TokenizeTitle(q.Title).Count;
      int hitTokens = TokenizeTitle(hit.Title).Count;
      bool isShortTitle = qTokens <= 4 || hitTokens <= 4;

      if (isShortTitle && !hasAuthorInfo && !hasYearInfo)
      {
         if (t >= 0.99)
         {
         }
         else
         {
            detail = $"t={t:F3}, a={a:F3}, y={y:F3}, tokens=({qTokens},{hitTokens}) (reject: short title without corroborating info) | hit={hit.Title}";
            return false;
         }
      }

      double thr = AdaptiveTitleThreshold(q, hit);

      if (hasAuthorInfo)
      {
         if (a < 0.15)
         {
            detail = $"t={t:F3}, a={a:F3}, y={y:F3}, thr={thr:F3} (reject: authors unrelated) | hit={hit.Title}";
            return false;
         }
      }

      if (hasYearInfo)
      {
         if (y == 0.0)
         {
            detail = $"t={t:F3}, a={a:F3}, y={y:F3}, thr={thr:F3} (reject: year mismatch) | hit={hit.Title}";
            return false;
         }
      }

      if (!hasAuthorInfo && !hasYearInfo)
      {
         double strictThr = 0.98;
         if (t < strictThr)
         {
            detail = $"t={t:F3}, a={a:F3}, y={y:F3}, thr={strictThr:F3} (reject: no corroborating info, need t≥0.98) | hit={hit.Title}";
            return false;
         }
      }

      bool ok = t >= thr;
      detail = $"t={t:F3}, a={a:F3}, y={y:F3}, thr={thr:F3} | hit={hit.Title}";
      return ok;
   }

   private static bool AcceptByAuthorsYear(VerifyInput q, VerifyHit hit, out string? detail)
   {
      detail = null;
      if (hit == null) return false;

      double a = (q.Authors != null && q.Authors.Count > 0 && hit.Authors != null && hit.Authors.Count > 0)
         ? AuthorDice(q.Authors, hit.Authors)
         : 0.0;

      double y = YearScore(q.Year, hit.Year);

      bool ok;

      if (q.Authors != null && q.Authors.Count > 0 && hit.Authors != null && hit.Authors.Count > 0)
      {
         ok = a >= 0.40 && (string.IsNullOrWhiteSpace(q.Year) || string.IsNullOrWhiteSpace(hit.Year) || y > 0.0);
      }
      else if (!string.IsNullOrWhiteSpace(q.Year) && !string.IsNullOrWhiteSpace(hit.Year))
      {
         ok = y >= 1.0;
      }
      else
      {
         ok = false;
      }

      detail = $"a={a:F3}, y={y:F3} | hit={hit.Title ?? "(no title)"}";
      return ok;
   }

   private static double AdaptiveTitleThreshold(VerifyInput q, VerifyHit hit)
   {
      const double baseTitle = 0.95;

      double a = (q.Authors != null && q.Authors.Count > 0 && hit.Authors != null && hit.Authors.Count > 0)
         ? AuthorDice(q.Authors, hit.Authors)
         : 0.0;

      double y = YearScore(q.Year, hit.Year);

      double minTitle = (a >= 0.80) ? 0.80 : 0.85;

      double thr = baseTitle - (0.10 * a) - (0.03 * y);
      thr = Clamp(thr, minTitle, baseTitle);
      return thr;
   }

   private static HashSet<string> NormalizeAuthorTokens(IEnumerable<string> authors)
   {
      static string Norm(string s)
      {
         s = (s ?? "").Trim();

         int comma = s.IndexOf(',');
         if (comma >= 0) s = s.Substring(0, comma);

         s = s.ToLowerInvariant();

         s = Regex.Replace(s, @"[^\p{L}\p{N}\s\-]", " ");
         s = Regex.Replace(s, @"\s+", " ").Trim();
         return s;
      }

      var tokens = new HashSet<string>();
      foreach (var a in authors ?? Enumerable.Empty<string>())
      {
         var x = Norm(a);
         if (x.Length == 0) continue;

         var parts = x.Split(' ', StringSplitOptions.RemoveEmptyEntries);
         if (parts.Length == 0) continue;

         var last = parts[^1];
         tokens.Add(last);

         if (last.Contains('-'))
         {
            foreach (var p in last.Split('-', StringSplitOptions.RemoveEmptyEntries))
               tokens.Add(p);
         }
      }
      return tokens;
   }

   private static double AuthorDice(IEnumerable<string> aAuthors, IEnumerable<string> bAuthors)
   {
      var A = NormalizeAuthorTokens(aAuthors);
      var B = NormalizeAuthorTokens(bAuthors);
      if (A.Count == 0 || B.Count == 0) return 0;

      int inter = A.Intersect(B).Count();
      return (2.0 * inter) / (A.Count + B.Count);
   }

   private static double YearScore(string? qYear, string? hitYear)
   {
      if (string.IsNullOrWhiteSpace(qYear) || string.IsNullOrWhiteSpace(hitYear)) return 0;
      if (!int.TryParse(qYear, out var y1) || !int.TryParse(hitYear, out var y2)) return 0;

      int diff = Math.Abs(y1 - y2);
      return diff switch
      {
         0 => 1.0,
         1 => 0.6,
         2 => 0.3,
         _ => 0.0
      };
   }

   private static double Clamp(double v, double min, double max)
   {
      if (v < min) return min;
      if (v > max) return max;
      return v;
   }


   public static async Task<bool> CheckUrlExistenceAsync(string url, CancellationToken ct = default)
   {
      if (string.IsNullOrWhiteSpace(url)) return false;

      var cacheKey = CleanUrlForCache(url);
      if (UrlExistsCache.TryGetValue(cacheKey, out var cached))
         return cached;

      foreach (var uri in BuildUrlCandidates(url))
      {
         var ok = await ProbeUrlAsync(uri, ct);
         if (ok)
         {
            UrlExistsCache[cacheKey] = true;
            return true;
         }
      }

      UrlExistsCache[cacheKey] = false;
      return false;
   }

   private static string CleanUrlForCache(string raw)
   {
      var s = (raw ?? "").Trim();
      s = s.TrimEnd('.', ',', ';', ')', ']', '>', '"', '\'');
      s = Regex.Replace(s, @"^(https?):(?!//)", "$1://", RegexOptions.IgnoreCase);
      return s;
   }

   private static IEnumerable<Uri> BuildUrlCandidates(string raw)
   {
      var s = (raw ?? "").Trim();

      s = s.TrimEnd('.', ',', ';', ')', ']', '>', '"', '\'');

      if (s.StartsWith("urlhttp", StringComparison.OrdinalIgnoreCase))
      {
         s = s.Substring(3);
      }

      s = Regex.Replace(s, @"^(https?):(?!//)", "$1://", RegexOptions.IgnoreCase);

      if (s.StartsWith("//")) s = "https:" + s;

      s = s.Replace(" ", "%20");

      if (Uri.TryCreate(s, UriKind.Absolute, out var u1))
      {
         if (u1.Scheme == Uri.UriSchemeHttp || u1.Scheme == Uri.UriSchemeHttps)
         {
            yield return u1;
         }
      }

      var hostish = s.TrimStart('/');
      if (!hostish.StartsWith("http", StringComparison.OrdinalIgnoreCase))
      {
         if (Uri.TryCreate("https://" + hostish, UriKind.Absolute, out var u2))
         {
            yield return u2;

            var ub = new UriBuilder(u2) { Scheme = "http", Port = -1 };
            yield return ub.Uri;
         }
      }
   }

   private static async Task<bool> ProbeUrlAsync(Uri uri, CancellationToken ct)
   {
      const int MAX_REDIRECTS = 8;

      var methods = new[] { HttpMethod.Head, HttpMethod.Get };

      foreach (var method0 in methods)
      {
         var method = method0;
         Uri current = uri;

         for (int hop = 0; hop <= MAX_REDIRECTS; hop++)
         {
            using var req = new HttpRequestMessage(method, current);

            req.Headers.Accept.Clear();
            req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            req.Headers.AcceptLanguage.Clear();
            req.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.8,zh-CN;q=0.6");

            if (method == HttpMethod.Get)
               req.Headers.Range = new RangeHeaderValue(0, 0);

            HttpResponseMessage? res = null;
            try
            {
               res = await httpUrl.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

               if (IsResolvableForExistence(res.StatusCode))
                  return true;

               if (IsRedirect(res.StatusCode) && res.Headers.Location != null)
               {
                  var next = res.Headers.Location;
                  if (!next.IsAbsoluteUri)
                     next = new Uri(current, next);

                  if (res.StatusCode == HttpStatusCode.SeeOther)
                     method = HttpMethod.Get;

                  current = next;
                  continue;
               }

               if (method == HttpMethod.Head && res.StatusCode == HttpStatusCode.MethodNotAllowed)
                  break;

               int code = (int)res.StatusCode;
               if (code == 429 || code >= 500)
               {
                  await Task.Delay(250, ct);
                  continue;
               }

               break;
            }
            catch (OperationCanceledException) { return false; }
            catch (HttpRequestException) { break; }
            finally
            {
               res?.Dispose();
            }
         }
      }

      return false;
   }

   private static bool IsRedirect(HttpStatusCode code)
      => (int)code is 301 or 302 or 303 or 307 or 308;

   private static bool IsResolvableForExistence(HttpStatusCode code)
   {
      int c = (int)code;
      if (c >= 200 && c < 400) return true;
      if (code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return true;
      return false;
   }

   private static async Task<(bool IsValid, VerifyHit? Evidence, List<VerifyTraceStep> Trace)> IsDoiValidAsync(
      string doi, string? openAlexApiKey, string? s2ApiKey, CancellationToken ct)
   {
      var trace = new List<VerifyTraceStep>();
      var normalizedDoi = NormalizeDoi(doi);

      VerifyHit? evidence;

      evidence = await TryOpenAlexByDoiAsync(normalizedDoi, openAlexApiKey, ct);
      trace.Add(new VerifyTraceStep("OpenAlex DOI", evidence != null, evidence?.Title ?? "No match"));
      if (evidence != null) return (true, evidence, trace);

      evidence = await TryCrossrefByDoiAsync(normalizedDoi, ct);
      trace.Add(new VerifyTraceStep("Crossref DOI", evidence != null, evidence?.Title ?? "No match"));
      if (evidence != null) return (true, evidence, trace);

      evidence = await TryS2ByDoiAsync(normalizedDoi, s2ApiKey, ct);
      trace.Add(new VerifyTraceStep("SemanticScholar DOI", evidence != null, evidence?.Title ?? "No match"));
      if (evidence != null) return (true, evidence, trace);

      evidence = await TryOfficialDoiResolverAsync(normalizedDoi, ct);
      trace.Add(new VerifyTraceStep("doi.org (Official)", evidence != null, evidence?.Url ?? "No match"));
      if (evidence != null) return (true, evidence, trace);

      return (false, null, trace);
   }

   private static async Task<VerifyHit?> TryOfficialDoiResolverAsync(string doi, CancellationToken ct)
   {
      try
      {
         using var req = new HttpRequestMessage(HttpMethod.Head, $"https://doi.org/{Uri.EscapeDataString(doi)}");
         using var res = await httpUrl.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
         if (IsResolvableForExistence(res.StatusCode))
            return new VerifyHit("doi.org (Official)", doi, "DOI is resolvable.", new List<string>(), null, $"https://doi.org/{doi}");

         using var req2 = new HttpRequestMessage(HttpMethod.Get, $"https://doi.org/{Uri.EscapeDataString(doi)}");
         req2.Headers.Range = new RangeHeaderValue(0, 0);
         using var res2 = await httpUrl.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, ct);
         if (IsResolvableForExistence(res2.StatusCode))
            return new VerifyHit("doi.org (Official)", doi, "DOI is resolvable.", new List<string>(), null, $"https://doi.org/{doi}");
      }
      catch { }
      return null;
   }

   private static string NormalizeDoi(string s)
   {
      if (string.IsNullOrWhiteSpace(s)) return "";
      var x = s.Trim();
      x = Regex.Replace(x, @"^(doi:\s*|https?://(dx\.)?doi\.org/)", "", RegexOptions.IgnoreCase);
      x = x.Trim().TrimEnd('.', ',', ';', ')', ']', '>', '"', '\'');
      return x.Trim('/').Trim();
   }


   private static async Task<VerifyHit?> TryCrossrefByDoiAsync(string doi, CancellationToken ct)
   {
      try
      {
         var url = $"https://api.crossref.org/works/{Uri.EscapeDataString(doi)}";
         using var res = await GetApi(url, ct);
         if (!res.IsSuccessStatusCode) return null;

         var cr = await res.Content.ReadFromJsonAsync<CrossrefResponse>(J, ct);
         var msg = cr?.Message;
         if (msg == null) return null;

         var year = msg.Issued?.DateParts?.FirstOrDefault()?.FirstOrDefault().ToString();
         var authors = msg.Author?.Select(a => $"{a.Given} {a.Family}".Trim()).ToList() ?? new();

         return new VerifyHit("Crossref", NormalizeDoi(msg.DOI ?? ""), msg.Title?.FirstOrDefault(), authors, year, msg.URL);
      }
      catch { return null; }
   }

   private static async Task<List<VerifyHit>> TryCrossrefCandidatesByTitleAsync(string title, CancellationToken ct)
   {
      try
      {
         var url = $"https://api.crossref.org/works?rows=25&query.bibliographic={Uri.EscapeDataString(title)}";
         using var res = await GetApi(url, ct);
         if (!res.IsSuccessStatusCode) return new List<VerifyHit>();

         var cr = await res.Content.ReadFromJsonAsync<CrossrefResponse>(J, ct);
         var items = cr?.Message?.Items;
         if (items == null || items.Length == 0) return new List<VerifyHit>();

         var hits = new List<VerifyHit>();
         foreach (var it in items)
         {
            var year = it.Issued?.DateParts?.FirstOrDefault()?.FirstOrDefault().ToString();
            var authors = it.Author?.Select(a => $"{a.Given} {a.Family}".Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new();
            hits.Add(new VerifyHit("Crossref", NormalizeDoi(it.DOI ?? ""), it.Title?.FirstOrDefault(), authors, year, it.URL));
         }

         return DedupHits(hits);
      }
      catch { return new List<VerifyHit>(); }
   }


   private static async Task<VerifyHit?> TryOpenAlexByDoiAsync(string doi, string? openAlexApiKey, CancellationToken ct)
   {
      try
      {
         var url = $"https://api.openalex.org/works/doi:{Uri.EscapeDataString(doi)}";
         if (!string.IsNullOrWhiteSpace(openAlexApiKey))
            url += $"?api_key={openAlexApiKey}";

         using var res = await GetApi(url, ct);
         if (!res.IsSuccessStatusCode) return null;

         var work = await res.Content.ReadFromJsonAsync<OpenAlexWork>(J, ct);
         if (work == null) return null;

         var authors = work.Authorships?.Select(a => a.Author?.DisplayName).OfType<string>().ToList() ?? new();

         var doiNorm = NormalizeDoi(work.Ids?.Doi ?? "");

         return new VerifyHit("OpenAlex", doiNorm, work.Title, authors, work.PublicationYear?.ToString(), work.PrimaryLocation?.Source?.HomepageUrl);
      }
      catch { return null; }
   }

   private static async Task<List<VerifyHit>> TryOpenAlexCandidatesByTitleAsync(string title, string? openAlexApiKey, CancellationToken ct)
   {
      try
      {
         var url = $"https://api.openalex.org/works?search={Uri.EscapeDataString(title)}&per_page=25";
         if (!string.IsNullOrWhiteSpace(openAlexApiKey))
            url += $"&api_key={openAlexApiKey}";

         using var res = await GetApi(url, ct);
         if (!res.IsSuccessStatusCode) return new List<VerifyHit>();

         var oa = await res.Content.ReadFromJsonAsync<OpenAlexListResponse>(J, ct);
         if (oa?.Results == null || oa.Results.Count == 0) return new List<VerifyHit>();

         var hits = new List<VerifyHit>();
         foreach (var w in oa.Results)
         {
            var authors = w.Authorships?.Select(a => a.Author?.DisplayName).OfType<string>().ToList() ?? new();
            var doiNorm = NormalizeDoi(w.Ids?.Doi ?? "");
            hits.Add(new VerifyHit("OpenAlex", doiNorm, w.Title, authors, w.PublicationYear?.ToString(), w.PrimaryLocation?.Source?.HomepageUrl));
         }

         return DedupHits(hits);
      }
      catch { return new List<VerifyHit>(); }
   }


   private static async Task<VerifyHit?> TryS2ByDoiAsync(string doi, string? s2ApiKey, CancellationToken ct)
   {
      try
      {
         var url = $"https://api.semanticscholar.org/graph/v1/paper/DOI:{Uri.EscapeDataString(doi)}?fields=title,year,authors,url,externalIds";
         using var res = await GetApi(url, ct, s2ApiKey: s2ApiKey);
         if (!res.IsSuccessStatusCode) return null;

         var paper = await res.Content.ReadFromJsonAsync<S2Paper>(J, ct);
         if (paper == null) return null;

         var authors = paper.Authors?.Select(a => a.Name).OfType<string>().ToList() ?? new();
         return new VerifyHit("SemanticScholar", NormalizeDoi(paper.ExternalIds?.DOI ?? ""), paper.Title, authors, paper.Year?.ToString(), paper.Url);
      }
      catch { return null; }
   }

   private static async Task<List<VerifyHit>> TryS2CandidatesByTitleAsync(string title, string? s2ApiKey, CancellationToken ct)
   {
      var hits = new List<VerifyHit>();

      try
      {
         var url =
            "https://api.semanticscholar.org/graph/v1/paper/search/match" +
            $"?query={Uri.EscapeDataString(title.Trim())}" +
            "&fields=title,year,authors,url,externalIds";

         using var res = await GetApi(url, ct, s2ApiKey: s2ApiKey);
         if (res.IsSuccessStatusCode)
         {
            var paper = await ReadS2PaperFromPossiblyWrappedJson(res, ct);
            if (paper != null && !string.IsNullOrWhiteSpace(paper.Title))
            {
               var authors = paper.Authors?.Select(a => a.Name).OfType<string>().ToList() ?? new();
               hits.Add(new VerifyHit("SemanticScholar", NormalizeDoi(paper.ExternalIds?.DOI ?? ""), paper.Title, authors, paper.Year?.ToString(), paper.Url));
            }
         }
      }
      catch {}

      try
      {
         var cleanTitle = Regex.Replace(title, @"[:;""“”\(\)\[\]]", " ");
         cleanTitle = Regex.Replace(cleanTitle, @"\s+", " ").Trim();

         var url =
            "https://api.semanticscholar.org/graph/v1/paper/search" +
            $"?query={Uri.EscapeDataString(cleanTitle)}" +
            "&limit=25" +
            "&fields=title,year,authors,url,externalIds";

         using var res = await GetApi(url, ct, s2ApiKey: s2ApiKey);
         if (!res.IsSuccessStatusCode)
            return DedupHits(hits);

         var s2 = await res.Content.ReadFromJsonAsync<S2SearchResponse>(J, ct);
         if (s2?.Data == null || s2.Data.Count == 0)
            return DedupHits(hits);

         foreach (var paper in s2.Data)
         {
            var authors = paper.Authors?.Select(a => a.Name).OfType<string>().ToList() ?? new();
            hits.Add(new VerifyHit("SemanticScholar", NormalizeDoi(paper.ExternalIds?.DOI ?? ""), paper.Title, authors, paper.Year?.ToString(), paper.Url));
         }

         return DedupHits(hits);
      }
      catch
      {
         return DedupHits(hits);
      }
   }

   private static async Task<S2Paper?> ReadS2PaperFromPossiblyWrappedJson(HttpResponseMessage res, CancellationToken ct)
   {
      var json = await res.Content.ReadAsStringAsync(ct);
      if (string.IsNullOrWhiteSpace(json)) return null;

      try
      {
         var direct = JsonSerializer.Deserialize<S2Paper>(json, J);
         if (direct != null && !string.IsNullOrWhiteSpace(direct.Title)) return direct;
      }
      catch { }

      try
      {
         using var doc = JsonDocument.Parse(json);
         if (doc.RootElement.ValueKind == JsonValueKind.Object &&
             doc.RootElement.TryGetProperty("data", out var dataElem) &&
             dataElem.ValueKind == JsonValueKind.Object)
         {
            var wrapped = JsonSerializer.Deserialize<S2Paper>(dataElem.GetRawText(), J);
            return wrapped;
         }
      }
      catch { }

      return null;
   }


   private static async Task<VerifyHit?> TryArxivByTitleAsync(string title, string? firstAuthor, CancellationToken ct)
   {
      try
      {
         var query = $"ti:\"{title}\"" + (string.IsNullOrWhiteSpace(firstAuthor) ? "" : $" AND au:\"{firstAuthor}\"");
         var url = $"http://export.arxiv.org/api/query?search_query={Uri.EscapeDataString(query)}&max_results=1";

         using var res = await GetUrlRaw(url, ct, accept: "application/atom+xml");
         if (!res.IsSuccessStatusCode) return null;

         var xml = await res.Content.ReadAsStringAsync(ct);
         XNamespace atom = "http://www.w3.org/2005/Atom";

         var entry = XDocument.Parse(xml).Descendants(atom + "entry").FirstOrDefault();
         if (entry == null) return null;

         var authors = entry.Elements(atom + "author").Select(e => e.Element(atom + "name")?.Value ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
         var year = DateTime.TryParse(entry.Element(atom + "published")?.Value, out var dt) ? dt.Year.ToString() : null;
         var resultTitle = entry.Element(atom + "title")?.Value?.Trim();

         return new VerifyHit("arXiv", null, resultTitle, authors, year, entry.Element(atom + "id")?.Value);
      }
      catch { return null; }
   }


   private static double TitleSimilarity(string a, string b)
   {
      var av = TitleVariants(a).Distinct().ToList();
      var bv = TitleVariants(b).Distinct().ToList();

      double best = 0;
      foreach (var x in av)
         foreach (var y in bv)
            best = Math.Max(best, TitleSimilarityCore(x, y));

      return best;
   }

   private static IEnumerable<string> TitleVariants(string t)
   {
      t = (t ?? "").Trim();
      if (t.Length == 0) yield break;

      yield return t;

      yield return Regex.Replace(t, @"\bHE\b", "Higher Education", RegexOptions.IgnoreCase);
      yield return Regex.Replace(t, @"\bhigher\s+education\b", "HE", RegexOptions.IgnoreCase);

      yield return Regex.Replace(t, @"[:;]", " ");
   }

   private static double TitleSimilarityCore(string a, string b)
   {
      var ca = NormalizeTitleChars(a);
      var cb = NormalizeTitleChars(b);
      double charSim = CalculateSimilarity(ca, cb);

      var ta = TokenizeTitle(a);
      var tb = TokenizeTitle(b);
      double tokenSim = TokenDice(ta, tb);

      return Math.Max(charSim, tokenSim);
   }

   private static string NormalizeTitleChars(string title)
      => Regex.Replace((title ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

   private static HashSet<string> TokenizeTitle(string title)
   {
      var t = (title ?? "").ToLowerInvariant();
      t = Regex.Replace(t, @"[-–—/]", " ");
      t = Regex.Replace(t, @"[^a-z0-9\s]", " ");
      t = Regex.Replace(t, @"\s+", " ").Trim();
      if (t.Length == 0) return new HashSet<string>();
      return t.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
   }

   private static double TokenDice(HashSet<string> a, HashSet<string> b)
   {
      if (a.Count == 0 || b.Count == 0) return 0;
      int inter = a.Intersect(b).Count();
      return (2.0 * inter) / (a.Count + b.Count);
   }

   private static double CalculateSimilarity(string s1, string s2)
   {
      if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0;
      if (s1 == s2) return 1.0;

      var d = new int[s1.Length + 1, s2.Length + 1];
      for (int i = 0; i <= s1.Length; i++) d[i, 0] = i;
      for (int j = 0; j <= s2.Length; j++) d[0, j] = j;

      for (int i = 1; i <= s1.Length; i++)
         for (int j = 1; j <= s2.Length; j++)
         {
            int cost = s2[j - 1] == s1[i - 1] ? 0 : 1;
            d[i, j] = Math.Min(
               Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
               d[i - 1, j - 1] + cost);
         }

      int maxLen = Math.Max(s1.Length, s2.Length);
      return maxLen == 0 ? 1.0 : (double)(maxLen - d[s1.Length, s2.Length]) / maxLen;
   }

   private static List<VerifyHit> DedupHits(List<VerifyHit> hits)
   {
      var seenDoi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var seenTitle = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      var result = new List<VerifyHit>();
      foreach (var h in hits)
      {
         var doi = (h.DOI ?? "").Trim();
         if (!string.IsNullOrWhiteSpace(doi))
         {
            if (seenDoi.Add(doi))
               result.Add(h);
            continue;
         }

         var t = NormalizeTitleChars(h.Title ?? "");
         if (t.Length == 0) continue;

         if (seenTitle.Add(t))
            result.Add(h);
      }
      return result;
   }

   private static HttpClient CreateApiHttp()
   {
      var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
      {
         Timeout = TimeSpan.FromSeconds(12),
      };
      h.DefaultRequestHeaders.UserAgent.ParseAdd("CiteCheck/1.1 (mailto:chengcheng.han@auckland.ac.nz)");
      h.DefaultRequestHeaders.Accept.Clear();
      h.DefaultRequestHeaders.Accept.ParseAdd("application/json");
      return h;
   }

   private static HttpClient CreateUrlProbeHttp()
   {
      var handler = new HttpClientHandler
      {
         AutomaticDecompression = DecompressionMethods.All,
         AllowAutoRedirect = false
      };

      var h = new HttpClient(handler)
      {
         Timeout = TimeSpan.FromSeconds(12),
      };

      h.DefaultRequestHeaders.UserAgent.Clear();
      h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; CiteCheck/1.1; mailto:chengcheng.han@auckland.ac.nz)");
      h.DefaultRequestHeaders.Accept.Clear();
      h.DefaultRequestHeaders.Accept.ParseAdd("*/*");

      return h;
   }
   private static async Task<HttpResponseMessage> GetApi(string url, CancellationToken ct, string? s2ApiKey = null)
   {
      async Task<HttpResponseMessage> SendAsync()
      {
         using var request = new HttpRequestMessage(HttpMethod.Get, url);
         request.Headers.Accept.Clear();
         request.Headers.Accept.ParseAdd("application/json");
         if (!string.IsNullOrWhiteSpace(s2ApiKey))
            request.Headers.Add("x-api-key", s2ApiKey);
         return await httpApi.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
      }

      var delay = 300;
      for (int i = 0; i < 3; i++)
      {
         var res = await SendAsync();
         if ((int)res.StatusCode is 429 or >= 500)
         {
            res.Dispose();
            await Task.Delay(delay, ct);
            delay *= 2;
            continue;
         }
         return res;
      }

      return await SendAsync();
   }

   private static Task<HttpResponseMessage> GetUrlRaw(string url, CancellationToken ct, string? accept = null)
   {
      var req = new HttpRequestMessage(HttpMethod.Get, url);
      if (!string.IsNullOrWhiteSpace(accept))
      {
         req.Headers.Accept.Clear();
         req.Headers.Accept.ParseAdd(accept);
      }
      return httpUrl.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
   }

   private static readonly char[] BadTildes = { '\u223C', '\u02DC', '\uFF5E', '\u223E' };
}
