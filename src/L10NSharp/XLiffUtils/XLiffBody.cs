// ---------------------------------------------------------------------------------------------
#region // Copyright (c) 2009, SIL International. All Rights Reserved.
// <copyright from='2009' to='2009' company='SIL International'>
//		Copyright (c) 2009, SIL International. All Rights Reserved.
//
//		Distributable under the terms of either the Common Public License or the
//		GNU Lesser General Public License, as specified in the LICENSING.txt file.
// </copyright>
#endregion
//
// File: XLiffBody.cs
//
// <remarks>
// </remarks>
// ---------------------------------------------------------------------------------------------
using System;
using System.Linq;
using System.Collections.Generic;
using System.Xml.Serialization;
using L10NSharp.XLiffUtils;

namespace L10NSharp.XLiffUtils
{
	#region XLiffBody class
	/// ----------------------------------------------------------------------------------------
	[XmlType("body", Namespace = "urn:oasis:names:tc:xliff:document:1.2")]
	public class XLiffBody
	{
		// This is used when translation unit IDs are not found in the file (which seems to be
		// the case with Lingobit XLiff files).
		private int _transUnitId;
		private bool _idsVerified;
		private List<XLiffTransUnit> _transUnits = new List<XLiffTransUnit>();
		private int _translatedCount = -1;
		private int _approvedCount = -1;

		#region Properties
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the list of translation units in the file.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		[XmlElement("trans-unit")]
		public List<XLiffTransUnit> TransUnits
		{
			get
			{
				if (!_idsVerified && _transUnits != null && _transUnits.Count > 0)
				{
					foreach (var tu in _transUnits.Where(tu => string.IsNullOrEmpty(tu.Id)))
						tu.Id = (++_transUnitId).ToString();

					_idsVerified = true;
				}

				return _transUnits;
			}
			set { _transUnits = value; }
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Get a dictionary to access the translations by id.  For the default language (almost
		/// certainly English), this will be the value of the source.  For all other languages,
		/// this will be the value of the target.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		[XmlIgnore]
		public readonly Dictionary<string, string> TranslationsById = new Dictionary<string, string>();
		[XmlIgnore]
		public readonly Dictionary<string, bool> ApprovalsById = new Dictionary<string, bool>();
		#endregion


		#region Methods
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the translation unit for the specified id.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal XLiffTransUnit GetTransUnitForId(string id)
		{
			return _transUnits.FirstOrDefault(tu => tu.Id == id);
		}

		/// <summary>
		/// When all but the last part of the id changed, this can help reunite things
		/// </summary>
		internal XLiffTransUnit GetTransUnitForOrphan(XLiffTransUnit orphan)
		{
			var terminalIdToMatch = XLiffLocalizedStringCache.GetTerminalIdPart(orphan.Id);
			var defaultTextToMatch = GetDefaultVariantValue(orphan);
			return _transUnits.FirstOrDefault(tu => XLiffLocalizedStringCache.GetTerminalIdPart(tu.Id) == terminalIdToMatch && GetDefaultVariantValue(tu) == defaultTextToMatch);
		}

		string GetDefaultVariantValue(XLiffTransUnit tu)
		{
			var variant = tu.GetVariantForLang(LocalizationManager.kDefaultLang);
			return variant?.Value;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Adds the specified translation unit.
		/// </summary>
		/// <param name="tu">The translation unit.</param>
		/// <returns>true if the translation unit was successfully added. Otherwise, false.</returns>
		/// ------------------------------------------------------------------------------------
		internal bool AddTransUnit(XLiffTransUnit tu)
		{
			if (tu == null || tu.IsEmpty)
				return false;

			if (tu.Id == null)
				tu.Id = (++_transUnitId).ToString();

			// If a translation unit with the specified id already exists, then quit here.
			if (GetTransUnitForId(tu.Id) != null)
				return false;

			_transUnits.Add(tu);
			// If the target exists, store its value in the dictionary lookup.  Otherwise, store
			// the source value there.
			if (tu.Target != null && tu.Target.Value != null)
				TranslationsById[tu.Id] = tu.Target.Value;
			else
				TranslationsById[tu.Id] = tu.Source.Value;
			ApprovalsById[tu.Id] = tu.TranslationStatus == TranslationStatus.Approved;
			return true;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// If a translation unit does not already exist for the id in the specified
		/// translation unit, then the translation unit is added. Otherwise, if the variant
		/// for the specified language does not exist in the translation unit, it is added.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void AddTransUnitOrVariantFromExisting(XLiffTransUnit tu, string langId)
		{
			var variantToAdd = tu.GetVariantForLang(langId);

			if (variantToAdd == null || AddTransUnit(tu))
				return;

			var existingTu = GetTransUnitForId(tu.Id);

			//notice, we don't care if there is already a string in there for this language
			//(that was the cause of a previous bug), because the XLiff of language X should
			//surely take precedence, as the translation for that language.
			existingTu.AddOrReplaceVariant(variantToAdd);
			TranslationsById[tu.Id] = variantToAdd.Value;
			ApprovalsById[tu.Id] = tu.TranslationStatus == TranslationStatus.Approved;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Removes the specified translation unit.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void RemoveTransUnit(XLiffTransUnit tu)
		{
			if (tu == null)
				return;

			if (_transUnits.Contains(tu))
			{
				_transUnits.Remove(tu);
			}
			else if (tu.Id != null)
			{
				var tmptu = GetTransUnitForId(tu.Id);
				if (tmptu != null)
				{
					_transUnits.Remove(tmptu);
				}
			}
			if (tu.Id != null)
			{
				TranslationsById.Remove(tu.Id);
				ApprovalsById.Remove(tu.Id);
			}
		}

		#endregion

		/// <summary>
		/// Return the number of the strings that appear to be translated.
		/// </summary>
		/// <remarks>
		/// This value never changes once it is set.
		/// </remarks>
		internal int NumberTranslated
		{
			get
			{
				if (_translatedCount < 0)
				{
					_translatedCount = 0;
					foreach (var tu in TransUnits)
					{
						if (tu.Target == null || string.IsNullOrWhiteSpace(tu.Target.Value))
							continue;
						if (tu.TranslationStatus == TranslationStatus.Approved ||
							tu.Target.TargetState == XLiffTransUnitVariant.TranslationState.Translated)
						{
							++_translatedCount;
						}
						else if (tu.Target.Value != tu.Source.Value &&
							tu.Target.TargetState == XLiffTransUnitVariant.TranslationState.Undefined)
						{
							++_translatedCount;
						}
					}
				}
				return _translatedCount;
			}
		}

		/// <summary>
		/// Return the number of the strings that are translated and marked approved.
		/// </summary>
		/// <remarks>
		/// This value never changes once it is set.
		/// </remarks>
		internal int NumberApproved
		{
			get
			{
				if (_approvedCount < 0)
				{
					_approvedCount = 0;
					foreach (var tu in TransUnits)
					{
						if (tu.Target == null || string.IsNullOrWhiteSpace(tu.Target.Value))
							continue;
						if (tu.TranslationStatus == TranslationStatus.Approved)
							++_approvedCount;
					}
				}
				return _approvedCount;
			}
		}

		/// <summary>
		/// Return the total number of strings.
		/// </summary>
		internal int StringCount => TransUnits.Count;
	}

	#endregion
}
