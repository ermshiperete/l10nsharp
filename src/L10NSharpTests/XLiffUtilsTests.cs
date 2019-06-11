using System;
using NUnit.Framework;
using L10NSharp.XLiffUtils;
using System.Reflection;
using System.IO;
using System.Collections.Generic;

namespace L10NSharp.Tests
{
	[TestFixture]
	public class XLiffUtilsTests
	{
		string _testFolder;

		public XLiffUtilsTests()
		{
			var asmFile = Assembly.GetExecutingAssembly().CodeBase.Replace("file://", String.Empty);
			if (Environment.OSVersion.Platform == PlatformID.Win32NT)
				asmFile = asmFile.TrimStart('/');
			var folder = Path.GetDirectoryName(asmFile);	// will be something like <repodir>/output/Debug
			folder = Path.GetDirectoryName(folder);
			folder = Path.GetDirectoryName(folder);
			folder = Path.GetDirectoryName(folder);
			_testFolder = Path.Combine(folder, "src", "L10NSharpTests", "TestXliff");
		}

		[Test]
		public void TestHeaderNotes()
		{
			var enfile = Path.Combine(_testFolder, "Test.en.xlf");
			var doc = XLiffDocument.Read(enfile);

			Assert.AreEqual(2, doc.File.Header.Notes.Count);
			Assert.AreEqual("This is a test.  This is only a test.", doc.File.Header.Notes[0].Text);
			Assert.AreEqual("I'm not sure I agree with the previous note.", doc.File.Header.Notes[1].Text);

			// Test that what once was passed in a note is still being read correctly.
			Assert.AreEqual("\\n", doc.File.HardLineBreakReplacement);
		}

		[Test]
		public void TestCounts()
		{
			var enfile = Path.Combine(_testFolder, "Test.en.xlf");
			var endoc = XLiffDocument.Read(enfile);
			// These numbers may be counter-intuitive, but then the English isn't translated, is it?
			// Code at the LocalizationManagerInternal level will make English look okay for display by
			// faking the approved and translated counts.
			Assert.AreEqual(8, endoc.StringCount);
			Assert.AreEqual(0, endoc.NumberApproved);
			Assert.AreEqual(0, endoc.NumberTranslated);

			var frfile = Path.Combine(_testFolder, "Test.fr.xlf");
			var frdoc = XLiffDocument.Read(frfile);
			Assert.AreEqual(4, frdoc.StringCount);
			Assert.AreEqual(0, frdoc.NumberApproved);
			Assert.AreEqual(4, frdoc.NumberTranslated);

			var esfile = Path.Combine(_testFolder, "Test.es.xlf");
			var esdoc = XLiffDocument.Read(esfile);
			Assert.AreEqual(4, esdoc.StringCount);
			Assert.AreEqual(2, esdoc.NumberApproved);
			Assert.AreEqual(3, esdoc.NumberTranslated);
		}

		[Test]
		public void TestLoadingAllStringsAndApprovals()
		{
			var enfile = Path.Combine(_testFolder, "Test.en.xlf");
			var endoc = XLiffDocument.Read(enfile);
			// These numbers may be counter-intuitive, but then the English isn't translated, is it?
			// Code at the LocalizationManagerInternal level will make English look okay for display by
			// faking the approved and translated counts.
			Assert.AreEqual(8, endoc.StringCount);
			string source;
			bool approved;
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.Bird.Crow", out source));
			Assert.AreEqual("It's a crow", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.Bird.Raven", out source));
			Assert.AreEqual("It's not a crow", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.Chicken.Rooster", out source));
			Assert.AreEqual("It's a chicken", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.Bird.Eagle", out source));
			Assert.AreEqual("Fish-eating bird", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.SettingsProtection.CtrlShiftHint", out source));
			Assert.AreEqual("The button will show up when you hold down the Ctrl and Shift keys together.", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.SettingsProtection.LauncherButtonLabel", out source));
			Assert.AreEqual("Settings Protection...", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.SettingsProtection.NormallyHiddenCheckbox", out source));
			Assert.AreEqual("Hide the button that opens settings.", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.SettingsProtection.PasswordDialog.FactoryPassword", out source));
			Assert.AreEqual("Factory Password", source);
			Assert.IsTrue(endoc.File.Body.ApprovalsById.TryGetValue("TestItem.Bird.Crow", out approved));
			Assert.IsTrue(approved);
			Assert.IsTrue(endoc.File.Body.ApprovalsById.TryGetValue("TestItem.Bird.Raven", out approved));
			Assert.IsTrue(approved);
			Assert.IsTrue(endoc.File.Body.ApprovalsById.TryGetValue("TestItem.Chicken.Rooster", out approved));
			Assert.IsTrue(approved);
			Assert.IsTrue(endoc.File.Body.ApprovalsById.TryGetValue("TestItem.Bird.Eagle", out approved));
			Assert.IsTrue(approved);
			Assert.IsTrue(endoc.File.Body.ApprovalsById.TryGetValue("TestItem.SettingsProtection.CtrlShiftHint", out approved));
			Assert.IsTrue(approved);
			Assert.IsTrue(endoc.File.Body.ApprovalsById.TryGetValue("TestItem.SettingsProtection.LauncherButtonLabel", out approved));
			Assert.IsTrue(approved);
			Assert.IsTrue(endoc.File.Body.ApprovalsById.TryGetValue("TestItem.SettingsProtection.NormallyHiddenCheckbox", out approved));
			Assert.IsTrue(approved);
			Assert.IsTrue(endoc.File.Body.ApprovalsById.TryGetValue("TestItem.SettingsProtection.PasswordDialog.FactoryPassword", out approved));
			Assert.IsTrue(approved);

			var frfile = Path.Combine(_testFolder, "Test.fr.xlf");
			var frdoc = XLiffDocument.Read(frfile);
			Assert.AreEqual(4, frdoc.StringCount);
			Assert.IsTrue(frdoc.File.Body.TranslationsById.TryGetValue("TestItem.Bird.Crow", out source));
			Assert.AreEqual("C'est un corbeau", source);
			Assert.IsTrue(frdoc.File.Body.TranslationsById.TryGetValue("TestItem.Bird.Raven", out source));
			Assert.AreEqual("Ce n'est pas un corbeau", source);
			Assert.IsTrue(frdoc.File.Body.TranslationsById.TryGetValue("TestItem.Chicken.Rooster", out source));
			Assert.AreEqual("C'est un poulet", source);
			Assert.IsTrue(frdoc.File.Body.TranslationsById.TryGetValue("TestItem.Bird.Eagle", out source));
			Assert.AreEqual("Un oiseau qui mange des poissons", source);
			Assert.IsTrue(frdoc.File.Body.ApprovalsById.TryGetValue("TestItem.Bird.Crow", out approved));
			Assert.IsFalse(approved);
			Assert.IsTrue(frdoc.File.Body.ApprovalsById.TryGetValue("TestItem.Bird.Raven", out approved));
			Assert.IsFalse(approved);
			Assert.IsTrue(frdoc.File.Body.ApprovalsById.TryGetValue("TestItem.Chicken.Rooster", out approved));
			Assert.IsFalse(approved);
			Assert.IsTrue(frdoc.File.Body.ApprovalsById.TryGetValue("TestItem.Bird.Eagle", out approved));
			Assert.IsFalse(approved);

			var esfile = Path.Combine(_testFolder, "Test.es.xlf");
			var esdoc = XLiffDocument.Read(esfile);
			Assert.AreEqual(4, esdoc.StringCount);
			Assert.IsTrue(esdoc.File.Body.TranslationsById.TryGetValue("TestItem.SettingsProtection.CtrlShiftHint", out source));
			Assert.AreEqual("El botón se mostrará cuando se mantengan presionadas juntas las teclas Ctrl y Mayús.", source);
			Assert.IsTrue(esdoc.File.Body.TranslationsById.TryGetValue("TestItem.SettingsProtection.LauncherButtonLabel", out source));
			Assert.AreEqual("Protección de configuraciones...", source);
			Assert.IsTrue(esdoc.File.Body.TranslationsById.TryGetValue("TestItem.SettingsProtection.NormallyHiddenCheckbox", out source));
			Assert.AreEqual("Ocultar el botón que abre la configuración.", source);
			// note the next string is not stored and returned since it's marked as needing translation
			Assert.IsFalse(esdoc.File.Body.TranslationsById.TryGetValue("TestItem.SettingsProtection.PasswordDialog.FactoryPassword", out source));

			Assert.IsTrue(esdoc.File.Body.ApprovalsById.TryGetValue("TestItem.SettingsProtection.CtrlShiftHint", out approved));
			Assert.IsTrue(approved);
			Assert.IsTrue(esdoc.File.Body.ApprovalsById.TryGetValue("TestItem.SettingsProtection.LauncherButtonLabel", out approved));
			Assert.IsFalse(approved);
			Assert.IsTrue(esdoc.File.Body.ApprovalsById.TryGetValue("TestItem.SettingsProtection.NormallyHiddenCheckbox", out approved));
			Assert.IsTrue(approved);
			// note the next string is not stored and returned since it's marked as needing translation
			Assert.IsFalse(esdoc.File.Body.ApprovalsById.TryGetValue("TestItem.SettingsProtection.PasswordDialog.FactoryPassword", out approved));
		}

		[Test]
		public void TestReturningOnlyApprovedStrings()
		{
			try
			{
				LocalizationManager.UseLanguageCodeFolders = false;
				LocalizationManager.ReturnOnlyApprovedStrings = true;

				var l10nMgr = LocalizationManager.Create(TranslationMemory.XLiff, "en", "Test", "Test", "1.0.0.0", _testFolder, null, null, "fake@wherever.org", "TestItem");
				var tags = l10nMgr.GetAvailableUILanguageTags();
				var list = new List<string>();
				list.AddRange(tags);
				Assert.AreEqual(3, list.Count);
				Assert.IsTrue(list.Contains("en"));
				Assert.IsTrue(list.Contains("fr"));
				Assert.IsTrue(list.Contains("es-ES"));

				LocalizationManager.SetUILanguage("en", true);
				var text = l10nMgr.GetLocalizedString("TestItem.Bird.Crow", "It's a crow");
				Assert.AreEqual("It's a crow", text);
				text = l10nMgr.GetLocalizedString("TestItem.Bird.Raven", "It's a raven, not a crow");	// revised string
				Assert.AreEqual("It's a raven, not a crow", text);
				text = l10nMgr.GetLocalizedString("TestItem.Chicken.Rooster", "It's a chicken");
				Assert.AreEqual("It's a chicken", text);
				text = l10nMgr.GetLocalizedString("TestItem.Bird.Eagle", "Fish-eating bird");
				Assert.AreEqual("Fish-eating bird", text);

				LocalizationManager.SetUILanguage("fr", true);
				text = l10nMgr.GetLocalizedString("TestItem.Bird.Crow", "not there");
				Assert.AreEqual("not there", text);
				text = l10nMgr.GetLocalizedString("TestItem.Bird.Raven", "not there");
				Assert.AreEqual("not there", text);
				text = l10nMgr.GetLocalizedString("TestItem.Chicken.Rooster", "not there");
				Assert.AreEqual("not there", text);
				text = l10nMgr.GetLocalizedString("TestItem.Bird.Eagle", "not there");
				Assert.AreEqual("not there", text);

				LocalizationManager.SetUILanguage("es-ES", true);
				text = l10nMgr.GetLocalizedString("TestItem.SettingsProtection.CtrlShiftHint", "not there");
				Assert.AreEqual("El botón se mostrará cuando se mantengan presionadas juntas las teclas Ctrl y Mayús.", text);
				text = l10nMgr.GetLocalizedString("TestItem.SettingsProtection.LauncherButtonLabel", "not there");
				Assert.AreEqual("not there", text);
				text = l10nMgr.GetLocalizedString("TestItem.SettingsProtection.NormallyHiddenCheckbox", "not there");
				Assert.AreEqual("Ocultar el botón que abre la configuración.", text);
				text = l10nMgr.GetLocalizedString("TestItem.SettingsProtection.PasswordDialog.FactoryPassword", "not there");
				Assert.AreEqual("not there", text);

				LocalizationManager.ReturnOnlyApprovedStrings = false;	// SUT - dynamically changing this flag

				LocalizationManager.SetUILanguage("en", true);
				text = l10nMgr.GetLocalizedString("TestItem.Bird.Crow", "It's a crow");
				Assert.AreEqual("It's a crow", text);
				text = l10nMgr.GetLocalizedString("TestItem.Bird.Raven", "It's a raven, not a crow");   // revised string
				Assert.AreEqual("It's a raven, not a crow", text);
				text = l10nMgr.GetLocalizedString("TestItem.Chicken.Rooster", "It's a chicken");
				Assert.AreEqual("It's a chicken", text);
				text = l10nMgr.GetLocalizedString("TestItem.Bird.Eagle", "Fish-eating bird");
				Assert.AreEqual("Fish-eating bird", text);

				LocalizationManager.SetUILanguage("fr", true);
				text = l10nMgr.GetLocalizedString("TestItem.Bird.Crow", "not there");
				Assert.AreEqual("C'est un corbeau", text);
				text = l10nMgr.GetLocalizedString("TestItem.Bird.Raven", "not there");
				Assert.AreEqual("Ce n'est pas un corbeau", text);
				text = l10nMgr.GetLocalizedString("TestItem.Chicken.Rooster", "not there");
				Assert.AreEqual("C'est un poulet", text);
				text = l10nMgr.GetLocalizedString("TestItem.Bird.Eagle", "not there");
				Assert.AreEqual("Un oiseau qui mange des poissons", text);

				LocalizationManager.SetUILanguage("es-ES", true);
				text = l10nMgr.GetLocalizedString("TestItem.SettingsProtection.CtrlShiftHint", "not there");
				Assert.AreEqual("El botón se mostrará cuando se mantengan presionadas juntas las teclas Ctrl y Mayús.", text);
				text = l10nMgr.GetLocalizedString("TestItem.SettingsProtection.LauncherButtonLabel", "not there");
				Assert.AreEqual("Protección de configuraciones...", text);
				text = l10nMgr.GetLocalizedString("TestItem.SettingsProtection.NormallyHiddenCheckbox", "not there");
				Assert.AreEqual("Ocultar el botón que abre la configuración.", text);
				text = l10nMgr.GetLocalizedString("TestItem.SettingsProtection.PasswordDialog.FactoryPassword", "not there");
				Assert.AreEqual("Factory Password", text);	// original (xliff) English is fallback for Spanish
			}
			finally
			{
				LocalizationManager.ReturnOnlyApprovedStrings = false;  // restore default for other tests
				LocalizationManager.SetUILanguage(LocalizationManager.kDefaultLang, true);
			}
		}

		[Test]
		public void TestReturningHtmlMarkup()
		{
			var enfile = Path.Combine(_testFolder, "Test2.en.xlf");
			var endoc = XLiffDocument.Read(enfile);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.PlainText", out var source));
			Assert.AreEqual("This is plain text.", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.Bold", out source));
			Assert.AreEqual("This is <strong>bold</strong>.", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.Italic", out source));
			Assert.AreEqual("This is <em>italic</em>.", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.Superscript", out source));
			Assert.AreEqual("This is <sup>superscript</sup>.", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.Link", out source));
			Assert.AreEqual("This is a <a href=\"https://sil.org\" id=\"note1\">link</a>.", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.Image", out source));
			Assert.AreEqual("This has an embedded image: <img src=\"images/test.png\" alt=\"This is a test.\"/>.", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.Paragraph", out source));
			Assert.AreEqual("<p>This is a paragraph.</p>", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.Div", out source));
			Assert.AreEqual("<div class=\"author\">This is a div.</div>", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.Blockquote", out source));
			Assert.AreEqual("<blockquote class=\"poetry\">This is a block quote.</blockquote>", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.Pre", out source));
			Assert.AreEqual("<pre>This is in"+Environment.NewLine+"presentation"+Environment.NewLine+"display.</pre>", source);
			Assert.IsTrue(endoc.File.Body.TranslationsById.TryGetValue("TestItem.Multiple", out source));
			Assert.AreEqual("<em>This</em> is more <a href=\"https://mit.edu\">complex</a><strong>!!</strong>", source);
		}
	}
}
