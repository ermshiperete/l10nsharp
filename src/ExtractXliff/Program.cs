// Copyright (c) 2020 SIL International
// This software is licensed under the MIT License (http://opensource.org/licenses/MIT)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using L10NSharp;
using L10NSharp.CodeReader;
using L10NSharp.XLiffUtils;

namespace ExtractXliff
{
	/// <summary>
	/// This program uses the L10NSharp code for extracting static strings from one or
	/// more C# assemblies (either .dll or .exe). It requires command line arguments
	/// to set the internal XLIFF file element "original" attribute, to set the
	/// namespace beginning(s), and to set the output XLIFF filename. It also requires
	/// one or more assembly files to be specified on the command line. There are
	/// also optional command line arguments for specifying the XLIFF file element
	/// "datatype" attribute, the XLIFF file element "product-version" attribute, and
	/// an existing XLIFF file to merge from after reading everything from the input
	/// assemblies.
	/// </summary>
	class Program
	{
		delegate bool ProcessOption(string[] args, ref int i);

		static readonly List<string> _namespaces = new List<string>();   // namespace beginning(s) (-n)
		private static string _xliffOutputFilename;         // new XLIFF output file (-x)
		private static string _fileDatatype;                // file element attribute value (-d)
		private static string _fileOriginal;                // file element attribute value (-o)
		private static string _fileProductVersion;          // file element attribute value (-p)
		private static string _baseXliffFilename;           // input file that provides existing data to merge (-b)
		private static bool _verbose;                       // verbose console output (-v)
		private static bool _glob;
		private static bool _doneWithOptions;               // flag that either "--" or first assembly filename seen already
		private static readonly List<string> _assemblyFiles = new List<string>();	// input assembly file(s)
		// Tuple holds: namespace/class/method name(s) (specified using -m option)
		static readonly List<Tuple<string, string, string>> _additionalLocalizationMethodNames = new List<Tuple<string, string, string>>();
		private static readonly List<MethodInfo> _additionalLocalizationMethods = new List<MethodInfo>();

		const string kDefaultLangId = "en";
		const string kDefaultNewlineReplacement = "\\n";
		const string kDefaultAmpersandReplacement = "|amp|";

		static void Main(string[] args)
		{
			// Parse and check the command line arguments.
			if (!ParseOptions(args))
			{
				Usage();
				return;
			}

			LocalizationManager.TranslationMemoryKind = TranslationMemory.XLiff;

			// Load the input assemblies so that they can be scanned.
			List<Assembly> assemblies = new List<Assembly>();

			List<string> assemblyPaths = new List<string>();
			if (_glob)
			{
				foreach (var glob in _assemblyFiles)
				{
					assemblyPaths.AddRange(Directory.GetFiles(Path.GetDirectoryName(glob), Path.GetFileName(glob)));
				}
			}
			else
			{
				assemblyPaths = _assemblyFiles;
			}
			foreach (var file in assemblyPaths)
			{
				// Using LoadFrom to make sure we pick up other assemblies in the same directory so we don't fail
				// to load because of 'missing' dependencies
				var asm = Assembly.LoadFrom(file);
				assemblies.Add(asm);

				for (var index = 0; index < _additionalLocalizationMethodNames.Count; index++)
				{
					var methodNameSpec = _additionalLocalizationMethodNames[index];
					try
					{
						var type = asm.GetType(methodNameSpec.Item1 + "." + methodNameSpec.Item2);
						if (type == null)
							continue;
						_additionalLocalizationMethods.AddRange(type
							.GetMethods(BindingFlags.Static | BindingFlags.Public)
							.Where(m => m.Name == methodNameSpec.Item3));

						if (_verbose)
							Console.WriteLine($"Method {methodNameSpec.Item2}.{methodNameSpec.Item3} in {asm.GetName().FullName} will be treated as a localization method.");
						_additionalLocalizationMethodNames.RemoveAt(index--);
					}
					catch (Exception e)
					{
						if (_verbose)
							Console.WriteLine("Error using reflection on {asm.GetName().FullName} to get type {methodNameSpec.Item2} or method {methodNameSpec.Item3}:" + e.Message);
					}
				}
			}
			if (_verbose && _additionalLocalizationMethodNames.Any())
			{
				Console.WriteLine("Failed to find the following additional localization methods:");
				foreach (var methodNameSpec in _additionalLocalizationMethodNames)
					Console.WriteLine($"{methodNameSpec.Item1}.{methodNameSpec.Item2}.{methodNameSpec.Item3}");
			}

			// Scan the input assemblies for localizable strings.
			var extractor = new StringExtractor<XLiffDocument> { ExternalAssembliesToScan = assemblies.ToArray() };
			extractor.OutputErrorsToConsole = _verbose;
			var localizedStrings = extractor.DoExtractingWork(_additionalLocalizationMethods, _namespaces.ToArray(), null);

			// The arguments to this constructor don't really matter much as they're used internally by
			// L10NSharp for reasons that may not percolate out to xliff. We just need a LocalizationManagerInternal
			// to feed into the constructor the LocalizedStringCache that does some heavy lifting for us in
			// creating the XliffDocument from the newly extracted localized strings.
			var lm = new XLiffLocalizationManager(_fileOriginal, _fileOriginal, _fileProductVersion);
			var stringCache = new XLiffLocalizedStringCache(lm, false);
			foreach (var locInfo in localizedStrings)
				stringCache.UpdateLocalizedInfo(locInfo);

			// Get the newly loaded static strings (in newDoc) and the baseline XLIFF (in baseDoc).
			var newDoc = stringCache.GetDocument(kDefaultLangId);
			var baseDoc = LoadBaselineAndCompare(newDoc);

			// Save the results to the output file, merging in data from the baseline XLIFF if one was specified.
			var xliffOutput = XLiffLocalizationManager.MergeXliffDocuments(newDoc, baseDoc, _verbose);
			xliffOutput.File.SourceLang = kDefaultLangId;
			xliffOutput.File.ProductVersion = !string.IsNullOrEmpty(_fileProductVersion) ? _fileProductVersion : newDoc.File.ProductVersion;
			xliffOutput.File.HardLineBreakReplacement = kDefaultNewlineReplacement;
			xliffOutput.File.AmpersandReplacement = kDefaultAmpersandReplacement;
			xliffOutput.File.Original = _fileOriginal;
			xliffOutput.File.DataType = !string.IsNullOrEmpty(_fileDatatype) ? _fileDatatype : newDoc.File.DataType;
			xliffOutput.Save(_xliffOutputFilename);
		}

		/// <summary>
		/// Parse the command line arguments, storing the associated information for the program.
		/// </summary>
		/// <returns><c>true</c>, if the command line parse successfully, <c>false</c> otherwise.</returns>
		static bool ParseOptions(string[] args)
		{
			bool error = false;
			for (int i = 0; i < args.Length; ++i)
			{
				if (!_doneWithOptions)
				{
					if (ProcessArgument(args, "-n", "--namespace", AddNamespace, ref i, ref error))
						continue;
					if (ProcessArgument(args, "-x", "--xliff-file", SetXliff, ref i, ref error))
						continue;
					if (ProcessArgument(args, "-o", "--original", SetOriginal, ref i, ref error))
						continue;
					if (ProcessArgument(args, "-p", "--product-version", SetProductVersion, ref i, ref error))
						continue;
					if (ProcessArgument(args, "-d", "--datatype", SetDatatype, ref i, ref error))
						continue;
					if (ProcessArgument(args, "-b", "--base-xliff", SetBaseXliff, ref i, ref error))
						continue;
					if (ProcessArgument(args, "-v", "--verbose", SetVerbose, ref i, ref error))
						continue;
					// This switch allows the caller to specify the fully qualified method name
					// (namespace.class.method) for any additional (non L10nSharp) methods that
					// are used to get localizable strings. (See SIL.Localizer and the comment in
					// the LocalizationManager constructor for more details.)
					if (ProcessArgument(args, "-m", "--method", AddLocalizationMethod, ref i, ref error))
						continue;
					if (ProcessArgument(args, "-g", "--glob", SetGlob, ref i, ref error))
						continue;
					if (args[i] == "--")
					{
						_doneWithOptions = true;
						continue;
					}
					if (error || args[i].StartsWith("-"))
						return false;
				}
				_doneWithOptions = true;
				_assemblyFiles.Add(args[i]);
			}
			return _namespaces.Count > 0 &&
				!string.IsNullOrEmpty(_xliffOutputFilename) &&
				!string.IsNullOrEmpty(_fileOriginal) &&
				_assemblyFiles.Count > 0;
		}

		/// <summary>
		/// Try to process one command line option.
		/// The ref input "i" may be incremented if the option was matched. The ref input "error" is set if the option
		/// was matched but could not be fully processed.
		/// </summary>
		/// <returns><c>true</c>, if the option matches and was processed successfully, <c>false</c> otherwise.</returns>
		/// <param name="args">command line argument array</param>
		/// <param name="shortForm">short form of the option</param>
		/// <param name="longForm">long form of the option</param>
		/// <param name="action">method to process the option</param>
		/// <param name="i">index into args</param>
		/// <param name="error">flag an error in processing a matched option</param>
		static bool ProcessArgument(string[] args, string shortForm, string longForm, ProcessOption action, ref int i, ref bool error)
		{
			if (error)
				return false;
			if (args[i] == shortForm || longForm.StartsWith(args[i]))
			{
				var okay = action(args, ref i);
				if (okay)
					return true;
				error = true;
			}
			return false;
		}

		static bool AddNamespace(string[] args, ref int i)
		{
			// -n  --namespace = namespace beginning [one or more required]
			++i;
			if (i < args.Length)
			{
				if (args[i] == "--")
					return false;
				_namespaces.Add(args[i]);
				return true;
			}
			return false;
		}

		static bool AddLocalizationMethod(string[] args, ref int i)
		{
			// -m  --method = additional localization method [zero or more]
			++i;
			if (i < args.Length)
			{
				if (args[i] == "--")
					return false;

				const string validCSharpIdentifier = "[a-zA-Z_][a-zA-Z0-9_]*";
				// Namespace can consist of any number of valid identifiers separated by periods.
				// The second-to-last one will be the class name,
				// and the last one will be the method name.
				// Not that this does not support things like nested classes or generics (which
				// are unlikely to be used for localized strings anyway).
				var regexFullyQualifiedMethodName = new Regex(
					$"^(?<namespace>{validCSharpIdentifier}(\\.{validCSharpIdentifier})*)\\.(?<class>{validCSharpIdentifier})\\.(?<methodName>{validCSharpIdentifier})$");
				var match = regexFullyQualifiedMethodName.Match(args[i]);
				if (match.Success)
				{
					_additionalLocalizationMethodNames.Add(
						new Tuple<string, string, string>(
							match.Result("${namespace}"),
							match.Result("${class}"),
							match.Result("${methodName}")
						));
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Process an option that can occur only once and that has a string value. The ref input "i" is always
		/// incremented. An error occurs if that places it past the end of the input "args" array as an index.
		/// </summary>
		/// <returns><c>true</c>, if the option was processed successfully, <c>false</c> otherwise.</returns>
		static bool SetSingleValueOption(string[] args, ref int i, ref string optionValue)
		{
			++i;
			if (i < args.Length && optionValue == null)
			{
				if (args[i] == "--")
					return false;
				optionValue = args[i];
				return true;
			}
			return false;
		}

		static bool SetXliff(string[] args, ref int i)
		{
			// -x  --xliff-file = output .xlf file [one required]
			return SetSingleValueOption(args, ref i, ref _xliffOutputFilename);
		}

		static bool SetOriginal(string[] args, ref int i)
		{
			// -o  --original = file element attribute value [one required]
			return SetSingleValueOption(args, ref i, ref _fileOriginal);
		}

		static bool SetProductVersion(string[] args, ref int i)
		{
			// -p  --product-version = file element attribute value [one optional]
			return SetSingleValueOption(args, ref i, ref _fileProductVersion);
		}

		static bool SetDatatype(string[] args, ref int i)
		{
			// -d  --datatype = file element attribute value [one optional]
			return SetSingleValueOption(args, ref i, ref _fileDatatype);
		}

		static bool SetBaseXliff(string[] args, ref int i)
		{
			// -b  --base-xliff = existing xliff file to serve as base for output [one optional]
			return SetSingleValueOption(args, ref i, ref _baseXliffFilename);
		}

		static bool SetVerbose(string[] args, ref int i)
		{
			// -v  --verbose = produce verbose output on differences from base file [optional]
			_verbose = true;
			return true;
		}

		static bool SetGlob(string[] args, ref int i)
		{
			// -g  --glob = treat assembly arguments as globs instead of files [one optional]
			_glob = true;
			return true;
		}

		/// <summary>
		/// Display a usage message on the console.
		/// </summary>
		static void Usage()
		{
			Console.WriteLine("usage: ExtractXliff [options] assembly-file(s)");
			Console.WriteLine("-n  --namespace = namespace beginning [one or more required]");
			Console.WriteLine("-x  --xliff-file = output .xlf file [one required]");
			Console.WriteLine("-o  --original = file element attribute value [one required]");
			Console.WriteLine();
			Console.WriteLine("-d  --datatype = file element attribute value [one optional]");
			Console.WriteLine("-g  --glob = treat assembly arguments as filename globs instead of files (directory globs are not supported) [one optional]");
			Console.WriteLine("-p  --product-version = file element attribute value [one optional]");
			Console.WriteLine("-b  --base-xliff = existing xliff file to serve as base for output [one optional]");
			Console.WriteLine("-v  --verbose = produce verbose output on differences from base file [optional]");
			Console.WriteLine("-m  --method = fully-specified name (namespace.class.method) of additional localization method(s) [optional]");
			Console.WriteLine();
			Console.WriteLine("Every option except -v (--verbose) and -g (--glob) consumes a following argument as its value.");
			Console.WriteLine("The option list can be terminated by \"--\" in case an assembly filename starts");
			Console.WriteLine("with a dash (\"-\"). One or more assembly files (either .dll or .exe) are ");
			Console.WriteLine("required following all of the options. If a base xliff file is given, then its");
			Console.WriteLine("content serves as the base for the output, with the extracted strings merged");
			Console.WriteLine("into, and updating, the existing strings. Statistics are then written to the");
			Console.WriteLine("console for the number of new strings, changed strings, identical strings, and");
			Console.WriteLine("number of strings in the base that were not extracted.");
		}

		/// <summary>
		/// If one was provided, load the baseline XLIFF file and compare its file element attributes
		/// against the new data.
		/// </summary>
		/// <returns>The baseline XliffDocument, or null</returns>
		static XLiffDocument LoadBaselineAndCompare(XLiffDocument newDoc)
		{
			XLiffDocument baseDoc = null;
			if (_baseXliffFilename != null)
			{
				// Compare the new file element attributes against the baseline XLIFF file element attributes.
				// Complain if something has changed.
				baseDoc = XLiffDocument.Read(_baseXliffFilename);
				if (baseDoc.File.SourceLang != kDefaultLangId)
				{
					Console.WriteLine("ERROR: old source-language ({0}) is not the same as the new source-language ({1})",
						baseDoc.File.SourceLang, kDefaultLangId);
					throw new ApplicationException("Only " + kDefaultLangId + " is allowed as the source-language attribute");
				}
				if (baseDoc.File.Original != _fileOriginal)
				{
					Console.WriteLine("WARNING: old original ({0}) is not the same as the new original ({1})",
						baseDoc.File.Original, _fileOriginal);
				}
				if (string.IsNullOrEmpty(_fileDatatype))
				{
					if (baseDoc.File.DataType != newDoc.File.DataType)
					{
						Console.WriteLine("WARNING: old datatype ({0}) is not the same as the new datatype ({1})",
							baseDoc.File.DataType, newDoc.File.DataType);
					}
				}
				else
				{
					if (baseDoc.File.DataType != _fileDatatype)
					{
						Console.WriteLine("WARNING: old datatype ({0}) is not the same as the new datatype ({1})",
							baseDoc.File.DataType, _fileDatatype);
					}
				}
				if (string.IsNullOrEmpty(_fileProductVersion))
				{
					if (baseDoc.File.ProductVersion != newDoc.File.ProductVersion)
					{
						Console.WriteLine("WARNING: old product-version ({0}) is not the same as the new product-version ({1})",
							baseDoc.File.ProductVersion, newDoc.File.ProductVersion);
					}
				}
				else
				{
					if (baseDoc.File.ProductVersion != _fileProductVersion)
					{
						Console.WriteLine("WARNING: old product-version ({0}) is not the same as the new product-version ({1})",
							baseDoc.File.ProductVersion, _fileProductVersion);
					}
				}
			}
			return baseDoc;
		}
	}
}
