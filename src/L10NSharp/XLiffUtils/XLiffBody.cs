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
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
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
		static SpinLock _transUnitIdLock;

		private ConcurrentDictionary<string, XLiffTransUnit> _transUnitDict =
			new ConcurrentDictionary<string, XLiffTransUnit>();

		private object mutex = new object(); // lock for accessing non-concurrent variables.
		private int _translatedCount = -1;
		private int _approvedCount = -1;

		public class ListWrapper : IEnumerable<XLiffTransUnit>
		{
			private IEnumerable<XLiffTransUnit> _list;
			private XLiffBody _body;

			public ListWrapper(IEnumerable<XLiffTransUnit> startWith, XLiffBody body)
			{
				_list = startWith;
				_body = body;
			}
			public IEnumerator<XLiffTransUnit> GetEnumerator()
			{
				return _list.GetEnumerator();
			}

			IEnumerator IEnumerable.GetEnumerator()
			{
				return GetEnumerator();
			}

			public void Add(XLiffTransUnit item)
			{
				_body.AddTransUnitRaw(item);
			}
		}

		#region Properties
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the list of translation units in the file.
		/// Note, a new list is generated each time, so this can be inefficient. Consider using
		/// TransUnitsUnordered if you don't absolutely need a list. This method is retained
		/// mainly for backwards compatibility.
		/// Modifying the list returned (unlike in previous versions) will have no effect on
		/// the XliffBody.
		/// The list returned is not in any predictable order, specifically NOT the same order
		/// as any list previously passed to the setter.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		[XmlIgnore]
		public List<XLiffTransUnit> TransUnits
		{
			get
			{
				var result = TransUnitsUnordered.ToList();
				return result;
			}
			set
			{
				_transUnitDict.Clear();
				foreach (var tu in value)
				{
					AddTransUnit(tu);
				}
			}
		}

		/// <summary>
		/// This property exists solely to support serializing and deserializing an XliffBody in a
		/// backwards-compatible way. Previously, we simply put this annotation on TransUnits.
		/// However, the serialization code assumes it can get the list and use Add to put things
		/// into it, which does not work with the current version. The List wrapper implements just
		/// the functions required of an IEnumerable property that can be serialized and deserialized.
		/// </summary>
		[XmlElement("trans-unit")]
		public ListWrapper TransUnitsForXml
		{
			get
			{
				var result = TransUnitsUnordered.ToList();
				result.Sort(XLiffLocalizedStringCache.TuComparer);
				return new ListWrapper(result, this);
			}
		}


		[XmlIgnore] public IEnumerable<XLiffTransUnit> TransUnitsUnordered => _transUnitDict.Values;

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Get a dictionary to access the translations by id.  For the default language (almost
		/// certainly English), this will be the value of the source.  For all other languages,
		/// this will be the value of the target.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		[XmlIgnore]
		public readonly ConcurrentDictionary<string, string> TranslationsById = new ConcurrentDictionary<string, string>();
		#endregion


		#region Methods
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the translation unit for the specified id.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal XLiffTransUnit GetTransUnitForId(string id)
		{
			_transUnitDict.TryGetValue(id, out XLiffTransUnit result);
			return result;
		}

		/// <summary>
		/// When all but the last part of the id changed, this can help reunite things
		/// </summary>
		internal XLiffTransUnit GetTransUnitForOrphan(XLiffTransUnit orphan, XLiffBody source)
		{
			var terminalIdToMatch = XLiffLocalizedStringCache.GetTerminalIdPart(orphan.Id);
			var defaultTextToMatch = GetDefaultVariantValue(orphan);
			return TransUnitsUnordered.FirstOrDefault(tu =>
				XLiffLocalizedStringCache.GetTerminalIdPart(tu.Id) ==
				terminalIdToMatch // require last part of ID to match
				&& GetDefaultVariantValue(tu) == defaultTextToMatch // require text to match
				&& source?.GetTransUnitForId(tu.Id) == null); // and translation does not already have an element for this
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
		internal bool AddTransUnitRaw(XLiffTransUnit tu)
		{
			if (tu == null || tu.IsEmpty)
				return false;

			bool lockTaken = false;
			string key;
			try
			{
				_transUnitIdLock.Enter(ref lockTaken);
				// Efficiently lock this very small task so that if we need to modify
				// the TU, the key that it gets is guaranteed to be the one used to insert
				// it into the dictionary. This assumes nothing else modifies IDs once they
				// are in this system: once our locked code has given the TU an ID, any other
				// thread will see that it is non-empty.
				key = tu.Id;
				if (string.IsNullOrEmpty(key))
				{
					tu.Id = (System.Threading.Interlocked.Increment(ref _transUnitId)).ToString();
					key = tu.Id;
				}
			}
			finally
			{
				if (lockTaken) _transUnitIdLock.Exit(false);
			}

			// If a translation unit with the specified id already exists, then quit here.
			if (GetTransUnitForId(tu.Id) != null)
				return false;
			_transUnitDict[key] = tu;
			return true;
		}
		internal bool AddTransUnit(XLiffTransUnit tu)
		{
			if (!AddTransUnitRaw(tu))
				return false;
		
			// If the target exists, store its value in the dictionary lookup.  Otherwise, store
			// the source value there.
			if (tu.Target != null && tu.Target.Value != null)
				TranslationsById[tu.Id] = tu.Target.Value;
			else
				TranslationsById[tu.Id] = tu.Source.Value;
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
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Removes the specified translation unit.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		internal void RemoveTransUnit(XLiffTransUnit tu)
		{
			// if the ID is null, it can't be in our dictionary, unless someone
			// cheated and changed the ID by putting it there after inserting it.
			if (tu == null || tu.Id == null)
				return;

			_transUnitDict.TryRemove(tu.Id, out _);
			TranslationsById.TryRemove(tu.Id, out _);
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
				lock (mutex)
				{
					if (_translatedCount < 0)
					{
						_translatedCount = 0;
						foreach (var tu in TransUnitsUnordered)
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
				lock (mutex)
				{
					if (_approvedCount < 0)
					{
						_approvedCount = 0;
						foreach (var tu in TransUnitsUnordered)
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
		}

		/// <summary>
		/// Return the total number of strings.
		/// </summary>
		internal int StringCount => _transUnitDict.Count;
	}

	#endregion
}
