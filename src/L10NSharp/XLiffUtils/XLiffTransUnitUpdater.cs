using System;
using System.Collections.Generic;
using System.Diagnostics;
using L10NSharp.XLiffUtils;

namespace L10NSharp.XLiffUtils
{
	internal class XLiffTransUnitUpdater
	{
		internal const string kToolTipSuffix  = "_ToolTip_";
		internal const string kShortcutSuffix = "_ShortcutKeys_";

		// Cannot use Environment.NewLine because that also includes a carriage return
		// character which, when included, messes up the display of text in controls.
		internal const string kOSRealNewline = "\n";

		// This is the symbol for a newline that users put in their localized text when
		// they want a real newline inserted. The program will replace literal newlines
		// with the value of kOSNewline.
		internal string _literalNewline = "\\n";

		private readonly XLiffLocalizedStringCache _stringCache;
		private readonly string                    _defaultLang;
		private          bool                      _updated;


		/// ------------------------------------------------------------------------------------
		internal XLiffTransUnitUpdater(XLiffLocalizedStringCache cache)
		{
			_stringCache = cache;
			_defaultLang = LocalizationManager.kDefaultLang;
			var replacement = _stringCache.GetDocument(_defaultLang).File
				.HardLineBreakReplacement;
			if (replacement != null)
				_literalNewline = replacement;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Updates the localized info. in the cache with the info. from the specified
		/// LocalizedObjectInfo.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal bool Update(LocalizingInfo locInfo)
		{
			_updated = false;

			// Can't do anything without a language id.
			if (string.IsNullOrEmpty(locInfo.LangId))
				return _updated;

			var xliffSource = _stringCache.GetDocument(_defaultLang);
			Debug.Assert(xliffSource != null);

			XLiffDocument xliffTarget;
			if (!_stringCache.TryGetDocument(locInfo.LangId, out xliffTarget))
			{
				xliffTarget = new XLiffDocument();
				xliffTarget.File.AmpersandReplacement = xliffSource.File.AmpersandReplacement;
				xliffTarget.File.DataType = xliffSource.File.DataType;
				xliffTarget.File.HardLineBreakReplacement =
					xliffSource.File.HardLineBreakReplacement;
				xliffTarget.File.Original = xliffSource.File.Original;
				xliffTarget.File.ProductVersion = xliffSource.File.ProductVersion;
				xliffTarget.File.SourceLang = xliffSource.File.SourceLang;
				xliffTarget.File.TargetLang = locInfo.LangId;
				xliffTarget.IsDirty = true;
				_updated = true;
				_stringCache.AddDocument(locInfo.LangId, xliffTarget);
			}

			var tuSourceText = xliffSource.GetTransUnitForId(locInfo.Id);
			var tuSourceToolTip = xliffSource.GetTransUnitForId(locInfo.Id + kToolTipSuffix);
			var tuSourceShortcutKeys =
				xliffSource.GetTransUnitForId(locInfo.Id + kShortcutSuffix);
			if (locInfo.Priority == LocalizationPriority.NotLocalizable)
			{
				_updated = (tuSourceText != null || tuSourceToolTip != null ||
							tuSourceShortcutKeys != null);
				xliffSource.RemoveTransUnit(tuSourceText);
				xliffSource.RemoveTransUnit(tuSourceToolTip);
				xliffSource.RemoveTransUnit(tuSourceShortcutKeys);
				if (_defaultLang != locInfo.LangId)
				{
					xliffTarget.RemoveTransUnit(tuSourceText);
					xliffTarget.RemoveTransUnit(tuSourceToolTip);
					xliffTarget.RemoveTransUnit(tuSourceShortcutKeys);
				}

				return _updated;
			}

			// Save the shortcut keys
			var shortcutId = locInfo.Id + kShortcutSuffix;
			if ((locInfo.UpdateFields & UpdateFields.ShortcutKeys) == UpdateFields.ShortcutKeys)
			{
				UpdateValueAndComment(xliffTarget, tuSourceShortcutKeys, locInfo.ShortcutKeys,
					locInfo, shortcutId);
			}

			// Save the tooltips
			var tooltipId = locInfo.Id + kToolTipSuffix;
			if ((locInfo.UpdateFields & UpdateFields.ToolTip) == UpdateFields.ToolTip)
			{
				UpdateValueAndComment(xliffTarget, tuSourceToolTip, locInfo.ToolTipText, locInfo,
					tooltipId);
			}

			// Save the text
			if ((locInfo.UpdateFields & UpdateFields.Text) == UpdateFields.Text)
			{
				var text = locInfo.Text ?? string.Empty;
				// first because Environment.Newline might be one part of it. We include this explicitly
				// in case some Windows data somehow finds its way to Linux.
				text = text.Replace("\r\n", _literalNewline);
				text = text.Replace(Environment.NewLine, _literalNewline);
				text = text.Replace(_literalNewline, "@#$");
				text = text.Replace(kOSRealNewline, _literalNewline);
				text = text.Replace("@#$", _literalNewline);
				UpdateValueAndComment(xliffTarget, tuSourceText, text, locInfo, locInfo.Id);
			}

			if (_updated)
				xliffTarget.IsDirty = true;
			return _updated;
		}

		void UpdateValueAndComment(XLiffDocument xliffTarget, XLiffTransUnit tuSource,
			string                               newText,     LocalizingInfo locInfo, string tuId)
		{
			var tuTarget = UpdateValue(xliffTarget, tuSource, newText, locInfo, tuId);
			UpdateTransUnitComment(xliffTarget, tuSource, locInfo);
			UpdateTransUnitComment(xliffTarget, tuTarget, locInfo);
		}

		private void UpdateTransUnitComment(XLiffDocument xliffTarget, XLiffTransUnit tu,
			LocalizingInfo                                locInfo)
		{
			if (tu == null)
				return;

			if (locInfo.DiscoveredDynamically && !tu.Dynamic)
			{
				tu.Dynamic = true;
				_updated = true;
			}

			if ((locInfo.UpdateFields & UpdateFields.Comment) != UpdateFields.Comment)
				return;
			if (tu.Notes.Count == 0 && string.IsNullOrEmpty(locInfo.Comment))
				return; // empty comment and already no comment in XLiffTransUnit
			if (tu.NotesContain(locInfo.Comment))
				return; // exactly the same comment already exists in XLiffTransUnit

			_updated = true;
			tu.Notes.Clear();
			tu.AddNote("ID: " + tu.Id);
			if (!string.IsNullOrEmpty(locInfo.Comment))
				tu.AddNote(locInfo.Comment);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Updates the value for the specified translation unit with the specified new value.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private XLiffTransUnit UpdateValue(XLiffDocument xliffTarget, XLiffTransUnit tuSource,
			string                                       newValue,    LocalizingInfo locInfo,
			string                                       tuId)
		{
			// One would think there would be a source XLiffTransUnit, but that isn't necessarily true
			// with users editing interactively and adding tooltips or shortcuts.
			Debug.Assert(tuSource == null || tuId == tuSource.Id);
			Debug.Assert(tuId.StartsWith(locInfo.Id));
			var tuTarget = xliffTarget.GetTransUnitForId(tuId);
			// If the XLiffTransUnit exists in the target language, check whether we're removing the translation
			// instead of adding or changing it.
			if (tuTarget != null)
			{
				var tuvTarg = tuTarget.GetVariantForLang(locInfo.LangId);
				if (tuvTarg != null)
				{
					// don't need to update if the value hasn't changed
					if (tuvTarg.Value == newValue)
						return tuTarget;

					if (string.IsNullOrEmpty(newValue))
					{
						_updated = true;
						tuTarget.RemoveVariant(tuvTarg);
						if ((tuTarget.Source == null ||
							string.IsNullOrEmpty(tuTarget.Source.Value)) &&
							(tuTarget.Target == null ||
							string.IsNullOrEmpty(tuTarget.Target.Value)))
						{
							xliffTarget.RemoveTransUnit(tuTarget);
							tuTarget = null;
						}
					}
				}
			}

			// If we're removing an existing translation, we can quit now.
			if (string.IsNullOrEmpty(newValue))
			{
				xliffTarget.File.Body.TranslationsById.Remove(tuId);
				return tuTarget;
			}

			// If the XLiffTransUnit does not exist in the target language yet, create it and fill in the
			// source language value (if any).
			if (tuTarget == null)
			{
				tuTarget = new XLiffTransUnit();
				tuTarget.Id = tuId;
				tuTarget.Dynamic = locInfo.DiscoveredDynamically;
				xliffTarget.AddTransUnit(tuTarget);
				if (tuSource != null && locInfo.LangId != _defaultLang)
				{
					var tuvSrc = tuSource.GetVariantForLang(_defaultLang);
					if (tuvSrc != null && !string.IsNullOrEmpty(tuvSrc.Value))
						tuTarget.AddOrReplaceVariant(_defaultLang, tuvSrc.Value);
				}

				tuTarget.AddNote("ID: " + tuId);
			}

			tuTarget.AddOrReplaceVariant(locInfo.LangId, newValue);
			xliffTarget.File.Body.TranslationsById[tuId] = newValue;
			_updated = true;
			return tuTarget;
		}
	}
}
