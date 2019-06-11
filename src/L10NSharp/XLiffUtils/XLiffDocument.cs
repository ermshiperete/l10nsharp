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
// File: XLiffDocument.cs
//
// <remarks>
// </remarks>
// ---------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace L10NSharp.XLiffUtils
{
	#region XLiffDocment class

	/// ----------------------------------------------------------------------------------------

	[XmlRoot("xliff", Namespace = "urn:oasis:names:tc:xliff:document:1.2")]
	public class XLiffDocument : IDocument
	{
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Initializes a new instance of the <see cref="XLiffDocument"/> class.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public XLiffDocument()
		{
			File = new XLiffFile();
			Version = "1.2";
		}

		#region Properties

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets or sets the XLIFF version.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		[XmlAttribute("version")]
		public string Version { get; set; }

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets or sets the file.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		[XmlElement("file", Namespace = null)]
		public XLiffFile File { get; set; }


		/// <summary>
		/// Gets or sets a value indicating whether this has changes that need to be written.
		/// </summary>
		[XmlIgnore]
		public bool IsDirty { get; set; }

		#endregion

		#region Methods

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Gets the translation unit for the specified id.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public XLiffTransUnit GetTransUnitForId(string id)
		{
			return File.Body.GetTransUnitForId(id);
		}

		/// ------------------------------------------------------------------------------------
		public IEnumerable<XLiffTransUnit> GetTransUnitsForTextInLang(string langId, string text)
		{
			return from tu in File.Body.TransUnits
				let variant = tu.GetVariantForLang(langId)
				where variant != null && variant.Value == text
				select tu;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Adds the specified translation unit.
		/// </summary>
		/// <param name="tu">The translation unit.</param>
		/// <returns>true if the translation unit was successfully added. Otherwise, false.</returns>
		/// ------------------------------------------------------------------------------------
		public bool AddTransUnit(ITransUnit tu)
		{
			return File.Body.AddTransUnit(tu as XLiffTransUnit);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Remove the specified translation unit.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public void RemoveTransUnit(ITransUnit tu)
		{
			File.Body.RemoveTransUnit(tu as XLiffTransUnit);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Saves the XLIFFDocument to the specified XLIFF file.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public void Save(string xliffFile)
		{
			XLiffXmlSerializationHelper.SerializeToFile(xliffFile, this);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Reads the specified XLIFF file and returns a XLIFFDocument containing the information
		/// in the file.
		/// </summary>
		/// <param name= "xLiffFile">The XLiff file to read.</param>
		/// ------------------------------------------------------------------------------------
		public static XLiffDocument Read(string xLiffFile)
		{
			if (!System.IO.File.Exists(xLiffFile))
				throw new FileNotFoundException("XLiff file not found.", xLiffFile);

			Exception e;
			var xLiffDoc =
				XLiffXmlSerializationHelper.DeserializeFromFile<XLiffDocument>(xLiffFile, out e);

			if (e != null)
				throw e;

			// Fill in the fast lookup dictionary.
			var langId = xLiffDoc.File.TargetLang;
			if (string.IsNullOrEmpty(langId))
				langId = xLiffDoc.File.SourceLang;
			var storingDefaultSource = (langId == xLiffDoc.File.SourceLang && langId == LocalizationManager.kDefaultLang);
			foreach (var tu in xLiffDoc.File.Body.TransUnits)
			{
				if (xLiffDoc.File.Body.TranslationsById.ContainsKey(tu.Id))
				{
					Console.WriteLine("WARNING: string ID \"{0}\" already found in \"{1}\".",
						tu.Id, xLiffFile);
				}
				else
				{
					var target = tu.GetVariantForLang(langId);
					if (target != null && !string.IsNullOrEmpty(target.Value) &&
						(storingDefaultSource ||
						 tu.TranslationStatus == TranslationStatus.Approved ||
						 target.TargetState != XLiffTransUnitVariant.TranslationState.NeedsTranslation))
					{
						xLiffDoc.File.Body.TranslationsById.Add(tu.Id, target.Value);
						xLiffDoc.File.Body.ApprovalsById.Add(tu.Id,
							storingDefaultSource || (tu.TranslationStatus == TranslationStatus.Approved));
					}
				}
			}

			return xLiffDoc;
		}

		#endregion

		/// <summary>
		/// When we change ids after people have already been localizing, we have a BIG PROBLEM.
		/// This helps with the common case were we just changed the hierarchical organization of the id,
		/// that is, the parts of the id before th final '.'.
		/// </summary>
		public XLiffTransUnit GetTransUnitForOrphan(XLiffTransUnit orphan)
		{
			return File.Body.GetTransUnitForOrphan(orphan);
		}

		/// <summary>
		/// Return the total number of strings.
		/// </summary>
		public int StringCount => File.Body.StringCount;

		/// <summary>
		/// Return the number of strings that appear to be translated.
		/// </summary>
		/// <remarks>
		/// This value never changes once it is set.
		/// </remarks>
		public int NumberTranslated => File.Body.NumberTranslated;

		/// <summary>
		/// Return the number of strings that are translated and marked approved.
		/// </summary>
		/// <remarks>
		/// This value never changes once it is set.
		/// </remarks>
		public int NumberApproved => File.Body.NumberApproved;
	}


	#endregion
}