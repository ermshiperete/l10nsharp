using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using L10NSharp.CodeReader;

namespace L10NSharp.UI
{
	/// ----------------------------------------------------------------------------------------
	internal partial class InitializationProgressDlgBase : Form
	{
		protected readonly string[] _namespaceBeginnings;
		private readonly Icon _formIcon;

		/// ------------------------------------------------------------------------------------
		protected InitializationProgressDlgBase(string appName, params string[] namespaceBeginnings)
		{
			InitializeComponent();
			Text = appName;
			_namespaceBeginnings = namespaceBeginnings;
		}

		protected InitializationProgressDlgBase(string appName, Icon formIcon, params string[] namespaceBeginnings) : this(appName, namespaceBeginnings)
		{
			_formIcon = formIcon;
		}

		/// ------------------------------------------------------------------------------------
		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			_backgroundWorker.RunWorkerAsync();
		}

		protected virtual void backgroundWorker_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
		{
		}


		private void backgroundWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
		{
			_progressBar.Value = Math.Min(e.ProgressPercentage, 100);
		}

		protected virtual void backgroundWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
		{
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);

			// a bug in Mono requires us to wait to set Icon until handle created.
			if (_formIcon != null) Icon = _formIcon;
		}
	}
}
