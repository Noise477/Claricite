using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AcademicParsing;

public record AcademicReference(
    string RawCitation,
    string? Title,
    List<string> Authors,
    string? Doi,
    string? ArxivId,
    List<string> Urls,
    int OriginalNumber,
    string? SkipReason
);

public partial class ReferenceExtractor
{
   [GeneratedRegex(@"(?i)(?:^|\n|\.\s*)\s*(?:References|Bibliography|Works\s+Cited)\s*(?=\n|\[1\]|1\.)")]
   private static partial Regex SectionHeaderRegex();

   [GeneratedRegex(@"(?i)\n\s*(?:Appendix(?:\s+[A-Z0-9]|\s*\n|\s*$)|Acknowledgments|Acknowledgements|Supplementary|Ethical\s+Considerations|Broader\s+Impact|(?:\w+\s+)?(?:Paper\s+)?Checklist|[A-Z]\n\s*(?:Appendix|Technical|Proofs?|Additional|Extended|Experimental|Derivations?|Algorithms?|Detailed?)|[A-Z]\.\s*\n\s*(?:Prompt|Annotation|Evaluation|Training|Baseline)|[A-Z]\.\d+\s*\n)")]
   private static partial Regex SectionEndRegex();

   [GeneratedRegex(@"\n\s*\[\d+\]")]
   private static partial Regex BracketMarkerRegex();

   [GeneratedRegex(@"(?m)(?:USENIX\s+Association\s*\n?\s*)?(?:\d+\s+)?\d+(?:st|nd|rd|th)\s+USENIX\s+(?:Security\s+Symposium|OSDI|ATC|NSDI|HotCloud|WOOT)")]
   private static partial Regex UsenixHeaderRegex();

   [GeneratedRegex(@"(?m)^\s*(?:\d+\s+)?(?:IEEE\s+)?(?:Symposium\s+on\s+Security\s+and\s+Privacy|S&P|EuroS&P)(?:\s+\d{4})?(?:\s+\d+)?\s*$")]
   private static partial Regex IeeeHeaderRegex();

   [GeneratedRegex(@"(?:[A-Z][^\[.]*?)?Proceedings on Privacy Enhancing Technologies\s+\d{4}\(\d+\)")]
   private static partial Regex PopetsHeaderRegex();

   [GeneratedRegex(@"(?i)Authorized licensed use limited to:.*?Restrictions apply\.")]
   private static partial Regex IeeeFooterRegex();

   [GeneratedRegex(@"(?m)(?:^|\n|[\.\]0-9])\s*\[(\d+)\]\s*")]
   private static partial Regex IeeeSegmentRegex();

   [GeneratedRegex(@"(?m)(?:^|\n)\s*(\d{1,3})\.\s+")]
   private static partial Regex NumberedSegmentRegex();

   [GeneratedRegex(@"([a-z0-9)/]|[A-Z]{2})\.\n(?:\d{1,4}\n)?\s*([a-zA-Z\u00C0-\u024F\- ]+,\s+[A-Z]\.)")]
   private static partial Regex AaaiSegmentRegex();

   [GeneratedRegex(@"(?i)\b(?:19|20)\d{2}\b|doi\.org/|10\.\d{4,9}/|arxiv[:\.]|https?://|in\s+(?:proc|proceedings)|journal\s+of|symposium\s+on|usenix|ndss|acm|ieee|rfc\s*\d+")]
   private static partial Regex AcademicMarkerRegex();

   [GeneratedRegex(@"(?i)(?:https?://(?:dx\.)?doi\.org/|doi:\s*)?(10\.\d{4,9}/[^\s""<>]+)")]
   private static partial Regex DoiRegex();

   [GeneratedRegex(@"(?i)arxiv[:\.]\s*(\d{4}\.\d{4,5}(?:v\d+)?|[a-z\-]+(?:\.[A-Z]{2})?/\d{7}(?:v\d+)?)")]
   private static partial Regex ArxivRegex();

   [GeneratedRegex(@"https?://[^\s<>""']+")]
   private static partial Regex UrlRegex();

   [GeneratedRegex(@"\s+")]
   private static partial Regex WhitespaceRegex();

   private readonly int _minTitleWords = 3;

   public List<AcademicReference> Extract(string pdfText)
   {
      string? sectionText = FindReferencesSection(pdfText);
      if (string.IsNullOrWhiteSpace(sectionText))
         return new List<AcademicReference>();

      sectionText = StripPageHeaders(sectionText);
      var segments = SegmentReferences(sectionText);

      var references = new List<AcademicReference>();
      List<string> previousAuthors = new();

      for (int i = 0; i < segments.Count; i++)
      {
         var parsed = ParseSingleReference(
             segments[i].Text,
             segments[i].Number ?? i + 1,
             previousAuthors
         );

         if (parsed.SkipReason == null && parsed.Authors.Count > 0)
            previousAuthors = parsed.Authors;

         references.Add(parsed);
      }

      return references;
   }

   private string? FindReferencesSection(string text, double fallbackFraction = 0.7)
   {
      var matches = SectionHeaderRegex().Matches(text);
      Match? bestMatch = null;

      if (matches.Count > 1)
      {
         bestMatch = matches.OrderByDescending(m =>
             BracketMarkerRegex().Matches(text[(m.Index + m.Length)..]).Count
         ).First();
      }
      else if (matches.Count == 1)
      {
         bestMatch = matches[0];
      }

      if (bestMatch != null)
      {
         string rest = text[(bestMatch.Index + bestMatch.Length)..];
         var endMatch = SectionEndRegex().Match(rest);
         int refEnd = endMatch.Success ? endMatch.Index : rest.Length;
         return rest[..refEnd];
      }

      int cutoff = (int)(text.Length * fallbackFraction);
      return text[cutoff..];
   }

   private string StripPageHeaders(string text)
   {
      string result = IeeeFooterRegex().Replace(text, "\n");
      result = UsenixHeaderRegex().Replace(result, "\n");
      result = IeeeHeaderRegex().Replace(result, "\n");
      result = PopetsHeaderRegex().Replace(result, "\n");
      return result;
   }

   private record Segment(string Text, int? Number);
   private record StrategyResult(string Name, List<Segment> Segments, double SpecificityScore);

   private List<Segment> SegmentReferences(string text)
   {
      var strategies = new List<StrategyResult>();

      var ieeeRefs = TryIeeeFormat(text);
      if (ieeeRefs != null)
         strategies.Add(new StrategyResult("IEEE", ieeeRefs, 1.0));

      var numberedRefs = TryNumberedFormat(text);
      if (numberedRefs != null)
         strategies.Add(new StrategyResult("Numbered", numberedRefs, 0.95));

      var aaaiRefs = TryAaaiFormat(text);
      if (aaaiRefs != null)
         strategies.Add(new StrategyResult("AAAI", aaaiRefs, 0.8));

      var fallbackRefs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
          .Where(s => s.Trim().Length > 20)
          .Select(s => new Segment(s.Trim(), null))
          .ToList();

      if (fallbackRefs.Count > 0)
         strategies.Add(new StrategyResult("Fallback", fallbackRefs, 0.3));

      if (strategies.Count == 0)
         return new List<Segment>();

      var best = strategies.OrderByDescending(s =>
          s.SpecificityScore * s.Segments.Count(seg => LooksLikeReference(seg.Text))
      ).First();

      bool skipProseFilter = best.Name is "IEEE" or "Numbered";

      return best.Segments
          .Where(seg => skipProseFilter || LooksLikeReference(seg.Text))
          .ToList();
   }

   private List<Segment>? TryIeeeFormat(string text)
   {
      var matches = IeeeSegmentRegex().Matches(text);
      if (matches.Count < 3) return null;

      var firstNums = matches.Take(5)
          .Select(m => int.TryParse(m.Groups[1].Value, out int n) ? n : -1)
          .ToList();

      if (firstNums.Count == 0 || firstNums[0] != 1) return null;

      for (int i = 0; i < firstNums.Count - 1; i++)
      {
         if (firstNums[i + 1] != firstNums[i] + 1)
            return null;
      }

      var refs = new List<Segment>();

      for (int i = 0; i < matches.Count; i++)
      {
         int start = matches[i].Index + matches[i].Length;
         int end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;

         string content = text[start..end].Trim();

         if (!string.IsNullOrWhiteSpace(content))
            refs.Add(new Segment(content, int.Parse(matches[i].Groups[1].Value)));
      }

      return refs;
   }

   private List<Segment>? TryNumberedFormat(string text)
   {
      var matches = NumberedSegmentRegex().Matches(text);
      if (matches.Count < 3) return null;

      var firstNums = matches.Take(5)
          .Select(m => int.TryParse(m.Groups[1].Value, out int n) ? n : -1)
          .ToList();

      if (firstNums.Count == 0 || firstNums[0] != 1) return null;

      for (int i = 0; i < firstNums.Count - 1; i++)
      {
         if (firstNums[i + 1] != firstNums[i] + 1)
            return null;
      }

      var refs = new List<Segment>();

      for (int i = 0; i < matches.Count; i++)
      {
         int start = matches[i].Index + matches[i].Length;
         int end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;

         refs.Add(new Segment(text[start..end].Trim(), int.Parse(matches[i].Groups[1].Value)));
      }

      return refs;
   }

   private List<Segment>? TryAaaiFormat(string text)
   {
      var matches = AaaiSegmentRegex().Matches(text);
      if (matches.Count < 3) return null;

      var refs = new List<Segment>();

      for (int i = 0; i < matches.Count; i++)
      {
         int start = matches[i].Groups[2].Index;
         int end = i + 1 < matches.Count
             ? matches[i + 1].Groups[1].Index + matches[i + 1].Groups[1].Length
             : text.Length;

         refs.Add(new Segment(text[start..end].Trim(), null));
      }

      return refs;
   }

   private bool LooksLikeReference(string segment)
   {
      string trimmed = segment.Trim();
      if (string.IsNullOrWhiteSpace(trimmed)) return false;

      char first = trimmed[0];
      if (first is ',' or ';' or '!' or '.') return false;

      if (AcademicMarkerRegex().IsMatch(trimmed)) return true;

      return trimmed.Length >= 40 && Regex.IsMatch(trimmed, @"\b(?:19|20)\d{2}\b");
   }

   private AcademicReference ParseSingleReference(string refText, int originalNumber, List<string> prevAuthors)
   {
      refText = IeeeFooterRegex().Replace(refText, "");
      refText = Regex.Replace(refText, @"-\s+", "");
      refText = Regex.Replace(refText, @"(https?://\S+)\s+([a-zA-Z0-9\-_/?=&.]+)", "$1$2");

      string rawCitation = WhitespaceRegex().Replace(refText, " ").Trim();
      rawCitation = Regex.Replace(rawCitation, @"^\[\d+\]\s*", "");
      rawCitation = Regex.Replace(rawCitation, @"^\d+\.\s*", "");

      var doiMatch = DoiRegex().Match(rawCitation);
      string? doi = doiMatch.Success
          ? CleanDoi(doiMatch.Groups[1].Value)
          : null;

      string? arxivId = ArxivRegex().Match(rawCitation).Success
          ? ArxivRegex().Match(rawCitation).Value.TrimEnd('.', ',', ';')
          : null;

      var urls = UrlRegex().Matches(rawCitation)
          .Select(m => m.Value.TrimEnd('.', ',', ';', ')', ']'))
          .Where(url => !url.Contains("doi.org", StringComparison.OrdinalIgnoreCase))
          .Distinct()
          .ToList();

      var parsedMain = ExtractMainFields(rawCitation, prevAuthors);

      string? title = parsedMain.Title;
      List<string> authors = parsedMain.Authors;

      int titleWords = string.IsNullOrWhiteSpace(title)
          ? 0
          : title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

      string? skipReason = null;

      if (titleWords < _minTitleWords)
      {
         bool hasStrongSignal =
             doi != null ||
             arxivId != null ||
             urls.Count > 0 ||
             LooksLikeReference(refText);

         if (!hasStrongSignal)
            skipReason = "short_title";
      }

      return new AcademicReference(
          rawCitation,
          title,
          authors,
          doi,
          arxivId,
          urls,
          originalNumber,
          skipReason
      );
   }

   private static string? CleanDoi(string? doi)
   {
      if (string.IsNullOrWhiteSpace(doi)) return null;
      return doi.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '>', '"', '\'').TrimStart('(', '[', '<', '"', '\'');
   }

   private record MainFields(string? Title, List<string> Authors);

   private MainFields ExtractMainFields(string text, List<string> prevAuthors)
   {
      if (text.StartsWith("——") || text.StartsWith("---") || text.StartsWith("--"))
      {
         string? repeatedTitle = ExtractTitleHeuristics(text);
         return new MainFields(repeatedTitle, prevAuthors);
      }

      var quoted = ExtractQuotedReference(text);
      if (quoted != null)
         return quoted;

      var book = ExtractBookReference(text);
      if (book != null)
         return book;

      var fallbackTitle = ExtractTitleHeuristics(text);
      var fallbackAuthors = ExtractAuthorsHeuristics(text, prevAuthors);

      return new MainFields(fallbackTitle, fallbackAuthors);
   }

   private MainFields? ExtractQuotedReference(string text)
   {
      var quoteMatch = Regex.Match(text, "[\"“”](.*?)[\"“”]");
      if (!quoteMatch.Success) return null;

      string title = quoteMatch.Groups[1].Value.Trim().TrimEnd(',');

      string authorsPart = text[..quoteMatch.Index].Trim().TrimEnd(',');

      var authors = SplitAuthors(authorsPart);

      return new MainFields(title, authors);
   }

   private MainFields? ExtractBookReference(string text)
   {
      var match = Regex.Match(
          text,
          @"^(?<authors>.+?),\s+(?<title>.+?)(?:,\s+\d+(?:st|nd|rd|th)\s+ed\.|\.\s+)(?<rest>.+?(?:19|20)\d{2})\.?$",
          RegexOptions.IgnoreCase
      );

      if (!match.Success)
      {
         match = Regex.Match(
             text,
             @"^(?<authors>.+?),\s+(?<title>.+?),\s+(?<year>(?:19|20)\d{2})\.?$",
             RegexOptions.IgnoreCase
         );
      }

      if (!match.Success) return null;

      string authorsPart = match.Groups["authors"].Value.Trim();
      string title = match.Groups["title"].Value.Trim().TrimEnd(',', '.');

      if (!LooksLikeAuthorBlock(authorsPart)) return null;

      return new MainFields(title, SplitAuthors(authorsPart));
   }

   private string? ExtractTitleHeuristics(string text)
   {
      var quoteMatch = Regex.Match(text, "[\"“”](.*?)[\"“”]");
      if (quoteMatch.Success)
         return quoteMatch.Groups[1].Value.Trim().TrimEnd(',');

      var commaParts = text.Split(new[] { ", " }, 3, StringSplitOptions.None);

      if (commaParts.Length >= 2 && LooksLikeAuthorBlock(commaParts[0]))
      {
         string candidate = commaParts[1].Trim();

         candidate = Regex.Replace(candidate, @"\s+(?:in|In)\s+\d{4}\s+.*$", "");
         candidate = Regex.Replace(candidate, @"\.\s+[A-Z].*$", "");
         candidate = candidate.Trim().TrimEnd(',', '.');

         return candidate;
      }

      var match = Regex.Match(text, @"(?<!\b[A-Z])\.\s");
      if (match.Success)
      {
         var parts = text[(match.Index + 2)..]
             .Split(new[] { ". ", "? " }, StringSplitOptions.RemoveEmptyEntries);

         if (parts.Length > 0)
            return parts[0].Trim().TrimEnd(',', '.');
      }

      return null;
   }

   private List<string> ExtractAuthorsHeuristics(string text, List<string> prevAuthors)
   {
      if (text.StartsWith("——") || text.StartsWith("---") || text.StartsWith("--"))
         return prevAuthors;

      string authorsPart = "";

      var quoteMatch = Regex.Match(text, "[\"“”]");
      if (quoteMatch.Success)
      {
         authorsPart = text[..quoteMatch.Index];
      }
      else
      {
         int firstComma = text.IndexOf(',');
         if (firstComma > 0)
         {
            string beforeComma = text[..firstComma].Trim();
            if (LooksLikeAuthorBlock(beforeComma))
               authorsPart = beforeComma;
         }

         if (string.IsNullOrWhiteSpace(authorsPart))
         {
            var match = Regex.Match(text, @"(?<!\b[A-Z])\.\s");
            if (match.Success)
               authorsPart = text[..match.Index];
         }
      }

      authorsPart = authorsPart.Trim().TrimEnd(',', '.');

      if (string.IsNullOrWhiteSpace(authorsPart))
         return new List<string>();

      return SplitAuthors(authorsPart);
   }

   private static bool LooksLikeAuthorBlock(string text)
   {
      if (string.IsNullOrWhiteSpace(text)) return false;

      string t = text.Trim();

      if (t.Contains(" et al.", StringComparison.OrdinalIgnoreCase) ||
          t.Contains(" et al", StringComparison.OrdinalIgnoreCase))
         return true;

      if (Regex.IsMatch(t, @"^[A-Z]\.\s*[A-Z]?\s*[\p{L}'\-]+"))
         return true;

      if (Regex.IsMatch(t, @"^[\p{L}'\-]+,\s*[A-Z]\."))
         return true;

      if (Regex.IsMatch(t, @"\band\b") && Regex.IsMatch(t, @"[A-Z]\."))
         return true;

      return false;
   }

   private static List<string> SplitAuthors(string authorsPart)
   {
      authorsPart = authorsPart.Trim().TrimEnd(',', '.');

      if (string.IsNullOrWhiteSpace(authorsPart))
         return new List<string>();

      if (authorsPart.Contains(" et al", StringComparison.OrdinalIgnoreCase))
         return new List<string> { authorsPart };

      return Regex.Split(authorsPart, @"\s+and\s+|,\s+(?=[A-Z]\.)")
          .Select(a => a.Trim())
          .Where(a => !string.IsNullOrWhiteSpace(a))
          .ToList();
   }
}