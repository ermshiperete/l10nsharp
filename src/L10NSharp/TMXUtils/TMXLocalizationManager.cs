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

namespace L10NSharp.TMXUtils
{
	/// ----------------------------------------------------------------------------------------
	internal class TMXLocalizationManager : ILocalizationManagerInternal<TMXDocument>
	{
		internal const string FileExtension = ".tmx";

		public Icon ApplicationIcon { get; set; }
		private readonly string _installedTmxFileFolder;
		private readonly string _generatedDefaultTmxFileFolder;
		private readonly string _customTmxFileFolder;

		public Dictionary<IComponent, string> ComponentCache { get; }
		public Dictionary<Control, ToolTip> ToolTipCtrls { get; }
		public Dictionary<ILocalizableComponent, Dictionary<string, LocalizingInfo>> LocalizableComponents { get; }

		private static string UILanguageId => LocalizationManager.UILanguageId;

		#region Static methods
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Now that L10NSharp creates all writable TMX files under LocalApplicationData
		/// instead of the common/shared AppData folder, applications can use this method to
		/// purge old TMX files.</summary>
		/// <param name="appId">ID of the application used for creating the TMX files (typically
		/// the same ID passed as the 2nd parameter to LocalizationManagerInternal.Create).</param>
		/// <param name="directoryOfWritableTmxFiles">Folder from which to delete TMX files.
		/// </param>
		/// <param name="directoryOfInstalledTmxFiles">Used to limit file deletion to only
		/// include copies of the installed TMX files (plus the generated default file). If this
		/// is <c>null</c>, then all TMX files for the given appID will be deleted from
		/// <paramref name="directoryOfWritableTmxFiles"/></param>
		/// ------------------------------------------------------------------------------------
		public static void DeleteOldTmxFiles(string appId, string directoryOfWritableTmxFiles,
			string directoryOfInstalledTmxFiles)
		{
			//if (Assembly.GetEntryAssembly() == null)
			//    return; // Probably being called in a unit test.
			if (!Directory.Exists(directoryOfWritableTmxFiles))
				return; // Nothing to do.

			var oldDefaultTmxFilePath = Path.Combine(directoryOfWritableTmxFiles, GetTmxFileNameForLanguage(appId, LocalizationManager.kDefaultLang));
			if (!File.Exists(oldDefaultTmxFilePath))
				return; // Cleanup was apparently done previously

			File.Delete(oldDefaultTmxFilePath);

			foreach (var oldTmxFile in Directory.GetFiles(directoryOfWritableTmxFiles,
				GetTmxFileNameForLanguage(appId, "*")))
			{
				var filename = Path.GetFileName(oldTmxFile);
				if (string.IsNullOrEmpty(directoryOfInstalledTmxFiles) || File.Exists(Path.Combine(directoryOfInstalledTmxFiles, filename)))
				{
					try
					{
						File.Delete(oldTmxFile);
					}
					catch
					{
						// Oh, well, we tried.
					}
				}
			}
		}

		#endregion

		#region TMXLocalizationManager construction/disposal
		/// ------------------------------------------------------------------------------------
		internal TMXLocalizationManager(string appId, string appName, string appVersion,
			string directoryOfInstalledTmxFiles, string directoryForGeneratedDefaultTmxFile,
			string directoryOfUserModifiedTmxFiles,
			IEnumerable<MethodInfo> additionalLocalizationMethods,
			params string[] namespaceBeginnings)
		{
			// Test for a pathological case of bad install
			if (!Directory.Exists(directoryOfInstalledTmxFiles))
				throw new DirectoryNotFoundException(string.Format(
					"The default localizations folder {0} does not exist. This indicates a failed install for {1}. Please uninstall and reinstall {1}.",
					directoryOfInstalledTmxFiles, appName));
			Id = appId;
			Name = appName;
			AppVersion = appVersion;
			_installedTmxFileFolder = directoryOfInstalledTmxFiles;
			_generatedDefaultTmxFileFolder = directoryForGeneratedDefaultTmxFile;
			DefaultStringFilePath = GetTmxPathForLanguage(LocalizationManager.kDefaultLang, false);

			NamespaceBeginnings = namespaceBeginnings;
			CollectUpNewStringsDiscoveredDynamically = true;

			CreateOrUpdateDefaultTmxFileIfNecessary(additionalLocalizationMethods, namespaceBeginnings);

			_customTmxFileFolder = directoryOfUserModifiedTmxFiles;
			if (string.IsNullOrEmpty(_customTmxFileFolder))
			{
				_customTmxFileFolder = null;
				CanCustomizeLocalizations = false;
			}
			else
			{
				try
				{
					new FileIOPermission(FileIOPermissionAccess.Write, _customTmxFileFolder).Demand();
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
			StringCache = new TMXLocalizedStringCache(this);
			LocalizableComponents = new Dictionary<ILocalizableComponent, Dictionary<string, LocalizingInfo>>();
		}

		internal TMXLocalizationManager(string appId, string appName, string appVersion)
		{
			Id = appId;
			Name = appName;
			AppVersion = appVersion;
		}

		/// ------------------------------------------------------------------------------------
		private void CreateOrUpdateDefaultTmxFileIfNecessary(IEnumerable<MethodInfo> additionalLocalizationMethods, params string[] namespaceBeginnings)
		{
			// Make sure the folder exists.
			var dir = Path.GetDirectoryName(DefaultStringFilePath);
			if (dir != null && !Directory.Exists(dir))
				Directory.CreateDirectory(dir);

			var defaultStringFileInstalledPath = Path.Combine(_installedTmxFileFolder, GetTmxFileNameForLanguage(LocalizationManager.kDefaultLang));
			if (!DefaultStringFileExistsAndHasContents() && File.Exists(defaultStringFileInstalledPath))
			{
				File.Copy(defaultStringFileInstalledPath, DefaultStringFilePath, true);
			}

			if (DefaultStringFileExistsAndHasContents())
			{
				var xmlDoc = XElement.Load(DefaultStringFilePath);
				var header = xmlDoc.Element("header");
				XElement verElement = null;
				if (header != null)
				{
					verElement = header.Elements("prop")
						.FirstOrDefault(e => (string)e.Attribute("type") == LocalizationManager.kAppVersionPropTag);
				}

				if (verElement != null && new Version(verElement.Value) >= new Version(AppVersion ?? "0.0.1"))
					return;
			}

			// Before wasting a bunch of time, make sure we can open the file for writing.
			var fileStream = File.Open(DefaultStringFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
			fileStream.Close();

			var tmxDoc = TMXLocalizedStringCache.CreateEmptyStringFile();
			tmxDoc.Header.SetPropValue(LocalizationManager.kAppVersionPropTag, AppVersion);
			var tuUpdater = new TMXTransUnitUpdater(tmxDoc);

			using (var dlg = new InitializationProgressDlg<TMXDocument>(Name, ApplicationIcon, additionalLocalizationMethods, namespaceBeginnings))
			{
				dlg.ShowDialog();
				if (dlg.ExtractedInfo != null)
				{
					foreach (var locInfo in dlg.ExtractedInfo)
						tuUpdater.Update(locInfo);
				}
			}
			tmxDoc.Save(DefaultStringFilePath);
		}

		/// <summary> Sometimes, on Linux, there is an empty DefaultStringFile.  This causes problems. </summary>
		private bool DefaultStringFileExistsAndHasContents()
		{
			return File.Exists(DefaultStringFilePath) && !string.IsNullOrWhiteSpace(File.ReadAllText(DefaultStringFilePath));
		}

		/// ------------------------------------------------------------------------------------
		public void Dispose()
		{
			LocalizationManagerInternal<TMXDocument>.RemoveManager(Id);
		}

		#endregion

		#region Methods for showing localization dialog box
		/// ------------------------------------------------------------------------------------
		public void ShowLocalizationDialogBox(bool runInReadonlyMode, IWin32Window owner = null)
		{
			LocalizeItemDlg<TMXDocument>.ShowDialog(this, "", runInReadonlyMode, owner);
		}

		#endregion

		#region Public Properties
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// This is what identifies a localization manager for a particular set of
		/// localized strings. This would likely be a DLL or EXE name like 'PA' or 'SayMore'.
		/// This will be the file name of the portion of the TMX file in which localized
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
		/// written to the TMX file and used to determine whether or not the application needs
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

		internal string DefaultInstalledStringFilePath => Path.Combine(_installedTmxFileFolder,
			LocalizationManagerInternal<TMXDocument>.GetTranslationFileNameForLanguage(Id,
				LocalizationManager.kDefaultLang));

		/// ------------------------------------------------------------------------------------
		public ILocalizedStringCache<TMXDocument> StringCache { get; }

		public void MergeTranslationDocuments(string appId, TMXDocument newDoc, string oldDocPath)
		{
			// do nothing
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a value indicating whether or not user has authority to change localized strings.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public bool CanCustomizeLocalizations { get; private set; }

		public string[] NamespaceBeginnings { get; set; }

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Enumerates a TMX file for each language. Prefer the custom localizations folder version
		/// if it exists, otherwise the installed language folder.
		/// Exception: never return the English tmx, which is always handled separately and first.
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
				if (_customTmxFileFolder != null && Directory.Exists(_customTmxFileFolder))
				{
					if (LocalizationManager.UseLanguageCodeFolders)
					{
						foreach (var folder in Directory.GetDirectories(_customTmxFileFolder))
						{
							var tmxFile = Path.Combine(folder, $"{Id}{FileExtension}");
							langId = GetLanguageTagFromFilePath(tmxFile);
							if (string.IsNullOrEmpty(langId) || langId == LocalizationManager.kDefaultLang)
								continue;

							langIdsOfCustomizedLocales.Add(langId);
							yield return tmxFile;
						}
					}
					else
					{
						foreach (var tmxFile in Directory.GetFiles(_customTmxFileFolder,
							$"{Id}.*{FileExtension}"))
						{
							langId = GetLangIdFromTmxFileName(tmxFile);
							if (langId == LocalizationManager.kDefaultLang)
								continue;

							langIdsOfCustomizedLocales.Add(langId);
							yield return tmxFile;
						}
					}
				}

				if (_installedTmxFileFolder != null)
				{
					if (LocalizationManager.UseLanguageCodeFolders)
					{
						foreach (var folder in Directory.GetDirectories(_installedTmxFileFolder))
						{
							var tmxFile = Path.Combine(folder, $"{Id}{FileExtension}");
							langId = GetLanguageTagFromFilePath(tmxFile);
							if (string.IsNullOrEmpty(langId) || langId == LocalizationManager.kDefaultLang)
								continue;

							langIdsOfCustomizedLocales.Add(langId);
							yield return tmxFile;
						}
					}
					else
					{
						foreach (var tmxFile in Directory.GetFiles(_installedTmxFileFolder,
							$"{Id}.*{FileExtension}"))
						{
							langId = GetLangIdFromTmxFileName(tmxFile);
							if (langId != LocalizationManager
									.kDefaultLang && //Don't return the english TMX here because we separately process it first.
								!langIdsOfCustomizedLocales.Contains(langId))
								yield return tmxFile;
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
		public void RegisterComponentForLocalizing(IComponent component, string id,
			string defaultText, string defaultTooltip, string defaultShortcutKeys, string comment)
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
					var lm = LocalizationManagerInternal<TMXDocument>.GetLocalizationManagerForString(id);
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
			if (component is ToolStripItem toolStripItem)
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
				((TMXLocalizedStringCache)StringCache).SaveIfDirty(langIdsToForceCreate);
			}
			catch (IOException e)
			{
				CanCustomizeLocalizations = false;
				if (langIdsToForceCreate != null && langIdsToForceCreate.Any())
					MessageBox.Show(e.Message, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
		}

		/// ------------------------------------------------------------------------------------
		private string GetLangIdFromTmxFileName(string fileName)
		{
			fileName = fileName.Substring(0, fileName.Length - 4);
			int i = fileName.LastIndexOf('.');
			return (i < 0 ? null : fileName.Substring(i + 1));
		}

		/// ------------------------------------------------------------------------------------
		private string GetTmxFileNameForLanguage(string langId)
		{
			return GetTmxFileNameForLanguage(Id, langId);
		}

		/// ------------------------------------------------------------------------------------
		private static string GetTmxFileNameForLanguage(string appId, string langId)
		{
			return LocalizationManagerInternal<TMXDocument>.GetTranslationFileNameForLanguage(appId, langId);
		}

		/// ------------------------------------------------------------------------------------
		internal string GetTmxPathForLanguage(string langId, bool getCustomPathEvenIfNonexistent)
		{
			var filename = GetTmxFileNameForLanguage(langId);
			if (langId == LocalizationManager.kDefaultLang)
				return Path.Combine(_generatedDefaultTmxFileFolder, filename);
			if (_customTmxFileFolder != null)
			{
				var customTmxFile = Path.Combine(_customTmxFileFolder, filename);
				if (getCustomPathEvenIfNonexistent || File.Exists(customTmxFile))
					return customTmxFile;
			}
			return _installedTmxFileFolder != null ? Path.Combine(_installedTmxFileFolder, filename) : null /* Pretty sure this won't end well*/;
		}

		/// ------------------------------------------------------------------------------------
		public bool DoesCustomizedTranslationExistForLanguage(string langId)
		{
			return File.Exists(GetTmxPathForLanguage(langId, true));
		}

		/// ------------------------------------------------------------------------------------
		public void PrepareToCustomizeLocalizations()
		{
			if (_customTmxFileFolder == null)
				throw new InvalidOperationException("Localization manager for " + Id + "has no folder specified for customizing localizations");
			if (!CanCustomizeLocalizations)
				throw new InvalidOperationException("User does not have sufficient privilege to customize localizations for " + Id);
			try
			{
				// Make sure the folder exists.
				if (!Directory.Exists(_customTmxFileFolder))
					Directory.CreateDirectory(_customTmxFileFolder);
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

			return (text ?? LocalizationManager.StripOffLocalizationInfoFromText(defaultText));
		}

		/// ------------------------------------------------------------------------------------
		public string GetStringFromStringCache(string uiLangId, string id)
		{
			return StringCache.GetString(uiLangId, id);
		}

		/// ------------------------------------------------------------------------------------
		private string GetTooltipFromStringCache(string uiLangId, string id)
		{
			return StringCache.GetToolTipText(uiLangId, id);
		}

		/// ------------------------------------------------------------------------------------
		private Keys GetShortCutKeyFromStringCache(string uiLangId, string id)
		{
			return StringCache.GetShortcutKeys(uiLangId, id);
		}

		#endregion

		/// <summary>
		/// Set this to false if you don't want users to pollute tmx files they might send to you
		/// with strings that are unique to their documents. For example, Bloom looks for strings
		/// in html that might have been localized; but Bloom doesn't want to ship an ever-growing
		/// list of discovered strings for people to translate that aren't actually part of what you get
		/// with Bloom. So it sets this to False unless the app was compiled in DEBUG mode.
		/// Default is true.
		/// </summary>
		public bool CollectUpNewStringsDiscoveredDynamically { get; set; }

		/// ------------------------------------------------------------------------------------
		public string GetPathForLanguage(string langId, bool getCustomPathEvenIfNonexistent)
		{
			return GetTmxPathForLanguage(langId, getCustomPathEvenIfNonexistent);
		}

		#region Methods that apply localizations to an object.
		public void ApplyLocalizationsToILocalizableComponent(LocalizingInfo locInfo)
		{
			if (locInfo.Component is ILocalizableComponent locComponent &&
				LocalizableComponents.TryGetValue(locComponent, out var idToLocInfo))
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

			LocalizeItemDlg<TMXDocument>.FireStringsLocalizedEvent(this);
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

			// This used to be a for-each, but on rare occasions, a "Collection was
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
		public void ApplyLocalizationsToLocalizableComponent(ILocalizableComponent locComponent,
			Dictionary<string, LocalizingInfo> idToLocInfo)
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
			item.Text = (text ?? LocalizationManager.StripOffLocalizationInfoFromText(item.Text));
			item.ToolTipText = (toolTipText ?? LocalizationManager.StripOffLocalizationInfoFromText(item.ToolTipText));

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
			hdr.Text = (text ?? LocalizationManager.StripOffLocalizationInfoFromText(hdr.Text));
			return true;
		}

		/// ------------------------------------------------------------------------------------
		private bool ApplyLocalizationsToDataGridViewColumn(DataGridViewColumn col, string id)
		{
			if (col == null)
				return false;

			var text = GetStringFromStringCache(UILanguageId, id);
			col.HeaderText = (text ?? LocalizationManager.StripOffLocalizationInfoFromText(col.HeaderText));
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
			var owningForm = tsddi?.Owner?.FindForm();

			while (tsddi != null)
			{
				tsddi.DropDown.Close();

				if (tsddi.Owner is ContextMenuStrip)
					((ContextMenuStrip)tsddi.Owner).Close();

				tsddi = tsddi.OwnerItem as ToolStripDropDownItem;
			}

			LocalizeItemDlg<TMXDocument>.ShowDialog(this, (IComponent)sender, false,
				owningForm);
		}

		private static bool DoHandleMouseDown =>
			LocalizationManager.EnableClickingOnControlToBringUpLocalizationDialog &&
			Control.ModifierKeys == (Keys.Alt | Keys.Shift);

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

			var lm = LocalizationManagerInternal<TMXDocument>.GetLocalizationManagerForComponent(ctrl);

			LocalizationManager.OnLaunchingLocalizationDialog(lm);
			LocalizeItemDlg<TMXDocument>.ShowDialog(lm, ctrl, false, ctrl?.FindForm());
			LocalizationManager.OnClosingLocalizationDialog(lm);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// When controls get destroyed, do a little clean up.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void HandleControlDisposed(object sender, EventArgs e)
		{
			if (!(sender is Control ctrl))
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

			if (sender is ListView lv && ComponentCache.ContainsKey(lv.Columns[e.Column]))
				LocalizeItemDlg<TMXDocument>.ShowDialog(this, lv.Columns[e.Column], false,
					lv.FindForm());
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

			if (sender is DataGridView grid && e.RowIndex < 0 && ComponentCache.ContainsKey(grid.Columns[e.ColumnIndex]))
				LocalizeItemDlg<TMXDocument>.ShowDialog(this, grid.Columns[e.ColumnIndex], false,
					grid.FindForm());
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// When DataGridViewColumn controls get disposed, remove the reference to it from the
		/// object cache.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void HandleColumnDisposed(object sender, EventArgs e)
		{
			if (!(sender is DataGridViewColumn column))
				return;

			column.Disposed -= HandleColumnDisposed;
			ComponentCache.Remove(column);
		}
		#endregion

		/// ------------------------------------------------------------------------------------
		public override string ToString()
		{
			return $"{Id}, {Name}";
		}

		/// <summary>
		/// Return the language tags for those languages that have been localized for the given program.
		/// </summary>
		public IEnumerable<string> GetAvailableUILanguageTags()
		{
			var tags = new List<string>();

			if (LocalizationManager.UseLanguageCodeFolders)
			{
				if (Directory.Exists(_installedTmxFileFolder))
				{
					foreach (var folder in Directory.GetDirectories(_installedTmxFileFolder))
						tags.AddIfUniqueAndNotNull(GetLanguageTagFromFolderName(folder));
				}

				if (Directory.Exists(_generatedDefaultTmxFileFolder))
				{
					foreach (var folder in Directory.GetDirectories(_generatedDefaultTmxFileFolder))
						tags.AddIfUniqueAndNotNull(GetLanguageTagFromFolderName(folder));
				}

				if (Directory.Exists(_customTmxFileFolder))
				{
					foreach (var folder in Directory.GetDirectories(_customTmxFileFolder))
						tags.AddIfUniqueAndNotNull(GetLanguageTagFromFolderName(folder));
				}
			}
			else
			{
				if (Directory.Exists(_installedTmxFileFolder))
				{
					foreach (var filepath in Directory.GetFiles(_installedTmxFileFolder,
						$"{Id}.*{FileExtension}"))
						tags.AddIfUniqueAndNotNull(GetLanguageTagFromFileName(filepath));
				}

				if (Directory.Exists(_generatedDefaultTmxFileFolder))
				{
					foreach (var filepath in Directory.GetFiles(_generatedDefaultTmxFileFolder,
						$"{Id}.*{FileExtension}"))
						tags.AddIfUniqueAndNotNull(GetLanguageTagFromFileName(filepath));
				}

				if (Directory.Exists(_customTmxFileFolder))
				{
					foreach (var filepath in Directory.GetFiles(_customTmxFileFolder,
						$"{Id}.*{FileExtension}"))
						tags.AddIfUniqueAndNotNull(GetLanguageTagFromFileName(filepath));
				}
			}

			return tags;
		}

		public bool IsUILanguageAvailable(string langId) => GetAvailableUILanguageTags().Contains(langId);

		/// <summary>
		/// Gets the language code from the folder name, if using language code folders
		/// </summary>
		/// <param name="folder"></param>
		/// <returns></returns>
		private string GetLanguageTagFromFolderName(string folder)
		{
			var tmxFile = Path.Combine(folder, $"{Id}{FileExtension}");
			return GetLanguageTagFromFilePath(tmxFile);
		}

		/// <summary>
		/// Gets the language code from the file name, if not using language code folders
		/// </summary>
		/// <param name="filePath"></param>
		/// <returns></returns>
		private string GetLanguageTagFromFileName(string filePath)
		{
			var fileName = Path.GetFileNameWithoutExtension(filePath);
			return fileName?.Substring(Id.Length + 1);
		}

		/// <summary>
		/// If the given file exists, return its parent folder name as a language tag if it
		/// appears to be valid (2 or 3 letters long or "zh-CN").  Otherwise return null.
		/// </summary>
		private static string GetLanguageTagFromFilePath(string tmxFile)
		{
			Debug.Assert(LocalizationManager.UseLanguageCodeFolders);
			if (!File.Exists(tmxFile))
				return null;

			var langId = Path.GetFileName(Path.GetDirectoryName(tmxFile));
			if (Regex.IsMatch(langId, "[a-z]{2,3}") || langId == "zh-CN")
				return langId;
			return null;
		}
	}
}
