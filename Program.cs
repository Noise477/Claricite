using ClariCite.Grobid;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Utilities.CommandLine;

namespace ClariCite;
public class Program
{
   private sealed record PrintResult(List<string> Lines);

   private sealed class GrobidSettings
   {
      public string BaseUrl { get; set; } = string.Empty;
   }

   private sealed class ApiSettings
   {
      public string? OpenAlexApiKey { get; set; }
      public string? SemanticScholarApiKey { get; set; }
   }

   private sealed class ProcessingSettings
   {
      public int MaxConcurrency { get; set; } = 10;
      public bool Verbose { get; set; } = false;
   }

   private sealed class RuntimeSettings
   {
      public string InputPath { get; set; } = string.Empty;
      public string OutputFilePath { get; set; } = string.Empty;
      public string GrobidUrl { get; set; } = string.Empty;
      public string? OpenAlexApiKey { get; set; }
      public string? SemanticScholarApiKey { get; set; }
      public int MaxConcurrency { get; set; }
      public bool Verbose { get; set; } = false;
      public string ReferenceExtractor { get; set; } = REFERENCE_EXTRACTOR;
      public string ReferenceStyle { get; set; } = REFERENCE_STYLE;
   }

   public static async Task Main(string[] args)
   {
      AddOptions();

      string exePath = Environment.ProcessPath!;
      string globalOptions = Path.ChangeExtension(exePath, ".options");
      if (File.Exists(globalOptions))
      {
         options.LoadOptionFile(globalOptions);
      }
      string localOptions = Path.ChangeExtension(Path.GetFileName(globalOptions), ".options");
      if (File.Exists(localOptions))
      {
         options.LoadOptionFile(localOptions);
      }

      if (options.Parse(args) == false)
      {
         options.Usage();
         return;
      }

      if (options.IsFlagOptionSet(OPT_VERSION))
      {
         Console.WriteLine(VERSION);
         return;
      }

      if (options.IsFlagOptionSet(OPT_HELP))
      {
         options.Usage();
         return;
      }

      var settings = GetRuntimeSettings();
      int noOfFiles = options.NumberOfArguments;
      for (int file = 0; file < noOfFiles; ++file)
      {
         settings.InputPath = options.GetArgument(file);
         await RunAsync(settings);
      }
   }

   private static async Task<int> RunAsync(RuntimeSettings settings)
   {
      List<string> pdfFiles = ResolvePdfFiles(settings.InputPath);

      pdfFiles = pdfFiles
         .Distinct(StringComparer.OrdinalIgnoreCase)
         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
         .ToList();

      if (pdfFiles.Count == 0)
      {
         Console.WriteLine("Error: no PDF files found in the specified input path.");
         return 1;
      }

      Console.WriteLine($"Input: {settings.InputPath}");
      Console.WriteLine($"Output: {settings.OutputFilePath}");
      Console.WriteLine($"Extractor: {settings.ReferenceExtractor}");
      if (settings.ReferenceExtractor.Equals("local", StringComparison.OrdinalIgnoreCase))
      {
         Console.WriteLine($"Style: {settings.ReferenceStyle}");
      }

      GrobidClient? grobidClient = null;

      if (settings.ReferenceExtractor.Equals("grobid", StringComparison.OrdinalIgnoreCase))
      {
         if (string.IsNullOrWhiteSpace(settings.GrobidUrl))
         {
            Console.WriteLine("Error: -grobidUrl is required when using -extractor grobid.");
            return 1;
         }

         //Console.WriteLine($"Grobid: {settings.GrobidUrl}");
         grobidClient = new GrobidClient(settings.GrobidUrl);

         try
         {
            //Console.WriteLine($"Checking Grobid service at {settings.GrobidUrl}...");
            if (!await grobidClient.IsAliveAsync())
            {
               Console.WriteLine("Error: Grobid server is not alive.");
               grobidClient.Dispose();
               return 1;
            }
         }
         catch (Exception ex)
         {
            Console.WriteLine($"Error: could not connect to Grobid. {ex.Message}");
            grobidClient.Dispose();
            return 1;
         }
      }
      else if (!settings.ReferenceExtractor.Equals("local", StringComparison.OrdinalIgnoreCase))
      {
         Console.WriteLine($"Error: unknown reference extractor '{settings.ReferenceExtractor}'. Use 'local' or 'grobid'.");
         return 1;
      }

      if (settings.ReferenceExtractor.Equals("local", StringComparison.OrdinalIgnoreCase) &&
          !TryParseReferenceStyle(settings.ReferenceStyle, out _))
      {
         Console.WriteLine($"Error: unknown reference style '{settings.ReferenceStyle}'. Use 'default', 'ieee', or 'acm'.");
         return 1;
      }

      //Console.WriteLine($"PDF count: {pdfFiles.Count}");

      var notFoundSummary = new ConcurrentDictionary<string, ConcurrentBag<XmlParseResult>>();

      foreach (string pdfPath in pdfFiles)
      {
         string fileName = Path.GetFileName(pdfPath);

         List<XmlParseResult> references;
         try
         {
            //Console.WriteLine($"\nProcessing PDF: {fileName}...");
            references = await ExtractReferencesAsync(pdfPath, fileName, settings, grobidClient);
            Console.WriteLine($"Extracted {references.Count} references from PDF.");
         }
         catch (Exception ex)
         {
            Console.WriteLine($"Reference extraction failed for {fileName}: {ex.Message}");
            continue;
         }

         int totalRefCount = references.Count;
         int printedCount = 0;

         var processorBlock = new TransformBlock<(XmlParseResult reference, string fileName, int index), PrintResult>(
            async item =>
            {
               var (reference, currentFileName, index) = item;
               return await ProcessReferenceAsync(reference, currentFileName, index, settings, notFoundSummary);
            },
            new ExecutionDataflowBlockOptions
            {
               MaxDegreeOfParallelism = Math.Max(1, settings.MaxConcurrency),
               EnsureOrdered = true
            });

         var printBlock = new ActionBlock<PrintResult>(
            result =>
            {
               int current = Interlocked.Increment(ref printedCount);
               lock (Console.Out)
               {
                  Console.WriteLine(string.Join(Environment.NewLine, result.Lines));
                  Console.WriteLine($"[Progress] {fileName}: {current}/{totalRefCount}");
               }
            },
            new ExecutionDataflowBlockOptions
            {
               MaxDegreeOfParallelism = 1
            });

         processorBlock.LinkTo(printBlock, new DataflowLinkOptions { PropagateCompletion = true });

         for (int i = 0; i < references.Count; ++i)
         {
            processorBlock.Post((references[i], fileName, i + 1));
         }

         processorBlock.Complete();
         await printBlock.Completion;

         //Console.WriteLine($"Finished PDF: {fileName}");
      } // foreach pdf

      grobidClient?.Dispose();

      Console.WriteLine($"{Environment.NewLine}Verification complete.");

      WriteSummaryFile(settings.OutputFilePath, notFoundSummary);
      return 0;
   }

   private static async Task<List<XmlParseResult>> ExtractReferencesAsync(
      string pdfPath,
      string fileName,
      RuntimeSettings settings,
      GrobidClient? grobidClient)
   {
      if (settings.ReferenceExtractor.Equals("grobid", StringComparison.OrdinalIgnoreCase))
      {
         if (grobidClient == null)
         {
            throw new InvalidOperationException("Grobid client is not initialised.");
         }

         string teiXml = await grobidClient.ProcessReferencesAsync(pdfPath);
         return xmlParser.ParseTeiString(teiXml, fileName);
      }

      if (settings.ReferenceExtractor.Equals("local", StringComparison.OrdinalIgnoreCase))
      {
         return ExtractReferencesWithLocal(pdfPath, fileName, settings.ReferenceStyle);
      }

      throw new InvalidOperationException($"Unknown reference extractor: {settings.ReferenceExtractor}");
   }

   private static List<XmlParseResult> ExtractReferencesWithLocal(string pdfPath, string fileName, string referenceStyle)
   {
      string fullText;

      using (PdfDocument document = PdfDocument.Open(pdfPath))
      {
         var pagesText = document.GetPages()
            .Select(p => ContentOrderTextExtractor.GetText(p));

         fullText = string.Join("\n", pagesText);
      }

      if (!TryParseReferenceStyle(referenceStyle, out LocalReferenceStyle parsedStyle))
      {
         throw new InvalidOperationException($"Unknown reference style: {referenceStyle}");
      }

      var extractor = new ReferenceExtractor(parsedStyle);
      var refs = extractor.Extract(fullText);

      return refs
         .Where(r => r.SkipReason == null)
         .Select(r => new XmlParseResult(
            SourceFile: fileName,
            DOI: r.Doi,
            Title: NormalizeForVerifier(r.Title),
            Authors: r.Authors
               .Select(NormalizeForVerifier)
               .Where(a => !string.IsNullOrWhiteSpace(a))
               .Select(a => a!)
               .ToList(),
            Year: ExtractYearFromRawCitation(r.RawCitation, parsedStyle),
            Url: r.Urls.FirstOrDefault()
         ))
         .ToList();
   }

   private static string? NormalizeForVerifier(string? s)
   {
      if (string.IsNullOrWhiteSpace(s)) return null;

      s = s.Trim()
         .Replace("“", "")
         .Replace("”", "")
         .Replace("\"", "");
      s = Regex.Replace(s, @"^\(?\b(?:19|20)\d{2}\)?[\.\:\,\s]+", "", RegexOptions.IgnoreCase);

      s = Regex.Replace(s, @"^\[Online\]\.?\s*", "", RegexOptions.IgnoreCase);
      s = Regex.Replace(s, @"^Available:\s*", "", RegexOptions.IgnoreCase);

      return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
   }

   private static string? ExtractYearFromRawCitation(
      string? rawCitation,
      LocalReferenceStyle referenceStyle)
   {
      if (string.IsNullOrWhiteSpace(rawCitation)) return null;

      var matches = Regex.Matches(rawCitation, @"\b(?:19|20)\d{2}\b");
      if (matches.Count == 0) return null;

      // ACM puts the publication year immediately after the author list.
      // Using the first year avoids mistaking retrieval dates or conference
      // dates later in the citation for the publication year.
      if (referenceStyle == LocalReferenceStyle.Acm)
         return matches[0].Value;

      // Preserve the original behaviour for default and IEEE modes.
      return matches[^1].Value;
   }

   private static async Task<PrintResult> ProcessReferenceAsync(
      XmlParseResult reference,
      string fileName,
      int index,
      RuntimeSettings settings,
      ConcurrentDictionary<string, ConcurrentBag<XmlParseResult>> notFoundSummary)
   {
      var verifyInput = new VerifyInput(reference.DOI, reference.Title, reference.Authors, reference.Year, reference.Url);

      var verifyResult = await Verifier.CheckExistenceAsync(
         verifyInput,
         settings.OpenAlexApiKey,
         settings.SemanticScholarApiKey);

      if (!verifyResult.Exists)
      {
         notFoundSummary.GetOrAdd(fileName, _ => new ConcurrentBag<XmlParseResult>()).Add(reference);
      }

      var lines = new List<string>();

      if (settings.Verbose)
      {
         foreach (VerifyTraceStep step in verifyResult.Trace)
         {
            lines.Add($"[{fileName} #{index}] {step.Source}: {(step.Found ? "FOUND" : "NOT FOUND")} ({step.Detail})");
         }
      }

      string idLabel = string.IsNullOrWhiteSpace(reference.DOI)
         ? $"Title: {reference.Title}"
         : $"DOI: {reference.DOI}";

      lines.Add($"[{fileName} #{index}] Overall: {(verifyResult.Exists ? "FOUND" : "NOT FOUND")} ({idLabel})");
      return new PrintResult(lines);
   }

   private static void AddOptions()
   {
      options.UsageString = "[options] pdf_files_or_folders";
      options.IgnoreCase = true;

      options.AddFlag(OPT_HELP, "print this option summary");
      options.AddFlag(OPT_VERSION, "print the current version");
      options.AddValue(OPT_OUTPUT, "path to output file", "output.html", "htmlFile");
      options.AddFlag(OPT_VERBOSE, "print verification trace");
      options.AddValue(OPT_EXTRACTOR, "reference extractor: local or grobid", REFERENCE_EXTRACTOR, "extractor");
      options.AddValue(OPT_STYLE, "local reference style: default, ieee, or acm", REFERENCE_STYLE, "style");

      options.AddValue(OPT_MAX_CONCURRENCY, "maximum number of concurrent API calls", "10", "int");
      options.AddValue(OPT_GROBID_URL, "URL of the GROBID server", "", "url");
      options.AddValue(OPT_OPEN_ALEX_KEY, "OpenAlex API key", "", "key");
      options.AddValue(OPT_SEMANTIC_SCHOLAR_KEY, "Semantic Scholar API key", "", "key");
   }


   private static RuntimeSettings GetRuntimeSettings()
   {
      var settings = new RuntimeSettings();

      settings = new RuntimeSettings
      {
         InputPath = string.Empty,
         OutputFilePath = options[OPT_OUTPUT],
         GrobidUrl = options[OPT_GROBID_URL],
         OpenAlexApiKey = options[OPT_OPEN_ALEX_KEY],
         SemanticScholarApiKey = options[OPT_SEMANTIC_SCHOLAR_KEY],
         MaxConcurrency = int.TryParse(options[OPT_MAX_CONCURRENCY], out int parsedMaxConcurrency) && parsedMaxConcurrency > 0 ? parsedMaxConcurrency : 10,
         Verbose = options.IsFlagOptionSet(OPT_VERBOSE),
         ReferenceExtractor = NormalizeExtractor(options[OPT_EXTRACTOR]),
         ReferenceStyle = NormalizeReferenceStyle(options[OPT_STYLE]),
      };

      return settings;
   }

   private static string NormalizeExtractor(string? extractor)
   {
      if (string.IsNullOrWhiteSpace(extractor))
      {
         return REFERENCE_EXTRACTOR;
      }

      return extractor.Trim().ToLowerInvariant();
   }

   private static string NormalizeReferenceStyle(string? style)
   {
      if (string.IsNullOrWhiteSpace(style))
      {
         return REFERENCE_STYLE;
      }

      return style.Trim().ToLowerInvariant();
   }

   private static bool TryParseReferenceStyle(string? style, out LocalReferenceStyle parsedStyle)
   {
      switch (NormalizeReferenceStyle(style))
      {
         case "default":
            parsedStyle = LocalReferenceStyle.Default;
            return true;
         case "ieee":
            parsedStyle = LocalReferenceStyle.Ieee;
            return true;
         case "acm":
            parsedStyle = LocalReferenceStyle.Acm;
            return true;
         default:
            parsedStyle = LocalReferenceStyle.Default;
            return false;
      }
   }

   private static List<string> ResolvePdfFiles(string inputPath)
   {
      if (File.Exists(inputPath))
      {
         if (string.Equals(Path.GetExtension(inputPath), ".pdf", StringComparison.OrdinalIgnoreCase))
         {
            return new List<string> { inputPath };
         }

         return new List<string>();
      }

      if (Directory.Exists(inputPath))
      {
         return Directory.EnumerateFiles(inputPath, "*.pdf", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
      }

      return new List<string>();
   }

   private static void WriteSummaryFile(string outputFilePath, ConcurrentDictionary<string, ConcurrentBag<XmlParseResult>> notFoundSummary)
   {
      string? outDir = Path.GetDirectoryName(outputFilePath);
      if (!string.IsNullOrWhiteSpace(outDir))
      {
         Directory.CreateDirectory(outDir);
      }
      if (!File.Exists(outputFilePath))
      {
         File.WriteAllText(outputFilePath, @"
<style>
body {
    font-family: system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
    margin: 3em auto;
    max-width: 850px;
    line-height: 1.5;
    color: #333;
    padding: 0 1em;
}

h1 {
    font-size: 1.6em;
    color: #1a202c;
    margin-top: 3em;
    padding-bottom: 0.4em;
    border-bottom: 2px solid #e2e8f0;
}

h1:first-of-type {
    margin-top: 1em;
}

h1 + p {
    font-size: 0.95em;
    color: #4a5568;
    margin-bottom: 1.5em;
}

h2 {
    background-color: #f8fafc;
    border-left: 4px solid #ef4444;
    margin: 1.5em 0 0 0;
    padding: 1em 1.2em 0.2em 1.2em;
    font-size: 1.05em;
    color: #1e293b;
}

h2 + p, 
h2 + p + p, 
h2 + p + p + p {
    background-color: #f8fafc;
    margin: 0;
    padding: 0.3em 1.2em;
    font-size: 0.9em;
}

h2 + p + p + p {
    padding-bottom: 1em;
    border-bottom: 1px solid #e2e8f0;
    border-right: 1px solid #e2e8f0;
    border-top: none;
    border-bottom-right-radius: 4px;
}

h2 ~ p a {
    color: #2563eb;
    text-decoration: none;
}
h2 ~ p a:hover {
    text-decoration: underline;
}

p strong {
    color: #64748b;
    font-weight: 600;
    display: inline-block;
    width: 70px;
}
</style>
         ");
      }

      try
      {
         using var writer = new StreamWriter(outputFilePath, append: true);
         HtmlReportWriter reportWriter = new();
         foreach (string fileName in notFoundSummary.Keys.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
         {
            reportWriter.Heading($"{fileName}", 1);
            if (notFoundSummary.TryGetValue(fileName, out ConcurrentBag<XmlParseResult>? references))
            {
               reportWriter.Paragraph(
                 $"Found **{references.Count}** potential unverifiable reference(s).");
               foreach (var reference in references)
               {
                  reportWriter.Heading($"[{reference.Title}] {reference.Title}", 2);

                  reportWriter.DoiProperty(reference.DOI);
                  reportWriter.LinkProperty("URL", reference.Url);
                  reportWriter.Property("Authors", string.Join(", ", reference.Authors).ToUpperInvariant());
               }
            }
         }
         writer.WriteLine(reportWriter.ToString());
         Console.WriteLine($"Summary appended to '{outputFilePath}'");
      }
      catch (Exception ex)
      {
         Console.WriteLine(ex.Message);
      }
   }

   private const string VERSION = "26.09.22";
   private const string REFERENCE_EXTRACTOR = "local";
   private const string REFERENCE_STYLE = "default";

   private const string OPT_HELP = "help";
   private const string OPT_VERSION = "version";
   private const string OPT_OUTPUT = "output";
   private const string OPT_VERBOSE = "verbose";
   private const string OPT_EXTRACTOR = "extractor";
   private const string OPT_STYLE = "style";
   private const string OPT_GROBID_URL = "grobidUrl";
   private const string OPT_OPEN_ALEX_KEY = "openAlexKey";
   private const string OPT_SEMANTIC_SCHOLAR_KEY = "semanticScholarKey";
   private const string OPT_MAX_CONCURRENCY = "maxConcurrency";

   private static readonly OptionManager options = new();

}
