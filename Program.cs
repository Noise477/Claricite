using CiteCheck;
using CiteCheck.Grobid;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Utilities.CommandLine;

public class Program
{
   private sealed record PrintResult(List<string> Lines);

   private sealed class AppSettings
   {
      public GrobidSettings Grobid { get; set; } = new();
      public ApiSettings Apis { get; set; } = new();
      public ProcessingSettings Processing { get; set; } = new();
   }

   private sealed class GrobidSettings
   {
      public string BaseUrl { get; set; } = "https://redsox.uoa.auckland.ac.nz/grobid/";
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
      public string ConfigPath { get; set; } = DEFAULT_CONFIG_FILE;
      public string ConfigDirectory { get; set; } = Directory.GetCurrentDirectory();
      public string InputPath { get; set; } = "";
      public string OutputCsvPath { get; set; } = "";
      public string GrobidUrl { get; set; } = "";
      public string? OpenAlexApiKey { get; set; }
      public string? SemanticScholarApiKey { get; set; }
      public int MaxConcurrency { get; set; } = 10;
      public bool Verbose { get; set; } = false;
   }

   [STAThread]
   public static async Task<int> Main(string[] args)
   {
      AddOptions();

      if (args.Length == 0)
      {
         options.Usage();
         return 0;
      }

      if (options.Parse(args) == false)
      {
         options.Usage();
         return 1;
      }

      if (options.IsFlagOptionSet(OPT_VERSION))
      {
         Console.WriteLine(VERSION);
         return 0;
      }

      if (options.IsFlagOptionSet(OPT_HELP))
      {
         options.Usage();
         return 0;
      }

      AppSettings config;
      try
      {
         config = LoadConfig();
      }
      catch (Exception ex)
      {
         Console.WriteLine($"Error: failed to load {DEFAULT_CONFIG_FILE}. {ex.Message}");
         return 1;
      }

      if (TryBuildRuntimeSettings(config, out RuntimeSettings settings) == false)
      {
         options.Usage();
         return 1;
      }

      return await RunAsync(settings);
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

      Console.WriteLine($"Config: {settings.ConfigPath}");
      Console.WriteLine($"Input: {settings.InputPath}");
      Console.WriteLine($"Output: {settings.OutputCsvPath}");
      Console.WriteLine($"Grobid: {settings.GrobidUrl}");
      Console.WriteLine($"PDF count: {pdfFiles.Count}");

      using var grobidClient = new GrobidClient(settings.GrobidUrl);

      try
      {
         Console.WriteLine($"Checking Grobid service at {settings.GrobidUrl}...");
         if (!await grobidClient.IsAliveAsync())
         {
            Console.WriteLine("Error: Grobid server is not alive.");
            return 1;
         }
      }
      catch (Exception ex)
      {
         Console.WriteLine($"Error: could not connect to Grobid. {ex.Message}");
         return 1;
      }

      var notFoundSummary = new ConcurrentDictionary<string, ConcurrentBag<int>>();

      foreach (string pdfPath in pdfFiles)
      {
         string fileName = Path.GetFileName(pdfPath);

         List<XmlParseResult> references;
         try
         {
            Console.WriteLine($"\nProcessing PDF: {fileName}...");
            string teiXml = await grobidClient.ProcessReferencesAsync(pdfPath);
            references = xmlParser.ParseTeiString(teiXml, fileName);
            Console.WriteLine($"Extracted {references.Count} references from PDF.");
         }
         catch (Exception ex)
         {
            Console.WriteLine($"Grobid processing failed for {fileName}: {ex.Message}");
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

         Console.WriteLine($"Finished PDF: {fileName}");
      }

      Console.WriteLine("\nVerification complete.");

      if (notFoundSummary.IsEmpty)
      {
         Console.WriteLine("Success: all references were verified.");
         return 0;
      }

      WriteSummaryCsv(settings.OutputCsvPath, notFoundSummary);
      return 0;
   }

   private static async Task<PrintResult> ProcessReferenceAsync(
      XmlParseResult reference,
      string fileName,
      int index,
      RuntimeSettings settings,
      ConcurrentDictionary<string, ConcurrentBag<int>> notFoundSummary)
   {
      var verifyInput = new VerifyInput(reference.DOI, reference.Title, reference.Authors, reference.Year, reference.Url);

      var verifyResult = await Verifier.CheckExistenceAsync(
         verifyInput,
         settings.OpenAlexApiKey,
         settings.SemanticScholarApiKey);

      if (!verifyResult.Exists)
      {
         notFoundSummary.GetOrAdd(fileName, _ => new ConcurrentBag<int>()).Add(index);
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
      options.UsageString = "[options] <full-path-to-pdf-file-or-folder-containing-pdfs>";
      options.IgnoreCase = true;

      options.AddFlag(OPT_HELP, "print this option summary");
      options.AddFlag(OPT_VERSION, "print the current version");
      options.AddValue(OPT_OUTPUT, "path to output csv file", "", "csvFile");
      options.AddFlag(OPT_VERBOSE, "print verification trace");
   }

   private static AppSettings LoadConfig()
   {
      string configPath = Path.Combine(AppContext.BaseDirectory, DEFAULT_CONFIG_FILE);

      if (!File.Exists(configPath))
      {
         throw new FileNotFoundException("Config file not found.", configPath);
      }

      string json = File.ReadAllText(configPath);
      var settings = JsonSerializer.Deserialize<AppSettings>(json, jsonOptions);

      if (settings == null)
      {
         throw new InvalidOperationException("Config file is empty or invalid.");
      }

      return settings;
   }

   private static bool TryBuildRuntimeSettings(AppSettings config, out RuntimeSettings settings)
   {
      settings = new RuntimeSettings();

      if (options.NumberOfArguments != 1)
      {
         Console.WriteLine("Error: you must provide exactly one input path.");
         Console.WriteLine("The input path must be either:");
         Console.WriteLine("  1) a full path to one PDF file");
         Console.WriteLine("  2) a full path to one folder containing PDF files");
         return false;
      }

      string inputPath = options.GetArgument(0);
      if (string.IsNullOrWhiteSpace(inputPath))
      {
         Console.WriteLine("Error: input path is empty.");
         return false;
      }

      string outputCsvPath = options[OPT_OUTPUT];
      if (string.IsNullOrWhiteSpace(outputCsvPath))
      {
         Console.WriteLine("Error: missing -output.");
         return false;
      }

      bool verbose = config.Processing.Verbose;
      if (options.IsFlagOptionSet(OPT_VERBOSE))
      {
         verbose = true;
      }

      string configPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, DEFAULT_CONFIG_FILE));
      string configDirectory = AppContext.BaseDirectory;
      string resolvedInputPath = ResolvePath(Directory.GetCurrentDirectory(), inputPath);
      string resolvedOutputCsvPath = ResolvePath(Directory.GetCurrentDirectory(), outputCsvPath);

      settings = new RuntimeSettings
      {
         ConfigPath = configPath,
         ConfigDirectory = configDirectory,
         InputPath = resolvedInputPath,
         OutputCsvPath = resolvedOutputCsvPath,
         GrobidUrl = config.Grobid.BaseUrl,
         OpenAlexApiKey = config.Apis.OpenAlexApiKey,
         SemanticScholarApiKey = config.Apis.SemanticScholarApiKey,
         MaxConcurrency = config.Processing.MaxConcurrency > 0 ? config.Processing.MaxConcurrency : 10,
         Verbose = verbose
      };

      if (string.IsNullOrWhiteSpace(settings.GrobidUrl))
      {
         Console.WriteLine("Error: Grobid.BaseUrl is empty in appsettings.json.");
         return false;
      }

      if (!File.Exists(settings.InputPath) && !Directory.Exists(settings.InputPath))
      {
         Console.WriteLine("Error: input path does not exist.");
         Console.WriteLine(settings.InputPath);
         return false;
      }

      if (File.Exists(settings.InputPath) &&
          !string.Equals(Path.GetExtension(settings.InputPath), ".pdf", StringComparison.OrdinalIgnoreCase))
      {
         Console.WriteLine("Error: input file is not a PDF file.");
         Console.WriteLine(settings.InputPath);
         return false;
      }

      return true;
   }

   private static string ResolvePath(string baseDirectory, string path)
   {
      if (string.IsNullOrWhiteSpace(path))
      {
         return path;
      }

      if (Path.IsPathRooted(path))
      {
         return Path.GetFullPath(path);
      }

      return Path.GetFullPath(Path.Combine(baseDirectory, path));
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

   private static void WriteSummaryCsv(string outputCsvPath, ConcurrentDictionary<string, ConcurrentBag<int>> notFoundSummary)
   {
      string? outDir = Path.GetDirectoryName(outputCsvPath);
      if (!string.IsNullOrWhiteSpace(outDir))
      {
         Directory.CreateDirectory(outDir);
      }

      try
      {
         using var writer = new StreamWriter(outputCsvPath);

         foreach (string fileName in notFoundSummary.Keys.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
         {
            if (notFoundSummary.TryGetValue(fileName, out ConcurrentBag<int>? ids))
            {
               IEnumerable<int> sortedIds = ids.OrderBy(id => id);
               writer.WriteLine($"{EscapeCsvField(fileName)},{string.Join(",", sortedIds)}");
            }
         }
         Console.WriteLine($"Summary saved to '{outputCsvPath}'");
      }
      catch (Exception ex)
      {
         Console.WriteLine(ex.Message);
      }
   }

   private static string EscapeCsvField(string? field)
   {
      if (string.IsNullOrEmpty(field))
      {
         return "";
      }

      if (field.Contains('"'))
      {
         field = field.Replace("\"", "\"\"");
      }

      return field.Contains(',') || field.Contains('"')
         ? $"\"{field}\""
         : field;
   }

   private const string VERSION = "10.04.26";
   private const string DEFAULT_CONFIG_FILE = "appsettings.json";

   private const string OPT_HELP = "help";
   private const string OPT_VERSION = "version";
   private const string OPT_OUTPUT = "output";
   private const string OPT_VERBOSE = "verbose";

   private static readonly OptionManager options = new();

   private static readonly JsonSerializerOptions jsonOptions = new()
   {
      PropertyNameCaseInsensitive = true,
      ReadCommentHandling = JsonCommentHandling.Skip,
      AllowTrailingCommas = true
   };
}
