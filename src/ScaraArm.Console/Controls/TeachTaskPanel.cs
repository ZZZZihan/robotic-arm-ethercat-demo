using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using ScaraArm.Console.Theme;
using ScaraArm.Core.Tasks;

namespace ScaraArm.Console.Controls
{
    internal sealed class TeachTaskViewState
    {
        public bool CanEnableAll { get; set; }
        public bool AllAxesReady { get; set; }
        public bool CanTeachCurrent { get; set; }
        public bool CanEditProgram { get; set; }
        public bool CanRunTask { get; set; }
        public bool CanValidate { get; set; }
        public bool TaskRunning { get; set; }
        public int ActiveWaypointIndex { get; set; }
        public string ProgressSummary { get; set; }
    }

    /// <summary>
    /// Teach waypoint list and cyclic pick-and-place orchestration panel.
    /// Reuses CartesianMotionPlanner via TaskProgramRunner without altering lower layers.
    /// </summary>
    internal sealed class TeachTaskPanel : Panel
    {
        private readonly Button teachCurrentButton = new Button();
        private readonly Button addButton = new Button();
        private readonly Button editButton = new Button();
        private readonly Button moveUpButton = new Button();
        private readonly Button moveDownButton = new Button();
        private readonly Button deleteButton = new Button();
        private readonly Button loadButton = new Button();
        private readonly Button saveButton = new Button();

        private readonly ListView waypointList = new ListView();

        private readonly NumericUpDown velRevInput = new NumericUpDown();
        private readonly CheckBox safeTransitCheck = new CheckBox();
        private readonly NumericUpDown safeZInput = new NumericUpDown();
        private readonly NumericUpDown repeatCyclesInput = new NumericUpDown();
        private readonly CheckBox continuousCheck = new CheckBox();
        private readonly CheckBox returnHomeCheck = new CheckBox();

        private readonly Button enableAllButton = new Button();
        private readonly Button moveSelectedButton = new Button();
        private readonly Button validateButton = new Button();
        private readonly Label progressLabel = new Label();
        private readonly Button runButton = new Button();
        private readonly Button stopButton = new Button();

        private bool suppressListEvents;
        private bool suppressParamEvents;
        private bool lastCanEditProgram = true;
        private bool lastCanRunTask;
        private int activeRowIndex = -1;

        internal event EventHandler EnableAllRequested;
        internal event EventHandler TeachCurrentRequested;
        internal event EventHandler AddWaypointRequested;
        internal event Action<int> EditWaypointRequested;
        internal event Action<int> MoveUpRequested;
        internal event Action<int> MoveDownRequested;
        internal event Action<int> DeleteWaypointRequested;
        internal event Action<int, bool> WaypointEnabledToggled;
        internal event EventHandler LoadProgramRequested;
        internal event EventHandler SaveProgramRequested;
        internal event EventHandler ProgramSettingsChanged;
        internal event Action<int> MoveSelectedWaypointRequested;
        internal event EventHandler ValidateProgramRequested;
        internal event EventHandler RunTaskRequested;
        internal event EventHandler StopTaskRequested;

        // Diagnostic inspection hooks for UiSmokeTest
        internal bool TeachCurrentButtonEnabled { get { return teachCurrentButton.Enabled; } }
        internal bool AddWaypointButtonEnabled { get { return addButton.Enabled; } }
        internal bool LoadButtonEnabled { get { return loadButton.Enabled; } }
        internal bool SaveButtonEnabled { get { return saveButton.Enabled; } }
        internal bool EnableAllButtonEnabled { get { return enableAllButton.Enabled; } }
        internal bool MoveSelectedButtonEnabled { get { return moveSelectedButton.Enabled; } }
        internal bool ValidateButtonEnabled { get { return validateButton.Enabled; } }
        internal bool RunTaskButtonEnabled { get { return runButton.Enabled; } }
        internal bool StopTaskButtonEnabled { get { return stopButton.Enabled; } }
        internal int WaypointCount { get { return waypointList.Items.Count; } }

        internal int SelectedWaypointIndex
        {
            get { return waypointList.SelectedIndices.Count > 0 ? waypointList.SelectedIndices[0] : -1; }
        }

        internal TeachTaskPanel(UiTheme theme)
        {
            Dock = DockStyle.Fill;
            BackColor = Color.Transparent;

            TableLayoutPanel root = UiTheme.CreateGrid(1, 4);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            Controls.Add(root);

            root.Controls.Add(BuildToolbar(theme), 0, 0);
            root.Controls.Add(BuildWaypointList(theme), 0, 1);
            root.Controls.Add(BuildProgramSettingsRow(theme), 0, 2);
            root.Controls.Add(BuildExecutionBar(theme), 0, 3);
        }

        private Control BuildToolbar(UiTheme theme)
        {
            TableLayoutPanel bar = UiTheme.CreateGrid(9, 1);
            bar.Padding = new Padding(0, 0, 0, 6);
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));

            UiTheme.StyleButton(teachCurrentButton, "示教当前点", UiTheme.Accent, Color.White, 0);
            UiTheme.StyleButton(addButton, "添加点位", UiTheme.CardBg, UiTheme.Ink, 1);
            UiTheme.StyleButton(editButton, "编辑", UiTheme.CardBg, UiTheme.Ink, 1);
            UiTheme.StyleButton(moveUpButton, "上移", UiTheme.CardBg, UiTheme.Ink, 1);
            UiTheme.StyleButton(moveDownButton, "下移", UiTheme.CardBg, UiTheme.Ink, 1);
            UiTheme.StyleButton(deleteButton, "删除", UiTheme.CardBg, UiTheme.Danger, 1);
            UiTheme.StyleButton(loadButton, "加载…", UiTheme.CardBg, UiTheme.Ink, 1);
            UiTheme.StyleButton(saveButton, "保存…", UiTheme.CardBg, UiTheme.Ink, 1);

            teachCurrentButton.Font = theme.Bold;
            teachCurrentButton.Margin = new Padding(0, 0, 6, 0);
            addButton.Margin = new Padding(0, 0, 6, 0);
            editButton.Margin = new Padding(0, 0, 6, 0);
            moveUpButton.Margin = new Padding(0, 0, 4, 0);
            moveDownButton.Margin = new Padding(0, 0, 4, 0);
            deleteButton.Margin = new Padding(0, 0, 6, 0);
            loadButton.Margin = new Padding(0, 0, 6, 0);
            saveButton.Margin = new Padding(0);

            teachCurrentButton.Click += delegate { Raise(TeachCurrentRequested); };
            addButton.Click += delegate { Raise(AddWaypointRequested); };
            editButton.Click += delegate { RaiseSelected(EditWaypointRequested); };
            moveUpButton.Click += delegate { RaiseSelected(MoveUpRequested); };
            moveDownButton.Click += delegate { RaiseSelected(MoveDownRequested); };
            deleteButton.Click += delegate { RaiseSelected(DeleteWaypointRequested); };
            loadButton.Click += delegate { Raise(LoadProgramRequested); };
            saveButton.Click += delegate { Raise(SaveProgramRequested); };

            bar.Controls.Add(teachCurrentButton, 0, 0);
            bar.Controls.Add(addButton, 1, 0);
            bar.Controls.Add(editButton, 2, 0);
            bar.Controls.Add(moveUpButton, 3, 0);
            bar.Controls.Add(moveDownButton, 4, 0);
            bar.Controls.Add(deleteButton, 5, 0);
            bar.Controls.Add(loadButton, 7, 0);
            bar.Controls.Add(saveButton, 8, 0);
            return bar;
        }

        private Control BuildWaypointList(UiTheme theme)
        {
            waypointList.Dock = DockStyle.Fill;
            waypointList.View = View.Details;
            waypointList.FullRowSelect = true;
            waypointList.GridLines = true;
            waypointList.HideSelection = false;
            waypointList.MultiSelect = false;
            waypointList.CheckBoxes = true;
            waypointList.Font = theme.Small;
            waypointList.BorderStyle = BorderStyle.FixedSingle;
            waypointList.BackColor = UiTheme.CardBg;
            waypointList.ForeColor = UiTheme.Ink;
            waypointList.Margin = new Padding(0, 0, 0, 6);

            waypointList.Columns.Add("#", 44, HorizontalAlignment.Left);
            waypointList.Columns.Add("点位名称", 136, HorizontalAlignment.Left);
            waypointList.Columns.Add("类型", 52, HorizontalAlignment.Center);
            waypointList.Columns.Add("X (mm)", 68, HorizontalAlignment.Right);
            waypointList.Columns.Add("Y (mm)", 68, HorizontalAlignment.Right);
            waypointList.Columns.Add("Z (mm)", 68, HorizontalAlignment.Right);
            waypointList.Columns.Add("R (°)", 60, HorizontalAlignment.Right);
            waypointList.Columns.Add("保R", 40, HorizontalAlignment.Center);
            waypointList.Columns.Add("停留", 64, HorizontalAlignment.Right);

            waypointList.SelectedIndexChanged += delegate { UpdateSelectionDependentButtons(); };
            waypointList.DoubleClick += delegate
            {
                if (lastCanEditProgram) RaiseSelected(EditWaypointRequested);
            };
            waypointList.ItemCheck += delegate(object sender, ItemCheckEventArgs e)
            {
                if (suppressListEvents) return;
                if (!lastCanEditProgram)
                {
                    e.NewValue = e.CurrentValue;
                    return;
                }
                Action<int, bool> handler = WaypointEnabledToggled;
                if (handler != null) handler(e.Index, e.NewValue == CheckState.Checked);
            };

            return waypointList;
        }

        private Control BuildProgramSettingsRow(UiTheme theme)
        {
            TableLayoutPanel cfg = UiTheme.CreateGrid(5, 2);
            cfg.Padding = new Padding(0, 2, 0, 6);
            cfg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 21));
            cfg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
            cfg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 19));
            cfg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
            cfg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
            cfg.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            cfg.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

            cfg.Controls.Add(theme.CreateCaption("运行速度 (圈/秒)"), 0, 0);
            cfg.Controls.Add(theme.CreateCaption("门型安全提 Z (mm)"), 1, 0);
            cfg.Controls.Add(theme.CreateCaption("循环次数"), 2, 0);
            cfg.Controls.Add(theme.CreateCaption("循环模式"), 3, 0);
            cfg.Controls.Add(theme.CreateCaption("完成动作"), 4, 0);

            theme.ConfigureDecimalInput(velRevInput, 0.05m, 1.50m, 0.35m, 0.05m, 2);
            velRevInput.Font = theme.Regular;
            velRevInput.Margin = new Padding(0, 2, 10, 0);

            TableLayoutPanel safeZCell = UiTheme.CreateGrid(2, 1);
            safeZCell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            safeZCell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
            safeZCell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            safeZCell.Margin = new Padding(0, 2, 10, 0);
            safeTransitCheck.Text = "启用";
            safeTransitCheck.Dock = DockStyle.Fill;
            safeTransitCheck.Font = theme.Small;
            safeTransitCheck.ForeColor = UiTheme.Ink;
            safeTransitCheck.Checked = true;
            theme.ConfigureDecimalInput(safeZInput, 20.0m, 210.0m, 150.0m, 5.0m, 1);
            safeZInput.Font = theme.Regular;
            safeZInput.Margin = new Padding(0);
            safeZCell.Controls.Add(safeTransitCheck, 0, 0);
            safeZCell.Controls.Add(safeZInput, 1, 0);

            theme.ConfigureDecimalInput(repeatCyclesInput, 1m, TeachProgram.MaxCyclesLimit, 1m, 1m, 0);
            repeatCyclesInput.Font = theme.Regular;
            repeatCyclesInput.Margin = new Padding(0, 2, 10, 0);

            continuousCheck.Text = "无限循环";
            continuousCheck.Dock = DockStyle.Fill;
            continuousCheck.Font = theme.Small;
            continuousCheck.ForeColor = UiTheme.Ink;
            continuousCheck.Margin = new Padding(0, 2, 6, 0);

            returnHomeCheck.Text = "完后回零";
            returnHomeCheck.Dock = DockStyle.Fill;
            returnHomeCheck.Font = theme.Small;
            returnHomeCheck.ForeColor = UiTheme.Ink;
            returnHomeCheck.Margin = new Padding(0, 2, 0, 0);

            EventHandler onChanged = delegate
            {
                if (suppressParamEvents) return;
                safeZInput.Enabled = lastCanEditProgram && safeTransitCheck.Checked;
                repeatCyclesInput.Enabled = lastCanEditProgram && !continuousCheck.Checked;
                returnHomeCheck.Enabled = lastCanEditProgram && !continuousCheck.Checked;
                Raise(ProgramSettingsChanged);
            };
            velRevInput.ValueChanged += onChanged;
            safeTransitCheck.CheckedChanged += onChanged;
            safeZInput.ValueChanged += onChanged;
            repeatCyclesInput.ValueChanged += onChanged;
            continuousCheck.CheckedChanged += onChanged;
            returnHomeCheck.CheckedChanged += onChanged;

            cfg.Controls.Add(velRevInput, 0, 1);
            cfg.Controls.Add(safeZCell, 1, 1);
            cfg.Controls.Add(repeatCyclesInput, 2, 1);
            cfg.Controls.Add(continuousCheck, 3, 1);
            cfg.Controls.Add(returnHomeCheck, 4, 1);
            return cfg;
        }

        private Control BuildExecutionBar(UiTheme theme)
        {
            TableLayoutPanel bar = UiTheme.CreateGrid(6, 1);
            bar.Padding = new Padding(0, 2, 0, 0);
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));

            UiTheme.StyleButton(enableAllButton, "使能全部四轴", UiTheme.Success, Color.White, 0);
            UiTheme.StyleButton(moveSelectedButton, "单步到位", UiTheme.CardBg, UiTheme.Ink, 1);
            UiTheme.StyleButton(validateButton, "预检", UiTheme.CardBg, UiTheme.Ink, 1);
            UiTheme.StyleButton(runButton, "开始循环", UiTheme.Success, Color.White, 0);
            UiTheme.StyleButton(stopButton, "停止任务", UiTheme.Danger, Color.White, 0);

            enableAllButton.Font = theme.Bold;
            runButton.Font = theme.Bold;
            stopButton.Font = theme.Bold;
            enableAllButton.Margin = new Padding(0, 0, 6, 0);
            moveSelectedButton.Margin = new Padding(0, 0, 6, 0);
            validateButton.Margin = new Padding(0, 0, 8, 0);
            runButton.Margin = new Padding(8, 0, 6, 0);
            stopButton.Margin = new Padding(0);

            progressLabel.Dock = DockStyle.Fill;
            progressLabel.Font = theme.Small;
            progressLabel.ForeColor = UiTheme.Muted;
            progressLabel.TextAlign = ContentAlignment.MiddleLeft;
            progressLabel.AutoEllipsis = true;
            progressLabel.Text = "共 0 个点位";

            enableAllButton.Click += delegate { Raise(EnableAllRequested); };
            moveSelectedButton.Click += delegate { RaiseSelected(MoveSelectedWaypointRequested); };
            validateButton.Click += delegate { Raise(ValidateProgramRequested); };
            runButton.Click += delegate { Raise(RunTaskRequested); };
            stopButton.Click += delegate { Raise(StopTaskRequested); };

            bar.Controls.Add(enableAllButton, 0, 0);
            bar.Controls.Add(moveSelectedButton, 1, 0);
            bar.Controls.Add(validateButton, 2, 0);
            bar.Controls.Add(progressLabel, 3, 0);
            bar.Controls.Add(runButton, 4, 0);
            bar.Controls.Add(stopButton, 5, 0);
            return bar;
        }

        internal void BindProgram(TeachProgram program, int selectIndex)
        {
            if (program == null) return;

            suppressParamEvents = true;
            try
            {
                UiTheme.SetNumericClamped(velRevInput, (decimal)Math.Round(program.VelocityRevPerSec, 2));
                safeTransitCheck.Checked = program.UseSafeTransitZ;
                UiTheme.SetNumericClamped(safeZInput, (decimal)Math.Round(program.SafeTransitZMm, 1));
                continuousCheck.Checked = program.RepeatCycles == 0;
                if (program.RepeatCycles > 0)
                    UiTheme.SetNumericClamped(repeatCyclesInput, program.RepeatCycles);
                returnHomeCheck.Checked = program.ReturnHomeOnComplete && program.RepeatCycles > 0;
            }
            finally
            {
                suppressParamEvents = false;
            }

            suppressListEvents = true;
            waypointList.BeginUpdate();
            try
            {
                waypointList.Items.Clear();
                for (int i = 0; i < program.Waypoints.Count; i++)
                {
                    TeachWaypoint wp = program.Waypoints[i];
                    ListViewItem item = new ListViewItem((i + 1).ToString(CultureInfo.InvariantCulture));
                    item.Checked = wp.Enabled;
                    item.SubItems.Add(wp.DisplayName);
                    item.SubItems.Add(TeachWaypoint.FormatRole(wp.Role));
                    item.SubItems.Add(wp.XMm.ToString("0.1", CultureInfo.InvariantCulture));
                    item.SubItems.Add(wp.YMm.ToString("0.1", CultureInfo.InvariantCulture));
                    item.SubItems.Add(wp.ZMm.ToString("0.1", CultureInfo.InvariantCulture));
                    item.SubItems.Add(wp.RDeg.ToString("0.1", CultureInfo.InvariantCulture));
                    item.SubItems.Add(wp.KeepWorldR ? "是" : "否");
                    item.SubItems.Add(wp.DwellMs > 0 ? (wp.DwellMs.ToString(CultureInfo.InvariantCulture) + "ms") : "-");
                    item.ForeColor = wp.Enabled ? UiTheme.Ink : UiTheme.Muted;
                    waypointList.Items.Add(item);
                }

                if (selectIndex >= 0 && selectIndex < waypointList.Items.Count)
                {
                    waypointList.Items[selectIndex].Selected = true;
                    waypointList.Items[selectIndex].EnsureVisible();
                }
                ApplyActiveRowHighlight();
            }
            finally
            {
                waypointList.EndUpdate();
                suppressListEvents = false;
            }

            UpdateSelectionDependentButtons();
        }

        internal void ApplySettingsToProgram(TeachProgram program)
        {
            if (program == null) return;
            program.VelocityRevPerSec = (double)velRevInput.Value;
            program.UseSafeTransitZ = safeTransitCheck.Checked;
            program.SafeTransitZMm = (double)safeZInput.Value;
            program.RepeatCycles = continuousCheck.Checked ? 0 : (int)repeatCyclesInput.Value;
            program.ReturnHomeOnComplete = !continuousCheck.Checked && returnHomeCheck.Checked;
        }

        internal void SelectWaypoint(int index)
        {
            if (index < 0 || index >= waypointList.Items.Count) return;
            waypointList.SelectedItems.Clear();
            waypointList.Items[index].Selected = true;
            waypointList.Items[index].EnsureVisible();
            UpdateSelectionDependentButtons();
        }

        internal void UpdateView(TeachTaskViewState state)
        {
            lastCanEditProgram = state.CanEditProgram;
            lastCanRunTask = state.CanRunTask;
            activeRowIndex = state.ActiveWaypointIndex;

            teachCurrentButton.Enabled = state.CanTeachCurrent;
            teachCurrentButton.BackColor = state.CanTeachCurrent ? UiTheme.Accent : UiTheme.DisabledBg;
            teachCurrentButton.ForeColor = state.CanTeachCurrent ? Color.White : UiTheme.Muted;

            addButton.Enabled = state.CanEditProgram;
            addButton.ForeColor = state.CanEditProgram ? UiTheme.Ink : UiTheme.Muted;
            loadButton.Enabled = state.CanEditProgram;
            loadButton.ForeColor = state.CanEditProgram ? UiTheme.Ink : UiTheme.Muted;
            saveButton.Enabled = state.CanEditProgram;
            saveButton.ForeColor = state.CanEditProgram ? UiTheme.Ink : UiTheme.Muted;

            velRevInput.Enabled = state.CanEditProgram;
            safeTransitCheck.Enabled = state.CanEditProgram;
            safeZInput.Enabled = state.CanEditProgram && safeTransitCheck.Checked;
            continuousCheck.Enabled = state.CanEditProgram;
            repeatCyclesInput.Enabled = state.CanEditProgram && !continuousCheck.Checked;
            returnHomeCheck.Enabled = state.CanEditProgram && !continuousCheck.Checked;

            enableAllButton.Enabled = state.CanEnableAll;
            enableAllButton.Text = state.AllAxesReady ? "四轴已使能" : "使能全部四轴";
            enableAllButton.BackColor = state.CanEnableAll ? UiTheme.Success : UiTheme.DisabledBg;
            enableAllButton.ForeColor = state.CanEnableAll ? Color.White : UiTheme.Muted;

            validateButton.Enabled = state.CanValidate && waypointList.Items.Count > 0;
            validateButton.ForeColor = validateButton.Enabled ? UiTheme.Ink : UiTheme.Muted;

            bool hasSelected = SelectedWaypointIndex >= 0;
            bool canMoveOne = state.CanRunTask && hasSelected;
            moveSelectedButton.Enabled = canMoveOne;
            moveSelectedButton.ForeColor = canMoveOne ? UiTheme.Ink : UiTheme.Muted;

            bool canStart = state.CanRunTask && waypointList.Items.Count > 0;
            runButton.Enabled = canStart;
            runButton.BackColor = canStart ? UiTheme.Success : UiTheme.DisabledBg;
            runButton.ForeColor = canStart ? Color.White : UiTheme.Muted;

            stopButton.Enabled = state.TaskRunning;
            stopButton.BackColor = state.TaskRunning ? UiTheme.Danger : UiTheme.DisabledBg;
            stopButton.ForeColor = state.TaskRunning ? Color.White : UiTheme.Muted;

            if (!String.IsNullOrEmpty(state.ProgressSummary))
            {
                progressLabel.Text = state.ProgressSummary;
                progressLabel.ForeColor = state.TaskRunning ? UiTheme.Accent : UiTheme.Muted;
            }

            UpdateSelectionDependentButtons();
            ApplyActiveRowHighlight();
        }

        private void UpdateSelectionDependentButtons()
        {
            int idx = SelectedWaypointIndex;
            bool hasSelection = idx >= 0;
            bool canEdit = lastCanEditProgram && hasSelection;

            editButton.Enabled = canEdit;
            editButton.ForeColor = canEdit ? UiTheme.Ink : UiTheme.Muted;

            moveUpButton.Enabled = canEdit && idx > 0;
            moveUpButton.ForeColor = moveUpButton.Enabled ? UiTheme.Ink : UiTheme.Muted;

            moveDownButton.Enabled = canEdit && idx < waypointList.Items.Count - 1;
            moveDownButton.ForeColor = moveDownButton.Enabled ? UiTheme.Ink : UiTheme.Muted;

            deleteButton.Enabled = canEdit;
            deleteButton.ForeColor = canEdit ? UiTheme.Danger : UiTheme.Muted;

            bool canMoveOne = lastCanRunTask && hasSelection;
            moveSelectedButton.Enabled = canMoveOne;
            moveSelectedButton.ForeColor = canMoveOne ? UiTheme.Ink : UiTheme.Muted;
        }

        private void ApplyActiveRowHighlight()
        {
            for (int i = 0; i < waypointList.Items.Count; i++)
            {
                ListViewItem item = waypointList.Items[i];
                Color expected = (i == activeRowIndex) ? UiTheme.SuccessBg : UiTheme.CardBg;
                if (item.BackColor != expected)
                    item.BackColor = expected;
            }
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseSelected(Action<int> handler)
        {
            int idx = SelectedWaypointIndex;
            if (idx >= 0 && handler != null) handler(idx);
        }
    }
}
