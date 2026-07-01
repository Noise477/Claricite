// A command line option parser. S Manoharan.

using System.Collections.Generic;
using System;
using System.IO;

namespace Utilities.CommandLine
{
   sealed public class OptionManager
   {
      private class Option
      {
         internal enum Type
         {
            FlagOption, ValueOption
         } // enum Type

         internal enum Visibility
         {
            Public, Protected, Private
         } // enum Visibility

         internal string? optionName;
         internal string? description;
         internal Visibility visibility = Visibility.Private;
         internal Type optionType = Type.FlagOption;
         internal string? optionValue;
         internal string? valueName;
      } // class Option

      public OptionManager()
      {
         UsageString = "[options] arguments";
         OptionMarker = '-';
      } // OptionManager

      public void LoadOptionFile(string path)
      {
         if (File.Exists(path))
         {
            foreach (var rawLine in File.ReadAllLines(path))
            {
               var line = rawLine.Trim();

               if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                  continue;

               var parts = line.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
               if (parts.Length == 0)
                  continue;

               string optionName = parts[0][1..];
               string? optionValue = (parts.Length > 1) ? parts[1] : null;

               foreach (var opt in options)
               {
                  if (opt.visibility == Option.Visibility.Private)
                     continue;

                  string optionNameToTest = IgnoreCase ? optionName.ToUpperInvariant() : optionName;
                  string? thisOptionName = IgnoreCase ? opt.optionName!.ToUpperInvariant() : opt.optionName;
                  if (thisOptionName!.StartsWith(optionNameToTest, StringComparison.Ordinal))
                  {
                     if (opt.optionType == Option.Type.FlagOption)
                     {
                        opt.optionValue = "";
                        if (optionValue != null)
                        {
                           Console.WriteLine($"Warning: ignoring value [{optionValue}] supplied to flag option -{opt.optionName}");
                        }
                     }
                     else
                     {
                        opt.optionValue = optionValue;
                     }

                     break;
                  }
               }
            }
         } // file exists
      } // LoadOptionFile

      public bool Parse(string[] argsIn)
      {
         bool rval = true;
         args = AllowArgsBeforeOptions ? GetNormalisedArgs(argsIn, OptionMarker.ToString()) : argsIn;
         for (int i = 0; i < args.Length; ++i)
         {
            string s = args[i];
            string doubleOpt = OptionMarker.ToString() + OptionMarker.ToString();
            if (s == doubleOpt || s == OptionMarker.ToString())
            {
               argStart = i + 1;
               break;
            }
            if (s.StartsWith(OptionMarker.ToString(), StringComparison.Ordinal))
            {
               string optionName = s[1..];
               int colonIndx = optionName.IndexOf(':');
               if (colonIndx > 0)
               {
                  optionName = optionName[..colonIndx];
               }
               string optionNameToTest = IgnoreCase ? optionName.ToUpperInvariant() : optionName;
               int foundOptname = 0;
               foreach (Option opt in options)
               {
                  if (Option.Visibility.Private == opt.visibility)
                     continue; // private options cannot be used
                  string? thisOptionName = IgnoreCase ? opt.optionName!.ToUpperInvariant() : opt.optionName;
                  if (thisOptionName!.StartsWith(optionNameToTest, StringComparison.Ordinal))
                  {
                     if (opt.optionType == Option.Type.FlagOption)
                     {
                        opt.optionValue = "";
                     }
                     else
                     {
                        opt.optionValue = (i + 1 < args.Length) ? args[i + 1] : null;
                        ++i; //skip over the value
                     }
                     ++foundOptname;
                  }
               }
               argStart = i + 1;
               if (foundOptname == 0)
               {
                  Console.WriteLine("unknown option -{0}", optionName);
                  rval = false;
               }
               else if (foundOptname > 1)
               {
                  Console.WriteLine("option matches more than one available -{0}", optionName);
                  rval = false;
               }
            }
            else //if (i == 0) // First parameter is a non-option
            {
               // Done with the options. The arguments start here.
               break;
            }
         }
         return rval;
      } // Parse

      private void Add(string optionName, Option.Type optionType, string description,
         string optionValue, string valueName, Option.Visibility visibility)
      {
         var opt = new Option
         {
            optionName = optionName,
            optionType = optionType,
            description = description,
            optionValue = optionValue,
            valueName = valueName,
            visibility = visibility
         };
         options.Add(opt);
      } // Add

      public void AddFlag(string optionName, string description)
      {
         Add(optionName, Option.Type.FlagOption, description, null!, null!,
            Option.Visibility.Public);
      } // AddFlag

      public void AddProtectedFlag(string optionName, string description)
      {
         Add(optionName, Option.Type.FlagOption, description, null!, null!,
            Option.Visibility.Protected);
      } // AddProtectedFlag

      public void AddValue(string optionName, string description,
         string optionValue, string valueName)
      {
         Add(optionName, Option.Type.ValueOption, description, optionValue,
            valueName, Option.Visibility.Public);
      } // AddValue

      public void AddProtectedValue(string optionName, string description,
        string optionValue, string valueName)
      {
         Add(optionName, Option.Type.ValueOption, description, optionValue,
            valueName, Option.Visibility.Protected);
      } // AddProtectedValue

      public string this[string optionName]
      {
         get
         {
            string? rval = null;
            foreach (Option opt in options)
            {
               if (String.Compare(opt.optionName, optionName, Comparison) == 0)
               {
                  if (Option.Type.ValueOption == opt.optionType)
                  {
                     rval = opt.optionValue;
                  }
                  else
                  {
                     throw new Exception("Flag options have no value");
                  }
               }
            }
            return rval!;
         }
         set
         {
            bool setDone = false;
            foreach (Option opt in options)
            {
               if (String.Compare(opt.optionName, optionName, Comparison) == 0)
               {
                  if (Option.Type.ValueOption == opt.optionType)
                  {
                     opt.optionValue = (string)value;
                     setDone = true;
                     break;
                  }
                  throw new Exception("Flag options have no value");
               }
            }
            if (!setDone)
            {
               throw new Exception("Invalid option");
            }
         }
      } // this[string]

      public bool IsFlagOptionSet(string optionName)
      {
         bool rval = false;
         foreach (Option opt in options)
         {
            if (String.Compare(opt.optionName, optionName, Comparison) == 0)
            {
               if (Option.Type.FlagOption == opt.optionType)
               {
                  if (opt.optionValue != null)
                  {
                     rval = true;
                  }
               }
               else
               {
                  throw new Exception("Not a flag option");
               }
            }
         }
         return rval;
      } // IsFlagOptionSet

      public void SetFlagOption(string optionName)
      {
         bool done = false;
         foreach (Option opt in options)
         {
            if (String.Compare(opt.optionName, optionName, Comparison) == 0)
            {
               if (Option.Type.FlagOption == opt.optionType)
               {
                  opt.optionValue = "";
                  done = true;
               }
               else
               {
                  throw new Exception("Not a flag option");
               }
            }
         }
         if (!done)
         {
            throw new Exception("Invalid option");
         }
      } // SetFlagOption

      public string GetArgument(int i)
      {
         if (argStart + i < args!.Length)
         {
            return args[argStart + i];
         }
         return null!;
      } // GetArgument

      public int NumberOfArguments
      {
         get
         {
            return (argStart < args!.Length) ? (args.Length - argStart) : 0;
         }
      } // NumberOfArguments

      public string[] Arguments
      {
         get
         {
            var rval = new List<string>();
            for (int a = 0; a < NumberOfArguments; ++a)
            {
               rval.Add(GetArgument(a));
            }
            return rval.ToArray();
         }
      } // Arguments

      public static string ProgName
      {
         get
         {
            string path = Environment.GetCommandLineArgs()[0];
            string exename = System.IO.Path.GetFileNameWithoutExtension(path);
            return exename;
         }
      } // ProgName

      public string UsageString
      {
         get;
         set;
      } // UsageString

      public char OptionMarker
      {
         get;
         set;
      } // OptionMarker

      public bool IgnoreCase
      {
         get;
         set;
      } // IgnoreCase

      public bool AllowArgsBeforeOptions
      {
         get;
         set;
      } // AllowArgsBeforeOptions

      public void Usage()
      {
         Console.WriteLine("{0} {1}", ProgName, UsageString);
         foreach (Option opt in options)
         {
            if (Option.Visibility.Public != opt.visibility)
               continue;
            if (Option.Type.ValueOption == opt.optionType)
            {
               Console.WriteLine("\t{0}{1} {2} ({3})",
                                 OptionMarker, opt.optionName, opt.valueName, opt.description);
            }
            else
            {
               Console.WriteLine("\t{0}{1} ({2})",
                                 OptionMarker, opt.optionName, opt.description);
            }
         }
      } // Usage

      public void Values()
      {
         foreach (Option opt in options)
         {
            if (Option.Visibility.Public != opt.visibility)
               continue;
            if (Option.Type.ValueOption == opt.optionType)
            {
               Console.WriteLine("\t{0}\t{1}", opt.optionName, opt.optionValue ?? "(null)");
            }
         }
      } // Values

      // The OptionManager views the command line argument world as:  [options] files
      // If the format:  files [options] is supplied and AllowArgsBeforeOptions == true, 
      // the OptionManager will convert to the internal format before processing.
      private static string[] GetNormalisedArgs(IEnumerable<string> args, string optionMarker)
      {
         // if non option args come at the beginning we need to push them to the end
         var optionArgs = new List<string>();
         var nonOptionArgs = new List<string>();

         bool foundFirstOptionArg = false;
         foreach (string s in args)
         {
            if (!foundFirstOptionArg && s.StartsWith(optionMarker, StringComparison.Ordinal))
            {
               foundFirstOptionArg = true;
            }
            if (!foundFirstOptionArg)
            {
               nonOptionArgs.Add(s);
            }
            else
            {
               optionArgs.Add(s);
            }
         }
         optionArgs.AddRange(nonOptionArgs);
         return optionArgs.ToArray();
      } // SetNormalisedArgs

      private StringComparison Comparison
      {
         get
         {
            return IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
         }
      } // Comparison

      private readonly List<Option> options = new();
      private string[]? args;
      private int argStart;
   } // class OptionManager
} // namespace Utilities.CommandLine
