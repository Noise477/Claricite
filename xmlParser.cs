using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ClariCite
{
   public record XmlParseResult(
       string SourceFile,
       string? DOI,
       string? Title,
       List<string> Authors,
       string? Year,
       string? Url
   );

   public static class xmlParser
   {
      private static readonly XNamespace TEI = "http://www.tei-c.org/ns/1.0";

      public static List<XmlParseResult> ParseTeiFile(string xmlPath)
      {
         var doc = XDocument.Load(xmlPath);
         return ParseTeiDocument(doc, System.IO.Path.GetFileName(xmlPath));
      }

      public static List<XmlParseResult> ParseTeiString(string xmlContent, string sourceFileName)
      {
         var doc = XDocument.Parse(xmlContent);
         return ParseTeiDocument(doc, sourceFileName);
      }

      private static List<XmlParseResult> ParseTeiDocument(XDocument doc, string sourceFileName)
      {
         var refs = GetReferenceBiblStructs(doc).ToList();

         var list = new List<XmlParseResult>(capacity: refs.Count);
         foreach (var bibl in refs)
         {
            var analytic = bibl.Element(T("analytic"));
            var monogr = bibl.Element(T("monogr"));

            var title = Clean(PickTitle(analytic) ?? PickTitle(monogr));

            var authors = ExtractAuthors(analytic);
            if (authors.Count == 0)
            {
               authors = ExtractAuthors(monogr);
            }

            var year = Clean(ExtractYear(analytic) ?? ExtractYear(monogr));

            var idA = Idnos(analytic);
            var idM = Idnos(monogr);

            var doi = Clean(FirstNonEmpty(new[]
            {
               FirstId(idA, "doi"), FirstId(idM, "doi"),
               FirstId(idA, "DOI"), FirstId(idM, "DOI")
            }));

            var url = Clean(GetFirstPtrTarget(bibl));

            list.Add(new XmlParseResult(
               SourceFile: sourceFileName,
               DOI: doi,
               Title: title,
               Authors: authors,
               Year: year,
               Url: url
            ));
         }

         return list;
      }

      private static IEnumerable<XElement> GetReferenceBiblStructs(XDocument doc)
      {
         var listBiblRefs = doc.Descendants(T("listBibl")).Descendants(T("biblStruct"));
         if (listBiblRefs.Any())
         {
            return listBiblRefs;
         }

         var headerBibl = new HashSet<XElement>(doc.Descendants(T("teiHeader")).Descendants(T("biblStruct")));
         return doc.Descendants(T("biblStruct")).Where(b => !headerBibl.Contains(b));
      }

      private static XName T(string name) => TEI + name;

      private static string Normalize(string s)
      {
         if (string.IsNullOrWhiteSpace(s)) return "";
         string decomposed = s.Normalize(NormalizationForm.FormD);
         string deaccented = Regex.Replace(decomposed, @"\p{M}", "");
         string lowercased = deaccented.ToLowerInvariant();
         string withSpaces = Regex.Replace(lowercased, @"[-–—/&]", " ");
         string alphanumeric = Regex.Replace(withSpaces, @"[^\w\s]", "");
         return Regex.Replace(alphanumeric, @"\s+", " ").Trim();
      }

      private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

      private static string? FirstNonEmpty(IEnumerable<string?> xs)
      {
         foreach (var s in xs) if (!string.IsNullOrWhiteSpace(s)) return s;
         return null;
      }

      private static string? PickTitle(XElement? scope)
      {
         if (scope == null) return null;
         foreach (var t in scope.Elements(T("title")))
         {
            var lvl = (string?)t.Attribute("level");
            var okLevel = string.IsNullOrEmpty(lvl) || lvl == "a" || lvl == "m" || lvl == "j" || lvl == "s";
            if (!okLevel) continue;

            var v = Normalize(t.Value);
            if (!string.IsNullOrWhiteSpace(v)) return v;
         }
         return null;
      }

      private static List<string> ExtractAuthors(XElement? scope)
      {
         var results = new List<string>();
         if (scope == null) return results;

         foreach (var author in scope.Elements(T("author")))
         {
            foreach (var p in author.Elements(T("persName")))
            {
               var forenames = new List<string>();
               foreach (var f in p.Elements(T("forename")))
               {
                  var raw = f.Value == null ? "" : f.Value.Trim();
                  if (raw.Length == 0) continue;
                  forenames.Add(raw.Length == 1 ? raw + "." : raw);
               }

               var surname = p.Element(T("surname"))?.Value?.Trim() ?? "";
               var given = string.Join(" ", forenames);
               var full = string.Join(" ", new[] { given, surname }.Where(x => !string.IsNullOrWhiteSpace(x)));
               full = Normalize(full).Normalize(NormalizationForm.FormC);

               if (!string.IsNullOrWhiteSpace(full) && !results.Contains(full, StringComparer.OrdinalIgnoreCase))
                  results.Add(full);
            }
         }
         return results;
      }

      private static string? ExtractYear(XElement? scope)
      {
         if (scope == null) return null;
         var date = scope.Element(T("imprint"))?.Element(T("date")) ?? scope.Element(T("date"));
         if (date == null) return null;

         var when = (string?)date.Attribute("when");
         if (!string.IsNullOrWhiteSpace(when))
         {
            var mWhen = Regex.Match(when, @"\b(19|20)\d{2}\b");
            if (mWhen.Success) return mWhen.Value;
         }

         var text = Normalize(date.Value);
         var mText = Regex.Match(text, @"\b(19|20)\d{2}\b");
         return mText.Success ? mText.Value : null;
      }

      private static Dictionary<string, List<string>> Idnos(XElement? scope)
      {
         var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
         if (scope == null) return dict;

         foreach (var id in scope.Elements(T("idno")))
         {
            var raw = id.Value?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var key = ((string?)id.Attribute("type"))?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(key) && Regex.IsMatch(raw, @"10\.\d{4,9}/", RegexOptions.IgnoreCase))
               key = "doi";

            string val = (key.Equals("doi", StringComparison.OrdinalIgnoreCase)) ? CleanDoi(raw) : Normalize(raw);
            if (string.IsNullOrWhiteSpace(val)) continue;

            if (!dict.TryGetValue(key, out var list)) { list = new List<string>(); dict[key] = list; }
            list.Add(val);
         }
         return dict;
      }

      private static string? FirstId(Dictionary<string, List<string>> idnos, string key) =>
         (idnos.TryGetValue(key, out var list) && list.Count > 0) ? list[0] : null;

      private static string? GetFirstPtrTarget(XElement scope) =>
         scope.Descendants(T("ptr")).Select(p => (string?)p.Attribute("target")).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim();

      private static string CleanDoi(string raw)
      {
         if (string.IsNullOrWhiteSpace(raw)) return "";
         var s = Regex.Replace(raw.Trim(), @"^(doi:|https?://(dx\.)?doi\.org/)", "", RegexOptions.IgnoreCase).Trim();
         var m = Regex.Match(s, @"10\.\d{4,9}/[^\s<>""']+", RegexOptions.IgnoreCase);
         if (m.Success) s = m.Value;
         return s.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '>', '"', '\'').TrimStart('(', '[', '<', '"', '\'');
      }
   }
}