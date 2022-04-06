using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.XPath;
using L10NSharp;
using L10NSharp.XLiffUtils;

namespace CheckOrFixXliff
{
	/// <summary>
	/// This program optionally validates XLiff files in three ways:
	/// 1) Check for being well-formed XML.  This is the most basic check, and must pass for anything else to work.
	/// 2) Check against the XLiff 1.2 schema.  This is optional ("--validate" flag), and not really needed for Bloom
	///    and other L10NSharp clients who ignore the exact order of the child elements in trans-unit elements.  Note
	///    that crowdin doesn't always produce valid Xliff, and they don't seem all that concerned about fixing it.
	/// 3) Check all translated format strings (those that contain markers like '{0}' for validity: markers match
	///    between the source and target strings, and the markers in the target strings are not mangled in translation.
	///    If malformed markers are detected that would cause the program to crash, the 3 characters preceding and 6
	///    characters following each open brace ('{') are displayed following the warning messages for the string.
	/// If the third check reveals problems involving mangled substitution markers (most often in RTL scripts), the
	/// program can optionally try to repair the strings using some common patterns that have been observed ("--fix"
	/// flag).
	/// </summary>
	class Program
	{
		enum ErrorState
		{
			Okay = 0,
			Warning,
			Error
		}
		/// <summary>
		/// List of trans-unit id values whose target value contains a malformed substitution marker.
		/// </summary>
		private static List<string> _mangledTargets = new List<string>();
		private static bool _quiet = false;

		static int Main(string[] args)
		{
			LocalizationManager.TranslationMemoryKind = TranslationMemory.XLiff;

			bool validate = false;
			bool fix = false;
			bool missingFile = false;
			bool invalidXml = false;
			bool invalidXliff = false;
			bool mismatchedFormatMarker = false;
			bool invalidFormatMarker = false;

			for (int i = 0; i < args.Length; ++i)
			{
				if (args[i] == "--validate")
				{
					validate = true;
				}
				else if (args[i] == "--fix")
				{
					fix = true;
				}
				else if (args[i] == "--quiet")
				{
					_quiet = true;
				}
				else
				{
					string filename = args[i];
					if (!File.Exists(filename))
					{
						Console.WriteLine("{0} does not exist!", filename);
						missingFile = true;
						continue;
					}
					if (!CheckForWellFormedXml(filename))
					{
						invalidXml = true;
						continue;	// all other checks depend on loading XML
					}
					if (validate && !ValidateXliffAgainstSchema(filename))
						invalidXliff = true;
					var formatState = CheckFormatStringMarkers(filename);
					if (formatState == ErrorState.Warning)
						mismatchedFormatMarker = true;
					else if (formatState == ErrorState.Error)
						invalidFormatMarker = true;
					if (fix)
						FixMangledFormatMarkers(filename);
				}
			}
			if (missingFile || invalidXml || invalidXliff || invalidFormatMarker)
				return (int)ErrorState.Error;
			if (mismatchedFormatMarker)
				return (int)ErrorState.Warning;
			return (int)ErrorState.Okay;
		}

		/// <summary>
		/// Try to fix any target strings in the file that have one or more mangled substitution markers.
		/// </summary>
		/// <remarks>
		/// XDocument is used to preserve element ordering and other aspects of the xliff file that may be
		/// modified or omitted by XliffDocument.
		/// </remarks>
		private static void FixMangledFormatMarkers(string filename)
		{
			if (_mangledTargets.Count == 0)
				return;
			var reader = XmlReader.Create(new StreamReader(filename));
			var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo | LoadOptions.SetBaseUri);
			var namespaceManager = new XmlNamespaceManager(reader.NameTable);
			namespaceManager.AddNamespace("x", "urn:oasis:names:tc:xliff:document:1.2");
			foreach (var id in _mangledTargets)
			{
				var xpath = string.Format("//x:trans-unit[@id='{0}']", id);
				var tu = document.XPathSelectElement(xpath, namespaceManager);
				if (tu != null)
					FixBrokenTransUnit(tu, namespaceManager);
			}
			document.Save(filename + "-fixed");
		}

		private static void FixBrokenTransUnit(XElement tu, XmlNamespaceManager namespaceManager)
		{
			var tuid = tu.Attribute("id").Value;
			var source = tu.XPathSelectElement("x:source", namespaceManager);
			var target = tu.XPathSelectElement("x:target", namespaceManager);
			if (source.HasElements || target.HasElements)
			{
				Console.WriteLine("Cannot fix {0} because the translated material contains XML elements", tuid);
				return;
			}
			var targetValue = target.Value;
			if (!string.IsNullOrWhiteSpace(targetValue))
			{
				var targetFixed = XLiffLocalizedStringCache.FixBrokenFormattingString(targetValue);
				if (targetFixed != target.Value)
					target.SetValue(targetFixed);
			}
		}

		/// <summary>
		/// Check whether the given file is even valid ("well formed") XML.
		/// </summary>
		/// <returns>
		/// true if okay, false if the file cannot load as an XmlDocument
		/// </returns>
		private static bool CheckForWellFormedXml(string filename)
		{
			try
			{
				var xdoc = new XmlDocument();
				xdoc.Load(filename);
				return true;
			}
			catch (Exception e)
			{
				Console.WriteLine("{0} is invalid XML: {1}", filename, e.Message);
				return false;
			}
		}

		/// <summary>
		/// Check whether the given file validates against the XLIFF 1.2 schema.
		/// </summary>
		/// <returns>
		/// true if okay, false if the file fails to validate
		/// </returns>
		/// <remarks>
		/// TODO: tweak (break) our copy of the schema to disregard the order of child elements of trans-unit.
		/// </remarks>
		private static bool ValidateXliffAgainstSchema(string filename)
		{
			bool valid = true;
			var installedXliffDir = Path.GetDirectoryName(new Uri(Assembly.GetExecutingAssembly().CodeBase).LocalPath);
			var schemaLocation = Path.Combine(installedXliffDir, "xliff-core-1.2-transitional.xsd");
			var schemas = new XmlSchemaSet();
			using (var reader = XmlReader.Create(schemaLocation))
			{
				schemas.Add("urn:oasis:names:tc:xliff:document:1.2", reader);
				var document = XDocument.Load(filename, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo | LoadOptions.SetBaseUri);
				document.Validate(schemas, (sender, args) => {
					if (!_quiet)
						Console.WriteLine("{0} did not validate against schema on line {1}: {2}", filename, args.Exception.LineNumber, args.Message);
					valid = false;
				});
			}
			return valid;
		}

		/// <summary>
		/// Check that all the format substitution markers are present and okay.
		/// </summary>
		/// <returns>
		/// true if the translation substitution markers are all valid and match the source markers, false otherwise
		/// </returns>
		private static ErrorState CheckFormatStringMarkers(string filename)
		{
			var retval = ErrorState.Okay;
			var doc = XLiffDocument.Read(filename);
			var dictSourceMarkers = new Dictionary<string, int>();
			var dictTargetMarkers = new Dictionary<string, int>();
			foreach (var tu in doc.File.Body.TransUnitsUnordered)
			{
				if (tu.Source == null || tu.Target == null)
					continue;
				if (String.IsNullOrWhiteSpace(tu.Source.Value) || String.IsNullOrWhiteSpace(tu.Target.Value))
					continue;
				var matchesSource = Regex.Matches(tu.Source.Value, "{[0-9]+}");
				var matchesTarget = Regex.Matches(tu.Target.Value, "{[0-9]+}");
				if (matchesSource.Count == 0 && matchesTarget.Count == 0)
					continue;
				TabulateMarkers(matchesSource, dictSourceMarkers);
				TabulateMarkers(matchesTarget, dictTargetMarkers);
				var okay = CheckForExactlyMatchingSubstitutionMarkers(tu.Id, dictSourceMarkers, dictTargetMarkers);
				if (!okay && retval == ErrorState.Okay)
					retval = ErrorState.Warning;
				if (!XLiffLocalizedStringCache.CheckForValidSubstitutionMarkers(dictSourceMarkers.Count, tu.Target.Value, tu.Id, _quiet))
				{
					_mangledTargets.Add(tu.Id);
					retval = ErrorState.Error;
					okay = false;
				}
				if (!okay && !_quiet)
					Console.WriteLine();	// separate the messages for different trans-units
			}
			return retval;
		}

		/// <summary>
		/// Check that the target substitution markers exactly match those in the source.
		/// </summary>
		/// <returns>
		/// true if everything matches, false if there's any disparity
		/// </returns>
		private static bool CheckForExactlyMatchingSubstitutionMarkers(string tuId, Dictionary<string, int> dictSourceMarkers, Dictionary<string, int> dictTargetMarkers)
		{
			bool retval = true;
			foreach (var key in dictSourceMarkers.Keys)
			{
				if (dictTargetMarkers.ContainsKey(key))
				{
					if (dictTargetMarkers[key] < dictSourceMarkers[key])
					{
						// missing instances, but may be valid
						if (!_quiet)
							Console.WriteLine(@"Translation of {0} is missing {1} copies of the marker: {2}",
								tuId, dictSourceMarkers[key] - dictTargetMarkers[key], key);
						retval = false;
					}
					else if (dictTargetMarkers[key] > dictSourceMarkers[key])
					{
						// extra instances, but may be valid
						if (!_quiet)
							Console.WriteLine(@"Translation of {0} has {1} extra copies of the marker: {2}",
								tuId, dictTargetMarkers[key] - dictSourceMarkers[key], key);
						retval = false;
					}
				}
				else
				{
					// missing altogether, probably invalid
					if (!_quiet)
						Console.WriteLine(@"Translation of {0} is missing the marker: {1}", tuId, key);
					retval = false;
				}
			}
			foreach (var key in dictTargetMarkers.Keys)
			{
				if (!dictSourceMarkers.ContainsKey(key))
				{
					// introduced instance, certainly invalid!
					if (!_quiet)
						Console.WriteLine(@"Translation of {0} has an unexpected marker: {1}", tuId, key);
					retval = false;
				}
			}
			return retval;
		}

		/// <summary>
		/// Store the different markers found by the regex search and how often each occurs in the provided dictionary.
		/// </summary>
		private static void TabulateMarkers(MatchCollection matches, Dictionary<string, int> dictMarkers)
		{
			dictMarkers.Clear();
			for (int i = 0; i < matches.Count; ++i)
			{
				var key = matches[i].Value;
				if (dictMarkers.ContainsKey(key))
				{
					var val = dictMarkers[key];
					dictMarkers[key] = val + 1;
				}
				else
				{
					dictMarkers.Add(key, 1);
				}
			}
		}
	}
}
