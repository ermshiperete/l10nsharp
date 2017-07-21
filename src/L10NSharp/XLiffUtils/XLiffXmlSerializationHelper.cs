using System;
using System.Text;
using System.IO;
using System.Xml.Serialization;
using System.Diagnostics;
using System.Xml;
using System.Reflection;

namespace L10NSharp.XLiffUtils
{
	internal static class XLiffXmlSerializationHelper
	{
		#region XLiffXmlReader class
		public const string kSilNamespace = "http://sil.org/software/XLiff";

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Custom XmlTextReader that can preserve whitespace characters (spaces, tabs, etc.)
		/// that are in XML elements. This allows us to properly handle deserialization of
		/// paragraph runs that contain runs that contain only whitespace characters.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private class XLiffXmlReader : XmlTextReader
		{
			private readonly bool m_fKeepWhitespaceInElements;

			/// --------------------------------------------------------------------------------
			/// <summary>
			/// Initializes a new instance of the <see cref="XLiffXmlReader"/> class.
			/// </summary>
			/// <param name="reader">The stream reader.</param>
			/// <param name="fKeepWhitespaceInElements">if set to <c>true</c>, the reader
			/// will preserve and return elements that contain only whitespace, otherwise
			/// these elements will be ignored during a deserialization.</param>
			/// --------------------------------------------------------------------------------
			public XLiffXmlReader(TextReader reader, bool fKeepWhitespaceInElements) :
				base(reader)
			{
				m_fKeepWhitespaceInElements = fKeepWhitespaceInElements;
			}

			/// --------------------------------------------------------------------------------
			/// <summary>
			/// Initializes a new instance of the <see cref="XLiffXmlReader"/> class.
			/// </summary>
			/// <param name="filename">The filename.</param>
			/// <param name="fKeepWhitespaceInElements">if set to <c>true</c>, the reader
			/// will preserve and return elements that contain only whitespace, otherwise
			/// these elements will be ignored during a deserialization.</param>
			/// --------------------------------------------------------------------------------
			public XLiffXmlReader(string filename, bool fKeepWhitespaceInElements) :
				base(new StreamReader(filename))
			{
				m_fKeepWhitespaceInElements = fKeepWhitespaceInElements;
			}

			/// --------------------------------------------------------------------------------
			/// <summary>
			/// Reads the next node from the stream.
			/// </summary>
			/// --------------------------------------------------------------------------------
			public override bool Read()
			{
				// Since we use this class only for deserialization, catch file not found
				// exceptions for the case when the XML file contains a !DOCTYPE declearation
				// and the specified DTD file is not found. (This is because the base class
				// attempts to open the DTD by merely reading the !DOCTYPE node from the
				// current directory instead of relative to the XML document location.)
				try
				{
					return base.Read();
				}
				catch (FileNotFoundException)
				{
					return true;
				}
			}

			/// --------------------------------------------------------------------------------
			/// <summary>
			/// Gets the type of the current node.
			/// </summary>
			/// --------------------------------------------------------------------------------
			public override XmlNodeType NodeType
			{
				get
				{
					if (m_fKeepWhitespaceInElements &&
						(base.NodeType == XmlNodeType.Whitespace || base.NodeType == XmlNodeType.SignificantWhitespace) &&
						Value != null && Value.IndexOf('\n') < 0 && Value.Trim().Length == 0)
					{
						// We found some whitespace that was most
						// likely whitespace we want to keep.
						return XmlNodeType.Text;
					}

					return base.NodeType;
				}
			}
		}

		#endregion

		#region Methods for XML serializing and deserializing data
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Serializes an object to an XML string.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static string SerializeToString<T>(T data)
		{
			StringBuilder output = new StringBuilder();
			using (StringWriter writer = new StringWriter(output))
			{
				XmlSerializerNamespaces nameSpaces = new XmlSerializerNamespaces();
				nameSpaces.Add(string.Empty, "urn:oasis:names:tc:xliff:document:1.2");
				nameSpaces.Add("sil", kSilNamespace);
				XmlSerializer serializer = new XmlSerializer(typeof(T));
				serializer.Serialize(writer, data, nameSpaces);
				writer.Close();
			}
			return (output.Length == 0 ? null : output.ToString());
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Serializes an object to a the specified file.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static bool SerializeToFile<T>(string filename, T data)
		{
			// Ensure that the file can be written, even to a new language tag subfolder.
			var folder = Path.GetDirectoryName(filename);
			if (!String.IsNullOrEmpty(folder) && !Directory.Exists(folder))
				Directory.CreateDirectory(folder);
			using (TextWriter writer = new StreamWriter(filename))
			{
				XmlSerializerNamespaces nameSpaces = new XmlSerializerNamespaces();
				nameSpaces.Add(string.Empty, "urn:oasis:names:tc:xliff:document:1.2");
				nameSpaces.Add("sil", kSilNamespace);
				XmlSerializer serializer = new XmlSerializer(typeof(T));
				serializer.Serialize(writer, data, nameSpaces);
				writer.Close();
				return true;
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Serializes the specified data to a string and writes that XML using the specified
		/// writer. Since strings in .Net are UTF16, the serialized XML data string is, of
		/// course, UTF16. Before the string is written it is converted to UTF8. So the
		/// assumption is the writer is expecting UTF8 data.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static bool SerializeDataAndWriteAsNode<T>(XmlWriter writer, T data)
		{
			string xmlData = SerializeToString(data);

			using (XmlReader reader = XmlReader.Create(new StringReader(xmlData)))
			{
				// Read past declaration and whitespace.
				while (reader.NodeType != XmlNodeType.Element && reader.Read()) { }

				if (!reader.EOF)
				{
					xmlData = reader.ReadOuterXml();
					if (xmlData.Length > 0)
						writer.WriteRaw(Environment.NewLine + xmlData + Environment.NewLine);

					return true;
				}
			}

			return false;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Deserializes XML from the specified string to an object of the specified type.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static T DeserializeFromString<T>(string input) where T : class
		{
			Exception e;
			return (DeserializeFromString<T>(input, out e));
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Deserializes XML from the specified string to an object of the specified type.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static T DeserializeFromString<T>(string input, bool fKeepWhitespaceInElements)
			where T : class
		{
			Exception e;
			return (DeserializeFromString<T>(input, fKeepWhitespaceInElements, out e));
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Deserializes XML from the specified string to an object of the specified type.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static T DeserializeFromString<T>(string input, out Exception e) where T : class
		{
			return DeserializeFromString<T>(input, false, out e);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Deserializes XML from the specified string to an object of the specified type.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		public static T DeserializeFromString<T>(string input, bool fKeepWhitespaceInElements,
			out Exception e) where T : class
		{
			T data = null;
			e = null;

			try
			{
				if (string.IsNullOrEmpty(input))
					return null;

				// Whitespace is not allowed before the XML declaration,
				// so get rid of any that exists.
				input = input.TrimStart();

				using (XLiffXmlReader reader = new XLiffXmlReader(
					new StringReader(input), fKeepWhitespaceInElements))
				{
					data = DeserializeInternal<T>(reader);
				}
			}
			catch (Exception outEx)
			{
				e = outEx;
			}

			return data;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Deserializes XML from the specified file to an object of the specified type.
		/// </summary>
		/// <typeparam name="T">The object type</typeparam>
		/// <param name="filename">The filename from which to load</param>
		/// ------------------------------------------------------------------------------------
		public static T DeserializeFromFile<T>(string filename) where T : class
		{
			Exception e;
			return DeserializeFromFile<T>(filename, false, out e);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Deserializes XML from the specified file to an object of the specified type.
		/// </summary>
		/// <typeparam name="T">The object type</typeparam>
		/// <param name="filename">The filename from which to load</param>
		/// <param name="fKeepWhitespaceInElements">if set to <c>true</c>, the reader
		/// will preserve and return elements that contain only whitespace, otherwise
		/// these elements will be ignored during a deserialization.</param>
		/// ------------------------------------------------------------------------------------
		public static T DeserializeFromFile<T>(string filename, bool fKeepWhitespaceInElements)
			where T : class
		{
			Exception e;
			return DeserializeFromFile<T>(filename, fKeepWhitespaceInElements, out e);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Deserializes XML from the specified file to an object of the specified type.
		/// </summary>
		/// <typeparam name="T">The object type</typeparam>
		/// <param name="filename">The filename from which to load</param>
		/// <param name="e">The exception generated during the deserialization.</param>
		/// ------------------------------------------------------------------------------------
		public static T DeserializeFromFile<T>(string filename, out Exception e) where T : class
		{
			return DeserializeFromFile<T>(filename, false, out e);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Deserializes XML from the specified file to an object of the specified type.
		/// </summary>
		/// <typeparam name="T">The object type</typeparam>
		/// <param name="filename">The filename from which to load</param>
		/// <param name="fKeepWhitespaceInElements">if set to <c>true</c>, the reader
		/// will preserve and return elements that contain only whitespace, otherwise
		/// these elements will be ignored during a deserialization.</param>
		/// <param name="e">The exception generated during the deserialization.</param>
		/// ------------------------------------------------------------------------------------
		public static T DeserializeFromFile<T>(string filename, bool fKeepWhitespaceInElements,
			out Exception e) where T : class
		{
			T data = null;
			e = null;

			try
			{
				if (!File.Exists(filename))
					return null;

				using (XLiffXmlReader reader = new XLiffXmlReader(
					filename, fKeepWhitespaceInElements))
				{
					data = DeserializeInternal<T>(reader);
				}
			}
			catch (Exception outEx)
			{
				e = outEx;
			}

			return data;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Deserializes an object using the specified reader.
		/// </summary>
		/// <typeparam name="T">The type of object to deserialize</typeparam>
		/// <param name="reader">The reader.</param>
		/// <returns>The deserialized object</returns>
		/// ------------------------------------------------------------------------------------
		private static T DeserializeInternal<T>(XmlReader reader)
		{
			XmlSerializer deserializer = new XmlSerializer(typeof(T));
			deserializer.UnknownAttribute += deserializer_UnknownAttribute;
			return (T)deserializer.Deserialize(reader);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Handles the UnknownAttribute event of the deserializer control.
		/// </summary>
		/// <param name="sender">The source of the event.</param>
		/// <param name="e">The <see cref="System.Xml.Serialization.XmlAttributeEventArgs"/>
		/// instance containing the event data.</param>
		/// ------------------------------------------------------------------------------------
		static void deserializer_UnknownAttribute(object sender, XmlAttributeEventArgs e)
		{
			if (e.Attr.LocalName == "lang")
			{
				// This is special handling for the xml:lang attribute that is used to specify
				// the WS for the current paragraph, run in a paragraph, etc. The XmlTextReader
				// treats xml:lang as a special case and basically skips over it (but it does
				// set the current XmlLang to the specified value). This keeps the deserializer
				// from getting xml:lang as an attribute which keeps us from getting these values.
				// The fix for this is to look at the object that is being deserialized and,
				// using reflection, see if it has any fields that have an XmlAttribute looking
				// for the xml:lang and setting it to the value we get here. (TE-8328)
				object obj = e.ObjectBeingDeserialized;
				Type type = obj.GetType();
				foreach (FieldInfo field in type.GetFields())
				{
					object[] bla = field.GetCustomAttributes(typeof(XmlAttributeAttribute), false);
					if (bla.Length == 1 && ((XmlAttributeAttribute)bla[0]).AttributeName == "xml:lang")
					{
						field.SetValue(obj, e.Attr.Value);
						return;
					}
				}

				foreach (PropertyInfo prop in type.GetProperties())
				{
					object[] bla = prop.GetCustomAttributes(typeof(XmlAttributeAttribute), false);
					if (bla.Length == 1 && ((XmlAttributeAttribute)bla[0]).AttributeName == "xml:lang")
					{
						prop.SetValue(obj, e.Attr.Value, null);
						return;
					}
				}
			}
		}

		#endregion
	}
}
