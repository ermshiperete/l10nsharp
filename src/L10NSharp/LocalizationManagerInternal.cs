// Copyright (c) 2022 SIL International
// This software is licensed under the MIT License (http://opensource.org/licenses/MIT)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using L10NSharp.TMXUtils;
using L10NSharp.UI;
using L10NSharp.XLiffUtils;

namespace L10NSharp
{
	internal static class LocalizationManagerInternal<T>
	{
		private static List<string> s_fallbackLanguageIds =
			new List<string>(new[] { LocalizationManager.kDefaultLang });

		/// <summary>
		/// Map from the given language code to a variant we actually have.  (It can map from a
		/// language code onto itself. It can also map a code we know we don't have any variant of
		/// onto itself.)
		/// Because this is a concurrent dictionary, and lazy loading only puts correct data into
		/// it as a result of loading xliff files, it is safe (and desirable for performance) to
		/// attempt a retrieval from it without locking. However, modifications and possible loading
		/// of xliff files requires locking on LazyLoadLock. Also, since the data is incomplete
		/// until all lazy loading has been done, if you don't get a hit you must take steps to
		/// try to load any relevant missing data. This is currently handled in
		/// MapToExistingLanguageIfPossible(), which should always be used for simple retrievals.
		/// </summary>
		internal static ConcurrentDictionary<string, string> MapToExistingLanguage = new ConcurrentDictionary<string, string>();

		// If documents are loaded lazily, this lock must be held while loading one, or while using MapToExistingLanguage
		// in a way that might cause loading.
		internal static object LazyLoadLock = new object();

		private static readonly Dictionary<string, ILocalizationManagerInternal<T>> s_loadedManagers =
			new Dictionary<string, ILocalizationManagerInternal<T>>();

		#region Static methods for creating a LocalizationManagerInternal
		private static ILocalizationManager Create(string desiredUiLangId, string appId,
			string appName, string relativeSettingPathForLocalizationFolder,
			Icon applicationIcon, Func<string, ILocalizationManagerInternal<T>> createMethod)
		{
			if (string.IsNullOrEmpty(relativeSettingPathForLocalizationFolder))
				relativeSettingPathForLocalizationFolder = appName;
			else if (Path.IsPathRooted(relativeSettingPathForLocalizationFolder))
				throw new ArgumentException("Relative (non-rooted) path expected", "relativeSettingPathForLocalizationFolder");

			var directoryOfWritableTranslationFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				relativeSettingPathForLocalizationFolder, "localizations");

			if (!LoadedManagers.TryGetValue(appId, out var lm))
			{
				lm = createMethod(directoryOfWritableTranslationFiles);

				LoadedManagers[appId] = lm;
				PreviouslyLoadedManagers.Remove(appId);
			}

			lm.ApplicationIcon = applicationIcon;

			if (string.IsNullOrEmpty(desiredUiLangId))
			{
				desiredUiLangId = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
			}

			if (!IsDesiredUiCultureAvailable(desiredUiLangId))
			{
				using (var dlg = new LanguageChoosingDialog(L10NCultureInfo.GetCultureInfo(desiredUiLangId), applicationIcon))
				{
					dlg.ShowDialog();
					desiredUiLangId = dlg.SelectedLanguage;
				}
			}

			LocalizationManager.SetUILanguage(desiredUiLangId, false);

			LocalizationManager.EnableClickingOnControlToBringUpLocalizationDialog = true;

			return lm;
		}

		private static bool IsDesiredUiCultureAvailable(string desiredUiLangId)
		{
			if (IsLocalizationAvailable(desiredUiLangId))
				return true;
			// We may know about a closely related language (e.g., we have es-ES but were asked for es-BR).
			// If so we want to return true.
			var fallbackLangId = MapToExistingLanguageIfPossible(desiredUiLangId);
			// If the input and output of MapToExistingLanguageIfPossible are the same then there is no mapping
			// known for the language and we should return false instead of infinitely recursing.
			// (Storing such redundant mappings makes other code more performant.)
			if (fallbackLangId == desiredUiLangId)
				return false;

			return IsDesiredUiCultureAvailable(fallbackLangId);
		}

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
		/// <param name="directoryOfInstalledTmxFiles">The full folder path of the original TMX files
		/// installed with the application.</param>
		/// <param name="relativeSettingPathForLocalizationFolder">The path, relative to
		/// %appdata%, where your application stores user settings (e.g., "SIL\SayMore").
		/// A folder named "localizations" will be created there.</param>
		/// <param name="applicationIcon"> </param>
		/// <param name="additionalLocalizationMethods">MethodInfo objects representing
		/// additional methods that should be regarded as calls to get localizations. If the method
		/// is named "Localize", the extractor will attempt to parse its signature as an extension
		/// method with the parameters (this string s, string separateId="", string comment="").
		/// Otherwise, it will be treated like a L10nSharp GetString method if its signature
		/// matches one of the following: (string stringId, string englishText),
		/// (string stringId, string englishText, string comment), or
		/// (string stringId, string englishText, string comment, string englishToolTipText,
		/// string englishShortcutKey, IComponent component).</param>
		/// <param name="namespaceBeginnings">A list of namespace beginnings indicating
		/// what types to scan for localized string calls. For example, to only scan
		/// types found in Pa.exe and assuming all types in that assembly begin with
		/// 'Pa', then this value would only contain the string 'Pa'.</param>
		/// ------------------------------------------------------------------------------------
		public static ILocalizationManager CreateTmx(string desiredUiLangId, string appId,
			string appName, string appVersion, string directoryOfInstalledTmxFiles,
			string relativeSettingPathForLocalizationFolder,
			Icon applicationIcon, IEnumerable<MethodInfo> additionalLocalizationMethods,
			params string[] namespaceBeginnings)
		{
			return Create(desiredUiLangId, appId, appName,
				relativeSettingPathForLocalizationFolder, applicationIcon,
				directoryOfWritableTmxFiles =>
					(ILocalizationManagerInternal<T>) new TMXLocalizationManager(appId, appName,
						appVersion, directoryOfInstalledTmxFiles,
						directoryOfWritableTmxFiles, directoryOfWritableTmxFiles,
						additionalLocalizationMethods,
						namespaceBeginnings));
		}

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
		/// application. May include an optional file extension, which will be stripped off but
		/// used to correctly set the "original" attribute when persisting an XLIFF file. The
		/// base portion must still be unique (i.e., it is not valid to create a LM for
		/// "Blah.exe" and another for "Blah.dll").</param>
		/// <param name="appName">The application's name. This will appear to the user
		/// in the localization dialog box as a parent item in the tree.</param>
		/// <param name="appVersion"></param>
		/// <param name="directoryOfInstalledXliffFiles">The full folder path of the original Xliff files
		/// installed with the application.</param>
		/// <param name="relativeSettingPathForLocalizationFolder">The path, relative to
		/// %appdata%, where your application stores user settings (e.g., "SIL\SayMore").
		/// A folder named "localizations" will be created there.</param>
		/// <param name="applicationIcon"> </param>
		/// <param name="additionalLocalizationMethods">MethodInfo objects representing
		/// additional methods that should be regarded as calls to get localizations. If the method
		/// is named "Localize", the extractor will attempt to parse its signature as an extension
		/// method with the parameters (this string s, string separateId="", string comment="").
		/// Otherwise, it will be treated like a L10nSharp GetString method if its signature
		/// matches one of the following: (string stringId, string englishText),
		/// (string stringId, string englishText, string comment), or
		/// (string stringId, string englishText, string comment, string englishToolTipText,
		/// string englishShortcutKey, IComponent component).</param>
		/// <param name="namespaceBeginnings">A list of namespace beginnings indicating
		/// what types to scan for localized string calls. For example, to only scan
		/// types found in Pa.exe and assuming all types in that assembly begin with
		/// 'Pa', then this value would only contain the string 'Pa'.</param>
		/// ------------------------------------------------------------------------------------
		public static ILocalizationManager CreateXliff(string desiredUiLangId, string appId,
			string appName, string appVersion, string directoryOfInstalledXliffFiles,
			string relativeSettingPathForLocalizationFolder,
			Icon applicationIcon,
			IEnumerable<MethodInfo> additionalLocalizationMethods,
			params string[] namespaceBeginnings)
		{
			if (string.IsNullOrWhiteSpace(appId))
				throw new ArgumentNullException(nameof(appId));
			var origExeExtension = Path.GetExtension(appId);
			if (origExeExtension == string.Empty)
				origExeExtension = ".dll";
			appId = Path.GetFileNameWithoutExtension(appId);

			return Create(desiredUiLangId, appId, appName,
				relativeSettingPathForLocalizationFolder, applicationIcon,
				directoryOfWritableXliffFiles =>
					(ILocalizationManagerInternal<T>) new XLiffLocalizationManager(appId, origExeExtension, appName,
						appVersion, directoryOfInstalledXliffFiles,
						directoryOfWritableXliffFiles, directoryOfWritableXliffFiles,
						additionalLocalizationMethods,
						namespaceBeginnings));
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Now that L10NSharp creates all writable Xliff files under LocalApplicationData
		/// instead of the common/shared AppData folder, applications can use this method to
		/// purge old Xliff files.</summary>
		/// <param name="appId">ID of the application used for creating the Xliff files (typically
		/// the same ID passed as the 2nd parameter to LocalizationManagerInternal.Create, but
		/// without a file extension).</param>
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
			XLiffLocalizationManager.DeleteOldXliffFiles(appId, directoryOfWritableXliffFiles,
			 directoryOfInstalledXliffFiles);
		}

		/// ------------------------------------------------------------------------------------
		internal static Dictionary<string, ILocalizationManagerInternal<T>> LoadedManagers => s_loadedManagers;

		private static HashSet<string> PreviouslyLoadedManagers = new HashSet<string>();

		internal static void RemoveManager(string id)
		{
			if (LoadedManagers.ContainsKey(id))
			{
				LoadedManagers.Remove(id);
				PreviouslyLoadedManagers.Add(id);
			}
		}

		internal static void ShowLocalizationDialogBox(IComponent component,
			IWin32Window owner = null)
		{
			if (owner == null)
				owner = (component as Control)?.FindForm();
			TipDialog.ShowAltShiftClickTip(owner);
			LocalizeItemDlg<T>.ShowDialog(GetLocalizationManagerForComponent(component),
				component, false, owner);
		}

		public static void ShowLocalizationDialogBox(string id, IWin32Window owner = null)
		{
			TipDialog.ShowAltShiftClickTip(owner);
			LocalizeItemDlg<T>.ShowDialog(GetLocalizationManagerForString(id),
				id, false, owner);
		}

		/// ------------------------------------------------------------------------------------
		internal static ILocalizationManagerInternal<T> GetLocalizationManagerForComponent(
			IComponent component)
		{
			return LoadedManagers.Values.FirstOrDefault(lm => lm.ComponentCache.ContainsKey(component));
		}

		/// ------------------------------------------------------------------------------------
		internal static ILocalizationManagerInternal<T> GetLocalizationManagerForString(string id)
		{
			return LoadedManagers.Values.FirstOrDefault(
				lm => lm.StringCache.GetString(LocalizationManager.UILanguageId, id) != null);
		}

		#endregion

		#region Methods for getting, setting and verifying UI language id
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets or sets the list of languages (by id) used to fallback to when looking for a
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

		/// <summary>
		/// Fill FallbackLanguages with the right values for the current LocalizationManager.UILanguageId.  Also adjust
		/// LocalizationManager.UILanguageId if needed to exactly match an available language.
		/// </summary>
		internal static void SetAvailableFallbackLanguageIds(IEnumerable<string> availableLanguages)
		{
			var uiPieces = LocalizationManager.UILanguageId.Split('-');
			s_fallbackLanguageIds.Clear();
			var exactMatch = false;
			foreach (var lang in availableLanguages)
			{
				if (lang == LocalizationManager.UILanguageId)
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
				LocalizationManager.UILanguageId = s_fallbackLanguageIds[0];
				s_fallbackLanguageIds.RemoveAt(0);
			}
			// We always fall back to the default language (English), since it's guaranteed to have
			// a string, even if nobody can read it.
			if (!s_fallbackLanguageIds.Contains(LocalizationManager.kDefaultLang))
				s_fallbackLanguageIds.Add(LocalizationManager.kDefaultLang);
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

		public static bool IsLocalizationAvailable(string langId)
		{
			if (LoadedManagers == null)
				return false;
			return LoadedManagers.Values.Any(m => m.IsUILanguageAvailable(langId));
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

			var langsHavingLocalizations = GetAvailableLocalizedLanguages();

			// BL-1011: Make sure cultures that have existing localizations are included
			var missingCultures = langsHavingLocalizations.Where(l => allLangs.Any(al => al.Name == l) == false);
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
				where langsHavingLocalizations.Contains(ci.Name)
				orderby ci.DisplayName
				select ci;
		}

		/// <summary>
		/// Return the number of strings that appear to have been translated and approved for the
		/// given language in all the loaded managers.
		/// </summary>
		public static int NumberApproved(string lang)
		{
			if (lang == LocalizationManager.kDefaultLang)
				return StringCount(lang);
			var approved = 0;
			foreach (var lm in s_loadedManagers.Values)
			{
				approved += lm.StringCache.NumberApproved(lang);
			}
			return approved;
		}

		/// <summary>
		/// Return the fraction of strings that appear to have been translated and approved for the
		/// given language in all the loaded managers.
		/// </summary>
		public static float FractionApproved(string lang)
		{
			if (lang == LocalizationManager.kDefaultLang)
				return 1.0F;
			var total = Math.Max(StringCount(lang), StringCount(LocalizationManager.kDefaultLang));
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
			if (lang == LocalizationManager.kDefaultLang)
				return StringCount(lang);
			var translated = 0;
			foreach (var lm in s_loadedManagers.Values)
			{
				translated += lm.StringCache.NumberTranslated(lang);
			}
			return translated;
		}

		/// <summary>
		/// Return the fraction of strings that appear to have been translated for the given language
		/// in all the loaded managers.
		/// </summary>
		public static float FractionTranslated(string lang)
		{
			if (lang == LocalizationManager.kDefaultLang)
				return 1.0F;
			var total = Math.Max(StringCount(lang), StringCount(LocalizationManager.kDefaultLang));
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
				count += lm.StringCache.StringCount(lang);
			}
			return count;
		}

		#endregion

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

			if (LoadedManagers.TryGetValue(localizationManagerId, out var lm))
				lm.ReapplyLocalizationsToAllComponents();
		}


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

			return LocalizationManager.StripOffLocalizationInfoFromText(
				englishText ?? Utils.GetProperty(component, "Text") as string);
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
			return GetDynamicStringOrEnglish(appId, id, englishText, comment, LocalizationManager.UILanguageId);
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
		/// irrespective of what is in TMX/Xliff.
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
					if (LocalizationManager.ThrowIfManagerDisposed)
					{
						throw new ObjectDisposedException(
							$"The application id '{appId}' refers to a LocalizationManagerInternal that has been disposed");
					}
					return string.IsNullOrEmpty(englishText) ? id : englishText;
				}

				if (!string.IsNullOrEmpty(englishText) && langId == LocalizationManager.kDefaultLang)
					return englishText;
				return id;
			}
			if (!LoadedManagers.TryGetValue(appId, out var lm))
			{
				if (PreviouslyLoadedManagers.Contains(appId))
				{
					if (LocalizationManager.ThrowIfManagerDisposed)
					{
						throw new ObjectDisposedException(
							$"The application id '{appId}' refers to a LocalizationManagerInternal that has been disposed");
					}

					return string.IsNullOrEmpty(englishText) ? id : englishText;
				}
				throw new ArgumentException(
					$"The application id '{appId}' does not have an associated localization manager. " +
					$"Initialized LMs are {string.Join(", ", LoadedManagers.Keys)}");
			}

			// If they asked for English, we are going to use the supplied englishText, regardless of what may be in
			// some TMX/Xliff, following the rule that the current c# code always wins. In case we really need to
			// recover the TMX/Xliff version, we will retrieve that if no default is provided.
			// Otherwise, let's look up this string, maybe it has been translated and put into a TMX/Xliff
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
				LangId = LocalizationManager.kDefaultLang,
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

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Get the best possible language id to use for retrieving a string.  In most cases,
		/// we would expect an identity transformation.  But this allows "es-ES" to map to "es"
		/// and vice versa depending on the actual data available.
		/// With lazy loading of xliff documents, it's possible that MapToExistingLanguage does
		/// not yet contain data about this language. In that case, get the lock for
		/// loading xliff docs, load any relevant ones, and try again.
		/// </summary>
		internal static string MapToExistingLanguageIfPossible(string langId)
		{
			if (string.IsNullOrEmpty(langId))
				return null;
			// It's a concurrent dictionary, so we can (for performance) try this without a lock.
			if (MapToExistingLanguage.TryGetValue(langId, out var realId))
				return realId;
			lock (LazyLoadLock)
			{
				// Load any available data in any LM related to this language code.
				// If files are loaded, this will add appropriate entries to MapToExistingLanguage.
				foreach (var lm in LoadedManagers.Values)
					lm.StringCache.TryGetDocument(langId, out _);
				if (MapToExistingLanguage.TryGetValue(langId, out var realId2))
					return realId2;

				// The above will find the appropriate result
				// if we are looking for, e.g., es-ES and have loaded a file in the es folder
				// which actually contains es-ES data. But it's also just conceivable that we have such data
				// and are now looking for es-BR. The code above will put entries in MapToExistingLanguage,
				// if they weren't there already, for es->es-ES and es-ES->es-ES,
				// whether we found that data in the es folder or the es-ES one,
				// but now (since we didn't find separate data for es-BR) we'd like to map that to es-ES, too.
				var pieces = langId.Split('-');
				if (MapToExistingLanguage.ContainsKey(pieces[0]))
				{
					var realLangId2 = MapToExistingLanguage[pieces[0]];
					MapToExistingLanguage.TryAdd(langId, realLangId2);
					return realLangId2;
				}

				// In case we haven't found ANY useful mapping for langId (we haven't localized into it or any
				// variation of it), we don't need to do all the fallback logic again next time.
				if (!MapToExistingLanguage.ContainsKey(langId))
					MapToExistingLanguage[langId] = langId;
				return langId;
			}
		}

		/// ------------------------------------------------------------------------------------
		public static bool GetIsStringAvailableForLangId(string id, string langId)
		{
			if (string.IsNullOrEmpty(langId) || string.IsNullOrEmpty(id))
				return false;

			var str = MapToExistingLanguageOrAddMapping(id, langId, out _);
			return !string.IsNullOrEmpty(str);
		}

		/// ------------------------------------------------------------------------------------
		internal static string GetStringFromAnyLocalizationManager(string stringId)
		{
			// Note: this is odd semantics to me (JH); looks to be part of the rule that we prefer the
			// English from the program source to the English from the TMX/Xliff.

			// This will enforce that the text to localize is just returned to the caller
			// when the default language id is the same as the current UI language id.
			if (LocalizationManager.UILanguageId == LocalizationManager.kDefaultLang)
				return null;

			string languageIdUsed;

			var langSeq = new List<string>(new[] {LocalizationManager.UILanguageId});
			langSeq.AddRange(FallbackLanguageIds);
			// don't ever want a value from the "en" LM, even if that is the UI language and the
			// only thing in the sequence; want to use the program default. (This method will return null,
			// and clients, which are passed an English default value, will use it.)
			langSeq.Remove("en");
			return GetStringFromAnyLocalizationManager(stringId, langSeq, out languageIdUsed);
		}

		/// ------------------------------------------------------------------------------------
		internal static string GetStringFromAnyLocalizationManager(string stringId,
			IEnumerable<string> preferredLanguageIds, out string languageIdUsed)
		{
			foreach (var langId in preferredLanguageIds)
			{
				var bestAnswer = MapToExistingLanguageOrAddMapping(stringId, langId, out languageIdUsed);
				if (bestAnswer != null)
					return bestAnswer;
			}
			languageIdUsed = null;
			return null;
		}

		private static string MapToExistingLanguageOrAddMapping(string stringId, string langId,
			out string languageIdUsed)
		{
			var realLangId = MapToExistingLanguageIfPossible(langId);
			var bestAnswer = LoadedManagers.Values.Select(lm =>
					lm.StringCache.GetValueForExactLangAndId(realLangId, stringId, true))
				.FirstOrDefault(text => !string.IsNullOrEmpty(text));
			
			languageIdUsed = realLangId;
			return bestAnswer;
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
				LocalizationManager.StripOffLocalizationInfoFromText(englishText);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a string for the specified string id, in the specified language, or the
		/// englishText if that wasn't found. Prefers the englishText passed here to one that
		/// we might have got out of a TMX/Xliff, as is the non-obvious-but-ultimately-correct
		/// policy for this library.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetString(string stringId, string englishText, string comment, IEnumerable<string> preferredLanguageIds, out string languageIdUsed)
		{
			if (preferredLanguageIds.Count() == 0)
				throw new ArgumentException("preferredLanguageIds was empty");

			if (string.IsNullOrEmpty(englishText))
				throw new ArgumentException("englishText may not be empty (because common... that can't be what you meant to do...");

			var stringFromAnyLocalizationManager = GetStringFromAnyLocalizationManager(stringId, preferredLanguageIds, out languageIdUsed);

			//even if found in English TMX/Xliff, we prefer to use the version that came from the code
			if (languageIdUsed == "en" || string.IsNullOrEmpty(stringFromAnyLocalizationManager))
			{
				languageIdUsed = "en";
				return LocalizationManager.StripOffLocalizationInfoFromText(englishText);
			}

			return stringFromAnyLocalizationManager;
		}

		public static string GetTranslationFileNameForLanguage(string appId, string langId)
		{
			var fileExtension =
				LocalizationManager.TranslationMemoryKind == TranslationMemory.XLiff
					? XLiffLocalizationManager.FileExtension
					: TMXLocalizationManager.FileExtension;
			return LocalizationManager.UseLanguageCodeFolders
				? Path.Combine(langId, $"{appId}{fileExtension}")
				: $"{appId}.{langId}{fileExtension}";
		}

		/// ------------------------------------------------------------------------------------
		public static string GetLocalizedToolTipForControl(Control ctrl)
		{
			var lm = GetLocalizationManagerForComponent(ctrl);
			var topctrl = GetRealTopLevelControl(ctrl);
			if (topctrl == null || lm == null)
				return null;

			return lm.ToolTipCtrls.TryGetValue(topctrl, out var ttctrl) ? ttctrl.GetToolTip(ctrl)
			 : null;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the real top level control (using the control's TopLevelControl property
		/// seems to return null until the control is on a form).
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal static Control GetRealTopLevelControl(Control ctrl)
		{
			var parentControl = ctrl;
			while (parentControl.Parent != null)
				parentControl = parentControl.Parent;

			return parentControl;
		}

		/// <summary>
		/// Merge the existing English translation file into newly collected data and write the result to the temp
		/// directory.
		/// </summary>
		public static void MergeExistingEnglishTranslationFileIntoNew(string installedStringFileFolder, string appId)
		{
			if (!LoadedManagers.TryGetValue(appId, out var lm))
				return;
			if (!lm.StringCache.TryGetDocument("en", out var newDoc))
				return;
			var oldDocPath = Path.Combine(installedStringFileFolder,
				GetTranslationFileNameForLanguage(appId, "en"));

			lm.MergeTranslationDocuments(appId, newDoc, oldDocPath);
		}

	}
}
