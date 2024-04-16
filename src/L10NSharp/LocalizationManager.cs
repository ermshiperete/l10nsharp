// Copyright (c) 2022 SIL International
// This software is licensed under the MIT License (http://opensource.org/licenses/MIT)

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using L10NSharp.TMXUtils;
using L10NSharp.UI;
using L10NSharp.XLiffUtils;

namespace L10NSharp
{
	public static class LocalizationManager
	{
		public const string kDefaultLang = "en";
		internal const string kL10NPrefix = "_L10N_:";
		internal const string kAppVersionPropTag = "x-appversion";

		private static string s_uiLangId;
		internal static TranslationMemory TranslationMemoryKind { get; set; }

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
		/// Note that this must be set before creating any LocalizationManagerInternal objects.
		/// The default is the old way of organizing (by filename).
		/// </summary>
		public static bool UseLanguageCodeFolders;

		/// <summary>
		/// Ignore any existing English xliff files, creating the working (English) file only
		/// from what is gathered by static analysis or dynamic harvesting of requests.
		/// </summary>
		public static bool IgnoreExistingEnglishTranslationFiles;

		/// <summary>
		/// Ignore any translated strings that are not marked "approved", acting as though the
		/// translation didn't exist.
		/// </summary>
		public static bool ReturnOnlyApprovedStrings;

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Set this to false to make Localization Manager ignore clicks on the UI
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static bool EnableClickingOnControlToBringUpLocalizationDialog { get; set; }

		public static string EmailForSubmissions { get; set; }

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Creates a new instance of a localization manager for the specified application id.
		/// If a localization manager has already been created for the specified id, then
		/// that is returned.
		/// </summary>
		/// <param name="kind">Translation memory type to use</param>
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
		/// <param name="directoryOfInstalledFiles">The full folder path of the original l10n
		/// files installed with the application.</param>
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
		[Obsolete("Use the overload without `TranslationMemory kind` parameter.")]
		public static ILocalizationManager Create(TranslationMemory kind, string desiredUiLangId,
			string appId, string appName, string appVersion, string directoryOfInstalledFiles,
			string relativeSettingPathForLocalizationFolder,
			Icon applicationIcon, string emailForSubmissions, params string[] namespaceBeginnings)
		{
			if (kind != TranslationMemory.XLiff)
			{
				throw new ArgumentException($@"Unknown translation memory kind {kind}. Only XLiff
				 is supported.",
					nameof(kind));
			}

			return Create(desiredUiLangId,
				appId, appName, appVersion, directoryOfInstalledFiles,
				relativeSettingPathForLocalizationFolder,
				applicationIcon, emailForSubmissions,
				namespaceBeginnings);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Creates a new instance of a localization manager for the specified application id.
		/// If a localization manager has already been created for the specified id, then
		/// that is returned.
		/// </summary>
		/// <param name="kind">Translation memory type to use</param>
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
		/// <param name="directoryOfInstalledFiles">The full folder path of the original l10n
		/// files installed with the application.</param>
		/// <param name="relativeSettingPathForLocalizationFolder">The path, relative to
		/// %appdata%, where your application stores user settings (e.g., "SIL\SayMore").
		/// A folder named "localizations" will be created there.</param>
		/// <param name="applicationIcon"> </param>
		/// <param name="emailForSubmissions">This will be used in UI that helps the translator
		/// know what to do with their work</param>
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
		[Obsolete("Use the overload without `TranslationMemory kind` parameter.")]
		public static ILocalizationManager Create(TranslationMemory kind, string desiredUiLangId,
			string appId, string appName, string appVersion, string directoryOfInstalledFiles,
			string relativeSettingPathForLocalizationFolder,
			Icon applicationIcon, string emailForSubmissions,
			IEnumerable<MethodInfo> additionalLocalizationMethods,
			params string[] namespaceBeginnings)
		{
			if (kind != TranslationMemory.XLiff)
			{
				throw new ArgumentException($@"Unknown translation memory kind {kind}. Only XLiff
				 is supported.",
					nameof(kind));
			}

			return Create(desiredUiLangId, appId, appName, appVersion, directoryOfInstalledFiles,
				relativeSettingPathForLocalizationFolder, applicationIcon, emailForSubmissions,
				namespaceBeginnings, additionalLocalizationMethods);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Creates a new instance of a localization manager for the specified application id.
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
		/// <param name="directoryOfInstalledFiles">The full folder path of the original l10n
		/// files installed with the application.</param>
		/// <param name="relativeSettingPathForLocalizationFolder">The path, relative to
		/// %localappdata%, where your application stores user settings (e.g., "SIL\SayMore").
		/// A folder named "localizations" will be created there.</param>
		/// <param name="applicationIcon"> </param>
		/// <param name="emailForSubmissions">This will be used in UI that helps the translator
		/// know what to do with their work</param>
		/// <param name="namespaceBeginnings">A list of namespace beginnings indicating
		/// what types to scan for localized string calls. For example, to only scan
		/// types found in Pa.exe and assuming all types in that assembly begin with
		/// 'Pa', then this value would only contain the string 'Pa'.</param>
		/// <param name="additionalLocalizationMethods">MethodInfo objects representing
		/// additional methods that should be regarded as calls to get localizations. If the method
		/// is named "Localize", the extractor will attempt to parse its signature as an extension
		/// method with the parameters (this string s, string separateId="", string comment="").
		/// Otherwise, it will be treated like a L10nSharp GetString method if its signature
		/// matches one of the following: (string stringId, string englishText),
		/// (string stringId, string englishText, string comment), or
		/// (string stringId, string englishText, string comment, string englishToolTipText,
		/// string englishShortcutKey, IComponent component).</param>
		/// ------------------------------------------------------------------------------------
		public static ILocalizationManager Create(string desiredUiLangId,
			string appId, string appName, string appVersion, string directoryOfInstalledFiles,
			string relativeSettingPathForLocalizationFolder,
			Icon applicationIcon, string emailForSubmissions,
			string[] namespaceBeginnings,
			IEnumerable<MethodInfo> additionalLocalizationMethods = null)
		{
			TranslationMemoryKind = TranslationMemory.XLiff;
			EmailForSubmissions = emailForSubmissions;
			return LocalizationManagerInternal<XLiffDocument>.CreateXliff(desiredUiLangId,
				appId, appName, appVersion, directoryOfInstalledFiles,
				relativeSettingPathForLocalizationFolder, applicationIcon,
				additionalLocalizationMethods,
				namespaceBeginnings);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Now that L10NSharp creates all writable l10n files under LocalApplicationData
		/// instead of the common/shared AppData folder, applications can use this method to
		/// purge old files (including old TMX files, if specified).</summary>
		/// <param name="appId">ID of the application used for creating the l10n files
		/// (typically the same ID passed as the 2nd parameter to
		/// LocalizationManagerInternal.Create, but without a file extension).
		/// </param>
		/// <param name="directoryOfWritableTranslationFiles">Folder from which to delete
		/// l10n files.</param>
		/// <param name="directoryOfInstalledTranslationFiles">Used to limit file deletion to only
		/// include copies of the installed l10n files (plus the generated default file). If
		/// this is <c>null</c>, then all l10n files for the given appID will be deleted from
		/// <paramref name="directoryOfWritableTranslationFiles"/></param>
		/// <param name="cleanUpTmx">Although TMX files are no longer supported, calling this
		/// method with this flag set will clean up TMX files (instead of XLIFF files)</param>
		/// ------------------------------------------------------------------------------------
		public static void DeleteOldTranslationFiles(string appId,
			string directoryOfWritableTranslationFiles, string directoryOfInstalledTranslationFiles,
			bool cleanUpTmx = false)
		{
			if (cleanUpTmx)
			{
				TMXLocalizationManager.DeleteOldTmxFiles(appId,
					directoryOfWritableTranslationFiles,
					directoryOfInstalledTranslationFiles);
			}
			else
			{
				XliffLocalizationManager.DeleteOldXliffFiles(appId,
					directoryOfWritableTranslationFiles,
					directoryOfInstalledTranslationFiles);
			}
		}

		/// ------------------------------------------------------------------------------------
		public static void SetUILanguage(string langId,
			bool reapplyLocalizationsToAllObjectsInAllManagers)
		{
			if (UILanguageId == langId || string.IsNullOrEmpty(langId))
				return;
			var ci = L10NCultureInfo.GetCultureInfo(langId);
			Thread.CurrentThread.CurrentUICulture = ci.RawCultureInfo ??
				CultureInfo.InvariantCulture;
			L10NCultureInfo.CurrentCulture = ci;
			s_uiLangId = langId;

			switch (TranslationMemoryKind)
			{
				case TranslationMemory.XLiff:
					LocalizationManagerInternal<XLiffDocument>.SetAvailableFallbackLanguageIds(GetAvailableLocalizedLanguages());
					break;
			}

			if (reapplyLocalizationsToAllObjectsInAllManagers)
				ReapplyLocalizationsToAllObjectsInAllManagers();
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
					if (Utils.IsMono)
					{
						// The current version of Mono does not define a CultureInfo for "zh", so
						// it tends to throw exceptions when we try to use just plain "zh".
						if (s_uiLangId == "zh-CN")
							return s_uiLangId;
					}
					// Otherwise, we want the culture.neutral version.
					int i = s_uiLangId.IndexOf('-');
					if (i >= 0)
						s_uiLangId = s_uiLangId.Substring(0, i);

					switch (TranslationMemoryKind)
					{
						case TranslationMemory.XLiff:
							LocalizationManagerInternal<XLiffDocument>.SetAvailableFallbackLanguageIds(GetAvailableLocalizedLanguages());
							break;
					}
				}

				return s_uiLangId;
			}
			internal set => s_uiLangId = value;
		}

		/// <summary>
		/// Get the language tags for all languages that have localized data that has
		/// been loaded.
		/// </summary>
		public static List<string> GetAvailableLocalizedLanguages()
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.GetAvailableLocalizedLanguages();
			}
		}

		/// <summary>
		/// Returns one L10NCultureInfo object for each distinct language found in the collection
		/// of all cultures on the computer. Some languages are represented by more than one
		/// culture, and in those cases just the first culture is returned. There are several
		/// reasons for multiple cultures per language, the predominant one being there is more
		/// than one writing system for the language. An example of this is Chinese which has a
		/// Traditional and a Simplified writing system. Other languages have a Latin and a
		/// Cyrillic writing system.
		///
		/// Due to changes made in how this procedure determines what languages to return, it is
		/// possible that there may be an existing localization tied to a culture that is no longer
		/// returned in the collection. Because of this, a check is done to make sure all cultures
		/// represented by existing localizations are included in the list that is returned. This
		/// will result in that language being in the list twice, each instance having a different
		/// DisplayName.
		/// </summary>
		/// <param name="returnOnlyLanguagesHavingLocalizations">
		/// If TRUE then only languages represented by existing localizations are returned. If
		/// FALSE then all languages found are returned.
		/// </param>
		/// <returns>IEnumerable of L10NCultureInfo declared as IEnumerable of CultureInfo</returns>
		public static IEnumerable<L10NCultureInfo> GetUILanguages(
			bool returnOnlyLanguagesHavingLocalizations)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.GetUILanguages(
						returnOnlyLanguagesHavingLocalizations);
			}
		}

		/// <summary>
		/// Return the number of strings that appear to have been translated and approved for the
		/// given language in all the loaded managers.
		/// </summary>
		public static int NumberApproved(string lang)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.NumberApproved(lang);
			}
		}

		/// <summary>
		/// Return the fraction of strings that appear to have been translated and approved for the
		/// given language in all the loaded managers.
		/// </summary>
		public static float FractionApproved(string lang)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.FractionApproved(lang);
			}

		}

		/// <summary>
		/// Return the number of strings that appear to have been translated for the given language
		/// in all the loaded managers.
		/// </summary>
		public static int NumberTranslated(string lang)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.NumberTranslated(lang);
			}
		}

		/// <summary>
		/// Return the fraction of strings that appear to have been translated for the given language
		/// in all the loaded managers.
		/// </summary>
		public static float FractionTranslated(string lang)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.FractionTranslated(lang);
			}
		}

		/// <summary>
		/// Return the number of strings that appear to be available for the given language in all
		/// the loaded managers.
		/// </summary>
		public static int StringCount(string lang)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.StringCount(lang);
			}
		}

		public static void ShowLocalizationDialogBox(IComponent component)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					LocalizationManagerInternal<XLiffDocument>.ShowLocalizationDialogBox(component);
					break;
			}
		}

		public static void ShowLocalizationDialogBox(string id)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					LocalizationManagerInternal<XLiffDocument>.ShowLocalizationDialogBox(id);
					break;
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets or sets the list of languages (by id) used to fallback to when looking for a
		/// string in the current UI language fails. The fallback order goes from the first
		/// item in this list to the last.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static IEnumerable<string> FallbackLanguageIds
		{
			get
			{
				switch (TranslationMemoryKind)
				{
					default:
					case TranslationMemory.XLiff:
						return LocalizationManagerInternal<XLiffDocument>.FallbackLanguageIds;
				}
			}
			set
			{
				switch (TranslationMemoryKind)
				{
					default:
					case TranslationMemory.XLiff:
						LocalizationManagerInternal<XLiffDocument>.FallbackLanguageIds = value;
						break;
				}
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the text for the specified component. The englishText is returned when the text
		/// for the specified object cannot be found for the current UI language.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetStringForObject(IComponent component, string englishText)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.GetStringForObject(component,
						englishText);
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a string for the specified string id. The englishText is returned when
		/// a string cannot be found for the specified id and the current UI language.
		/// Currently this and other overloads are intended to be thread-safe when
		/// the xliff backing store is used.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetString(string stringId, string englishText)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.GetString(stringId, englishText);
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a string for the specified string id. The englishText is returned when
		/// a string cannot be found for the specified id and the current UI language.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetString(string stringId, string englishText, string comment)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.GetString(stringId, englishText,
						comment);
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a string for the specified string id. The englishText is returned when
		/// a string cannot be found for the specified id and the current UI language.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetString(string stringId, string englishText, string comment,
			IComponent component)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.GetString(stringId, englishText,
						comment, component);
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a string for the specified string id. The englishText is returned when
		/// a string cannot be found for the specified id and the current UI language.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetString(string stringId, string englishText, string comment,
			string englishToolTipText, string englishShortcutKey, IComponent component)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.GetString(stringId, englishText,
						comment, englishToolTipText, englishShortcutKey, component);
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a string for the specified string id, in the specified language, or the
		/// englishText if that wasn't found. Prefers the englishText passed here to one that
		/// we might have got out of a l10n file, as is the non-obvious-but-ultimately-correct
		/// policy for this library.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetString(string stringId, string englishText, string comment,
			IEnumerable<string> preferredLanguageIds, out string languageIdUsed)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.GetString(stringId, englishText,
						comment, preferredLanguageIds, out languageIdUsed);
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
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.GetDynamicString(appId, id,
						englishText);
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
		public static string GetDynamicString(string appId, string id, string englishText,
			string comment)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.GetDynamicString(appId, id,
						englishText);
			}
		}

		/// <summary>
		/// This is useful in unit testing. If some unit tests create LMs and dispose them,
		/// but other unit tests assume default behavior when no LMs exist at all,
		/// the unit tests that dispose of LMs should also call this so the others don't
		/// throw ObjectDisposedExceptions.
		/// </summary>
		public static void ForgetDisposedManagers()
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					LocalizationManagerInternal<XLiffDocument>.ForgetDisposedManagers();
					break;
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a string for the specified application id and string id, in the requested
		/// language. When a string for the
		/// specified id cannot be found, then one is added  using the specified englishText is
		/// returned when a string cannot be found for the specified id and the current UI
		/// language. Use GetIsStringAvailableForLangId if you need to know if we have the
		/// value or not.
		/// Special case: unless englishText is null, that is what will be returned for
		/// langId = 'en', irrespective of what is in the l10n file/cache.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string GetDynamicStringOrEnglish(string appId, string id, string englishText,
			string comment, string langId)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.GetDynamicStringOrEnglish(appId, id,
						englishText, comment, langId);
			}
		}

		public static bool GetIsStringAvailableForLangId(string id, string langId)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.GetIsStringAvailableForLangId(id,
						langId);
			}
		}

		public static string StripOffLocalizationInfoFromText(string text)
		{
			if (text == null || !text.StartsWith(kL10NPrefix))
				return text;

			text = text.Substring(kL10NPrefix.Length);
			var i = text.IndexOf("!", StringComparison.Ordinal);
			return i < 0 ? text : text.Substring(i + 1);
		}

		internal static string GetTranslationFileNameForLanguage(string appId, string langId)
		{
			string fileExtension;
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					fileExtension = XliffLocalizationManager.FileExtension;
					break;
			}
			return GetTranslationFileNameForLanguage(appId, langId, fileExtension);
		}

		internal static string GetTranslationFileNameForLanguage(string appId, string langId, string fileExtension)
		{
			return UseLanguageCodeFolders
				? Path.Combine(langId, $"{appId}{fileExtension}")
				: $"{appId}.{langId}{fileExtension}";
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Reapplies the localizations to all objects in the localization manager's cache of
		/// localized objects.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static void ReapplyLocalizationsToAllObjectsInAllManagers()
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					LocalizationManagerInternal<XLiffDocument>.ReapplyLocalizationsToAllObjectsInAllManagers();
					break;
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Reapplies the localizations to all objects in the localization manager's cache of
		/// localized objects.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static void ReapplyLocalizationsToAllObjects(string localizationManagerId)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					LocalizationManagerInternal<XLiffDocument>.ReapplyLocalizationsToAllObjects(localizationManagerId);
					break;
			}
		}

		public static string GetLocalizedToolTipForControl(Control ctrl)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					return LocalizationManagerInternal<XLiffDocument>.GetLocalizedToolTipForControl(ctrl);
			}
		}

		/// <summary>
		/// Merge the existing English l10n file into newly collected data and write the
		/// result to the temp directory.
		/// </summary>
		public static void MergeExistingEnglishTranslationFileIntoNew(
			string installedStringFileFolder, string appId)
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					LocalizationManagerInternal<XLiffDocument>.MergeExistingEnglishTranslationFileIntoNew(
						installedStringFileFolder, appId);
					break;
			}
		}

		internal static void OnLaunchingLocalizationDialog(ILocalizationManager lm)
		{
			LaunchingLocalizationDialog?.Invoke(lm, new EventArgs());
		}

		internal static void OnClosingLocalizationDialog(ILocalizationManager lm)
		{
			ClosingLocalizationDialog?.Invoke(lm, new EventArgs());
		}

		/// ------------------------------------------------------------------------------------
		internal static Dictionary<string, ILocalizationManagerInternal> LoadedManagers
		{
			get
			{
				switch (TranslationMemoryKind)
				{
					default:
					case TranslationMemory.XLiff:
					{
						var loadedManagers = new Dictionary<string, ILocalizationManagerInternal>();
						foreach (var keyValuePair in LocalizationManagerInternal<XLiffDocument>.LoadedManagers)
						{
							loadedManagers.Add(keyValuePair.Key, keyValuePair.Value);
						}

						return loadedManagers;
					}
				}
			}
		}

		internal static void ClearLoadedManagers()
		{
			switch (TranslationMemoryKind)
			{
				default:
				case TranslationMemory.XLiff:
					LocalizationManagerInternal<XLiffDocument>.LoadedManagers.Clear();
					break;
			}
		}

		/// <summary>
		/// True (default) to throw if we try to get a string from a particular manager
		/// and it has been disposed. When false, we will instead just return the English string,
		/// or if none, the ID. This is useful in some apps (e.g., Bloom) which may
		/// accidentally request a localized string during shutdown after disposing of
		/// the localization managers.
		/// </summary>
		public static bool ThrowIfManagerDisposed = true;

		/// <summary>
		/// True (default) to throw if we try to get a localized string before creating any localization managers.
		/// This is to prevent an invalid state where language IDs get mapped incorrectly at the beginning and
		/// then never get updated which can cause us to fail to return properly localized strings when requested (see BL-13245).
		/// The fix is to ensure that a LocalizationManager is created before calling any localization methods.
		/// Or, to maintain prior behavior, set this to false.
		/// </summary>
		public static bool StrictInitializationMode = true;
	}
}
