﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Permissions;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;
using L10NSharp.UI;
using L10NSharp.XLiffUtils;

namespace L10NSharp
{
	/// ----------------------------------------------------------------------------------------
	public class LocalizationManager : IDisposable
	{
		/// ------------------------------------------------------------------------------------
		public const string kDefaultLang = "en";
		internal const string kAppVersionPropTag = "x-appversion";
		internal const string kL10NPrefix = "_L10N_:";
		internal const string kFileExtension = ".xlf";

		/// <summary>
		/// Map from the given language code to a variant we actually have.  (It can map from a
		/// language code onto itself.)
		/// </summary>
		internal static Dictionary<string, string> MapToExistingLanguage = new Dictionary<string, string>();

		/// <summary>
		/// These two events allow us to know when the localization dialog is running.
		/// For example, HearThis needs to turn off some event prefiltering.
		/// </summary>
		public static event EventHandler LaunchingLocalizationDialog;
		public static event EventHandler ClosingLocalizationDialog;

		/// <summary>
		/// Flag that the program organizes translation files by folder rather than by filename.
		/// That is, localization/en/AppName.xlf (English) and localization/id/AppName.xlf (Indonesian)
		/// instead of localization/AppName.en.xlf and localization/AppName.id.xlf.
		/// Note that this must be set before creating any LocalizationManager objects.
		/// The default is the old way of organizing (by filename).
		/// </summary>
		public static bool UseLanguageCodeFolders;

		/// <summary>
		/// Ignore any existing English xliff files, creating the working (English) file only from what
		/// is gathered by static analyis or dynamic harvesting of requests.
		/// </summary>
		public static bool IgnoreExistingEnglishXliffFiles;

		/// <summary>
		/// Ignore any translated strings that are not marked "approved", acting as though the
		/// translation didn't exist.
		/// </summary>
		public static bool ReturnOnlyApprovedStrings;

		private static string s_uiLangId;
		private static List<string> s_fallbackLanguageIds = new List<string>(new[] { kDefaultLang });

		private static readonly Dictionary<string, LocalizationManager> s_loadedManagers =
			new Dictionary<string, LocalizationManager>();

		private static Icon _applicationIcon;
		private readonly string _installedXliffFileFolder;
		private readonly string _generatedDefaultXliffFileFolder;
		private readonly string _customXliffFileFolder;

		internal Dictionary<IComponent, string> ComponentCache { get; private set; }
		internal Dictionary<Control, ToolTip> ToolTipCtrls { get; private set; }
		internal Dictionary<ILocalizableComponent, Dictionary<string, LocalizingInfo>> LocalizableComponents { get; private set; }

		#region Static methods for creating a LocalizationManager
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Creates a new instance of a localization manager for the specifed application id.
		/// If a localization manager has already been created for the specified id, then
		/// that is returned.
		/// </summary>
		/// <param name="desiredUiLangId">The language code of the desired UI language. If
		/// there are no translations for that ID, a message is displayed and the UI language
		/// falls back to the default.</param>
		/// <param name="appId">The application Id (e.g. 'Pa' for Phonology Assistant).
		/// This should be a unique name that identifies the manager for an assembly or
		/// application.</param>
		/// <param name="appName">The application's name. This will appear to the user
		/// in the localization dialog box as a parent item in the tree.</param>
		/// <param name="appVersion"></param>
		/// <param name="directoryOfInstalledXliffFiles">The full folder path of the original Xliff files
		/// installed with the application.</param>
		/// <param name="relativeSettingPathForLocalizationFolder">The path, relative to
		/// %appdata%, where your application stores user settings (e.g., "SIL\SayMore").
		/// A folder named "localizations" will be created there.</param>
		/// <param name="applicationIcon"> </param>
		/// <param name="emailForSubmissions">This will be used in UI that helps the translator
		/// know what to do with their work</param>
		/// <param name="namespaceBeginnings">A list of namespace beginnings indicating
		/// what types to scan for localized string calls. For example, to only scan
		/// types found in Pa.exe and assuming all types in that assembly begin with
		/// 'Pa', then this value would only contain the string 'Pa'.</param>
		/// ------------------------------------------------------------------------------------
		public static LocalizationManager Create(string desiredUiLangId, string appId,
			string appName, string appVersion, string directoryOfInstalledXliffFiles,
			string relativeSettingPathForLocalizationFolder,
			Icon applicationIcon, string emailForSubmissions, params string[] namespaceBeginnings)
		{
			EmailForSubmissions = emailForSubmissions;
			_applicationIcon = applicationIcon;

			if (string.IsNullOrEmpty(relativeSettingPathForLocalizationFolder))
				relativeSettingPathForLocalizationFolder = appName;
			else if (Path.IsPathRooted(relativeSettingPathForLocalizationFolder))
				throw new ArgumentException("Relative (non-rooted) path expected", "relativeSettingPathForLocalizationFolder");

			var directoryOfWritableXliffFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				relativeSettingPathForLocalizationFolder, "localizations");

			LocalizationManager lm;
			if (!LoadedManagers.TryGetValue(appId, out lm))
			{
				lm = new LocalizationManager(appId, appName, appVersion, directoryOfInstalledXliffFiles,
					directoryOfWritableXliffFiles, directoryOfWritableXliffFiles, namespaceBeginnings);

				LoadedManagers[appId] = lm;
				PreviouslyLoadedManagers.Remove(appId);
			}

			if (string.IsNullOrEmpty(desiredUiLangId))
			{
				desiredUiLangId = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
			}

			var ci = L10NCultureInfo.GetCultureInfo(desiredUiLangId);
			if (!GetUILanguages(true).Contains(ci))
			{
				using (var dlg = new LanguageChoosingDialog(ci, applicationIcon))
				{
					dlg.ShowDialog();
					desiredUiLangId = dlg.SelectedLanguage;
				}
			}

			SetUILanguage(desiredUiLangId, false);

			EnableClickingOnControlToBringUpLocalizationDialog = true;

			return lm;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Now that L10NSharp creates all writable Xliff files under LocalApplicationData
		/// instead of the common/shared AppData folder, applications can use this method to
		/// purge old Xliff files.</summary>
		/// <param name="appId">ID of the application used for creating the Xliff files (typically
		/// the same ID passed as the 2nd parameter to LocalizationManager.Create).</param>
		/// <param name="directoryOfWritableXliffFiles">Folder from which to delete Xliff files.
		/// </param>
		/// <param name="directoryOfInstalledXliffFiles">Used to limit file deletion to only
		/// include copies of the installed Xliff files (plus the generated default file). If this
		/// is <c>null</c>, then all Xliff files for the given appID will be deleted from
		/// <paramref name="directoryOfWritableXliffFiles"/></param>
		/// ------------------------------------------------------------------------------------
		public static void DeleteOldXliffFiles(string appId, string directoryOfWritableXliffFiles,
			string directoryOfInstalledXliffFiles)
		{
			//if (Assembly.GetEntryAssembly() == null)
			//    return; // Probably being called in a unit test.
			if (!Directory.Exists(directoryOfWritableXliffFiles))
				return; // Nothing to do.

			var oldDefaultXliffFilePath = Path.Combine(directoryOfWritableXliffFiles, GetXliffFileNameForLanguage(appId, kDefaultLang));
			if (!File.Exists(oldDefaultXliffFilePath))
				return; // Cleanup was apparently done previously

			File.Delete(oldDefaultXliffFilePath);

			foreach (var oldXliffFile in Directory.GetFiles(directoryOfWritableXliffFiles,
				GetXliffFileNameForLanguage(appId, "*")))
			{
				var filename = Path.GetFileName(oldXliffFile);
				if (string.IsNullOrEmpty(directoryOfInstalledXliffFiles) || File.Exists(Path.Combine(directoryOfInstalledXliffFiles, filename)))
				{
					try
					{
						File.Delete(oldXliffFile);
					}
					catch
					{
						// Oh, well, we tried.
					}
				}
			}
		}

		/// ------------------------------------------------------------------------------------
		internal static Dictionary<string, LocalizationManager> LoadedManagers
		{
			get { return s_loadedManagers; }
		}

		private static HashSet<string> PreviouslyLoadedManagers = new HashSet<string>();

		#endregion

		#region LocalizationManager construction/disposal
		/// ------------------------------------------------------------------------------------
		internal LocalizationManager(string appId, string appName, string appVersion,
			string directoryOfInstalledXliffFiles, string directoryForGeneratedDefaultXliffFile,
			string directoryOfUserModifiedXliffFiles, params string[] namespaceBeginnings)
		{
			// Test for a pathological case of bad install
			if (!Directory.Exists(directoryOfInstalledXliffFiles))
				throw new DirectoryNotFoundException(string.Format(
					"The default localizations folder {0} does not exist. This indicates a failed install for {1}. Please uninstall and reinstall {1}.",
					directoryOfInstalledXliffFiles, appName));
			Id = appId;
			Name = appName;
			AppVersion = appVersion;
			_installedXliffFileFolder = directoryOfInstalledXliffFiles;
			_generatedDefaultXliffFileFolder = directoryForGeneratedDefaultXliffFile;
			DefaultStringFilePath = GetXliffPathForLanguage(kDefaultLang, false);

			NamespaceBeginnings = namespaceBeginnings;
			CollectUpNewStringsDiscoveredDynamically = true;

			CreateOrUpdateDefaultXliffFileIfNecessary(namespaceBeginnings);

			_customXliffFileFolder = directoryOfUserModifiedXliffFiles;
			if (string.IsNullOrEmpty(_customXliffFileFolder))
			{
				_customXliffFileFolder = null;
				CanCustomizeLocalizations = false;
			}
			else
			{
				try
				{
					new FileIOPermission(FileIOPermissionAccess.Write, _customXliffFileFolder).Demand();
					CanCustomizeLocalizations = true;
				}
				catch (Exception e)
				{
					if (e is SecurityException)
						CanCustomizeLocalizations = false;
					else
						throw;
				}
			}

			ComponentCache = new Dictionary<IComponent, string>();
			ToolTipCtrls = new Dictionary<Control, ToolTip>();
			StringCache = new LocalizedStringCache(this);
			LocalizableComponents = new Dictionary<ILocalizableComponent, Dictionary<string, LocalizingInfo>>();
		}

		/// <summary>
		/// Minimal constructor for a new instance of the <see cref="L10NSharp.LocalizationManager"/> class.
		/// </summary>
		/// <param name="appId">
		/// The application Id (e.g. 'Pa' for Phonology Assistant).  This should be a unique name that
		/// identifies the manager for an assembly or application.
		/// </param>
		/// <param name="appName">
		/// The application's name. This will appear to the user in the localization dialog box as a
		/// parent item in the tree.  It may be the same as appId.
		/// </param>
		/// <param name="appVersion">
		/// The application's version.
		/// </param>
		internal LocalizationManager(string appId, string appName, string appVersion)
		{
			Id = appId;
			Name = appName;
			AppVersion = appVersion;
		}

		/// ------------------------------------------------------------------------------------
		private void CreateOrUpdateDefaultXliffFileIfNecessary(params string[] namespaceBeginnings)
		{
			// Make sure the folder exists.
			var dir = Path.GetDirectoryName(DefaultStringFilePath);
			if (dir != null && !Directory.Exists(dir))
				Directory.CreateDirectory(dir);

			var defaultStringFileInstalledPath = Path.Combine(_installedXliffFileFolder, GetXliffFileNameForLanguage(kDefaultLang));

			if (ScanningForCurrentStrings && DefaultStringFileExistsAndHasContents())
				File.Delete(DefaultStringFilePath);

			if (!ScanningForCurrentStrings && !DefaultStringFileExistsAndHasContents() && File.Exists(defaultStringFileInstalledPath))
				File.Copy(defaultStringFileInstalledPath, DefaultStringFilePath, true);

			if (DefaultStringFileExistsAndHasContents())
			{
				var xmlDoc = XElement.Load(DefaultStringFilePath);
				var docNamespace = xmlDoc.GetDefaultNamespace();
				var file = xmlDoc.Element(docNamespace + "file");

				XAttribute verAttribute = null;
				if (file != null)
				{
					verAttribute = file.Attribute("product-version");
				}

				if (verAttribute != null && new Version(verAttribute.Value) >= new Version(AppVersion ?? "0.0.1"))
					return;
			}

			// Before wasting a bunch of time, make sure we can open the file for writing.
			var fileStream = File.Open(DefaultStringFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
			fileStream.Close();

			var stringCache = new LocalizedStringCache(this, false);

			using (var dlg = new InitializationProgressDlg(Name, _applicationIcon, namespaceBeginnings))
			{
				dlg.ShowDialog();
				foreach (var locInfo in dlg.ExtractedInfo)
					stringCache.UpdateLocalizedInfo(locInfo);
			}

			stringCache.SaveIfDirty(stringCache.XliffDocuments.Keys);
		}

		/// <summary> Sometimes, on Linux, there is an empty DefaultStringFile.  This causes problems. </summary>
		private bool DefaultStringFileExistsAndHasContents()
		{
			return File.Exists(DefaultStringFilePath) && !String.IsNullOrWhiteSpace(File.ReadAllText(DefaultStringFilePath));
		}

		/// <summary>Check if we are collecting strings to localize, not setting up to display localized strings.</summary>
		internal static bool ScanningForCurrentStrings
		{
			get { return UILanguageId == "en" && IgnoreExistingEnglishXliffFiles; }
		}

		/// ------------------------------------------------------------------------------------
		public void Dispose()
		{
			if (LoadedManagers.ContainsKey(Id))
			{
				LoadedManagers.Remove(Id);
				PreviouslyLoadedManagers.Add(Id);
			}
		}

		#endregion

		#region Methods for getting, setting and verifying UI language id
		/// ------------------------------------------------------------------------------------
		public static void SetUILanguage(string langId,
			bool reapplyLocalizationsToAllObjectsInAllManagers)
		{
			if (UILanguageId == langId || string.IsNullOrEmpty(langId))
				return;
			var ci = L10NCultureInfo.GetCultureInfo(langId);
			if (ci.RawCultureInfo != null)
				Thread.CurrentThread.CurrentUICulture = ci.RawCultureInfo;
			else
				Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
			L10NCultureInfo.CurrentCulture = ci;
			s_uiLangId = langId;
			SetAvailableFallbackLanguageIds(GetAvailableLocalizedLanguages());

			if (reapplyLocalizationsToAllObjectsInAllManagers)
				ReapplyLocalizationsToAllObjectsInAllManagers();
		}

		/// <summary>
		/// Fill FallbackLanguages with the right values for the current s_uiLangId.  Also adjust
		/// s_uiLangId if needed to exactly match an available language.
		/// </summary>
		internal static void SetAvailableFallbackLanguageIds(IEnumerable<string> availableLanguages)
		{
			var uiPieces = s_uiLangId.Split('-');
			s_fallbackLanguageIds.Clear();
			var exactMatch = false;
			foreach (var lang in availableLanguages)
			{
				if (lang == s_uiLangId)
				{
					exactMatch = true;
					continue;
				}
				var langPieces = lang.Split('-');
				if (uiPieces[0] == langPieces[0])
					s_fallbackLanguageIds.Add(lang);
			}
			if (!exactMatch && s_fallbackLanguageIds.Count > 0)
			{
				// The exact language doesn't exist, but we do have something close (for example,
				// "es" instead of "es-ES" or "fr-FR" instead of "fr".  Change our idea of the UI
				// language to be something we have, not necessarily what the system thinks it has.
				s_uiLangId = s_fallbackLanguageIds[0];
				s_fallbackLanguageIds.RemoveAt(0);
			}
			// We always fall back to the default language (English), since it's guaranteed to have
			// a string, even if nobody can read it.
			if (!s_fallbackLanguageIds.Contains(kDefaultLang))
				s_fallbackLanguageIds.Add(kDefaultLang);
		}

		/// <summary>
		/// Get the language tags for all languages that have localized data that has
		/// been loaded.
		/// </summary>
		public static List<string> GetAvailableLocalizedLanguages()
		{
			var langsHavinglocalizations = (LoadedManagers == null ? new List<string>() :
				LoadedManagers.Values.SelectMany(lm => lm.GetAvailableUILanguageTags())
					.Distinct().ToList());
			return langsHavinglocalizations;
		}

		/// <summary>
		/// Returns one L10NCultureInfo object for each distinct language found in the collection of all
		/// cultures on the computer. Some languages are represented by more than one culture, and in
		/// those cases just the first culture is returned. There are several reasons for multiple
		/// cultures per language, the predominant one being there is more than one writing system
		/// for the language. An example of this is Chinese which has a Traditional and a Simplified
		/// writing system. Other languages have a Latin and a Cyrilic writing system.
		///
		/// Due to changes made in how this procedure determines what languages to return, it is
		/// possible that there may be an existing localization tied to a culture that is no longer
		/// returned in the collection. Because of this, a check is done to make sure all cultures
		/// represented by existing localizations are included in the list that is returned. This
		/// will result in that language being in the list twice, each instance having a different
		/// DisplayName.
		/// </summary>
		/// <param name="returnOnlyLanguagesHavingLocalizations">
		/// If TRUE then only languages represented by existing localizations are returned. If FALSE
		/// then all languages found are returned.
		/// </param>
		/// <returns>IEnumerable of L10NCultureInfo declared as IEnumerable of CultureInfo</returns>
		public static IEnumerable<L10NCultureInfo> GetUILanguages(bool returnOnlyLanguagesHavingLocalizations)
		{
			// BL-922, filter out duplicate languages. It may be surprising that we get more than one
			// neutral culture for a given language; however, some languages are written in more than one
			// script, and each script can have a neutral culture (e.g., uz-Cyrl, uz-Latn). We may eventually
			// have a need to localize into two scripts of the same language, but until users actually ask
			// for this, it just confuses things, so we're not supporting it.

			// first, get all installed cultures
			var allCultures = from ci in L10NCultureInfo.GetCultures(CultureTypes.NeutralCultures)
							  where ci.TwoLetterISOLanguageName != "iv"
							  select ci;

			// second, group by ISO 3 letter language code
			var groups = allCultures.GroupBy(c => c.ThreeLetterISOLanguageName);

			// finally, select the first culture in each language code group
			var allLangs = groups.Select(g => g.First()).ToList();

			var langsHavinglocalizations = GetAvailableLocalizedLanguages();

			// BL-1011: Make sure cultures that have existing localizations are included
			var missingCultures = langsHavinglocalizations.Where(l => allLangs.Any(al => al.Name == l) == false);
			allLangs.AddRange(missingCultures.Select(lang =>
			{
				try
				{
					return new L10NCultureInfo(lang); // return to the select, that is
				}
				catch (CultureNotFoundException)
				{
					// Unfortunately there is no way to create a CultureInfo for a culture the system
					// doesn't recognize, so we just can't offer this language on this system
					// (short of at least a major change of API).
					return null; // to the Select; filtered out below
				}
			}).Where(ci => ci != null));

			if (!returnOnlyLanguagesHavingLocalizations)
				return from ci in allLangs
					   orderby ci.DisplayName
					   select ci;

			return from ci in allLangs
				   where langsHavinglocalizations.Contains(ci.Name)
				   orderby ci.DisplayName
				   select ci;
		}

		/// <summary>
		/// Return the language tags for those languages that have been localized for the given program.
		/// </summary>
		public IEnumerable<string> GetAvailableUILanguageTags()
		{
			return StringCache.XliffDocuments.Keys.ToList();
		}

		/// <summary>
		/// Return the number of strings that appear to have been translated and approved for the
		/// given language in all the loaded managers.
		/// </summary>
		public static int NumberApproved(string lang)
		{
			if (lang == kDefaultLang)
				return StringCount(lang);
			var approved = 0;
			foreach (var lm in s_loadedManagers.Values)
			{
				XLiffDocument xdoc;
				if (lm.StringCache.XliffDocuments.TryGetValue(lang, out xdoc))
					approved += xdoc.NumberApproved;
			}
			return approved;
		}

		/// <summary>
		/// Return the fraction of strings that appear to have been translated and approved for the
		/// given language in all the loaded managers.
		/// </summary>
		public static float FractionApproved(string lang)
		{
			if (lang == kDefaultLang)
				return 1.0F;
			var total = Math.Max(StringCount(lang), StringCount(kDefaultLang));
			if (total == 0)
				return 0.0F;
			var approved = NumberApproved(lang);
			return (float)approved / (float)total;
		}

		/// <summary>
		/// Return the number of strings that appear to have been translated for the given language
		/// in all the loaded managers.
		/// </summary>
		public static int NumberTranslated(string lang)
		{
			if (lang == kDefaultLang)
				return StringCount(lang);
			var translated = 0;
			foreach (var lm in s_loadedManagers.Values)
			{
				XLiffDocument xdoc;
				if (lm.StringCache.XliffDocuments.TryGetValue(lang, out xdoc))
					translated += xdoc.NumberTranslated;
			}
			return translated;
		}

		/// <summary>
		/// Return the fraction of strings that appear to have been translated for the given language
		/// in all the loaded managers.
		/// </summary>
		public static float FractionTranslated(string lang)
		{
			if (lang == kDefaultLang)
				return 1.0F;
			var total = Math.Max(StringCount(lang), StringCount(kDefaultLang));
			if (total == 0)
				return 0.0F;
			var translated = NumberTranslated(lang);
			return (float)translated / (float)total;
		}

		/// <summary>
		/// Return the number of strings that appear to be available for the given language in all
		/// the loaded managers.
		/// </summary>
		public static int StringCount(string lang)
		{
			var count = 0;
			foreach (var lm in s_loadedManagers.Values)
			{
				XLiffDocument xdoc;
				if (lm.StringCache.XliffDocuments.TryGetValue(lang, out xdoc))
					count += xdoc.StringCount;
			}
			return count;
		}

		/// <summary>
		/// If the given file exists, return its parent folder name as a language tag if it
		/// appears to be valid (2 or 3 letters long or "zh-CN").  Otherwise return null.
		/// </summary>
		private static string GetLanguageTagFromFilePath(string xliffFile)
		{
			Debug.Assert(UseLanguageCodeFolders);
			if (File.Exists(xliffFile))
			{
				var langId = Path.GetFileName(Path.GetDirectoryName(xliffFile));
				if (System.Text.RegularExpressions.Regex.IsMatch(langId, "[a-z]{2,3}") || langId == "zh-CN")
					return langId;
			}
			return null;
		}
		#endregion

		#region Methods for showing localization dialog box
		/// ------------------------------------------------------------------------------------
		public void ShowLocalizationDialogBox(bool runInReadonlyMode)
		{
			LocalizeItemDlg.ShowDialog(this, "", runInReadonlyMode);
		}

		/// ------------------------------------------------------------------------------------
		public static void ShowLocalizationDialogBox(IComponent component)
		{
			TipDialog.Show("If you click on an item while you hold alt and shift keys down, this tool will open up with that item already selected.");
			LocalizeItemDlg.ShowDialog(GetLocalizationManagerForComponent(component), component, false);
		}

		/// ------------------------------------------------------------------------------------
		public static void ShowLocalizationDialogBox(string id)
		{
			TipDialog.Show("If you click on an item while you hold alt and shift keys down, this tool will open up with that item already selected.");
			LocalizeItemDlg.ShowDialog(GetLocalizationManagerForString(id), id, false);
		}

		#endregion

		#region Public Properties
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets or sets the list of langauges (by id) used to fallback to when looking for a
		/// string in the current UI language fails. The fallback order goes from the first
		/// item in this list to the last.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static IEnumerable<string> FallbackLanguageIds
		{
			get { return s_fallbackLanguageIds; }
			set
			{
				if (s_fallbackLanguageIds != null && s_fallbackLanguageIds.Count > 0)
					s_fallbackLanguageIds = value.ToList();
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the current UI language Id (i.e. the target language).
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string UILanguageId
		{
			get
			{
				if (s_uiLangId == null)
				{
					s_uiLangId = Thread.CurrentThread.CurrentUICulture.Name;
#if __MonoCS__
					// The current version of Mono does not define a CultureInfo for "zh", so
					// it tends to throw exceptions when we try to use just plain "zh".
					if (s_uiLangId == "zh-CN")
						return s_uiLangId;
					// Otherwise, we want the culture.neutral version.
#endif
					int i = s_uiLangId.IndexOf('-');
					if (i >= 0)
						s_uiLangId = s_uiLangId.Substring(0, i);
					SetAvailableFallbackLanguageIds(GetAvailableLocalizedLanguages());
				}

				return s_uiLangId;
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// This is what identifies a localization manager for a particular set of
		/// localized strings. This would likely be a DLL or EXE name like 'PA' or 'SayMore'.
		/// This will be the file name of the portion of the XLIFF file in which localized
		/// strings are stored. This would usually be the name of the assembly that owns a
		/// set of localized strings.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public string Id { get; private set; }

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// This is the presentable name for the set of localized strings. For example, the
		/// Id might be 'PA' but the LocalizationSetName might be 'Phonology Assistant'.
		/// This should be a name presentable to the user.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public string Name { get; private set; }

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// This is sent from the application that's creating the localization manager. It's
		/// written to the Xliff file and used to determine whether or not the application needs
		/// to be rescanned for localized strings.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public string AppVersion { get; private set; }

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Full file name and path to the default string file (i.e. English strings).
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal string DefaultStringFilePath { get; private set; }

		internal string DefaultInstalledStringFilePath
		{
			get { return Path.Combine(_installedXliffFileFolder, GetXliffFileNameForLanguage(Id, kDefaultLang)); }
		}

		/// ------------------------------------------------------------------------------------
		internal LocalizedStringCache StringCache { get; private set; }

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a value indicating whether or not user has authority to change localized strings.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public bool CanCustomizeLocalizations { get; private set; }

		public string[] NamespaceBeginnings { get; set; }

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Enumerates a Xliff file for each language. Prefer the custom localizations folder version
		/// if it exists, otherwise the installed langauge folder.
		/// Exception: never return the English Xliff, which is always handled separately and first.
		/// Doing this serves to insert any new dynamic strings into the cache, thus validating
		/// them as non-obsolete if we encounter them in other languages.
		/// Enhance JohnT: there ought to be some way NOT to load data for a language until we need it.
		/// This wastes time AND space.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public IEnumerable<string> XliffFilenamesToAddToCache
		{
			get
			{
				HashSet<string> langIdsOfCustomizedLocales = new HashSet<string>();
				string langId;
				if (_customXliffFileFolder != null && Directory.Exists(_customXliffFileFolder))
				{
					if (UseLanguageCodeFolders)
					{
						foreach (var folder in Directory.GetDirectories(_customXliffFileFolder))
						{
							var XliffFile = Path.Combine(folder, Id + kFileExtension);
							langId = GetLanguageTagFromFilePath(XliffFile);
							if (!String.IsNullOrEmpty(langId) && langId != kDefaultLang)
							{
								langIdsOfCustomizedLocales.Add(langId);
								yield return XliffFile;
							}
						}
					}
					else
					{
						foreach (var XliffFile in Directory.GetFiles(_customXliffFileFolder, Id + ".*" + kFileExtension))
						{
							langId = GetLangIdFromXliffFileName(XliffFile);
							if (langId != kDefaultLang)
							{
								langIdsOfCustomizedLocales.Add(langId);
								yield return XliffFile;
							}
						}
					}
				}
				if (_installedXliffFileFolder != null)
				{
					if (UseLanguageCodeFolders)
					{
						foreach (var folder in Directory.GetDirectories(_installedXliffFileFolder))
						{
							var XliffFile = Path.Combine(folder, Id + kFileExtension);
							langId = GetLanguageTagFromFilePath(XliffFile);
							if (!String.IsNullOrEmpty(langId) && langId != kDefaultLang)
							{
								langIdsOfCustomizedLocales.Add(langId);
								yield return XliffFile;
							}
						}
					}
					else
					{
						foreach (var XliffFile in Directory.GetFiles(_installedXliffFileFolder, Id + ".*" + kFileExtension))
						{
							langId = GetLangIdFromXliffFileName(XliffFile);
							if (langId != kDefaultLang &&    //Don't return the english Xliff here because we separately process it first.
								!langIdsOfCustomizedLocales.Contains(langId))
								yield return XliffFile;
						}
					}
				}
			}
		}
		#endregion

		#region Methods for caching and localizing objects.
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Adds the specified component to the localization manager's cache of objects to be
		/// localized and then applies localizations for the current UI language.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private void RegisterComponentForLocalizing(IComponent component, string id, string defaultText, string defaultTooltip, string defaultShortcutKeys, string comment)
		{
			RegisterComponentForLocalizing(new LocalizingInfo(component, id)
			{
				Text = defaultText,
				ToolTipText = defaultTooltip,
				ShortcutKeys = defaultShortcutKeys,
				Comment = comment
			}, null);
		}

		internal void RegisterComponentForLocalizing(LocalizingInfo info, Action<LocalizationManager, LocalizingInfo> successAction)
		{
			var component = info.Component;
			var id = info.Id;
			if (component == null || String.IsNullOrWhiteSpace(id))
				return;

			try
			{

				// This if/else used to be more concise but sometimes there were occassions
				// adding an item the first time using ComponentCache[component] = id would throw an
				// index outside the bounds of the array exception. I have no clue why nor
				// can I reliably reproduce the error nor do I know if this change will solve
				// the problem. Hopefully it will, but my guess is the same underlying code
				// will be called.
				if (ComponentCache.ContainsKey(component))
					ComponentCache[component] = id;  //somehow, we sometimes see "Msg: Index was outside the bounds of the array."
				else
				{
					var lm = GetLocalizationManagerForString(id);
					if (lm != null && lm != this)
					{
						lm.RegisterComponentForLocalizing(info, successAction);
						return;
					}
					if (component is ILocalizableComponent)
						ComponentCache.Add(component, id);
					else
					{
						// If this is the first time this object has passed this way, then
						// prepare it to be available for end-user localization.
						PrepareComponentForRuntimeLocalization(component);
						ComponentCache.Add(component, id);
						// Make it available for the config dialog to localize.
						StringCache.UpdateLocalizedInfo(info);
					}
				}

				if (successAction != null)
					successAction(this, info);
			}
			catch (Exception)
			{
#if DEBUG
				throw; // if you hit this ( Index was outside the bounds of the array) try to figure out why. What is the hash (?) value for the component?
#endif
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Prepares the specified component for runtime localization by subscribing to a
		/// mouse down event that will monitor whether or not to show the localization
		/// dialog box.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private void PrepareComponentForRuntimeLocalization(IComponent component)
		{
			var toolStripItem = component as ToolStripItem;
			if (toolStripItem != null)
			{
				toolStripItem.MouseDown += HandleToolStripItemMouseDown;
				toolStripItem.Disposed += HandleToolStripItemDisposed;
				return;
			}

			// For component that are part of an owning parent control that needs to
			// do some special handling when the user wants to localize, we need
			// the parent to subscribe to the mouse event, but we don't want to
			// subscribe once per column/page, so we first unsubscribe and then
			// subscribe. It's a little ugly, but there doesn't seem to be a better way:
			// http://stackoverflow.com/questions/399648/preventing-same-event-handler-assignment-multiple-times

			var ctrl = component as Control;
			if (ctrl != null)
			{
				ctrl.Disposed += HandleControlDisposed;

				TabPage tpg = ctrl as TabPage;
				if (tpg != null && tpg.Parent is TabControl)
				{
					tpg.Parent.MouseDown -= HandleControlMouseDown;
					tpg.Parent.MouseDown += HandleControlMouseDown;
					tpg.Parent.Disposed -= HandleControlDisposed;
					tpg.Parent.Disposed += HandleControlDisposed;
					tpg.Disposed += HandleTabPageDisposed;
					return;
				}

				ctrl.MouseDown += HandleControlMouseDown;
				return;
			}

			var columnHeader = component as ColumnHeader;
			if (columnHeader != null && columnHeader.ListView != null)
			{
				columnHeader.ListView.Disposed -= HandleListViewDisposed;
				columnHeader.ListView.Disposed += HandleListViewDisposed;
				columnHeader.ListView.ColumnClick -= HandleListViewColumnHeaderClicked;
				columnHeader.ListView.ColumnClick += HandleListViewColumnHeaderClicked;
				columnHeader.Disposed += HandleListViewColumnDisposed;
			}

			var dataGridViewColumn = component as DataGridViewColumn;
			if (dataGridViewColumn != null && dataGridViewColumn.DataGridView != null)
			{
				dataGridViewColumn.DataGridView.CellMouseDown -= HandleDataGridViewCellMouseDown;
				dataGridViewColumn.DataGridView.CellMouseDown += HandleDataGridViewCellMouseDown;
				dataGridViewColumn.DataGridView.Disposed -= HandleDataGridViewDisposed;
				dataGridViewColumn.DataGridView.Disposed += HandleDataGridViewDisposed;
				dataGridViewColumn.Disposed += HandleColumnDisposed;
			}
		}

		/// ------------------------------------------------------------------------------------
		internal void SaveIfDirty(ICollection<string> langIdsToForceCreate)
		{
			try
			{
				StringCache.SaveIfDirty(langIdsToForceCreate);
			}
			catch (IOException e)
			{
				CanCustomizeLocalizations = false;
				if (langIdsToForceCreate != null && langIdsToForceCreate.Any())
					MessageBox.Show(e.Message, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
		}

		/// ------------------------------------------------------------------------------------
		private string GetLangIdFromXliffFileName(string fileName)
		{
			Debug.Assert(!UseLanguageCodeFolders);
			fileName = fileName.Substring(0, fileName.Length - kFileExtension.Length);
			int i = fileName.LastIndexOf('.');
			return (i < 0 ? null : fileName.Substring(i + 1));
		}

		/// ------------------------------------------------------------------------------------
		private string GetXliffFileNameForLanguage(string langId)
		{
			return GetXliffFileNameForLanguage(Id, langId);
		}

		/// ------------------------------------------------------------------------------------
		internal string GetXliffPathForLanguage(string langId, bool getCustomPathEvenIfNonexistent)
		{
			var filename = GetXliffFileNameForLanguage(langId);
			if (langId == kDefaultLang)
				return Path.Combine(_generatedDefaultXliffFileFolder, filename);
			if (_customXliffFileFolder != null)
			{
				var customXliffFile = Path.Combine(_customXliffFileFolder, filename);
				if (getCustomPathEvenIfNonexistent || File.Exists(customXliffFile))
					return customXliffFile;
			}
			return _installedXliffFileFolder != null ? Path.Combine(_installedXliffFileFolder, filename) : null /* Pretty sure this won't end well*/;
		}

		/// ------------------------------------------------------------------------------------
		public bool DoesCustomizedXliffExistForLanguage(string langId)
		{
			return File.Exists(GetXliffPathForLanguage(langId, true));
		}

		/// ------------------------------------------------------------------------------------
		public void PrepareToCustomizeLocalizations()
		{
			if (_customXliffFileFolder == null)
				throw new InvalidOperationException("Localization manager for " + Id + "has no folder specified for customizing localizations");
			if (!CanCustomizeLocalizations)
				throw new InvalidOperationException("User does not have sufficient privilege to customize localizations for " + Id);
			try
			{
				// Make sure the folder exists.
				if (!Directory.Exists(_customXliffFileFolder))
					Directory.CreateDirectory(_customXliffFileFolder);
			}
			catch (Exception e)
			{
				if (e is SecurityException || e is UnauthorizedAccessException || e is IOException)
				{
					CanCustomizeLocalizations = false;
				}
				else
					throw;
			}
		}
		#endregion

		#region Methods for adding localized strings to cache.
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Add a localized string to the string cache.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public void AddString(string id, string defaultText, string defaultTooltip,
			string defaultShortcutKeys, string comment)
		{
			var locInfo = new LocalizingInfo(id)
			{
				Text = defaultText,
				ToolTipText = defaultTooltip,
				ShortcutKeys = defaultShortcutKeys,
				Comment = comment,
				LangId = kDefaultLang
			};

			StringCache.UpdateLocalizedInfo(locInfo);
		}

		#endregion

		#region Non static methods for getting localized strings
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the localized text for the specified component.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public string GetLocalizedString(IComponent component, string id, string defaultText, string defaultTooltip, string defaultShortcutKeys, string comment)
		{
			return GetLocalizedString(id, defaultText);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the localized text for the specified id.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public string GetLocalizedString(string id, string defaultText)
		{
			var text = (UILanguageId != kDefaultLang ? GetStringFromStringCache(UILanguageId, id) : null);

			return (text ?? StripOffLocalizationInfoFromText(defaultText));
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Get the best possible language id to use for retrieving a string.  In most cases,
		/// we would expect an identity transformation.  But this allows "es-ES" to map to "es"
		/// and vice versa depending on the actual data available.
		/// </summary>
		private static string MapToExistingLanguageIfPossible(string langId)
		{
			if (string.IsNullOrEmpty(langId))
				return langId;
			string realId;
			if (MapToExistingLanguage.TryGetValue(langId, out realId))
				return realId;
			return langId;
		}

		/// ------------------------------------------------------------------------------------
		private string GetStringFromStringCache(string uiLangId, string id)
		{
			var realLangId =  MapToExistingLanguageIfPossible(uiLangId);
			return StringCache.GetString(realLangId, id);
		}

		/// ------------------------------------------------------------------------------------
		private string GetTooltipFromStringCache(string uiLangId, string id)
		{
			var realLangId =  MapToExistingLanguageIfPossible(uiLangId);
			return StringCache.GetToolTipText(realLangId, id);
		}

		/// ------------------------------------------------------------------------------------
		private Keys GetShortCutKeyFromStringCache(string uiLangId, string id)
		{
			var realLangId =  MapToExistingLanguageIfPossible(uiLangId);
			return StringCache.GetShortcutKeys(realLangId, id);
		}

		#endregion

		#region GetString static methods
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the text for the specified component. The englishText is returned when the text
		/// for the specified object cannot be found for the current UI language.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetStringForObject(IComponent component, string englishText)
		{
			var lm = GetLocalizationManagerForComponent(component);

			if (lm != null)
			{
				string id;
				if (lm.ComponentCache.TryGetValue(component, out id))
					return lm.GetLocalizedString(id, englishText);
			}

			return (StripOffLocalizationInfoFromText(englishText ??
				Utils.GetProperty(component, "Text") as string));
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a string for the specified string id. The englishText is returned when
		/// a string cannot be found for the specified id and the current UI language.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetString(string stringId, string englishText)
		{
			return GetString(stringId, englishText, null, null, null, null);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a string for the specified string id. The englishText is returned when
		/// a string cannot be found for the specified id and the current UI language.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetString(string stringId, string englishText, string comment)
		{
			return GetString(stringId, englishText, comment, null, null, null);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a string for the specified string id. The englishText is returned when
		/// a string cannot be found for the specified id and the current UI language.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetString(string stringId, string englishText, string comment, IComponent component)
		{
			return GetString(stringId, englishText, comment, null, null, component);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a string for the specified string id. The englishText is returned when
		/// a string cannot be found for the specified id and the current UI language.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetString(string stringId, string englishText, string comment, string englishToolTipText,
			string englishShortcutKey, IComponent component)
		{
			if (component != null)
			{
				var lm = GetLocalizationManagerForComponent(component) ??
					GetLocalizationManagerForString(stringId);

				if (lm != null)
				{
					lm.RegisterComponentForLocalizing(component, stringId, englishText,
						englishToolTipText, englishShortcutKey, comment);

					return lm.GetLocalizedString(stringId, englishText);
				}
			}

			return GetStringFromAnyLocalizationManager(stringId) ??
				StripOffLocalizationInfoFromText(englishText);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a string for the specified string id, in the specified language, or the
		/// englishText if that wasn't found. Prefers the englishText passed here to one that
		/// we might have got out of a Xliff, as is the non-obvious-but-ultimately-correct
		/// policy for this library.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetString(string stringId, string englishText, string comment, IEnumerable<string> preferredLanguageIds, out string languageIdUsed)
		{
			if (preferredLanguageIds.Count() == 0)
				throw new ArgumentException("preferredLanguageIds was empty");

			if (string.IsNullOrEmpty(englishText))
				throw new ArgumentException("englishText may not be empty (because common... that can't be what you meant to do...");

			var r = GetStringFromAnyLocalizationManager(stringId, preferredLanguageIds, out languageIdUsed);

			//even if found in English Xliff, we prefer to use the version that came from the code
			if (languageIdUsed == "en" || string.IsNullOrEmpty(r))
			{
				languageIdUsed = "en";
				return StripOffLocalizationInfoFromText(englishText);
			}
			else
			{
				return r;
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a string for the specified application id and string id. When a string for the
		/// specified id cannot be found, then one is added  using the specified englishText is
		/// returned when a string cannot be found for the specified id and the current UI
		/// language.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetDynamicString(string appId, string id, string englishText)
		{
			return GetDynamicString(appId, id, englishText, null);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a string for the specified application id and string id. When a string for the
		/// specified id cannot be found, then one is added  using the specified englishText is
		/// returned when a string cannot be found for the specified id and the current UI
		/// language.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetDynamicString(string appId, string id, string englishText, string comment)
		{
			return GetDynamicStringOrEnglish(appId, id, englishText, comment, UILanguageId);
		}

		/// <summary>
		/// This is useful in unit testing. If some unit tests create LMs and dispose them,
		/// but other unit tests assume default behavior when no LMs exist at all,
		/// the unit tests that dispose of LMs should also call this so the others don't
		/// throw ObjectDisposedExceptions.
		/// </summary>
		public static void ForgetDisposedManagers()
		{
			PreviouslyLoadedManagers.Clear();
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a string for the specified application id and string id, in the requested
		/// language. When a string for the
		/// specified id cannot be found, then one is added  using the specified englishText is
		/// returned when a string cannot be found for the specified id and the current UI
		/// language. Use GetIsStringAvailableForLangId if you need to know if we have the
		/// value or not.
		/// Special case: unless englishText is null, that is what will be returned for langId = 'en',
		/// irrespective of what is in Xliff.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetDynamicStringOrEnglish(string appId, string id, string englishText, string comment, string langId)
		{
			//this happens in unit test environments or apps that
			//have imported a library that is L10N'ized, but the app
			//itself isn't initializing L10N yet.
			if (LoadedManagers.Count == 0)
			{
				if (PreviouslyLoadedManagers.Contains(appId))
				{
					throw new ObjectDisposedException(
						string.Format("The application id '{0}' refers to a LocalizationManager that has been disposed",
							appId));
				}
				return id;
			}
			LocalizationManager lm;
			if (!LoadedManagers.TryGetValue(appId, out lm))
			{
				if (PreviouslyLoadedManagers.Contains(appId))
				{
					throw new ObjectDisposedException(
						string.Format("The application id '{0}' refers to a LocalizationManager that has been disposed",
							appId));
				}
				throw new ArgumentException(
					string.Format("The application id '{0}' does not have an associated localization manager. Initialized LMs are {1}",
					appId, String.Join(", ", LoadedManagers.Keys)));
			}

			// If they asked for English, we are going to use the supplied englishText, regardless of what may be in
			// some Xliff, following the rule that the current c# code always wins. In case we really need to
			// recover the Xliff version, we will retrieve that if no default is provided.
			// Otherwise, let's look up this string, maybe it has been translated and put into a Xliff
			if (langId != "en" || englishText == null)
			{
				var text = lm.GetStringFromStringCache(langId, id);
				if (text != null)
					return text;
			}

			if (!lm.CollectUpNewStringsDiscoveredDynamically)
				return englishText;

			var locInfo = new LocalizingInfo(id)
			{
				LangId = kDefaultLang,
				Text = englishText,
				DiscoveredDynamically = true,
				UpdateFields = UpdateFields.Text
			};

			if (!string.IsNullOrEmpty(comment))
			{
				locInfo.Comment = comment;
				locInfo.UpdateFields |= UpdateFields.Comment;
			}

			lm.StringCache.UpdateLocalizedInfo(locInfo);
			lm.SaveIfDirty(null);// this will be common for GetDynamic string on users restricted from writing to ProgramData
			return englishText;
		}

		/// <summary>
		/// Set this to false if you don't want users to pollute Xliff files they might send to you
		/// with strings that are unique to their documents. For example, Bloom looks for strings
		/// in html that might have been localized; but Bloom doesn't want to ship an ever-growing
		/// list of discovered strings for people to translate that aren't actually part of what you get
		/// with Bloom. So it sets this to False unless the app was compiled in DEBUG mode.
		/// Default is true.
		/// </summary>
		public bool CollectUpNewStringsDiscoveredDynamically { get; set; }

		/// ------------------------------------------------------------------------------------
		public static bool GetIsStringAvailableForLangId(string id, string langId)
		{
			if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(langId))
				return false;
			var realLangId =  MapToExistingLanguageIfPossible(langId);
			var str = LoadedManagers.Values.Select(lm => lm.StringCache.GetValueForExactLangAndId(realLangId, id, false))
				.FirstOrDefault(txt => !string.IsNullOrEmpty(txt));
			if (string.IsNullOrEmpty(str) && !MapToExistingLanguage.ContainsKey(langId) && langId.Contains('-'))
			{
				var pieces = langId.Split('-');
				if (MapToExistingLanguage.ContainsKey(pieces[0]))
				{
					var realLangId2 = MapToExistingLanguage[pieces[0]];
					MapToExistingLanguage.Add(langId, realLangId2);
					if (realLangId != realLangId2)
						str = LoadedManagers.Values.Select(lm => lm.StringCache.GetValueForExactLangAndId(realLangId2, id, false))
							.FirstOrDefault(txt => !string.IsNullOrEmpty(txt));
				}
			}
			if (!MapToExistingLanguage.ContainsKey(langId))
				MapToExistingLanguage[langId] = langId;	// prevent trying this again and again.
			return !string.IsNullOrEmpty(str);
		}

		/// ------------------------------------------------------------------------------------
		public static string StripOffLocalizationInfoFromText(string text)
		{
			if (text == null || !text.StartsWith(kL10NPrefix))
				return text;

			text = text.Substring(kL10NPrefix.Length);
			int i = text.IndexOf("!", StringComparison.Ordinal);
			return (i < 0 ? text : text.Substring(i + 1));
		}

		/// ------------------------------------------------------------------------------------
		private static string GetStringFromAnyLocalizationManager(string stringId)
		{
			// Note: this is odd semantics to me (JH); looks to be part of the rule that we prefer the
			// English from the program source to the English from the Xliff.

			// This will enforce that the text to localize is just returned to the caller
			// when the default language id is the same as the current UI langauge id.
			if (UILanguageId == kDefaultLang)
				return null;

			string languageIdUsed;
			return GetStringFromAnyLocalizationManager(stringId, new[] { UILanguageId }, out languageIdUsed);
		}

		/// ------------------------------------------------------------------------------------
		private static string GetStringFromAnyLocalizationManager(string stringId, IEnumerable<string> preferredLanguageIds, out string languageIdUsed)
		{
			foreach (var langId in preferredLanguageIds)
			{
				var realLangId =  MapToExistingLanguageIfPossible(langId);
				var bestAnswer = LoadedManagers.Values.Select(lm => lm.StringCache.GetValueForExactLangAndId(realLangId, stringId, true))
					.FirstOrDefault(text => !string.IsNullOrEmpty(text));
				if (!string.IsNullOrEmpty(bestAnswer))
				{
					languageIdUsed = realLangId;
					return bestAnswer;
				}
				if (langId.Contains('-') && !MapToExistingLanguage.ContainsKey(langId))
				{
					var pieces = langId.Split('-');
					if (MapToExistingLanguage.ContainsKey(pieces[0]))
					{
						var realLangId2 = MapToExistingLanguage[pieces[0]];
						MapToExistingLanguage.Add(langId, realLangId2);
						if (realLangId != realLangId2)
						{
							bestAnswer = LoadedManagers.Values.Select(lm => lm.StringCache.GetValueForExactLangAndId(realLangId2, stringId, true))
								.FirstOrDefault(text => !string.IsNullOrEmpty(text));
							if (!string.IsNullOrEmpty(bestAnswer))
							{
								languageIdUsed = realLangId2;
								return bestAnswer;
							}
						}
					}
					if  (!MapToExistingLanguage.ContainsKey(langId))
						MapToExistingLanguage[langId] = langId;	// prevent trying this again and again.
				}
			}
			languageIdUsed = null;
			return null;
		}

		/// ------------------------------------------------------------------------------------
		public static string GetXliffFileNameForLanguage(string appId, string langId)
		{
			if (UseLanguageCodeFolders)
			{
				return Path.Combine(langId, appId + kFileExtension);
			}
			else
			{
				return string.Format("{0}.{1}{2}", appId, langId, kFileExtension);
			}
		}

		/// ------------------------------------------------------------------------------------
		private static LocalizationManager GetLocalizationManagerForComponent(IComponent component)
		{
			return LoadedManagers.Values.FirstOrDefault(lm => lm.ComponentCache.ContainsKey(component));
		}

		/// ------------------------------------------------------------------------------------
		private static LocalizationManager GetLocalizationManagerForString(string id)
		{
			return LoadedManagers.Values.FirstOrDefault(
				lm => lm.StringCache.GetString(UILanguageId, id) != null);
		}

		#endregion

		#region Methods that apply localizations to an object.
		internal void ApplyLocalizationsToILocalizableComponent(LocalizingInfo locInfo)
		{
			Dictionary<string, LocalizingInfo> idToLocInfo; // out variable

			var locComponent = locInfo.Component as ILocalizableComponent;
			if (locComponent != null && LocalizableComponents.TryGetValue(locComponent, out idToLocInfo))
			{
				ApplyLocalizationsToLocalizableComponent(locComponent, idToLocInfo);
				return;
			}
#if DEBUG
			var msg =
				"Either locInfo.component is not an ILocalizableComponent or LocalizableComponents hasn't been updated with id={0}.";
			throw new ApplicationException(string.Format(msg, locInfo.Id));
#endif
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Reapplies the localizations to all objects in the localization manager's cache of
		/// localized objects.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static void ReapplyLocalizationsToAllObjectsInAllManagers()
		{
			if (LoadedManagers == null)
				return;

			foreach (var lm in LoadedManagers.Values)
				lm.ReapplyLocalizationsToAllComponents();
		}
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Reapplies the localizations to all objects in the localization manager's cache of
		/// localized objects.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static void ReapplyLocalizationsToAllObjects(string localizationManagerId)
		{
			if (LoadedManagers == null)
				return;

			LocalizationManager lm;
			if (LoadedManagers.TryGetValue(localizationManagerId, out lm))
				lm.ReapplyLocalizationsToAllComponents();
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Reapplies the localizations to all components in the localization manager's cache of
		/// localized components.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void ReapplyLocalizationsToAllComponents()
		{
			foreach (IComponent component in ComponentCache.Keys)
				ApplyLocalization(component);

			LocalizeItemDlg.FireStringsLocalizedEvent();
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Recreates the tooltip control and updates the tooltip text for each object having
		/// a tooltip. This is necessary sometimes when controls get moved from form to form
		/// during runtime.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public void RefreshToolTips()
		{
			foreach (var toolTipCtrl in ToolTipCtrls.Values)
				toolTipCtrl.Dispose();

			ToolTipCtrls.Clear();

			// This used to be a for-each, but on rare occassions, a "Collection was
			// modified; enumeration operation may not execute" exception would be
			// thrown. This should solve the problem.
			var controls = ComponentCache.Where(x => x.Key is Control).ToArray();
			for (int i = 0; i < controls.Length; i++)
			{
				var toolTipText = GetTooltipFromStringCache(UILanguageId, controls[i].Value);
				if (!string.IsNullOrEmpty(toolTipText)) //JH: hoping to speed this up a bit
					ApplyLocalizedToolTipToControl((Control)controls[i].Key, toolTipText);
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Initializes the specified component.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void ApplyLocalization(IComponent component)
		{
			if (component == null)
				return;

			string id;
			if (!ComponentCache.TryGetValue(component, out id))
				return;

			var locComponent = component as ILocalizableComponent;
			if (locComponent != null)
			{
				Dictionary<string, LocalizingInfo> idToLocInfo;
				if (LocalizableComponents.TryGetValue(locComponent, out idToLocInfo))
				{
					ApplyLocalizationsToLocalizableComponent(locComponent, idToLocInfo);
					return;
				}
			}

			if (ApplyLocalizationsToControl(component as Control, id))
				return;

			if (ApplyLocalizationsToToolStripItem(component as ToolStripItem, id))
				return;

			if (ApplyLocalizationsToListViewColumnHeader(component as ColumnHeader, id))
				return;

			ApplyLocalizationsToDataGridViewColumn(component as DataGridViewColumn, id);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Initializes the specified ILocalizableComponent.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void ApplyLocalizationsToLocalizableComponent(ILocalizableComponent locComponent, Dictionary<string, LocalizingInfo> idToLocInfo)
		{
			if (locComponent == null)
				return;

			foreach (var kvp in idToLocInfo)
			{
				var id = kvp.Key;
				var locInfo = kvp.Value;
				locComponent.ApplyLocalizationToString(locInfo.Component, id, GetLocalizedString(id, locInfo.Text));
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Initializes the specified control.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private bool ApplyLocalizationsToControl(Control ctrl, string id)
		{
			if (ctrl == null)
				return false;

			var text = GetStringFromStringCache(UILanguageId, id);
			var toolTipText = GetTooltipFromStringCache(UILanguageId, id);

			if (text != null && string.CompareOrdinal(ctrl.Text, text) != 0)
				ctrl.Text = text;

			ApplyLocalizedToolTipToControl(ctrl, toolTipText);
			return true;
		}

		/// ------------------------------------------------------------------------------------
		private void ApplyLocalizedToolTipToControl(Control ctrl, string toolTipText)
		{
			var topctrl = GetRealTopLevelControl(ctrl);
			if (topctrl == null)
				return;

			// Check if the control's top level control has a reference to a tooltip. If
			// it does, then use that tooltip for assigning tooltip text to the control.
			// Otherwise, create a new tooltip and reference it using the control's top
			// level control.
			ToolTip ttctrl;
			if (!ToolTipCtrls.TryGetValue(topctrl, out ttctrl))
			{
				if (string.IsNullOrEmpty(toolTipText))
					return;

				ttctrl = new ToolTip();
				ToolTipCtrls[topctrl] = ttctrl;
				topctrl.ParentChanged += HandleToolTipRefChanged;
				topctrl.HandleDestroyed += HandleToolTipRefDestroyed;
			}

			ttctrl.SetToolTip(ctrl, toolTipText);
		}

		/// ------------------------------------------------------------------------------------
		public static string GetLocalizedToolTipForControl(Control ctrl)
		{
			LocalizationManager lm = GetLocalizationManagerForComponent(ctrl);
			var topctrl = GetRealTopLevelControl(ctrl);
			if (topctrl == null || lm == null)
				return null;

			ToolTip ttctrl;
			return (lm.ToolTipCtrls.TryGetValue(topctrl, out ttctrl)) ? ttctrl.GetToolTip(ctrl) : null;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the real top level control (using the control's TopLevelControl property
		/// seems to return null until the control is on a form).
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private static Control GetRealTopLevelControl(Control ctrl)
		{
			var pctrl = ctrl;
			while (pctrl.Parent != null)
				pctrl = pctrl.Parent;

			return pctrl;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Handles the case when a tooltip instance was created and assinged to a top level
		/// control that has now been added to another control, thus making the other control
		/// top level instead. Therefore, we need to make sure the tooltip is reassigned to
		/// the new top level control.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private void HandleToolTipRefChanged(object sender, EventArgs e)
		{
			var oldtopctrl = sender as Control;
			var newtopctrl = GetRealTopLevelControl(oldtopctrl);
			if (oldtopctrl == null || newtopctrl == null)
				return;

			oldtopctrl.ParentChanged -= HandleToolTipRefChanged;
			newtopctrl.ParentChanged += HandleToolTipRefChanged;
			RefreshToolTips();
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Handles removing tooltip controls from the global tool tip collection for top level
		/// controls that are destroyed and have controls on them using tool tip controls from
		/// that collection.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private void HandleToolTipRefDestroyed(object sender, EventArgs e)
		{
			var topctrl = sender as Control;
			if (topctrl == null)
				return;

			topctrl.ParentChanged -= HandleToolTipRefChanged;
			topctrl.HandleDestroyed -= HandleToolTipRefDestroyed;

			ToolTip ttctrl;
			if (ToolTipCtrls.TryGetValue(topctrl, out ttctrl))
				ttctrl.Dispose();

			ToolTipCtrls.Remove(topctrl);
		}

		/// ------------------------------------------------------------------------------------
		private bool ApplyLocalizationsToToolStripItem(ToolStripItem item, string id)
		{
			if (item == null)
				return false;

			var text = GetStringFromStringCache(UILanguageId, id);
			var toolTipText = GetTooltipFromStringCache(UILanguageId, id);
			item.Text = (text ?? StripOffLocalizationInfoFromText(item.Text));
			item.ToolTipText = (toolTipText ?? StripOffLocalizationInfoFromText(item.ToolTipText));

			var shortcutKeys = GetShortCutKeyFromStringCache(UILanguageId, id);
			if (item is ToolStripMenuItem && shortcutKeys != Keys.None)
				((ToolStripMenuItem)item).ShortcutKeys = shortcutKeys;

			return true;
		}

		/// ------------------------------------------------------------------------------------
		private bool ApplyLocalizationsToListViewColumnHeader(ColumnHeader hdr, string id)
		{
			if (hdr == null)
				return false;

			var text = GetStringFromStringCache(UILanguageId, id);
			hdr.Text = (text ?? StripOffLocalizationInfoFromText(hdr.Text));
			return true;
		}

		/// ------------------------------------------------------------------------------------
		private bool ApplyLocalizationsToDataGridViewColumn(DataGridViewColumn col, string id)
		{
			if (col == null)
				return false;

			var text = GetStringFromStringCache(UILanguageId, id);
			col.HeaderText = (text ?? StripOffLocalizationInfoFromText(col.HeaderText));
			col.ToolTipText = GetTooltipFromStringCache(UILanguageId, id);
			return true;
		}

		#endregion

		#region Mouse down, handle destroyed, and dispose handlers
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Handles Ctrl-Shift-Click on ToolStripItems;
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void HandleToolStripItemMouseDown(object sender, MouseEventArgs e)
		{
			if (!DoHandleMouseDown)
				return;

			// Make sure all drop-downs are closed that are in the
			// chain of menu items for this item.
			var tsddi = sender as ToolStripDropDownItem;
			while (tsddi != null)
			{
				tsddi.DropDown.Close();

				if (tsddi.Owner is ContextMenuStrip)
					((ContextMenuStrip)tsddi.Owner).Close();

				tsddi = tsddi.OwnerItem as ToolStripDropDownItem;
			}

			LocalizeItemDlg.ShowDialog(this, (IComponent)sender, false);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Set this to false to make Localization Manager ignore clicks on the UI
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static bool EnableClickingOnControlToBringUpLocalizationDialog { get; set; }

		private static bool DoHandleMouseDown
		{
			get
			{
				return EnableClickingOnControlToBringUpLocalizationDialog &&
					Control.ModifierKeys == (Keys.Alt | Keys.Shift);
			}
		}

		public static string EmailForSubmissions;

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Handles the tool strip item disposed.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private void HandleToolStripItemDisposed(object sender, EventArgs e)
		{
			var item = sender as ToolStripItem;
			if (item != null)
			{
				item.MouseDown -= HandleToolStripItemMouseDown;
				item.Disposed -= HandleToolStripItemDisposed;

				ComponentCache.Remove(item);
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Handles Alt-Shift-Click on controls.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void HandleControlMouseDown(object sender, MouseEventArgs e)
		{
			if (!DoHandleMouseDown)
				return;

			var ctrl = sender as Control;

			if (ctrl is TabControl)
			{
				var tabctrl = ctrl as TabControl;
				for (int i = 0; i < tabctrl.TabPages.Count; i++)
				{
					if (tabctrl.GetTabRect(i).Contains(e.Location))
					{
						ctrl = tabctrl.TabPages[i];
						break;
					}
				}
			}

			var lm = GetLocalizationManagerForComponent(ctrl);

			if (LaunchingLocalizationDialog != null)
				LaunchingLocalizationDialog(lm, new EventArgs());
			LocalizeItemDlg.ShowDialog(lm, ctrl, false);
			if (ClosingLocalizationDialog != null)
				ClosingLocalizationDialog(lm, new EventArgs());
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// When controls get destroyed, do a little clean up.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void HandleControlDisposed(object sender, EventArgs e)
		{
			var ctrl = sender as Control;
			if (ctrl == null)
				return;

			ctrl.Disposed -= HandleControlDisposed;
			ctrl.MouseDown -= HandleControlMouseDown;

			ComponentCache.Remove(ctrl);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// When a TabPage gets disposed, remove reference to it from the object cache.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void HandleTabPageDisposed(object sender, EventArgs e)
		{
			var tabPage = sender as TabPage;
			if (tabPage == null)
				return;

			tabPage.Disposed -= HandleTabPageDisposed;
			ComponentCache.Remove(tabPage);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// When DataGridView controls get disposed, do a little clean up.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void HandleDataGridViewDisposed(object sender, EventArgs e)
		{
			var grid = sender as DataGridView;
			if (grid == null)
				return;

			grid.Disposed -= HandleControlDisposed;
			grid.CellMouseDown -= HandleDataGridViewCellMouseDown;

			ComponentCache.Remove(grid);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Handles ListView column header clicks. Unfortunately, even if the localization
		/// dialog box is shown, this click on the header will not get eaten (like it does
		/// for other controls). Therefore, if clicking on the column header sorts the column,
		/// that sorting will take place after the dialog box is closed.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void HandleListViewColumnHeaderClicked(object sender, ColumnClickEventArgs e)
		{
			if (DoHandleMouseDown)
				return;

			var lv = sender as ListView;
			if (lv != null && ComponentCache.ContainsKey(lv.Columns[e.Column]))
				LocalizeItemDlg.ShowDialog(this, lv.Columns[e.Column], false);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// When ListView controls get disposed, do a little clean up.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void HandleListViewDisposed(object sender, EventArgs e)
		{
			var lv = sender as ListView;
			if (lv == null)
				return;

			lv.Disposed -= HandleListViewDisposed;
			lv.ColumnClick -= HandleListViewColumnHeaderClicked;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// When ListView ColumnHeader controls get disposed, remove the reference to it from the
		/// object cache.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void HandleListViewColumnDisposed(object sender, EventArgs e)
		{
			var column = sender as ColumnHeader;
			if (column == null)
				return;

			column.Disposed -= HandleListViewColumnDisposed;
			ComponentCache.Remove(column);
		}

		/// ------------------------------------------------------------------------------------
		internal void HandleDataGridViewCellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
		{
			if (!DoHandleMouseDown)
				return;

			var grid = sender as DataGridView;
			if (grid != null && e.RowIndex < 0 && ComponentCache.ContainsKey(grid.Columns[e.ColumnIndex]))
				LocalizeItemDlg.ShowDialog(this, grid.Columns[e.ColumnIndex], false);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// When DataGridViewColumn controls get disposed, remove the reference to it from the
		/// object cache.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void HandleColumnDisposed(object sender, EventArgs e)
		{
			var column = sender as DataGridViewColumn;
			if (column == null)
				return;

			column.Disposed -= HandleColumnDisposed;
			ComponentCache.Remove(column);
		}
		#endregion

		/// ------------------------------------------------------------------------------------
		public override string ToString()
		{
			return Id + ", " + Name;
		}

		/// <summary>
		/// Merge the existing English xliff file into newly collected data and write the result to the temp
		/// directory.
		/// </summary>
		public static void MergeExistingEnglishXliffFileIntoNew(string installedStringFileFolder, string appId)
		{
			LocalizationManager lm;
			if (!LoadedManagers.TryGetValue(appId, out lm))
				return;
			XLiffDocument newDoc;
			if (!lm.StringCache.XliffDocuments.TryGetValue("en", out newDoc))
				return;
			var oldDocPath = Path.Combine(installedStringFileFolder, LocalizationManager.GetXliffFileNameForLanguage(appId, "en"));
			var oldDoc = XLiffDocument.Read(oldDocPath);
			var outputDoc = MergeXliffDocuments(newDoc, oldDoc, true);

			outputDoc.File.SourceLang = oldDoc.File.SourceLang;
			outputDoc.File.ProductVersion = oldDoc.File.ProductVersion;
			outputDoc.File.HardLineBreakReplacement = oldDoc.File.HardLineBreakReplacement;
			outputDoc.File.AmpersandReplacement = oldDoc.File.AmpersandReplacement;
			outputDoc.File.Original = oldDoc.File.Original;
			outputDoc.File.DataType = oldDoc.File.DataType;
			var outputPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(LocalizationManager.GetXliffFileNameForLanguage(appId, "en")));
			outputDoc.Save(outputPath);
		}

		/// <summary>
		/// Return the XLiffDocument that results from merging two XLiffDocument objects.
		/// </summary>
		internal static XLiffDocument MergeXliffDocuments(XLiffDocument xliffNew, XLiffDocument xliffOld, bool verbose = false)
		{
			// xliffNew has the data found in the current scan.
			// xliffOld is that data from the (optional) input baseline XLIFF file.
			// xliffOutput combines data from both xliffNew and xliffOld, preferring the new data when the two differ in the
			//    actual string content.

			// write the header elements of the new XLIFF file.
			var xliffOutput = new XLiffDocument();

			var newStringCount = 0;
			var changedStringCount = 0;
			var wrongDynamicFlagCount = 0;
			var missingDynamicStringCount = 0;
			var missingStringCount = 0;
			var newDynamicCount = 0;

			var newStringIds = new List<string>();
			var changedStringIds = new List<string>();
			var wrongDynamicStringIds = new List<string>();
			var missingDynamicStringIds = new List<string>();
			var missingStringIds = new List<string>();

			// write out the newly-found units, comparing against units with the same ids
			// found in the old XLIFF file.
			foreach (var tu in xliffNew.File.Body.TransUnits)
			{
				xliffOutput.File.Body.TransUnits.Add(tu);
				if (tu.Dynamic)
					++newDynamicCount;
				if (xliffOld != null)
				{
					var tuOld = xliffOld.File.Body.GetTransUnitForId(tu.Id);
					if (tuOld == null)
					{
						++newStringCount;
						newStringIds.Add(tu.Id);
					}
					else
					{
						foreach (var note in tuOld.Notes)
						{
							bool haveAlready = false;
							foreach (var newnote in tu.Notes)
							{
								if (newnote.Text == note.Text)
								{
									haveAlready = true;
									break;
								}
							}
							if (!haveAlready)
							{
								if (note.Text.StartsWith("[OLD NOTE]") || note.Text.StartsWith("OLD TEXT"))
									tu.AddNote(note.NoteLang, note.Text);
								else
									tu.AddNote(note.NoteLang, "[OLD NOTE] " + note.Text);
							}
						}
						if (tu.Source.Value != tuOld.Source.Value)
						{
							++changedStringCount;
							changedStringIds.Add(tu.Id);
							tu.AddNote("en", String.Format("OLD TEXT (before {0}): {1}", xliffNew.File.ProductVersion, tuOld.Source.Value));
						}
						if (tuOld.Dynamic && !tu.Dynamic)
						{
							++wrongDynamicFlagCount;
							wrongDynamicStringIds.Add(tu.Id);
							tu.AddNote("en", String.Format("Not dynamic: found in static scan of compiled code (version {0})", xliffNew.File.ProductVersion));
						}
					}
				}
			}

			// write out any units found in the old XLIFF file that were not found
			// in the new scan.
			if (xliffOld != null)
			{
				foreach (var tu in xliffOld.File.Body.TransUnits)
				{
					var tuNew = xliffNew.File.Body.GetTransUnitForId(tu.Id);
					if (tuNew == null)
					{
						xliffOutput.File.Body.TransUnits.Add(tu);
						if (tu.Dynamic)
						{
							++missingDynamicStringCount;
							missingDynamicStringIds.Add(tu.Id);
							if (newDynamicCount > 0)	// note only if attempt made to collect dynamic strings
								tu.AddNote("en", String.Format("Not found when running compiled program (version {0})", xliffNew.File.ProductVersion));
						}
						else
						{
							++missingStringCount;
							missingStringIds.Add(tu.Id);
							tu.AddNote("en", String.Format("Not found in static scan of compiled code (version {0})", xliffNew.File.ProductVersion));
						}
					}
				}
			}

			// report on the differences between the new scan and the old XLIFF file.
			if (newStringCount > 0)
			{
				if (verbose)
				{
					Console.WriteLine("Added {0} new strings to the {1} xliff file", newStringCount, xliffNew.File.Original);
					newStringIds.Sort();
					foreach (var id in newStringIds)
						Console.WriteLine("    {0}", id);
				}
			}
			if (changedStringCount > 0)
			{
				if (verbose)
				{
					Console.WriteLine("{0} strings were updated in the {1} xliff file.", changedStringCount, xliffNew.File.Original);
					changedStringIds.Sort();
					foreach (var id in changedStringIds)
						Console.WriteLine("    {0}", id);
				}
			}
			if (wrongDynamicFlagCount > 0)
			{
				if (verbose)
				{
					Console.WriteLine("{0} strings were marked dynamic incorrectly in the old {1} xliff file.", wrongDynamicFlagCount, xliffNew.File.Original);
					wrongDynamicStringIds.Sort();
					foreach (var id in wrongDynamicStringIds)
						Console.WriteLine("    {0}", id);
				}
			}
			if (missingDynamicStringCount > 0)
			{
				if (verbose)
				{
					Console.WriteLine("{0} dynamic strings were added back from the old {1} xliff file", missingDynamicStringCount, xliffNew.File.Original);
					missingDynamicStringIds.Sort();
					foreach (var id in missingDynamicStringIds)
						Console.WriteLine("    {0}", id);
				}
			}
			if (missingStringCount > 0)
			{
				if (verbose)
				{
					Console.WriteLine("{0} possibly obsolete (maybe dynamic?) strings were added back from the old {1} xliff file", missingStringCount, xliffNew.File.Original);
					missingStringIds.Sort();
					foreach (var id in missingStringIds)
						Console.WriteLine("    {0}", id);
				}
			}
			xliffOutput.File.Body.TransUnits.Sort(LocalizedStringCache.TuComparer);
			return xliffOutput;
		}

	}
}
