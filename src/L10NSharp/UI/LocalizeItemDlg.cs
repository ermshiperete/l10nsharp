using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using L10NSharp.Properties;

namespace L10NSharp.UI
{
	/// ----------------------------------------------------------------------------------------
	public partial class LocalizeItemDlg<T> : Form
	{
		public static Font DefaultDisplayFont { get; set; }

		/// ------------------------------------------------------------------------------------
		public delegate void StringsLocalizedHandler(ILocalizationManager localizationManager);
		/// ------------------------------------------------------------------------------------
		public delegate string SetDialogSettingsHandler(LocalizeItemDlg<T> dlg);
		/// ------------------------------------------------------------------------------------
		public delegate void SaveDialogSettingsHandler(LocalizeItemDlg<T> dlg, string settings);
		/// ------------------------------------------------------------------------------------
		public static event SetDialogSettingsHandler SetDialogSettings;
		/// ------------------------------------------------------------------------------------
		public static event SaveDialogSettingsHandler SaveDialogSettings;
		/// ------------------------------------------------------------------------------------
		public static event StringsLocalizedHandler StringsLocalized;

		private readonly LocalizeItemDlgViewModel<T> _viewModel;
		private readonly ILocalizationManagerInternal<T> _callingManager;
		private int _tmpSplitDistance;
		private DateTime _timeToGiveUpLookingForTranslator;


		/// ------------------------------------------------------------------------------------
		internal static DialogResult ShowDialog(ILocalizationManagerInternal<T> callingManager,
			IComponent component, bool runInReadonlyMode, IWin32Window owner = null)
		{
			if (callingManager != null && !callingManager.CanCustomizeLocalizations)
				runInReadonlyMode = true;

			var viewModel = new LocalizeItemDlgViewModel<T>(runInReadonlyMode);

			var id = (callingManager == null ? viewModel.GetObjIdFromAnyCache(component) :
				callingManager.ComponentCache.FirstOrDefault(kvp => kvp.Key == component).Value);

			using (var dlg = new LocalizeItemDlg<T>(viewModel, id, callingManager))
			{
				if (owner != null)
					dlg.StartPosition = FormStartPosition.CenterParent;
				return dlg.ShowDialog(owner);
			}
		}

		/// ------------------------------------------------------------------------------------
		internal static DialogResult ShowDialog(ILocalizationManagerInternal<T> callingManager,
			string id, bool runInReadonlyMode, IWin32Window owner = null)
		{
			if (callingManager != null && !callingManager.CanCustomizeLocalizations)
				runInReadonlyMode = true;

			var viewModel = new LocalizeItemDlgViewModel<T>(runInReadonlyMode);

			using (var dlg = new LocalizeItemDlg<T>(viewModel, id, callingManager))
			{
				if (owner != null)
					dlg.StartPosition = FormStartPosition.CenterParent;
				return dlg.ShowDialog(owner);
			}
		}

		#region Constrution/initialization
		/// ------------------------------------------------------------------------------------
		private LocalizeItemDlg()
		{
			if (DefaultDisplayFont == null)
				DefaultDisplayFont = SystemFonts.MenuFont;

			InitializeComponent();
		}

		/// ------------------------------------------------------------------------------------
		private LocalizeItemDlg(LocalizeItemDlgViewModel<T> viewModel, string id,
			ILocalizationManagerInternal<T> callingManager) : this()
		{
			_viewModel = viewModel;
			_callingManager = callingManager;

			Initialize();

			if (id == null)
				return;

			var node = _viewModel.FindNode(id, _treeView.Nodes);
			if (node == null)
				return;

			_viewModel.CurrentNode = node;
			_treeView.SelectedNode = node;
			UpdateSingleItemView();
		}

		/// ------------------------------------------------------------------------------------
		private void Initialize()
		{
			_tableLayout.Dock = DockStyle.Fill;
			_grid.Dock = DockStyle.Fill;

			_tableLayout.BringToFront();
			_grid.BringToFront();

			_toolStripLeftSide.Renderer = new NoToolStripBorderRenderer();
			_toolStripRightSide.Renderer = new NoToolStripBorderRenderer();

			InitializeColorsAndFonts();

			GetDialogBoxSettings();

			_comboSourceLang.Items.AddRange(LocalizationManagerInternal<T>.GetUILanguages(true).ToArray());
			_comboTargetLang.Items.AddRange(LocalizationManagerInternal<T>.GetUILanguages(false).ToArray());
			_comboSourceLang.ComboBox.DisplayMember = "NativeName";
			_comboTargetLang.ComboBox.DisplayMember = "NativeName";

			UpdateLanguageSensitiveControls();
			_viewModel.LoadTreeNodes(_treeView);
			_labelCount.Text = _viewModel.GetNumberOfTranslatedItemsString();
			_grid.MultiSelect = true;
			UpdateGridSortGlyph();
		}

		/// ------------------------------------------------------------------------------------
		private void InitializeColorsAndFonts()
		{
			_textBoxSrcTranslation.BackColor = PaintingHelper.CalculateColor(SystemColors.Control, Color.White, 140);
			_textBoxSrcToolTip.BackColor = _textBoxSrcTranslation.BackColor;
			_textBoxSrcShortcutKeys.BackColor = _textBoxSrcTranslation.BackColor;
			btnCopyText.BackColor = _textBoxSrcTranslation.BackColor;
			btnCopyToolTip.BackColor = _textBoxSrcTranslation.BackColor;
			btnCopyShortcutKeys.BackColor = _textBoxSrcTranslation.BackColor;

			_grid.Font = DefaultDisplayFont;
			_grid.ColumnHeadersDefaultCellStyle.Font = DefaultDisplayFont;
			_shortcutKeysDropDown.Font = DefaultDisplayFont;
			_textBoxSrcTranslation.Font = DefaultDisplayFont;
			_colSrcToolTip.DefaultCellStyle.Font = DefaultDisplayFont;
			_colTgtToolTip.DefaultCellStyle.Font = DefaultDisplayFont;
			_treeView.Font = DefaultDisplayFont;
			_textBoxSrcToolTip.Font = new Font(DefaultDisplayFont.FontFamily,
				_textBoxSrcToolTip.Font.SizeInPoints, FontStyle.Regular);
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Get the splitter position from the caller.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);

			if (_tmpSplitDistance > 0)
				splitContainer.SplitterDistance = _tmpSplitDistance;

			_grid.AutoResizeColumnHeadersHeight();
			_grid.ColumnHeadersHeight += 8;

			_grid.Visible = (_grid.RowCount > 0);
			_tableLayout.Visible = (_grid.RowCount == 0);

			_treeView.AfterSelect -= HandleTreeViewAfterSelect;
			_treeView.AfterSelect += HandleTreeViewAfterSelect;
			_viewModel.SetNodeColors();
			Activate();

			if (_viewModel.CurrentNode == null && _treeView.Nodes.Count > 0)
			{
				_treeView.Nodes[0].Expand();
				Application.DoEvents();
				HandleTreeViewAfterSelect(null, new TreeViewEventArgs(_treeView.Nodes[0]));
			}

			if (_viewModel.BingTranslator != null)
			{
				_buttonBingTranslator.Enabled = true;
				return;
			}

			// Attempt to enable the translator button for 5 seconds.
			_timeToGiveUpLookingForTranslator = DateTime.Now.AddSeconds(5d);
			Application.Idle += HandleCheckingForTranslator;
		}

		/// ------------------------------------------------------------------------------------
		void HandleCheckingForTranslator(object sender, EventArgs e)
		{
			_buttonBingTranslator.Enabled =
				(_viewModel.BingTranslator != null && _viewModel.CurrentNode != null);

			if (_buttonBingTranslator.Enabled || DateTime.Now >= _timeToGiveUpLookingForTranslator)
				Application.Idle -= HandleCheckingForTranslator;
		}

		#endregion

		#region Misc. updating methods
		/// ------------------------------------------------------------------------------------
		private void UpdateLanguageSensitiveControls()
		{
			var ci = L10NCultureInfo.GetCultureInfo(_viewModel.SrcLangId);
			_comboSourceLang.SelectedItem = ci;
			_comboSourceLang.ToolTipText = ci.DisplayName;
			_groupBoxSrcTranslation.Text = _colSourceText.HeaderText = ci.NativeName;
			_colSrcToolTip.HeaderText = string.Format("{0} Tooltip", ci.NativeName);

			ci = L10NCultureInfo.GetCultureInfo(_viewModel.TgtLangId);
			_comboTargetLang.SelectedItem = ci;
			_comboTargetLang.ToolTipText = ci.DisplayName;
			_groupBoxTgtTranslation.Text = _colTargetText.HeaderText = ci.NativeName;
			_colTgtToolTip.HeaderText = string.Format("{0} Tooltip", ci.NativeName);
		}

		/// ------------------------------------------------------------------------------------
		private void UpdateViewAfterSavingChange()
		{
			_viewModel.SetNodeColors();
			_labelCount.Text = _viewModel.GetNumberOfTranslatedItemsString();
		}

		#endregion

		#region Methods for updating and saving changes in the single-item view
		/// ------------------------------------------------------------------------------------
		private void ResetSingleItemView()
		{
			_buttonBingTranslator.Enabled =
				(_viewModel.BingTranslator != null && _viewModel.CurrentNode != null);

			_groupBoxImage.Visible = false;

			_labelSrcToolTip.Enabled = _labelTgtToolTip.Enabled = false;
			_textBoxSrcToolTip.Enabled = _textBoxTgtToolTip.Enabled = false;
			_labelSrcShortcutKeys.Enabled = _labelTgtShortcutKeys.Enabled = false;
			_textBoxSrcShortcutKeys.Enabled = _shortcutKeysDropDown.Enabled = false;
			_groupBoxComment.Enabled = _groupBoxSrcTranslation.Enabled = _groupBoxTgtTranslation.Enabled = false;
			_labelStringId.Enabled = false;

			_labelStringIdValue.Text = string.Empty;
			_textBoxSrcTranslation.Text = string.Empty;
			_textBoxTgtTranslation.Text = string.Empty;
			_textBoxSrcToolTip.Text = string.Empty;
			_textBoxTgtToolTip.Text = string.Empty;
			_textBoxSrcShortcutKeys.Text = string.Empty;
			_textBoxComment.Text = string.Empty;
		}

		/// ------------------------------------------------------------------------------------
		private void UpdateSingleItemView()
		{
			Utils.SetWindowRedraw(this, false, false);

			ResetSingleItemView();

			if (_viewModel.CurrentNode == null || _viewModel.CurrentNode.Id == null)
			{
				Utils.SetWindowRedraw(this, true, true);
				return;
			}

			_textBoxTgtTranslation.Enabled = _textBoxTgtToolTip.Enabled = _groupBoxTgtTranslation.Enabled =
				(_viewModel.CurrentNode.Id != null && !_viewModel.TgtLangId.StartsWith(LocalizationManager.kDefaultLang));

			_groupBoxComment.Enabled = _groupBoxSrcTranslation.Enabled = (_viewModel.CurrentNode.Id != null);

			_labelStringId.Enabled = (_viewModel.CurrentNode.Id != null);
			_labelStringIdValue.Text = _viewModel.CurrentNode.Id;
			_textBoxSrcTranslation.Text = _viewModel.CurrentNodeSourceText;
			_textBoxTgtTranslation.Text = _viewModel.CurrentNodeTargetText;
			_textBoxSrcToolTip.Text = _viewModel.CurrentNodeSourceToolTip;
			_textBoxTgtToolTip.Text = _viewModel.CurrentNodeTargetToolTip;
			_textBoxSrcShortcutKeys.Text = _viewModel.CurrentNodeSourceShortcutKeys;
			_shortcutKeysDropDown.ShortcutKeysAsString = _viewModel.CurrentNodeTargetShortcutKeys;
			_textBoxComment.Text = _viewModel.CurrentNodeComment;

			//btnCopyText.Enabled = (txtSrcTranslation.Text != string.Empty);
			//btnCopyToolTip.Enabled = (txtSrcToolTip.Text != string.Empty);
			//btnCopyShortcutKeys.Enabled = (txtSrcShortcutKeys.Text != string.Empty);

			_textBoxSrcTranslation.Text = _textBoxSrcTranslation.Text.Replace(
				LocalizedStringCache.s_literalNewline, Environment.NewLine);

			_textBoxTgtTranslation.Text = _textBoxTgtTranslation.Text.Replace(
				LocalizedStringCache.s_literalNewline, Environment.NewLine);

			_textBoxTgtTranslation.SelectAll();
			var component = _viewModel.GetFirstObjectForId();

			if (component != null)
				UpdateSingleItemViewForComponent(component);

			var fnt = _viewModel.GetFontForObject(component);

			_textBoxSrcTranslation.Font = fnt;
			_textBoxTgtTranslation.Font = fnt;
			_colSourceText.DefaultCellStyle.Font = fnt;
			_colTargetText.DefaultCellStyle.Font = fnt;

			Utils.SetWindowRedraw(this, true, true);
		}

		/// ------------------------------------------------------------------------------------
		private void UpdateSingleItemViewForComponent(IComponent component)
		{
			var img = _viewModel.GetObjectsImage(component);
			if (img != null)
			{
				_pictureImage.Image = img;
				_groupBoxImage.Visible = true;
			}

			if (component is Control)
			{
				_labelSrcToolTip.Enabled = _labelTgtToolTip.Enabled = true;
				_textBoxSrcToolTip.Enabled = _textBoxTgtToolTip.Enabled = true;
			}
			else if (component is ToolStripItem)
			{
				_labelSrcToolTip.Enabled = _labelTgtToolTip.Enabled = true;
				_textBoxSrcToolTip.Enabled = _textBoxTgtToolTip.Enabled = true;

				if (component is ToolStripMenuItem)
				{
					_labelSrcShortcutKeys.Enabled = _labelTgtShortcutKeys.Enabled = true;
					_textBoxSrcShortcutKeys.Enabled = _shortcutKeysDropDown.Enabled = true;
				}
			}
		}

		/// ------------------------------------------------------------------------------------
		private void SaveChangesFromSingleItemView()
		{
			var node = _viewModel.CurrentNode;

			if (_grid.Visible || node == null)
				return;

			var locInfo = new LocalizingInfo(node.Id);
			locInfo.Text = _textBoxTgtTranslation.Text.Trim();
			locInfo.ToolTipText = _textBoxTgtToolTip.Text.Trim();
			locInfo.ShortcutKeys = _shortcutKeysDropDown.Text.Trim();
			locInfo.Comment = _textBoxComment.Text.Trim();

			_viewModel.SaveChangesInMemory(locInfo);
			UpdateViewAfterSavingChange();
		}

		#endregion

		#region Methods for handling the form closing
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Allow delegates to save some settings.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			base.OnFormClosing(e);

			SaveDialogBoxSettings();

			if (DialogResult != DialogResult.OK)
			{
				if (e.CloseReason == CloseReason.None || !_viewModel.GetIsDirty())
					return;

				var result = MessageBox.Show(this, "Would you like to save your changes?",
					Application.ProductName, MessageBoxButtons.YesNoCancel);

				if (result != DialogResult.Yes)
				{
					e.Cancel = (result == DialogResult.Cancel);
					return;
				}
			}

			if (!_grid.Visible)
				SaveChangesFromSingleItemView();
			else if (_grid.IsCurrentCellInEditMode)
				_grid.EndEdit(DataGridViewDataErrorContexts.Commit);

			if (_viewModel.Save())
			{
				foreach (var manager in _viewModel.GetModifiedManagers())
					FireStringsLocalizedEvent(manager);
			}
		}

		/// ------------------------------------------------------------------------------------
		internal static void FireStringsLocalizedEvent(ILocalizationManager lm)
		{
			StringsLocalized?.Invoke(lm);
		}

		#endregion

		#region Saving and Getting Settings
		/// ------------------------------------------------------------------------------------
		private void SaveDialogBoxSettings()
		{
			var bldr = new StringBuilder();
			bldr.AppendFormat("{0},", _viewModel.SrcLangId);
			bldr.AppendFormat("{0},", _viewModel.TgtLangId);
			bldr.AppendFormat("{0},", Bounds.X);
			bldr.AppendFormat("{0},", Bounds.Y);
			bldr.AppendFormat("{0},", Bounds.Width);
			bldr.AppendFormat("{0},", Bounds.Height);
			bldr.AppendFormat("{0},", splitContainer.SplitterDistance);
			bldr.AppendFormat("{0},", (int)_viewModel.GridSortField);

			if (_viewModel.GridSortOrder == SortOrder.None)
				bldr.AppendFormat("{0},", 0);
			else
				bldr.AppendFormat("{0},", _viewModel.GridSortOrder == SortOrder.Ascending ? 1 : 2);

			foreach (DataGridViewColumn col in _grid.Columns)
				bldr.AppendFormat("{0},", col.Width);

			var allSettings = bldr.ToString().TrimEnd(',');

			if (SaveDialogSettings != null)
				SaveDialogSettings(this, allSettings);

			Settings.Default.LocalizationDialogSettings = allSettings;
			Settings.Default.Save();
		}

		/// ------------------------------------------------------------------------------------
		private void GetDialogBoxSettings()
		{
			var allSettings = (SetDialogSettings != null ? SetDialogSettings(this) :
				Settings.Default.LocalizationDialogSettings);

			if (string.IsNullOrEmpty(allSettings))
				return;

			var settings = allSettings.Split(',');
			if (settings.Length < 9)
				return;

			try
			{
				if (_viewModel.SrcLangId == _viewModel.TgtLangId)
				{
					var srcLangId = settings[0];
					var tgtLangId = settings[1];
					_viewModel.SetLanguageIds(srcLangId, tgtLangId);
				}
			}
			catch
			{
				_viewModel.SetLanguageIds(null, null);
			}

			try
			{
				var bounds = Bounds;
				int value;

				if (int.TryParse(settings[2], out value))
					bounds.X = value;
				if (int.TryParse(settings[3], out value))
					bounds.Y = value;
				if (int.TryParse(settings[4], out value))
					bounds.Width = value;
				if (int.TryParse(settings[5], out value))
					bounds.Height = value;

				Bounds = bounds;

				if (int.TryParse(settings[6], out value))
					_tmpSplitDistance = value;
				if (int.TryParse(settings[7], out value))
					_viewModel.GridSortField = (NodeComparer<T>.SortField)value;
				if (int.TryParse(settings[8], out value))
					_viewModel.GridSortOrder = (value == 2 ? SortOrder.Descending : SortOrder.Ascending);

				int i = 9;
				foreach (DataGridViewColumn col in _grid.Columns)
				{
					if (i == settings.Length)
						return;

					if (int.TryParse(settings[i++], out value))
						col.Width = value;
				}
			}
			catch { }
		}

		#endregion

		#region Methods for kicking off a translation
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Translate the source text using Bing translating service.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private void HandleTranslatorServiceButtonClick(object sender, EventArgs e)
		{
			Dictionary<int, LocTreeNode<T>> nodesToTranslate = null;

			if (!_grid.Visible)
			{
				nodesToTranslate = new Dictionary<int,LocTreeNode<T>>();
				nodesToTranslate[0] = _treeView.SelectedNode as LocTreeNode<T>;
			}
			else
			{
				if (_grid.IsCurrentCellInEditMode)
				{
					_grid.EndEdit(DataGridViewDataErrorContexts.Commit);
					_grid.CurrentCell = _grid[0, _grid.CurrentCellAddress.Y];
				}

				nodesToTranslate = _grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index)
					.ToDictionary(r => r.Index, r => _viewModel.AllLeafNodesShowingInGrid[r.Index]);
			}

			TranslateSelectedItems(nodesToTranslate);
		}

		/// ------------------------------------------------------------------------------------
		private void TranslateSelectedItems(IDictionary<int, LocTreeNode<T>> nodesToTranslate)
		{
			if (nodesToTranslate == null || nodesToTranslate.Count == 0)
				return;

			_progressBar.Visible = true;
			_progressBar.Maximum = nodesToTranslate.Count;
			_progressBar.Value = 0;
			Cursor = Cursors.WaitCursor;

			_viewModel.Translate(nodesToTranslate, (progressBarValue, translatedNodeIndex) =>
			{
				_progressBar.Value = progressBarValue;

				if (_grid.Visible)
					_grid.InvalidateRow(translatedNodeIndex);
				else
					UpdateSingleItemView();

				_labelCount.Text = _viewModel.GetNumberOfTranslatedItemsString();
			});

			while (_viewModel.TranslatorBusy)
				Application.DoEvents();

			_progressBar.Visible = false;
			_viewModel.SetNodeColors();
			Cursor = Cursors.Default;
		}

		#endregion

		#region TreeView event handlers
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Update the string cache for the node the user is leaving.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private void HandleTreeViewBeforeSelect(object sender, TreeViewCancelEventArgs e)
		{
			SaveChangesFromSingleItemView();
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Update the right side of the dialog with the information for the node just selected.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private void HandleTreeViewAfterSelect(object sender, TreeViewEventArgs e)
		{
			var node = e.Node as LocTreeNode<T>;

			_grid.RowCount = 0;

			if (node == null)
				return;

			Cursor = Cursors.WaitCursor;
			_viewModel.CurrentNode = node;
			_grid.RowCount = _viewModel.AllLeafNodesShowingInGrid.Count;
			_grid.Visible = (_grid.RowCount > 0);
			_tableLayout.Visible = (_grid.RowCount == 0);
			UpdateSingleItemView();
			_grid.AutoResizeRows(DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders);
			Cursor = Cursors.Default;
		}

		#endregion

		#region Event handlers for for copying, moving from item to item and for searching
		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Handles the Click event of the btnCopyText control.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private void btnCopyText_Click(object sender, EventArgs e)
		{
			//txtTgtTranslation.Text = txtSrcTranslation.Text;
			//txtTgtTranslation.SelectAll();
			//txtTgtTranslation.Focus();
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Handles the Click event of the btnCopyToolTip control.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private void btnCopyToolTip_Click(object sender, EventArgs e)
		{
			//txtTgtToolTip.Text = txtSrcToolTip.Text;
			//txtTgtToolTip.SelectAll();
			//txtTgtToolTip.Focus();
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Handles the Click event of the btnCopyShortcutKeys control.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private void btnCopyShortcutKeys_Click(object sender, EventArgs e)
		{
			//shortcutKeysDropDown.ShortcutKeysAsString = txtSrcShortcutKeys.Text;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Handles the Click event of the mnuCopyAll control.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private void mnuCopyAll_Click(object sender, EventArgs e)
		{
			//txtTgtTranslation.Text = txtSrcTranslation.Text;
			//txtTgtToolTip.Text = txtSrcToolTip.Text;
			//shortcutKeysDropDown.ShortcutKeysAsString = txtSrcShortcutKeys.Text;
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Handles the KeyDown event of the tcboSearch control.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private void tcboSearch_KeyDown(object sender, KeyEventArgs e)
		{
			if (e.KeyCode != Keys.Enter)
				return;

			var text = tcboSearch.Text.Trim();
			if (text == string.Empty)
				return;

			if (tcboSearch.Items.Contains(text))
				tcboSearch.Items.Remove(text);

			tcboSearch.Items.Insert(0, text);
			tcboSearch.Text = text;
			tcboSearch.SelectAll();

			// TODO: Do search
		}

		/// ------------------------------------------------------------------------------------
		private void tcboSearch_Enter(object sender, EventArgs e)
		{
			tcboSearch.SelectAll();
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Move to the previous leaf node in the tree.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private void _buttonMovePrev_Click(object sender, EventArgs e)
		{
			//int i = _leafNodes.IndexOf(tvGroups.SelectedNode as LocTreeNode);
			//if (i - 1 >= 0)
			//    tvGroups.SelectedNode = _leafNodes[i - 1];
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Move to the next leaf node in the tree.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private void _buttonMoveNext_Click(object sender, EventArgs e)
		{
			//int i = _leafNodes.IndexOf(tvGroups.SelectedNode as LocTreeNode);
			//if (i >= 0 && i + 1 < _leafNodes.Count)
			//    tvGroups.SelectedNode = _leafNodes[i + 1];
		}

		#endregion

		#region Language ComboBox event handlers
		/// ------------------------------------------------------------------------------------
		private void HandleEditSourceBeforeTranslatingUsingBing(object sender, EventArgs e)
		{
			_viewModel.ShowEditSourceBeforeTranslatingDlg(this);
		}

		/// ------------------------------------------------------------------------------------
		private void HandleSourceLangChanged(object sender, EventArgs e)
		{
			SaveChangesFromSingleItemView();
			_viewModel.SrcLangId = ((L10NCultureInfo)_comboSourceLang.SelectedItem).Name;
			UpdateLanguageSensitiveControls();
			UpdateSingleItemView();
			_viewModel.SetNodeColors();
		}

		/// ------------------------------------------------------------------------------------
		private void HandleTargetLangChanged(object sender, EventArgs e)
		{
			SaveChangesFromSingleItemView();
			_viewModel.TgtLangId = ((L10NCultureInfo)_comboTargetLang.SelectedItem).Name;
			UpdateLanguageSensitiveControls();
			UpdateSingleItemView();
			_viewModel.SetNodeColors();
		}

		#endregion

		#region Grid event handlers
		/// ------------------------------------------------------------------------------------
		private void HandleGridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
		{
			if (e.ColumnIndex == -1 && e.RowIndex == -1)
			{
				// Draw an icon in the upper, left cell where the user can click to select all the rows.
				e.Paint(e.CellBounds, e.PaintParts);
				e.Handled = true;
				var img = Resources.SelectAllRows;
				var rc = new Rectangle(0, 0, img.Width, img.Height);
				rc.X = e.CellBounds.X + (int)(Math.Round((e.CellBounds.Width - img.Width) / 2f, MidpointRounding.AwayFromZero));
				rc.Y = e.CellBounds.Y + (int)(Math.Round((e.CellBounds.Height - img.Height) / 2f, MidpointRounding.AwayFromZero));
				e.Graphics.DrawImage(img, rc);
			}
		}

		/// ------------------------------------------------------------------------------------
		private void HandleGridCellMouseEnter(object sender, DataGridViewCellEventArgs e)
		{
			if (e.ColumnIndex == -1 && e.RowIndex == -1)
			{
				var pt = PointToClient(MousePosition);
				pt.Y += (SystemInformation.CaptionHeight + Cursor.Size.Height - 3);
				_tooltip.Show("Select All Rows", this, pt);
			}
		}

		/// ------------------------------------------------------------------------------------
		private void HandleGridCellMouseLeave(object sender, DataGridViewCellEventArgs e)
		{
			if (e.ColumnIndex == -1 && e.RowIndex == -1)
				_tooltip.Hide(_grid);
		}

		/// ------------------------------------------------------------------------------------
		private void HandleGridCurrentRowChanged(object sender, EventArgs e)
		{
			_viewModel.SetCurrentNodeFromGridIndex(_grid.CurrentCellAddress.Y);
		}

		/// ------------------------------------------------------------------------------------
		private void HandleGridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
		{
			var gridNodes = _viewModel.AllLeafNodesShowingInGrid;

			if (gridNodes == null || e.RowIndex >= gridNodes.Count || e.ColumnIndex < 1 || e.ColumnIndex > 2)
				return;

			var node = gridNodes[e.RowIndex];
			var objInfo = node.Manager.ComponentCache.FirstOrDefault(kvp => kvp.Value == node.Id);
			e.CellStyle.Font = _viewModel.GetFontForObject(objInfo.Key);
		}

		/// ------------------------------------------------------------------------------------
		private void HandleGridCellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
		{
			var parentNode = _treeView.SelectedNode as LocTreeNode<T>;

			switch (e.ColumnIndex)
			{
				case 0: e.Value = _viewModel.GetStringIdForGridIndex(e.RowIndex, parentNode.Name); break;
				case 1: e.Value = _viewModel.GetSourceTextForGridIndex(e.RowIndex); break;
				case 2: e.Value = _viewModel.GetTargetTextForGridIndex(e.RowIndex); break;
				case 3: e.Value = _viewModel.GetSourceToolTipForGridIndex(e.RowIndex); break;
				case 4: e.Value = _viewModel.GetTargetToolTipForGridIndex(e.RowIndex); break;
				case 5: e.Value = _viewModel.GetCommentForGridIndex(e.RowIndex); break;
				default: e.Value = null; break;
			}
		}

		/// ------------------------------------------------------------------------------------
		private void HandleGridCellValuePushed(object sender, DataGridViewCellValueEventArgs e)
		{
			if (_viewModel.AllLeafNodesShowingInGrid == null || e.RowIndex >= _viewModel.AllLeafNodesShowingInGrid.Count)
				return;

			var locInfo = new LocalizingInfo(_viewModel.CurrentNode.Id);
			locInfo.UpdateFields = UpdateFields.None;
			switch (e.ColumnIndex)
			{
				case 2:
					locInfo.Text = (e.Value as string) ?? string.Empty;
					locInfo.UpdateFields = UpdateFields.Text;
					break;

				case 4:
					locInfo.ToolTipText = (e.Value as string) ?? string.Empty;
					locInfo.UpdateFields = UpdateFields.ToolTip;
					break;

				case 5:
					locInfo.Comment = (e.Value as string) ?? string.Empty;
					locInfo.UpdateFields = UpdateFields.Comment;
					break;

				default: return;
			}

			_viewModel.SaveChangesInMemory(locInfo);
			UpdateViewAfterSavingChange();
		}

		/// ------------------------------------------------------------------------------------
		private void HandleColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
		{
			var sortColumn = (int)_viewModel.GridSortField;

			if (sortColumn != e.ColumnIndex)
				_viewModel.GridSortOrder = SortOrder.Ascending;
			else
			{
				_viewModel.GridSortOrder = (_grid.Columns[sortColumn].HeaderCell.SortGlyphDirection == SortOrder.Ascending ?
					SortOrder.Descending : SortOrder.Ascending);
			}

			_viewModel.GridSortField = (NodeComparer<T>.SortField)e.ColumnIndex;
			UpdateGridSortGlyph();
			_viewModel.SortGridNodes();
			_viewModel.SetCurrentNodeFromGridIndex(_grid.CurrentCellAddress.Y);
			_grid.Invalidate();
		}

		/// ------------------------------------------------------------------------------------
		private void UpdateGridSortGlyph()
		{
			var sortColumn = (int)_viewModel.GridSortField;

			foreach (DataGridViewColumn col in _grid.Columns)
				col.HeaderCell.SortGlyphDirection = SortOrder.None;

			_grid.Columns[sortColumn].HeaderCell.SortGlyphDirection = _viewModel.GridSortOrder;
		}

		#endregion

		/// ------------------------------------------------------------------------------------
		private void _buttonFallbackLanguages_Click(object sender, EventArgs e)
		{
			// TODO: Uncomment this button and test to see if the fallback system works.
			using (var dlg = new FallbackLanguagesDlg<T>())
			{
				if (dlg.ShowDialog(this) == DialogResult.OK)
					LocalizationManagerInternal<T>.FallbackLanguageIds = dlg.FallbackLanguageIds.ToList();
			}
		}

		/// ------------------------------------------------------------------------------------
		/// <summary>
		/// Paint a faint line between the source translation and the source tooltip and
		/// shortcut keys text boxes.
		/// </summary>
		/// ------------------------------------------------------------------------------------
		private void HandleGroupSrcTranslationPaint(object sender, PaintEventArgs e)
		{
			using (var pen = new Pen(PaintingHelper.CalculateColor(Color.Black, Color.White, 45)))
			{
				e.Graphics.DrawLine(pen, _textBoxSrcTranslation.Left, _textBoxSrcTranslation.Bottom + 5,
					_textBoxSrcTranslation.Right - 1, _textBoxSrcTranslation.Bottom + 5);
			}
		}

		private void _howToDistribute_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
		{
			using (var dlg = new HowToDistributeDialog(LocalizationManager.EmailForSubmissions,
				_callingManager.GetPathForLanguage(_viewModel.TgtLangId, true)))
			{
				dlg.ShowDialog();
			}
		}

		private void LocalizeItemDlg_Load(object sender, EventArgs e)
		{

		}
	}
}
