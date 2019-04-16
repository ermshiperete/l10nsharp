using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using L10NSharp;
using SampleApp.Properties;

namespace SampleApp
{
	static class Program
	{
		private static ILocalizationManager _localizationManager;
		/// <summary>
		/// The main entry point for the application.
		/// </summary>
		[STAThread]
		static void Main()
		{
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);

			SetUpLocalization();

			LocalizationManager.SetUILanguage(Settings.Default.UserInterfaceLanguage, false);

			Application.Run(new Form1());
			Settings.Default.Save();

			_localizationManager?.Dispose();
			_localizationManager = null;
		}

		public static void SetUpLocalization()
		{
			//your installer should have a folder where you place the Xliff files you're shipping with the program
			var directoryOfInstalledXliffFiles = "../../LocalizationFilesFromInstaller";
			Directory.CreateDirectory(directoryOfInstalledXliffFiles);

			try
			{
				// By using "null" for the following two things, we get AppData/Product.
				// Note: non-admin-rights users can't write to that folder.
				string directoryOfDefaultXliffFile = null;
				// When a user does some translation, their work goes in this directory.
				string directoryOfUserModifiedXliffFiles = null;

				//if this is your first time running the app, the library will query the OS for the
				//the default language. If it doesn't have that, it puts up a dialog listing what
				//it does have to offer.

				var theLanguageYouRememberedFromLastTime = Settings.Default.UserInterfaceLanguage;

				_localizationManager = LocalizationManager.Create(TranslationMemory.XLiff,
					theLanguageYouRememberedFromLastTime,
					"SampleApp", "SampleApp", Application.ProductVersion,
						directoryOfInstalledXliffFiles,
					"MyCompany/L10NSharpSample",
						Resources.Icon, //replace with your icon
					"sampleappLocalizations@nowhere.com", "SampleApp");

				Settings.Default.UserInterfaceLanguage = LocalizationManager.UILanguageId;
			}
			catch (Exception error)
			{
				if (Process.GetProcesses().Count(p => p.ProcessName.ToLower().Contains("SampleApp")) > 1)
				{
					MessageBox.Show("There is another copy of SampleApp already running while SampleApp was trying to set up localization.");
					Environment.FailFast("SampleApp couldn't set up localization");
				}

				if (error.Message.Contains("SampleApp.en.xlf"))
				{
					MessageBox.Show("Sorry. SampleApp is trying to set up your machine to use this new version, but something went wrong getting at the file it needs. If you restart your computer, all will be well.");

					Environment.FailFast("SampleApp couldn't set up localization");
				}

				//otherwise, we don't know what caused it.
				throw;
			}
		}

	}
}
