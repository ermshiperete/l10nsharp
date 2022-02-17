// // Copyright (c) 2019 SIL International
// // This software is licensed under the MIT License (http://opensource.org/licenses/MIT)

using System.Collections.Generic;
using System.Windows.Forms;
using L10NSharp.UI;
using L10NSharp.XLiffUtils;

namespace L10NSharp
{
	internal interface ILocalizedStringCache<T>
	{
		bool TryGetDocument(string langId, out T doc);
		IEnumerable<string> AvailableLangKeys { get; }
		List<LocTreeNode<T>> LeafNodeList { get; }
		string GetString(string langId, string id);
		string GetString(string langId, string id, bool formatForDisplay);
		string GetToolTipText(string langId, string id);
		string GetToolTipText(string langId, string id, bool formatForDisplay);
		Keys GetShortcutKeys(string langId, string id);
		string GetShortcutKeysText(string langId, string id);
		string GetComment(string id);
		string GetValueForExactLangAndId(string langId, string id, bool formatForDisplay);

		void UpdateLocalizedInfo(LocalizingInfo locInfo);
		void LoadGroupNodes(TreeNodeCollection topCollection);

		int NumberApproved(string lang);
		int NumberTranslated(string lang);
		int StringCount(string lang);

		bool DoTranslationsExist(string langId, string id);
	}
}
