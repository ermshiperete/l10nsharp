// Copyright (c) 2020 SIL International
// This software is licensed under the MIT License (http://opensource.org/licenses/MIT)

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using L10NSharp.XLiffUtils;
using NUnit.Framework;

namespace L10NSharp.Tests
{
	[TestFixture]
	public class XLiffLocalizationManagerTests : LocalizationManagerTestsBase<XLiffDocument>
	{
		[SetUp]
		public void Setup()
		{
			LocalizationManager.TranslationMemoryKind = TranslationMemory.XLiff;
			LocalizationManager.AvoidProgressDialogs = true;
		}

		internal override ILocalizationManagerInternal<XLiffDocument> CreateLocalizationManager(
			string          appId,                               string appName, string appVersion, string directoryOfInstalledTmxFiles,
			string          directoryForGeneratedDefaultTmxFile, string directoryOfUserModifiedXliffFiles,
			IEnumerable<MethodInfo> additionalGetStringMethodInfo = null,
			params string[] namespaceBeginnings)
		{
			return new XLiffLocalizationManager(appId, appName, appVersion, directoryOfInstalledTmxFiles,
				directoryForGeneratedDefaultTmxFile, directoryOfUserModifiedXliffFiles, additionalGetStringMethodInfo,
				namespaceBeginnings);
		}

		internal override ILocalizationManagerInternal<XLiffDocument> CreateLocalizationManager(
			string appId, string appName, string appVersion)
		{
			return new XLiffLocalizationManager(appId, appName, appVersion);
		}

		protected override XLiffDocument CreateNewDocument(string productVersion,    string sourceLang,
			string                                                targetLang = null, string otherAppVersion = null)
		{
			var doc = new XLiffDocument { File = { SourceLang = sourceLang } };
			if (!string.IsNullOrEmpty(productVersion))
				doc.File.ProductVersion = productVersion;
			if (!string.IsNullOrEmpty(targetLang))
				doc.File.TargetLang = targetLang;
			if (!string.IsNullOrEmpty(otherAppVersion))
				doc.File.SetPropValue(LocalizationManager.kAppVersionPropTag, otherAppVersion);
			doc.File.HardLineBreakReplacement = LiteralNewline;
			doc.File.Original = "test.dll";
			return doc;
		}

		protected override ITransUnit CreateTransUnit(string id,              bool               dynamic,
			ITransUnitVariant                                sourceVariant,   ITransUnitVariant  targetVariant     = null,
			string                                           noteText = null, TranslationStatus? translationStatus = null, bool? noLongerUsed = null)
		{
			var tu = new XLiffTransUnit {
				Id = id,
				Source = (XLiffTransUnitVariant)sourceVariant,
				Target = (XLiffTransUnitVariant)targetVariant

			};

			if (dynamic)
				tu.Dynamic = true;

			if (!string.IsNullOrEmpty(noteText))
				tu.Notes = new List<XLiffNote> { new XLiffNote { Text = noteText } };

			if (translationStatus.HasValue)
				tu.TranslationStatus = translationStatus.Value;
			return tu;
		}

		protected override ITransUnitVariant CreateTransUnitVariant(string lang, string value)
		{
			return new XLiffTransUnitVariant { Lang = lang, Value = value };
		}

		private void AdjustDocumentForTestingMerge(XLiffDocument doc)
		{
			doc.File.ProductVersion = "3.1.4";

			// Test for adding new units.
			var tu1 = CreateTestTransUnit("This.new1", "This is a new test.", new[] {"This is still only a test"}, false);
			doc.AddTransUnit(tu1);
			var tu2 = CreateTestTransUnit("That.new2", "That was an old test.", new[] {"That should be easy to translate."}, false);
			doc.AddTransUnit(tu2);
			var tu3 = CreateTestTransUnit("What.new3", "What's up, doc?", new string[] {}, true);
			doc.AddTransUnit(tu3);

			// Test for invalid dynamic setting
			var tu = doc.GetTransUnitForId("What.this");
			tu.Dynamic = false;
			tu.Notes.Clear();
			tu.AddNote("ID: What.this");

			// Test for deleted static unit
			tu = doc.GetTransUnitForId("That.test");
			doc.RemoveTransUnit(tu);

			// Test for modified unit
			tu = doc.GetTransUnitForId("What.test");
			tu.Source.Value = "What is a good test?";
			tu.Notes.Clear();
			tu.AddNote("ID: What.test");

			// Test for deleted (or not covered) dynamic unit
			tu = doc.GetTransUnitForId("How.now");
			doc.RemoveTransUnit(tu);
		}

		private XLiffDocument CreateTestDocument()
		{
			var doc = new XLiffDocument();
			doc.File.Original = "Testing.dll";
			doc.File.DataType = "plaintext";
			doc.File.ProductVersion = "2.7.1";
			doc.File.SourceLang = "en";
			doc.Version = "1.2";

			var tu1 = CreateTestTransUnit("This.test", "This is a test.", new[] {"This is only a test", }, false);
			doc.AddTransUnit(tu1);
			var tu2 = CreateTestTransUnit("That.test", "That was a test.", new[] {"That is hard to explain, but a literal rendition is okay."}, false);
			doc.AddTransUnit(tu2);
			var tu3 = CreateTestTransUnit("What.test", "What is good test.", new string[] {"Whatever you say...", "[OLD NOTE] This should be a question.", "OLD TEXT (before 1.0): What are good test." }, true);
			doc.AddTransUnit(tu3);
			var tu4 = CreateTestTransUnit("What.this", "What is this nonsense?", new string[] {}, true);
			doc.AddTransUnit(tu4);
			var tu5 = CreateTestTransUnit("How.now", "How now brown cow", new string[] {}, true);
			doc.AddTransUnit(tu5);

			return doc;
		}

		protected override string GetGeneratedVersion(XElement xmlDoc)
		{
			var docNamespace = xmlDoc.GetDefaultNamespace();
			var fileElt = xmlDoc.Element(docNamespace + "file");
			Assert.NotNull(fileElt);
			var generatedVersion = fileElt.Attribute("product-version").Value;
			return generatedVersion;
		}

		[TestCase("en", ExpectedResult = 1.0F)]
		[TestCase("ar", ExpectedResult = 2F/3F)]
		[TestCase("es", ExpectedResult = 1.0F)]
		[TestCase("fr", ExpectedResult = 1F/3F)]
		[TestCase("ii", ExpectedResult = 1F/3F)]
		public float FractionTranslated(string langId)
		{
			LocalizationManager.UseLanguageCodeFolders = true;
			using (var folder = new TempFolder())
			{
				AddRandomTranslation("ii", GetInstalledDirectory(folder));
				SetupManager(folder, "ii" /* UI language not important */);

				return LocalizationManager.FractionTranslated(langId);
			}
		}

		[TestCase("en", ExpectedResult = 1.0F)]
		[TestCase("ar", ExpectedResult = 2F/3F)]
		[TestCase("es", ExpectedResult = 2F/3F)]
		[TestCase("fr", ExpectedResult = 0.0F)]
		[TestCase("ii", ExpectedResult = 1F/3F)]
		public float FractionApproved(string langId)
		{
			LocalizationManager.UseLanguageCodeFolders = true;
			using (var folder = new TempFolder())
			{
				AddRandomTranslation("ii", GetInstalledDirectory(folder));
				SetupManager(folder, "ii" /* UI language not important */);

				return LocalizationManager.FractionApproved(langId);
			}
		}

		[Test]
		public void MergeXliffDocuments_WorksAsExpected()
		{
			var oldDoc = CreateTestDocument();
			var newDoc = CreateTestDocument();
			AdjustDocumentForTestingMerge(newDoc);

			var mergedDoc = XLiffLocalizationManager.MergeXliffDocuments(newDoc, oldDoc, true);
			Assert.IsNotNull(mergedDoc);
			Assert.That(5, Is.EqualTo(oldDoc.File.Body.TransUnitsUnordered.Count()));
			Assert.That(6, Is.EqualTo(newDoc.File.Body.TransUnitsUnordered.Count()));
			Assert.That(8, Is.EqualTo(mergedDoc.File.Body.TransUnitsUnordered.Count()));

			var tu = mergedDoc.GetTransUnitForId("This.test");
			CheckMergedTransUnit(tu, "This is a test.",
				new[] { "ID: This.test", "This is only a test" }, false);

			tu = mergedDoc.GetTransUnitForId("That.test");
			CheckMergedTransUnit(tu, "That was a test.",
				new[] {
					"ID: That.test",
					"That is hard to explain, but a literal rendition is okay.",
					"Not found in static scan of compiled code (version 3.1.4)"
				}, false);

			tu = mergedDoc.GetTransUnitForId("What.test");
			CheckMergedTransUnit(tu, "What is a good test?", new[] {
				"ID: What.test",
				"[OLD NOTE] Whatever you say...", "[OLD NOTE] This should be a question.",
				"OLD TEXT (before 1.0): What are good test.",
				"OLD TEXT (before 3.1.4): What is good test."
			}, true);

			tu = mergedDoc.GetTransUnitForId("What.this");
			CheckMergedTransUnit(tu, "What is this nonsense?",
				new[] {
					"ID: What.this",
					"Not dynamic: found in static scan of compiled code (version 3.1.4)"
				}, false);
			Assert.IsNotNull(tu);

			tu = mergedDoc.GetTransUnitForId("This.new1");
			CheckMergedTransUnit(tu, "This is a new test.",
				new[] { "ID: This.new1", "This is still only a test" }, false);

			tu = mergedDoc.GetTransUnitForId("That.new2");
			CheckMergedTransUnit(tu, "That was an old test.",
				new[] { "ID: That.new2", "That should be easy to translate." }, false);

			tu = mergedDoc.GetTransUnitForId("What.new3");
			CheckMergedTransUnit(tu, "What's up, doc?", new[] { "ID: What.new3" }, true);

			tu = mergedDoc.GetTransUnitForId("How.now");
			CheckMergedTransUnit(tu, "How now brown cow",
				new[] {
					"ID: How.now", "Not found when running compiled program (version 3.1.4)"
				}, true);
		}

		private void CheckMergedTransUnit(XLiffTransUnit tu, string sourceText, string[] notes, bool isDynamic)
		{
			Assert.IsNotNull(tu);
			Assert.That("en", Is.EqualTo(tu.Source.Lang));
			Assert.That(sourceText, Is.EqualTo(tu.Source.Value));
			Assert.That(notes.Length, Is.EqualTo(tu.Notes.Count));
			for (int i = 0; i < notes.Length; ++i)
				Assert.That(notes[i], Is.EqualTo(tu.Notes[i].Text));
			Assert.That(isDynamic, Is.EqualTo(tu.Dynamic));
		}

	}
}
