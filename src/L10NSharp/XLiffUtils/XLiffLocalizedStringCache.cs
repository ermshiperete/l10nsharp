using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using L10NSharp.UI;

namespace L10NSharp.XLiffUtils
{
	/// ----------------------------------------------------------------------------------------
	internal class XLiffLocalizedStringCache : LocalizedStringCache, ILocalizedStringCache<XLiffDocument>
	{
		private readonly XLiffTransUnitUpdater _tuUpdater;

		public List<LocTreeNode<XLiffDocument>> LeafNodeList { get; private set; }
		internal XLiffLocalizationManager OwningManager { get; private set; }
		private XLiffDocument DefaultXliffDocument { get; set; } // matches LanguageManager.kDefaultLanguage

		/// <summary>
		/// Record the xliff document loaded for each language. Use this with care...XLiff documents are only
		/// loaded as needed, so unless _unloadedXliffDocuments is empty, XliffDocuments won't necessarily
		/// contain the one you want or have a complete list of keys. This class has its own GetDocument,
		/// TryGetDocument, and AvailableLangKeys which should usually be used instead. To help enforce this,
		/// XliffDocuments should be kept private, and any access to it should go through methods that
		/// take lazy loading of Xliff documents into account.
		/// </summary>
		private readonly ConcurrentDictionary<string, XLiffDocument> XliffDocuments = new ConcurrentDictionary<string, XLiffDocument>();

		private readonly ConcurrentDictionary<string, string> _unloadedXliffDocuments = new ConcurrentDictionary<string, string>();

		#region Loading methods

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Loads the string cache from all the specified Xliff files
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal XLiffLocalizedStringCache(ILocalizationManager owningManager, bool loadAvailableXliffFiles = true)
		{
			OwningManager = owningManager as XLiffLocalizationManager;
			if (loadAvailableXliffFiles)
			{
				try
				{
					MergeXliffFilesIntoCache(OwningManager.FilenamesToAddToCache);
				}
				catch (Exception e)
				{
					MessageBox.Show("Error occurred reading localization file:" + Environment.NewLine + e.Message,
						Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
					LocalizationManager.SetUILanguage(LocalizationManager.kDefaultLang, false);
				}
			}
			else
			{
				DefaultXliffDocument = CreateEmptyStringFile();
				DefaultXliffDocument.File.Original = OwningManager.Name + ".dll";
				XliffDocuments.TryAdd(LocalizationManager.kDefaultLang, DefaultXliffDocument);
			}
			_tuUpdater = new XLiffTransUnitUpdater(this);

			var replacement = DefaultXliffDocument.File.AmpersandReplacement;
			if (replacement != null)
				_ampersandReplacement = replacement;

			replacement = DefaultXliffDocument.File.HardLineBreakReplacement;
			if (replacement != null)
				s_literalNewline = replacement;

			LeafNodeList = new List<LocTreeNode<XLiffDocument>>();
			IsDirty = false;
		}

		internal XLiffDocument GetDocument(string langId)
		{
			TryGetDocument(langId, out XLiffDocument doc);
			return doc;
		}

		public bool TryGetDocument(string langId, out XLiffDocument doc)
		{
			// It's tempting to try to do this with the ConcurrentDictionary method GetOrAdd.
			// But it's not guaranteed that the doc which LoadXLiff loads will be put in
			// the dictionary with exactly the key langId. And it would not help much...the action
			// to create the new value if not found still runs unlocked, so several threads could be
			// doing it at once in either case.
			if (XliffDocuments.TryGetValue(langId, out doc))
				return true;
			lock (LocalizationManagerInternal<XLiffDocument>.LazyLoadLock)
			{
				LoadXliffAndUpdateExistingLanguageMap(langId);
			}

			return XliffDocuments.TryGetValue(langId, out doc);
		}

		public IEnumerable<string> AvailableLangKeys
		{
			get
			{
				// We need the real target-language values out of the files, so we may
				// as well go ahead and load them properly.
				foreach (var langId in _unloadedXliffDocuments.Keys.ToList())
				{
					// If by any chance multiple threads are doing this, we'll
					// improve performance if some of the attempted adds work out to
					// simple retrievals.
					TryGetDocument(langId, out _);
				}

				return XliffDocuments.Keys;
			}
		}

		internal void AddDocument(string langId, XLiffDocument doc)
		{
			XliffDocuments.TryAdd(langId, doc);
		}

		/// <summary>
		/// May only be called from constructor. Not thread-safe.
		/// </summary>
		private void MergeXliffFilesIntoCache(IEnumerable<string> xliffFiles)
		{
			DefaultXliffDocument = XLiffDocument.Read(OwningManager.DefaultStringFilePath); // read the generated file
			// It's possible (I think when there is no customizable Xliff, as on first install, but the version in the installed Xliff
			// is out of date with the app) that we don't have all the info from the installed Xliff in the customizable one.
			// We want to make sure that (a) any new dynamic strings in the installed one are considered valid by default
			// (b) any newly obsolete IDs are noted.
			if (File.Exists(OwningManager.DefaultInstalledStringFilePath))
			{
				if (!XLiffLocalizationManager.ScanningForCurrentStrings)
				{
					var defaultInstalledXliffDoc = XLiffDocument.Read(OwningManager.DefaultInstalledStringFilePath);
					foreach (var tu in defaultInstalledXliffDoc.File.Body.TransUnitsUnordered)
						DefaultXliffDocument.File.Body.AddTransUnitOrVariantFromExisting(tu,
							LocalizationManager.kDefaultLang);
				}
			}

			XliffDocuments.TryAdd(LocalizationManager.kDefaultLang, DefaultXliffDocument);
			// Map the default language onto itself.
			LocalizationManagerInternal<XLiffDocument>.MapToExistingLanguage[LocalizationManager.kDefaultLang] =
				LocalizationManager.kDefaultLang;

			foreach (var file in xliffFiles)
			{
				var langId = XLiffLocalizationManager.GetLangIdFromXliffFileName(file);
				Debug.Assert(!string.IsNullOrEmpty(langId));
				Debug.Assert(langId != LocalizationManager.kDefaultLang);
				_unloadedXliffDocuments[langId] = file;
			}
		}

		/// <summary>
		/// Load the language data, if any, from the unloaded xliff file associated  with langId
		/// (or its primary language, if different) and update MapToExistingLanguage according
		/// to what we find. 
		/// Use only from TryGetDocument. Should hold LazyLoadLock.
		/// </summary>
		private void LoadXliffAndUpdateExistingLanguageMap(string langId)
		{
			if (!_unloadedXliffDocuments.TryRemove(langId, out string file))
			{
				// Often an xliff in a plain lang folder (like "es") contains
				// a target-language that is more specific (like "es-ES").
				// If we're asked to try to load the xliff for es-ES and don't find one,
				// Try loading the one for es.
				var pieces = langId.Split('-');
				if (pieces.Length <= 1)
					return;
				if (!_unloadedXliffDocuments.TryRemove(pieces[0], out file))
					return;
			}

			var xliffDoc = XLiffDocument.Read(file);

			// This might be different, typically more specific, than the name we deduced from the file path.
			var targetLang = xliffDoc.File.TargetLang;

			// Now we have some maintenance to do on MapToExistingLanguage, which does not yet contain data
			// about this file. It is definitely the one to use for targetLanguage.
			LocalizationManagerInternal<XLiffDocument>.MapToExistingLanguage[targetLang] = targetLang;

			var piecesOfTargetLang = targetLang.Split('-');
			if (piecesOfTargetLang.Length > 1)
			{
				var rootLangId = piecesOfTargetLang[0];
				// If we don't already have an xliff to use for the root language, tell it to use this one.
				// For example, suppose we have file in the es folder whose target-language is es-ES.
				// We probably want MapToExistingLanguage to have es -> es-ES (as well as es-ES -> es-ES).
				// But we have to be careful. It's also possible that we have both an es folder (where target-language is es)
				// AND an es-ES folder where targetLanguage is es-ES. We might load the plain es one either first or second.
				// In those cases, we don't want to get es -> es-ES. In case we haven't already loaded it,
				// we check that the root language isn't still waiting to load (in _unlodedXliffDocuments);
				// in case we already did, we make sure there isn't already a value under that key in MapToExistingLanguage.
				// (This also means that, in case we have e.g. es-ES and also es-BR but no plain es, one of the two
				// wins out as the one to use for es, and doesn't change later.)
				if (!LocalizationManagerInternal<XLiffDocument>.MapToExistingLanguage.TryGetValue(rootLangId, out _)
				    && !_unloadedXliffDocuments.TryGetValue(rootLangId, out _))
				{
					LocalizationManagerInternal<XLiffDocument>.MapToExistingLanguage[rootLangId] = targetLang;
				}
			}

			XliffDocuments.TryAdd(targetLang, xliffDoc);
			var defunctUnits = new List<XLiffTransUnit>();
			foreach (var tu in xliffDoc.File.Body.TransUnitsUnordered.ToList()) // need a list here because we may modify it while enumerating
			{
				// This block attempts to find 'orphans', that is, localizations that have been done using an obsolete ID.
				// We assume the default language Xliff has only current IDs, and therefore don't look for orphans in that case.
				// This guards against cases such as recently occurred in Bloom, where a dynamic ID EditTab.AddPageDialog.Title
				// was regarded as an obsolete id for PublishTab.Upload.Title
				if (langId != LocalizationManager.kDefaultLang &&
				    DefaultXliffDocument.GetTransUnitForId(tu.Id) == null &&
				    !tu.Id.EndsWith(kToolTipSuffix) && !tu.Id.EndsWith(kShortcutSuffix))
				{
					//if we couldn't find it, maybe the id just changed and then if so re-id it.
					var movedUnit = DefaultXliffDocument.GetTransUnitForOrphan(tu, xliffDoc.File.Body);
					if (movedUnit == null)
					{
						// with dynamic strings, by definition we won't find them during a static code scan
						if (!tu.Dynamic)
						{
							defunctUnits.Add(tu);
							xliffDoc.IsDirty = true;
							IsDirty = true;
						}
					}
					else
					{
						if (xliffDoc.File.Body.TranslationsById.ContainsKey(tu.Id))
						{
							// adjust the document's internal cache
							xliffDoc.File.Body.TranslationsById[movedUnit.Id] =
								xliffDoc.File.Body.TranslationsById[tu.Id];
							xliffDoc.File.Body.TranslationsById.TryRemove(tu.Id, out _);
						}

						// Note: this function is used inside a lock, so we don't have to worry
						// about other threads interfering here.
						xliffDoc.File.Body.RemoveTransUnit(tu);
						tu.Id = movedUnit.Id;
						xliffDoc.File.Body.AddTransUnit(tu);
						xliffDoc.IsDirty = true;
						IsDirty = true;
					}
				}
			}

			// Now we can delete any invalid XLiffTransUnit objects from this document.
			foreach (var tuBad in defunctUnits)
				xliffDoc.File.Body.RemoveTransUnit(tuBad);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Creates an empty string file.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal static XLiffDocument CreateEmptyStringFile()
		{
			var xliffDoc = new XLiffDocument();
			xliffDoc.File.SourceLang = LocalizationManager.kDefaultLang;
			xliffDoc.File.ProductVersion = "0.0.0";
			xliffDoc.File.HardLineBreakReplacement = s_literalNewline;
			xliffDoc.File.AmpersandReplacement = _ampersandReplacement;
			return xliffDoc;
		}

		#endregion

		#region Properties
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a value indicating whether the cache has unsaved changes.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public bool IsDirty { get; private set; }
		#endregion

		#region Methods for updating values in cache
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Updates the localized info. in the cache with the info. from the specified
		/// LocalizedObjectInfo.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public void UpdateLocalizedInfo(LocalizingInfo locInfo)
		{
			if (_tuUpdater.Update(locInfo))
				IsDirty = true;
		}

		#endregion

		#region Methods for saving cache to disk

		internal void SaveIfDirty()
		{
			SaveIfDirty(XliffDocuments.Keys);
		}
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Saves the cache to the files from which the cache was originally loaded, but only
		/// if the cache is dirty. If the cache is dirty and saved, then true is returned.
		/// Otherwise, false is returned.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void SaveIfDirty(ICollection<string> langIdsToForceCreate)
		{
			if (!IsDirty)
				return;

			StringBuilder errorMsg = null;
			foreach (var langId in XliffDocuments.Keys)
			{
				try
				{
					if (XliffDocuments[langId].IsDirty)
						SaveFileForLangId(langId, langIdsToForceCreate != null && langIdsToForceCreate.Contains(langId), XliffDocuments[langId]);
				}
				catch (Exception e)
				{
					if (e is SecurityException || e is UnauthorizedAccessException || e is IOException)
					{
						if (errorMsg == null)
						{
							errorMsg = new StringBuilder();
							errorMsg.AppendLine("Failed to save localization changes in the following files:");
						}
						errorMsg.AppendLine();
						errorMsg.Append("File: ");
						errorMsg.AppendLine(OwningManager.GetPathForLanguage(langId, true));
						errorMsg.Append("Error Type: ");
						errorMsg.AppendLine(e.GetType().ToString());
						errorMsg.Append("Message: ");
						errorMsg.AppendLine(e.Message);
					}
				}
			}

			if (errorMsg != null)
				throw new IOException(errorMsg.ToString());

			IsDirty = false;
		}

		/// ------------------------------------------------------------------------------------
		private void SaveFileForLangId(string langId, bool forceCreation, XLiffDocument xliffOriginal)
		{
			if (!forceCreation && !OwningManager.DoesCustomizedTranslationExistForLanguage(langId))
				return;

			var xliffOutput = CreateEmptyStringFile();
			if (langId != LocalizationManager.kDefaultLang)
				xliffOutput.File.TargetLang = langId;
			xliffOutput.File.ProductVersion = OwningManager.AppVersion;
			xliffOutput.File.HardLineBreakReplacement = s_literalNewline;
			xliffOutput.File.AmpersandReplacement = _ampersandReplacement;
			if (OwningManager != null && OwningManager.Name != null)
				xliffOutput.File.Original = OwningManager.Name + ".dll";

			foreach (var tu in DefaultXliffDocument.File.Body.TransUnitsUnordered)
			{
				var tuTarget = xliffOriginal.File.Body.GetTransUnitForId(tu.Id);
				XLiffTransUnitVariant tuv = null;
				if (tuTarget != null)
					tuv = tuTarget.GetVariantForLang(langId);
				// REVIEW: should we write units with no translation (target)?
				var newTu = new XLiffTransUnit { Id = tu.Id, Dynamic = tu.Dynamic };
				newTu.AddOrReplaceVariant(tu.GetVariantForLang(LocalizationManager.kDefaultLang));
				if (tuv != null)
					newTu.AddOrReplaceVariant(tuv);
				newTu.Notes = tu.CopyNotes();
				xliffOutput.AddTransUnit(newTu);
			}
			xliffOutput.Save(OwningManager.GetPathForLanguage(langId, true));
		}
		#endregion

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Compares two translation units for equality.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal static int TuComparer(XLiffTransUnit tu1, XLiffTransUnit tu2)
		{
			if (tu1 == null && tu2 == null)
				return 0;

			if (tu1 == null)
				return -1;

			if (tu2 == null)
				return 1;

			string x = tu1.Group;
			string y = tu2.Group;

			if (x == y)
				return string.CompareOrdinal(tu1.Id, tu2.Id);

			if (x == null)
				return -1;

			if (y == null)
				return 1;

			return string.CompareOrdinal(x, y);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a value indicating whether or not translations exist for the specified
		/// string id for the current language id.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public bool DoTranslationsExist(string langId, string id)
		{
			var text = GetValueForExactLangAndId(langId, id, false);
			var toolTip = GetValueForExactLangAndId(langId, id + kToolTipSuffix, false);
			var shortcutKeys = GetValueForExactLangAndId(langId, id + kShortcutSuffix, false);

			return (!string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(toolTip) ||
				!string.IsNullOrEmpty(shortcutKeys));
		}

		#region Methods for getting localized strings (including shortcut key strings)
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the localized text for the specified id and suffix.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public string GetString(string langId, string id)
		{
			return GetValueForLangAndIdWithFallback(langId, id);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the localized text for the specified id and suffix. If formatForDisplay is
		/// true, then the string will have ampersands and newlines converted so the text
		/// is displayed nicely at runtime.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public string GetString(string langId, string id, bool formatForDisplay)
		{
			return GetValueForExactLangAndId(langId, id, formatForDisplay);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the localized tooltip text for the specified id and suffix.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public string GetToolTipText(string langId, string id)
		{
			return GetValueForLangAndIdWithFallback(langId, id + kToolTipSuffix);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the localized tooltip text for the specified id and suffix. If
		/// formatForDisplay is true, then the string will have ampersands and newlines
		/// converted so the text is displayed nicely at runtime.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public string GetToolTipText(string langId, string id, bool formatForDisplay)
		{
			return GetValueForExactLangAndId(langId, id + kToolTipSuffix, formatForDisplay);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the localized tooltip text for the specified id and suffix.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public Keys GetShortcutKeys(string langId, string id)
		{
			string keys = GetValueForLangAndIdWithFallback(langId, id + kShortcutSuffix);
			return ShortcutKeysEditor.KeysFromString(keys);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the localized tooltip text for the specified id and suffix.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public string GetShortcutKeysText(string langId, string id)
		{
			return GetValueForExactLangAndId(langId, id + kShortcutSuffix, false);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Tries to get a string value for the specified id, starting with the current
		/// language id. If that fails, then the fall back language id is used and if that
		/// fails, then the default (i.e. "en") is used.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private string GetValueForLangAndIdWithFallback(string langId, string id)
		{
			var value = GetValueForExactLangAndId(langId, id, true);
			if (value != null)
				return value;

			foreach (var fallbackLangId in LocalizationManagerInternal<XLiffDocument>.FallbackLanguageIds)
			{
				value = GetValueForExactLangAndId(fallbackLangId, id, true);
				if (value != null)
					return value;
			}

			return GetValueForExactLangAndId(LocalizationManager.kDefaultLang, id, true);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the localized value for the specified id. If formatForDisplay is true, then
		/// the string will have ampersands and newlines converted so the text is displayed
		/// nicely at runtime.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public string GetValueForExactLangAndId(string langId, string id, bool formatForDisplay)
		{
			if (string.IsNullOrEmpty(langId) || string.IsNullOrEmpty(id))
				return null;
			if (!TryGetDocument(langId, out var xliff))
				return null;
			if (!xliff.File.Body.TranslationsById.TryGetValue(id, out var value))
				return null;
			if (string.IsNullOrEmpty(value))
				return null;

			if (formatForDisplay && s_literalNewline != null)
				value = value.Replace(s_literalNewline, kOSRealNewline);

			if (formatForDisplay && _ampersandReplacement != null)
				value = value.Replace(_ampersandReplacement, "&");

			if (langId == "en")
				return value;

			var tu = xliff.File.Body.GetTransUnitForId(id);
			if (tu?.Source == null || string.IsNullOrWhiteSpace(tu.Source.Value))
				return value;

			var markersCount = CountSubstitutionMarkers(tu);
			if (CheckForValidSubstitutionMarkers(markersCount, value, id))
				return value;

			var fixedValue = FixBrokenFormattingString(value);
			if (fixedValue == value || !CheckForValidSubstitutionMarkers(markersCount, fixedValue, id))
				return null; // don't use an invalid formatting string!

			Console.WriteLine("L10NSharp fixed invalid substitution markers in {0} for {1}", id, langId);
			return fixedValue;
		}

		#endregion

		#region Methods for getting localized string metadata
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the comment for the specified id.
		/// </summary>
		/// <remarks>
		/// The xliff standard allows multiple notes in a trans-unit element.  We use one to
		/// represent the id string (prefacing it with "ID: ").  Any other note is liable to be
		/// considered the "comment" if it exists.
		/// </remarks>
		/// ------------------------------------------------------------------------------------
		public string GetComment(string id)
		{
			XLiffTransUnit tu = DefaultXliffDocument.GetTransUnitForId(id);
			return (tu == null ? null : tu.GetComment());
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the group for the specified id.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal string GetGroup(string id)
		{
			XLiffTransUnit tu = DefaultXliffDocument.GetTransUnitForId(id);
			return (tu == null ? null : tu.Group);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the priority for the specified id.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal LocalizationPriority GetPriority(string id)
		{
			XLiffTransUnit tu = DefaultXliffDocument.GetTransUnitForId(id);
			if (tu != null)
			{
				if (string.IsNullOrEmpty(tu.Priority))
					return LocalizationPriority.High;

				try
				{
					return (LocalizationPriority)Enum.Parse(typeof(LocalizationPriority), tu.Priority);
				}
				catch { }
			}
			return LocalizationPriority.NotLocalizable;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the category for the specified id.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal LocalizationCategory GetCategory(string id)
		{
			XLiffTransUnit tu = DefaultXliffDocument.GetTransUnitForId(id);
			if (tu != null)
			{
				string category = tu.Category;
				if (string.IsNullOrEmpty(category))
					return LocalizationCategory.DontCare;

				try
				{
					return (LocalizationCategory)Enum.Parse(typeof(LocalizationCategory), category);
				}
				catch { }
			}

			return LocalizationCategory.Other;
		}

		#endregion

		#region Methods for loading a tree node collection with all the localizable strings.
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the base id for the specified translation unit id. For each UI object, there
		/// may be up to three translation unit (all having their own id). One for the object's
		/// text, one for the object's tooltip and one for the object's shortcut keys. The
		/// translation unit id for the tooltip and shortcutkeys are just the text's id
		/// with a suffix added. This method will receive a translation unit id and strip off
		/// either of those suffixes, if they are present.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal string GetBaseId(string tuid)
		{
			if (tuid.EndsWith(kToolTipSuffix))
			{
				int i = tuid.LastIndexOf(kToolTipSuffix, StringComparison.Ordinal);
				return tuid.Substring(0, i);
			}

			if (tuid.EndsWith(kShortcutSuffix))
			{
				int i = tuid.LastIndexOf(kShortcutSuffix, StringComparison.Ordinal);
				return tuid.Substring(0, i);
			}

			return tuid;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Loads the specified tree node collection with all the string groups and their
		/// localizable string ids.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public void LoadGroupNodes(TreeNodeCollection topCollection)
		{
			LeafNodeList.Clear();

			foreach (var tu in GetTranslationUnitsForTree())
			{
				string id = GetBaseId(tu.Id);
				var groupChain = ParseGroupAndId(GetGroup(tu.Id), id);
				var nodeKey = string.Empty;
				var nodeCollection = topCollection;
				LocTreeNode<XLiffDocument> newNode;

				for (int i = groupChain.Count - 1; i > 0; i--)
				{
					nodeKey = (nodeKey + "." + groupChain[i]).TrimStart('.');

					var nodes = nodeCollection.Find(nodeKey, true);
					if (nodes.Length > 0)
						nodeCollection = nodes[0].Nodes;
					else
					{
						newNode = new LocTreeNode<XLiffDocument>(OwningManager, groupChain[i], null,
						nodeKey);
						nodeCollection.Add(newNode);
						nodeCollection = newNode.Nodes;
					}
				}

				nodeKey = nodeKey + ("." + groupChain[0]).TrimStart('.');
				newNode = new LocTreeNode<XLiffDocument>(OwningManager, groupChain[0], id, nodeKey);
				nodeCollection.Add(newNode);
				LeafNodeList.Add(newNode);
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets a list of only those translation units that should show up in the localizing
		/// dialog's tree control.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private IEnumerable<XLiffTransUnit> GetTranslationUnitsForTree()
		{
			foreach (var tu in DefaultXliffDocument.File.Body.TransUnitsUnordered)
			{
				// If the translation unit is not for a tooltip or shortcutkey, then return it.
				if (!tu.Id.EndsWith(kToolTipSuffix) && !tu.Id.EndsWith(kShortcutSuffix))
					yield return tu;

				// At this point, we know the translation unit is for a tooltip or shortcutkeys.
				// Therefore, we need to determine whether or not the tooltip or shortcutkeys
				// translation unit has an associated 'base' translation unit. If so, then
				// skip the current tooltip or shortcutkeys translation unit since the base
				// one is all that's needed in the tree.
				var tmpId = GetBaseId(tu.Id);
				if (DefaultXliffDocument.File.Body.TransUnitsUnordered.Any(t => t.Id == tmpId))
					continue;

				// At this point, we know there is not a base translation unit so return the
				// translation unit if it's for a tooltip.
				if (tu.Id.EndsWith(kToolTipSuffix))
					yield return tu;

				// At this point, we know there is not a base translation unit and the current
				// translation unit is for a shortcutkeys. Therefore, only return the current
				// translation unit if there is not associated tooltip translation unit.
				tmpId = tu.Id.Replace(kShortcutSuffix, kToolTipSuffix);
				if (!DefaultXliffDocument.File.Body.TransUnitsUnordered.Any(t => t.Id == tmpId))
					yield return tu;
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Parses the group and id and returns all the pieces between periods.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private static List<string> ParseGroupAndId(string group, string id)
		{
			var allPieces = new List<string>();
			string[] pieces;

			if (@group != null)
			{
				pieces = @group.Split('.');
				foreach (string piece in pieces)
					allPieces.Insert(0, piece);
			}

			pieces = id.Split('.');
			foreach (string piece in pieces)
				allPieces.Insert(0, piece);

			return allPieces;
		}

		public static string GetTerminalIdPart(string id)
		{
			var pieces = id.Split('.');
			if (pieces.Length == 0)
				return "";
			return pieces.Last();
		}

		#endregion

		/// <summary>
		/// Check that all substitution markers in the target string are valid.
		/// </summary>
		/// <returns>
		/// true if all markers are okay, false if any are malformed.
		/// </returns>
		internal static bool CheckForValidSubstitutionMarkers(int markersCount, string targetValue, string tuId, bool quiet = true)
		{
			try
			{
				string s = null;
				switch (markersCount)
				{
					case 0:
						// targetValue won't be presented to String.Format().
						s = targetValue;
						break;
					case 1:
						s = string.Format(targetValue, "FIRST");
						break;
					case 2:
						s = string.Format(targetValue, "FIRST", "SECOND");
						break;
					case 3:
						s = string.Format(targetValue, "FIRST", "SECOND", "THIRD");
						break;
					case 4:
						s = string.Format(targetValue, "FIRST", "SECOND", "THIRD", "FOURTH");
						break;
					case 5:
						s = string.Format(targetValue, "FIRST", "SECOND", "THIRD", "FOURTH", "FIFTH");
						break;
					case 6:
						s = string.Format(targetValue, "FIRST", "SECOND", "THIRD", "FOURTH", "FIFTH", "SIXTH");
						break;
					case 7:
						s = string.Format(targetValue, "FIRST", "SECOND", "THIRD", "FOURTH", "FIFTH", "SIXTH", "SEVENTH");
						break;
					case 8:
						s = string.Format(targetValue, "FIRST", "SECOND", "THIRD", "FOURTH", "FIFTH", "SIXTH", "SEVENTH", "EIGHTH");
						break;
					case 9:
						s = string.Format(targetValue, "FIRST", "SECOND", "THIRD", "FOURTH", "FIFTH", "SIXTH", "SEVENTH", "EIGHTH", "NINTH");
						break;
					case 10:
						s = string.Format(targetValue, "FIRST", "SECOND", "THIRD", "FOURTH", "FIFTH", "SIXTH", "SEVENTH", "EIGHTH", "NINTH", "TENTH");
						break;
					default:
						s = targetValue;
						Console.WriteLine("trans-unit {0} has more than ten distinct substitution markers!", tuId);
						break;
				}
				return s != null;
			}
			catch (Exception)
			{
				if (!quiet)
				{
					Console.WriteLine(@"Translation of " + tuId + @" will cause crash");
					WritePossiblyBadSubstitutionMarkerDataForAnalysis(tuId, targetValue);
				}
				return false;
			}
		}

		private static int CountSubstitutionMarkers(XLiffTransUnit tu)
		{
			var matchesSource = Regex.Matches(tu.Source.Value, "{[0-9]+}");
			var markers = new List<string>();
			for (int i = 0; i < matchesSource.Count; ++i)
			{
				var key = matchesSource[i].Value;
				if (!markers.Contains(key))
					markers.Add(key);
			}
			return markers.Count;
		}

		/// <summary>
		/// Dump the 3 characters before and 6 characters following each { character in the string.  This
		/// should be enough to show how a substitution marker has been mangled, or that it is okay.
		/// </summary>
		private static void WritePossiblyBadSubstitutionMarkerDataForAnalysis(string tuid, string target)
		{
			var data = target.ToCharArray();
			for (int i = 0; i < data.Length; ++i)
			{
				if (data[i] == '{')
				{
					Console.Write("{0}({1}):", tuid, i);
					var first = Math.Max(0, i - 3);
					var last = Math.Min(i + 6, data.Length - 1);
					for (int j = first; j <= last; ++j)
					{
						if (data[j] > 32 && data[j] < 127)
							Console.Write(" " + data[j]);
						else
							Console.Write(" {0:X4}", (ushort)data[j]);
					}
					Console.WriteLine();
					i = last;
				}
			}
		}

		/// <summary>
		/// Fix any mangled format substitution markers that we can.  Most of the problems I've seen are due
		/// to confusion in RTL scripts.  The following regular expression operations fix the patterns of mistakes
		/// that I've seen.  The first four would look okay visually in RTL scripts even though the underlying
		/// string is wrong.
		/// The other mistake I've seen is translating the number inside the curly brackets.  Bengali is the only
		/// language with an occurrence of this problem so far.
		/// </summary>
		/// <remarks>
		/// In C# Regular Expressions, unquoted parentheses group the characters match inside the parentheses,
		/// unquoted square brackets group a set of alternative characters to match, unquoted curly brackets
		/// (braces) match themselves, unquoted + means "1 or more of the previous expression", and unquoted *
		/// means "0 or more of the previous expression".
		/// The order of replacement operations in this matter should not matter.
		/// </remarks>
		internal static string FixBrokenFormattingString(string target)
		{
			// RTL confusion
			// Note that \u200E is 'LEFT-TO-RIGHT MARK' and \u200F is 'RIGHT-TO-LEFT MARK'.  The Unicode standard
			// defines some characters as mirrored in RTL vs LTR display.  CURLY BRACKET, SQUARE BRACKET, and
			// PARENTHESIS are some of those mirroring pairs.  Other characters like SPACE, SINGLE QUOTE MARK and
			// DOUBLE QUOTE MARK display the same in either direction and do not affect the directionality of the
			// display.  Many characters have an implicit direction and will change the directionality of the
			// display automatically.  This includes the ASCII numerals 0-9.  This means that embedding something
			// like {0} in a RTL string is inherently difficult to get right.  It can look right on the screen
			// and still cause a program to crash when trying to use the string in String.Format().
			// The best fix I've come up with is to have an LTR MARK precede any quotation marks and the OPEN CURLY
			// BRACKET, then the rest of the substitution marker and any quotation marks, and then an RTL MARK.

			// Fix having an LTR mark follow an OPEN CURLY BRACKET, possibly with an enclosing QUOTE MARKs
			// or SPACEs, and an optional trailing RTL mark.
			var target1 = Regex.Replace(target,  "'{\u200E'{([0-9]+)\u200F*", "\u200E'{$1}'\u200F", RegexOptions.CultureInvariant);
			var target2 = Regex.Replace(target1, "'{\u200E([0-9]+)}'\u200F*", "\u200E'{$1}'\u200F", RegexOptions.CultureInvariant);
			var target3 = Regex.Replace(target2, "\"{\u200E\"{([0-9]+)\u200F*", "\u200E\"{$1}\"\u200F", RegexOptions.CultureInvariant);
			var target4 = Regex.Replace(target3, " {\u200E {([0-9]+)\u200F*", " \u200E{$1}\u200F ", RegexOptions.CultureInvariant);
			var target5 = Regex.Replace(target4, "{\u200E{([0-9]+)\u200F*", "\u200E{$1}\u200F", RegexOptions.CultureInvariant);
			var target6 = Regex.Replace(target5, "{\u200E([0-9]+)}\u200F*", "\u200E{$1}\u200F", RegexOptions.CultureInvariant);
			// Fix having no LTR or RTL marks in the input, with a doubled QUOTE MARK and OPEN CURLY BRACKET
			// pair, with or without a trailing period.  This confusion indicates an underlying RTL language
			// since the bidirectional algorithm may make it look okay on the screen.
			var target7 = Regex.Replace(target6, " \"{\"{([0-9]+)\\. ", " \u200E\"{$1}\"\u200F. ", RegexOptions.CultureInvariant);
			var target8 = Regex.Replace(target7, " \"{\"{([0-9]+) ", " \u200E\"{$1}\"\u200F ", RegexOptions.CultureInvariant);
			// Fix a very broken pattern found in one string with doubled OPEN CURLY BRACKETs and a CLOSE CURLY BRACKET.
			var target9 = Regex.Replace(target8, " ([0-9]+)}{{\\. ", " \u200E{$1}\u200F. ", RegexOptions.CultureInvariant);
			// Fix probably bogus (since repeated) LTR marks.  Leave the first one since it shouldn't hurt anything, and
			// conceivably could be needed.
			var target10 = Regex.Replace(target9, "\u200E{([0-9]+)\u200E}\u200E", "\u200E{$1}", RegexOptions.CultureInvariant);
			var target11 = Regex.Replace(target10, "\u200E{([0-9]+)\u200E}", "\u200E{$1}", RegexOptions.CultureInvariant);

			// Bengali numbers
			var target12 = target11.Replace("{\u09E6}", "{0}").Replace("{\u09E7}", "{1}").Replace("{\u09E8}", "{2}")
								.Replace("{\u09E9}", "{3}").Replace("{\u09EA}", "{4}").Replace("{\u09EB}", "{5}")
								.Replace("{\u09EC}", "{6}").Replace("{\u09ED}", "{7}").Replace("{\u09EE}", "{8}")
								.Replace("{\u09EF}", "{9}");
			return target12;
		}

		/// <summary>
		/// Return the number of strings that appear to have been translated and approved for the
		/// given language.
		/// </summary>
		public int NumberApproved(string lang)
		{
			return TryGetDocument(lang, out var doc) ? doc.NumberApproved : 0;
		}

		/// <summary>
		/// Return the number of strings that appear to have been translated for the given language.
		/// </summary>
		public int NumberTranslated(string lang)
		{
			return TryGetDocument(lang, out var doc) ? doc.NumberTranslated : 0;
		}

		/// <summary>
		/// Return the number of strings that appear to be available for the given language.
		/// </summary>
		public int StringCount(string lang)
		{
			return TryGetDocument(lang, out var doc) ? doc.StringCount : 0;
		}
	}
}
