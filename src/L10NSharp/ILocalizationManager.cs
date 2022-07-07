// Copyright (c) 2019 SIL International
// This software is licensed under the MIT License (http://opensource.org/licenses/MIT)

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Forms;

namespace L10NSharp
{
	public interface ILocalizationManager: IDisposable
	{
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// This is what identifies a localization manager for a particular set of
		/// localized strings. This would likely be a DLL or EXE name like 'PA' or 'SayMore'.
		/// This will be the file name of the portion of the XLIFF file in which localized
		/// strings are stored. This would usually be the name of the assembly that owns a
		/// set of localized strings.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		string Id { get; }

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// This is the presentable name for the set of localized strings. For example, the
		/// Id might be 'PA' but the LocalizationSetName might be 'Phonology Assistant'.
		/// This should be a name presentable to the user.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		string Name { get; }

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// This is sent from the application that's creating the localization manager. It's
		/// written to the Xliff/TMX file and used to determine whether or not the application
		/// needs to be rescanned for localized strings.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		string AppVersion { get; }

		/// <summary>
		/// Set this to false if you don't want users to pollute Xliff/TMX files they might send
		/// to you with strings that are unique to their documents. For example, Bloom looks for
		/// strings in html that might have been localized; but Bloom doesn't want to ship an
		/// ever-growing list of discovered strings for people to translate that aren't actually
		/// part of what you get with Bloom. So it sets this to <c>false</c> unless the app was
		/// compiled in DEBUG mode. Default is <c>true</c>.
		/// </summary>
		bool CollectUpNewStringsDiscoveredDynamically { get; set; }

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a value indicating whether or not user has authority to change localized strings.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		bool CanCustomizeLocalizations { get; }

		string[] NamespaceBeginnings { get; set; }

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Enumerates a Xliff/TMX file for each language. Prefer the custom localizations folder
		/// version if it exists, otherwise the installed language folder.
		/// Exception: never return the English Xliff/TMX, which is always handled separately and
		/// first. Doing this serves to insert any new dynamic strings into the cache, thus
		/// validating them as non-obsolete if we encounter them in other languages.
		/// Enhance JohnT: there ought to be some way NOT to load data for a language until we
		/// need it. This wastes time AND space.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		IEnumerable<string> FilenamesToAddToCache { get; }


		/// <summary>
		/// Return the language tags for those languages that have been localized for the given
		/// program.
		/// </summary>
		IEnumerable<string> GetAvailableUILanguageTags();

		bool IsUILanguageAvailable(string langId);

		bool DoesCustomizedTranslationExistForLanguage(string langId);

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Recreates the tooltip control and updates the tooltip text for each object having
		/// a tooltip. This is necessary sometimes when controls get moved from form to form
		/// during runtime.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		void RefreshToolTips();

		void PrepareToCustomizeLocalizations();
		void ShowLocalizationDialogBox(bool runInReadonlyMode, IWin32Window owner = null);

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Add a localized string to the string cache.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		void AddString(string id, string defaultText, string defaultTooltip,
			string defaultShortcutKeys, string comment);

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the localized text for the specified component.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		string GetLocalizedString(IComponent component, string id, string defaultText,
			string defaultTooltip, string defaultShortcutKeys, string comment);

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the localized text for the specified id.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		string GetLocalizedString(string id, string defaultText);
	}
}
