using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Permissions;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml.Linq;
using L10NSharp.UI;

namespace L10NSharp.XLiffUtils
{
	/// ----------------------------------------------------------------------------------------
	internal class XLiffLocalizationManager : ILocalizationManagerInternal<XLiffDocument>
	{
		/// ------------------------------------------------------------------------------------
		public const string FileExtension = ".xlf";

		private static Icon _applicationIcon;
		private readonly string _installedXliffFileFolder;
		private readonly string _generatedDefaultXliffFileFolder;
		private readonly string _customXliffFileFolder;

		public Dictionary<IComponent, string> ComponentCache { get; }
		public Dictionary<Control, ToolTip> ToolTipCtrls { get; }
		public Dictionary<ILocalizableComponent, Dictionary<string, LocalizingInfo>> LocalizableComponents { get; }

		#region Static methods
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Now that L10NSharp creates all writable Xliff files under LocalApplicationData
		/// instead of the common/shared AppData folder, applications can use this method to
		/// purge old Xliff files.</summary>
		/// <param name="appId">ID of the application used for creating the Xliff files (typically
		/// the same ID passed as the 2nd parameter to LocalizationManagerInternal.Create).</param>
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

			var oldDefaultXliffFilePath = Path.Combine(directoryOfWritableXliffFiles,
				LocalizationManagerInternal<XLiffDocument>.GetTranslationFileNameForLanguage(appId,
				LocalizationManager.kDefaultLang));
			if (!File.Exists(oldDefaultXliffFilePath))
				return; // Cleanup was apparently done previously

			File.Delete(oldDefaultXliffFilePath);

			foreach (var oldXliffFile in Directory.GetFiles(directoryOfWritableXliffFiles,
				LocalizationManagerInternal<XLiffDocument>.GetTranslationFileNameForLanguage(appId, "*")))
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

		#endregion

		#region XLiffLocalizationManager construction/disposal
		/// ------------------------------------------------------------------------------------
		internal XLiffLocalizationManager(string appId, string appName, string appVersion,
			string directoryOfInstalledXliffFiles, string directoryForGeneratedDefaultXliffFile,
			string directoryOfUserModifiedXliffFiles,
			IEnumerable<MethodInfo> additionalLocalizationMethods,
			params string[] namespaceBeginnings)
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
			DefaultStringFilePath = GetPathForLanguage(LocalizationManager.kDefaultLang,
				false);

			NamespaceBeginnings = namespaceBeginnings;
			CollectUpNewStringsDiscoveredDynamically = true;

			CreateOrUpdateDefaultXliffFileIfNecessary(additionalLocalizationMethods, namespaceBeginnings);

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
			StringCache = new XLiffLocalizedStringCache(this);
			LocalizableComponents = new Dictionary<ILocalizableComponent,
				Dictionary<string, LocalizingInfo>>();
		}

		/// <summary>
		/// Minimal constructor for a new instance of the <see cref="XLiffLocalizationManager"/> class.
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
		internal XLiffLocalizationManager(string appId, string appName, string appVersion)
		{
			Id = appId;
			Name = appName;
			AppVersion = appVersion;
		}

		/// ------------------------------------------------------------------------------------
		private void CreateOrUpdateDefaultXliffFileIfNecessary(
			IEnumerable<MethodInfo> additionalLocalizationMethods,
			params string[] namespaceBeginnings)
		{
			// Make sure the folder exists.
			var dir = Path.GetDirectoryName(DefaultStringFilePath);
			if (dir != null && !Directory.Exists(dir))
				Directory.CreateDirectory(dir);

			var defaultStringFileInstalledPath = Path.Combine(_installedXliffFileFolder,
				GetXliffFileNameForLanguage(LocalizationManager.kDefaultLang));

			if (ScanningForCurrentStrings && DefaultStringFileExistsAndHasContents())
				File.Delete(DefaultStringFilePath);

			if (!ScanningForCurrentStrings && !DefaultStringFileExistsAndHasContents() && File.Exists(defaultStringFileInstalledPath))
				File.Copy(defaultStringFileInstalledPath, DefaultStringFilePath, true);

			if (DefaultStringFileExistsAndHasContents())
			{
				XAttribute verAttribute = null;
				try
				{
					var xmlDoc = XElement.Load(DefaultStringFilePath);
					var docNamespace = xmlDoc.GetDefaultNamespace();
					var file = xmlDoc.Element(docNamespace + "file");
					if (file != null)
					{
						verAttribute = file.Attribute("product-version");
					}
				}
				catch (System.Xml.XmlException)
				{
					// If the file has been corrupted somehow, delete it and carry on.
					// See https://silbloom.myjetbrains.com/youtrack/issue/BL-6146.
					File.Delete(DefaultStringFilePath);
					Console.WriteLine("WARNING - L10NSharp Update deleted corrupted {0}", DefaultStringFilePath);
				}
				if (verAttribute != null && new Version(verAttribute.Value) >= new Version(AppVersion ?? "0.0.1"))
					return;
			}

			// Before wasting a bunch of time, make sure we can open the file for writing.
			var fileStream = File.Open(DefaultStringFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
			fileStream.Close();

			var stringCache = new XLiffLocalizedStringCache(this, false);

			var extractedInfo = ExtractStringsWIthoutDialog(Name, additionalLocalizationMethods, namespaceBeginnings);
			if (extractedInfo != null)
			{
				if (extractedInfo.Any())
				{
					foreach (var locInfo in extractedInfo)
						stringCache.UpdateLocalizedInfo(locInfo);
				}
				else
				{
					stringCache.UpdateLocalizedInfo(new LocalizingInfo("_dummyEntryToGetValidFile")
					{
						LangId = "en",
						Text = "No strings were collected. This entry prevents an invalid, zero-length file. Delete this file to try regenerating it."
					}
					);
				}
			}
			stringCache.SaveIfDirty();
		}

		public static IEnumerable<LocalizingInfo> ExtractStringsWIthoutDialog(String name, IEnumerable<MethodInfo> additionalLocalizationMethods, String[] namespaceBeginnings)
		{
			try
			{
				Console.WriteLine("Starting to extract localization strings for {0}", name);
				var extractor = new L10NSharp.CodeReader.StringExtractor<XLiffDocument>();
				extractor.OutputErrorsToConsole = true;
				var result = extractor.DoExtractingWork(additionalLocalizationMethods, namespaceBeginnings, null);
				Console.WriteLine("Extracted {0} localization strings for {1}", result.Count(), name);
				return result;
			}
			catch (Exception e)
			{
				Console.WriteLine("ERROR: extracting localization strings for {0} caught an exception: {1}", name, e);
				return null;
			}
		}

		/// <summary> Sometimes, on Linux, there is an empty DefaultStringFile.  This causes problems. </summary>
		private bool DefaultStringFileExistsAndHasContents()
		{
			return File.Exists(DefaultStringFilePath) && !string.IsNullOrWhiteSpace(File.ReadAllText(DefaultStringFilePath));
		}

		/// <summary>Check if we are collecting strings to localize, not setting up to display localized strings.</summary>
		internal static bool ScanningForCurrentStrings => UILanguageId == "en" && LocalizationManager.IgnoreExistingEnglishTranslationFiles;

		/// ------------------------------------------------------------------------------------
		public void Dispose()
		{
			LocalizationManagerInternal<XLiffDocument>.RemoveManager(Id);
		}

		#endregion

		#region Methods for showing localization dialog box
		/// ------------------------------------------------------------------------------------
		public void ShowLocalizationDialogBox(bool runInReadonlyMode)
		{
			LocalizeItemDlg<XLiffDocument>.ShowDialog(this, "", runInReadonlyMode);
		}

		/// ------------------------------------------------------------------------------------
		public static void ShowLocalizationDialogBox(IComponent component)
		{
			TipDialog.Show("If you click on an item while you hold alt and shift keys down, this tool will open up with that item already selected.");
			LocalizeItemDlg<XLiffDocument>.ShowDialog(LocalizationManagerInternal<XLiffDocument>.GetLocalizationManagerForComponent(component),
				component, false);
		}

		/// ------------------------------------------------------------------------------------
		public static void ShowLocalizationDialogBox(string id)
		{
			TipDialog.Show("If you click on an item while you hold alt and shift keys down, this tool will open up with that item already selected.");
			LocalizeItemDlg<XLiffDocument>.ShowDialog(LocalizationManagerInternal<XLiffDocument>.GetLocalizationManagerForString(id), id, false);
		}

		#endregion

		#region Public Properties
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// This is what identifies a localization manager for a particular set of
		/// localized strings. This would likely be a DLL or EXE name like 'PA' or 'SayMore'.
		/// This will be the file name of the portion of the XLIFF file in which localized
		/// strings are stored. This would usually be the name of the assembly that owns a
		/// set of localized strings.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public string Id { get; }

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// This is the presentable name for the set of localized strings. For example, the
		/// Id might be 'PA' but the LocalizationSetName might be 'Phonology Assistant'.
		/// This should be a name presentable to the user.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public string Name { get; }

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// This is sent from the application that's creating the localization manager. It's
		/// written to the Xliff file and used to determine whether or not the application needs
		/// to be rescanned for localized strings.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public string AppVersion { get; }

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Full file name and path to the default string file (i.e. English strings).
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal string DefaultStringFilePath { get; }

		internal string DefaultInstalledStringFilePath =>
			Path.Combine(_installedXliffFileFolder,
				LocalizationManagerInternal<XLiffDocument>.GetTranslationFileNameForLanguage(Id,
					LocalizationManager.kDefaultLang));

		/// ------------------------------------------------------------------------------------
		public ILocalizedStringCache<XLiffDocument> StringCache { get; }

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
		/// if it exists, otherwise the installed language folder.
		/// Exception: never return the English Xliff, which is always handled separately and first.
		/// Doing this serves to insert any new dynamic strings into the cache, thus validating
		/// them as non-obsolete if we encounter them in other languages.
		/// Enhance JohnT: there ought to be some way NOT to load data for a language until we need it.
		/// This wastes time AND space.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public IEnumerable<string> FilenamesToAddToCache
		{
			get
			{
				HashSet<string> langIdsOfCustomizedLocales = new HashSet<string>();
				string langId;
				if (_customXliffFileFolder != null && Directory.Exists(_customXliffFileFolder))
				{
					if (LocalizationManager.UseLanguageCodeFolders)
					{
						foreach (var folder in Directory.GetDirectories(_customXliffFileFolder))
						{
							var xliffFile = Path.Combine(folder, Id + FileExtension);
							langId = GetLanguageTagFromFilePath(xliffFile);
							if (string.IsNullOrEmpty(langId) || langId == LocalizationManager.kDefaultLang)
								continue;

							langIdsOfCustomizedLocales.Add(langId);
							yield return xliffFile;
						}
					}
					else
					{
						foreach (var xliffFile in Directory.GetFiles(_customXliffFileFolder,
							$"{Id}.*{FileExtension}"))
						{
							langId = GetLangIdFromXliffFileName(xliffFile);
							if (langId == LocalizationManager.kDefaultLang)
								continue;

							langIdsOfCustomizedLocales.Add(langId);
							yield return xliffFile;
						}
					}
				}
				if (_installedXliffFileFolder != null)
				{
					if (LocalizationManager.UseLanguageCodeFolders)
					{
						foreach (var folder in Directory.GetDirectories(_installedXliffFileFolder))
						{
							var xliffFile = Path.Combine(folder, Id + FileExtension);
							langId = GetLanguageTagFromFilePath(xliffFile);
							if (string.IsNullOrEmpty(langId) || langId == LocalizationManager.kDefaultLang)
								continue;

							langIdsOfCustomizedLocales.Add(langId);
							yield return xliffFile;
						}
					}
					else
					{
						foreach (var xliffFile in Directory.GetFiles(_installedXliffFileFolder,
							$"{Id}.*{FileExtension}"))
						{
							langId = GetLangIdFromXliffFileName(xliffFile);
							if (langId != LocalizationManager.kDefaultLang &&    //Don't return the english Xliff here because we separately process it first.
								!langIdsOfCustomizedLocales.Contains(langId))
								yield return xliffFile;
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
		public void RegisterComponentForLocalizing(IComponent component, string id, string
		defaultText, string defaultTooltip, string defaultShortcutKeys, string comment)
		{
			RegisterComponentForLocalizing(new LocalizingInfo(component, id)
			{
				Text = defaultText,
				ToolTipText = defaultTooltip,
				ShortcutKeys = defaultShortcutKeys,
				Comment = comment
			}, null);
		}

		public void RegisterComponentForLocalizing(LocalizingInfo info,
			Action<ILocalizationManagerInternal, LocalizingInfo> successAction)
		{
			var component = info.Component;
			var id = info.Id;
			if (component == null || string.IsNullOrWhiteSpace(id))
				return;

			try
			{

				// This if/else used to be more concise but sometimes there were occasions
				// adding an item the first time using ComponentCache[component] = id would throw an
				// index outside the bounds of the array exception. I have no clue why nor
				// can I reliably reproduce the error nor do I know if this change will solve
				// the problem. Hopefully it will, but my guess is the same underlying code
				// will be called.
				if (ComponentCache.ContainsKey(component))
					ComponentCache[component] = id;  //somehow, we sometimes see "Msg: Index was outside the bounds of the array."
				else
				{
					var lm = LocalizationManagerInternal<XLiffDocument>.GetLocalizationManagerForString(id);
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

				successAction?.Invoke(this, info);
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
		public void SaveIfDirty(ICollection<string> langIdsToForceCreate)
		{
			try
			{
				((XLiffLocalizedStringCache)StringCache).SaveIfDirty(langIdsToForceCreate);
			}
			catch (IOException e)
			{
				CanCustomizeLocalizations = false;
				if (langIdsToForceCreate != null && langIdsToForceCreate.Any())
					MessageBox.Show(e.Message, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
		}

		/// ------------------------------------------------------------------------------------
		internal static string GetLangIdFromXliffFileName(string fileName)
		{
			if (LocalizationManager.UseLanguageCodeFolders)
			{
				return Path.GetFileName(Path.GetDirectoryName(fileName));
			}
			fileName = fileName.Substring(0, fileName.Length - FileExtension.Length);
			int i = fileName.LastIndexOf('.');
			return i < 0 ? null : fileName.Substring(i + 1);
		}

		/// ------------------------------------------------------------------------------------
		private string GetXliffFileNameForLanguage(string langId)
		{
			return LocalizationManagerInternal<XLiffDocument>.GetTranslationFileNameForLanguage(Id,
				langId);
		}

		/// ------------------------------------------------------------------------------------
		public string GetPathForLanguage(string langId, bool getCustomPathEvenIfNonexistent)
		{
			var filename = GetXliffFileNameForLanguage(langId);
			if (langId == LocalizationManager.kDefaultLang)
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
		public bool DoesCustomizedTranslationExistForLanguage(string langId)
		{
			return File.Exists(GetPathForLanguage(langId, true));
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
				LangId = LocalizationManager.kDefaultLang
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
			var text = (UILanguageId != LocalizationManager.kDefaultLang ? GetStringFromStringCache(UILanguageId, id) : null);

			return text ?? LocalizationManager.StripOffLocalizationInfoFromText(defaultText);
		}

		/// ------------------------------------------------------------------------------------
		public string GetStringFromStringCache(string uiLangId, string id)
		{
			var realLangId = LocalizationManagerInternal<XLiffDocument>.MapToExistingLanguageIfPossible(uiLangId);
			return StringCache.GetString(realLangId, id);
		}

		/// ------------------------------------------------------------------------------------
		private string GetTooltipFromStringCache(string uiLangId, string id)
		{
			var realLangId = LocalizationManagerInternal<XLiffDocument>.MapToExistingLanguageIfPossible(uiLangId);
			return StringCache.GetToolTipText(realLangId, id);
		}

		/// ------------------------------------------------------------------------------------
		private Keys GetShortCutKeyFromStringCache(string uiLangId, string id)
		{
			var realLangId = LocalizationManagerInternal<XLiffDocument>.MapToExistingLanguageIfPossible(uiLangId);
			return StringCache.GetShortcutKeys(realLangId, id);
		}

		#endregion

		#region GetString static methods

		/// <summary>
		/// Set this to false if you don't want users to pollute Xliff files they might send to you
		/// with strings that are unique to their documents. For example, Bloom looks for strings
		/// in html that might have been localized; but Bloom doesn't want to ship an ever-growing
		/// list of discovered strings for people to translate that aren't actually part of what you get
		/// with Bloom. So it sets this to False unless the app was compiled in DEBUG mode.
		/// Default is true.
		/// </summary>
		public bool CollectUpNewStringsDiscoveredDynamically { get; set; }

		#endregion

		#region Methods that apply localizations to an object.
		public void ApplyLocalizationsToILocalizableComponent(LocalizingInfo locInfo)
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
		/// Reapplies the localizations to all components in the localization manager's cache of
		/// localized components.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public void ReapplyLocalizationsToAllComponents()
		{
			foreach (var component in ComponentCache.Keys)
				ApplyLocalization(component);

			LocalizeItemDlg<XLiffDocument>.FireStringsLocalizedEvent(this);
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
		public void ApplyLocalization(IComponent component)
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
		internal void ApplyLocalizationsToLocalizableComponent(
			ILocalizableComponent locComponent, Dictionary<string, LocalizingInfo> idToLocInfo)
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
			var topctrl = LocalizationManagerInternal<XLiffDocument>.GetRealTopLevelControl(ctrl);
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
			var newtopctrl = LocalizationManagerInternal<XLiffDocument>.GetRealTopLevelControl(oldtopctrl);
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

		private static string UILanguageId => LocalizationManager.UILanguageId;

		/// ------------------------------------------------------------------------------------
		private bool ApplyLocalizationsToToolStripItem(ToolStripItem item, string id)
		{
			if (item == null)
				return false;

			var text = GetStringFromStringCache(UILanguageId, id);
			var toolTipText = GetTooltipFromStringCache(UILanguageId, id);
			item.Text = text ?? LocalizationManager.StripOffLocalizationInfoFromText(item.Text);
			item.ToolTipText = toolTipText ?? LocalizationManager.StripOffLocalizationInfoFromText(item.ToolTipText);

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
			hdr.Text = text ?? LocalizationManager.StripOffLocalizationInfoFromText(hdr.Text);
			return true;
		}

		/// ------------------------------------------------------------------------------------
		private bool ApplyLocalizationsToDataGridViewColumn(DataGridViewColumn col, string id)
		{
			if (col == null)
				return false;

			var text = GetStringFromStringCache(UILanguageId, id);
			col.HeaderText = text ?? LocalizationManager.StripOffLocalizationInfoFromText(col.HeaderText);
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

			LocalizeItemDlg<XLiffDocument>.ShowDialog(this, (IComponent)sender, false);
		}

		private static bool DoHandleMouseDown
		{
			get
			{
				return LocalizationManager.EnableClickingOnControlToBringUpLocalizationDialog &&
					Control.ModifierKeys == (Keys.Alt | Keys.Shift);
			}
		}

		public Icon ApplicationIcon
		{
			get => _applicationIcon;
			set => _applicationIcon = value;
		}

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

			var lm = LocalizationManagerInternal<XLiffDocument>.GetLocalizationManagerForComponent(ctrl);

			LocalizationManager.OnLaunchingLocalizationDialog(lm);
			LocalizeItemDlg<XLiffDocument>.ShowDialog(lm, ctrl, false);
			LocalizationManager.OnClosingLocalizationDialog(lm);
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
			if (!DoHandleMouseDown)
				return;

			var lv = sender as ListView;
			if (lv != null && ComponentCache.ContainsKey(lv.Columns[e.Column]))
				LocalizeItemDlg<XLiffDocument>.ShowDialog(this, lv.Columns[e.Column], false);
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
				LocalizeItemDlg<XLiffDocument>.ShowDialog(this, grid.Columns[e.ColumnIndex], false);
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

		public void MergeTranslationDocuments(string appId, XLiffDocument newDoc,
			string oldDocPath)
		{
			var oldDoc = XLiffDocument.Read(oldDocPath);
			var outputDoc = MergeXliffDocuments(newDoc, oldDoc, true);

			outputDoc.File.SourceLang = oldDoc.File.SourceLang;
			outputDoc.File.ProductVersion = oldDoc.File.ProductVersion;
			outputDoc.File.HardLineBreakReplacement = oldDoc.File.HardLineBreakReplacement;
			outputDoc.File.AmpersandReplacement = oldDoc.File.AmpersandReplacement;
			outputDoc.File.Original = oldDoc.File.Original;
			outputDoc.File.DataType = oldDoc.File.DataType;
			var outputPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(
				LocalizationManagerInternal<XLiffDocument>.GetTranslationFileNameForLanguage(appId, "en")));
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
			foreach (var tu in xliffNew.File.Body.TransUnitsUnordered)
			{
				xliffOutput.File.Body.AddTransUnit(tu);
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
							tu.AddNote("en", string.Format("OLD TEXT (before {0}): {1}", xliffNew.File.ProductVersion, tuOld.Source.Value));
						}
						if (tuOld.Dynamic && !tu.Dynamic)
						{
							++wrongDynamicFlagCount;
							wrongDynamicStringIds.Add(tu.Id);
							tu.AddNote("en", string.Format("Not dynamic: found in static scan of compiled code (version {0})", xliffNew.File.ProductVersion));
						}
					}
				}
			}

			// write out any units found in the old XLIFF file that were not found
			// in the new scan.
			if (xliffOld != null)
			{
				foreach (var tu in xliffOld.File.Body.TransUnitsUnordered)
				{
					var tuNew = xliffNew.File.Body.GetTransUnitForId(tu.Id);
					if (tuNew == null)
					{
						xliffOutput.File.Body.AddTransUnit(tu);
						if (tu.Dynamic)
						{
							++missingDynamicStringCount;
							missingDynamicStringIds.Add(tu.Id);
							if (newDynamicCount > 0)	// note only if attempt made to collect dynamic strings
								tu.AddNote("en", string.Format("Not found when running compiled program (version {0})", xliffNew.File.ProductVersion));
						}
						else
						{
							++missingStringCount;
							missingStringIds.Add(tu.Id);
							tu.AddNote("en", string.Format("Not found in static scan of compiled code (version {0})", xliffNew.File.ProductVersion));
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
			return xliffOutput;
		}

		/// <summary>
		/// Return the language tags for those languages that have been localized for the given program.
		/// </summary>
		public IEnumerable<string> GetAvailableUILanguageTags()
		{
			return StringCache.AvailableLangKeys.ToList();
		}

		public bool IsUILanguageAvailable(string langId) => StringCache.TryGetDocument(langId, out _);

		/// <summary>
		/// If the given file exists, return its parent folder name as a language tag if it
		/// appears to be valid (2 or 3 letters long or "zh-CN").  Otherwise return null.
		/// </summary>
		private static string GetLanguageTagFromFilePath(string xliffFile)
		{
			Debug.Assert(LocalizationManager.UseLanguageCodeFolders);
			if (!File.Exists(xliffFile))
				return null;

			var langId = Path.GetFileName(Path.GetDirectoryName(xliffFile));
			if (Regex.IsMatch(langId, "[a-z]{2,3}") || langId == "zh-CN")
				return langId;
			return null;
		}

	}
}
