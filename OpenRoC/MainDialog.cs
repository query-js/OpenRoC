﻿namespace oroc
{
    using liboroc;

    using System;
    using System.IO;
    using System.Windows.Forms;
    using System.Collections.Generic;
    using System.Windows.Forms.DataVisualization.Charting;

    public partial class MainDialog : Form
    {
        private ExecutorService screenshotService;
        private Metrics.Manager metricsManager;
        private SensuInterface sensuInterface;
        private ProcessDialog editProcessForm;
        private ProcessDialog addProcessForm;
        private SettingsDialog settingsForm;
        private AboutDialog aboutForm;
        private LogsDialog logsForm;

        private Series CpuChart;
        private Series GpuChart;
        private Series RamChart;

        public ProcessManager ProcessManager { get; private set; }
        private bool inhibitAutoCheck = false;

        public MainDialog()
        {
            InitializeComponent();
            logsForm = new LogsDialog();
            SetupMainDialogStatusTexts();
            HandleCreated += OnHandleCreated;
            ProcessManager = new ProcessManager();
            metricsManager = new Metrics.Manager();
            screenshotService = new ExecutorService();

            ProcessListView.SetDoubleBuffered(true);
            MetricsChart.SetDoubleBuffered(true);
        }

        private void OnHandleCreated(object sender, EventArgs e)
        {
            Log.d("Main dialog handle created.");

            List<ProcessOptions> launchOptions = Settings.Instance.Read<List<ProcessOptions>>
                (Properties.Resources.SettingsProcessListNode);

            Log.d("Launch options parsed. Number of launch processes: {0}", launchOptions.Count);

            if (Settings.Instance.IsSensuInterfaceEnabled)
            {
                SensuInterfaceUpdateTimer.Enabled = true;
                SensuInterfaceUpdateTimer.Tick += OnSensuInterfaceUpdateTimerTick;
                SetSensuInterfaceUpdateTimerInterval(Settings.Instance.SensuInterfaceTTL);

                sensuInterface = new SensuInterface(
                    ProcessManager,
                    Settings.Instance.SensuInterfaceHost,
                    Settings.Instance.SensuInterfacePort,
                    Settings.Instance.SensuInterfaceTTL);
            }

            launchOptions.ForEach((opt) => { ProcessManager.Add(opt); });
            ProcessManager.ProcessesChanged += OnProcessManagerPropertyChanged;

            CpuChart = MetricsChart.Series[nameof(CpuChart)];
            GpuChart = MetricsChart.Series[nameof(GpuChart)];
            RamChart = MetricsChart.Series[nameof(RamChart)];
        }

        public void SetSensuInterfaceUpdateTimerInterval(uint seconds)
        {
            int interval = (int)TimeSpan
                .FromSeconds(seconds * 0.8)
                .TotalMilliseconds;

            if (SensuInterfaceUpdateTimer.Interval == interval)
                return;

            Log.i("Sensu checks TTL and timeout is: {0} seconds.", seconds);
            Log.i("Sensu checks interval is: {0} miliseconds.", interval);

            SensuInterfaceUpdateTimer.Interval = interval;
            sensuInterface?.SetTTL(seconds);
        }

        private void OnSensuInterfaceUpdateTimerTick(object sender, EventArgs e)
        {
            Log.d("Sending Sensu checks.");
            sensuInterface.SendChecks();
        }

        private void OnProcessManagerPropertyChanged()
        {
            Settings.Instance.Write(Properties.Resources.SettingsProcessListNode, ProcessManager.Options);
            Settings.Instance.Save();
        }

        private void OnProcessListViewResize(object sender, EventArgs e)
        {
            if (ProcessListView.Columns.Count > 0)
                ProcessListView.AutoResizeColumn(
                    ProcessListView.Columns.Count - 1,
                    ColumnHeaderAutoResizeStyle.HeaderSize);
        }

        public void UpdateProcessList()
        {
            foreach (ListViewItem item in ProcessListView.Items)
                if (!ProcessManager.Contains(item.Text))
                    item.Remove();

            ProcessManager.Runners.ForEach(p =>
            {
                if (ProcessListView.Items.ContainsKey(p.ProcessOptions.Path))
                {
                    ProcessListView.Items[p.ProcessOptions.Path].Checked = p.State != ProcessRunner.Status.Disabled;
                    ProcessListView.Items[p.ProcessOptions.Path].SubItems[1].Text = p.GetStateString();
                }
                else
                {
                    ListViewItem item = new ListViewItem();

                    item.Checked = p.State != ProcessRunner.Status.Disabled;
                    item.Text = p.ProcessOptions.Path;
                    item.Name = p.ProcessOptions.Path;
                    item.SubItems.Add(p.State.ToString());

                    p.StateChanged += () => { Log.i("Process {0} changed state to: {1}", p.ProcessOptions.Path, p.State); };
                    p.OptionsChanged += () => { Log.d("Process changed options to: {0}", p.ProcessOptions.ToJson()); };
                    p.ProcessCrashed += () =>
                    {
                        Log.e("Process {0} crashed or stopped.", p.ProcessOptions.Path);

                        if (p.ProcessOptions.ScreenShotEnabled)
                            TakeScreenShot();
                    };

                    ProcessListView.Items.Add(item);
                }
            });
        }

        private void OnSettingsButtonClick(object sender, EventArgs e)
        {
            HandleDialogRequest(ref settingsForm);
        }

        private void OnAddButtonClick(object sender, EventArgs e)
        {
            HandleDialogRequest(ref addProcessForm);
        }

        private void OnAboutButtonClick(object sender, EventArgs e)
        {
            HandleDialogRequest(ref aboutForm);
        }

        private void OnLogButtonClick(object sender, EventArgs e)
        {
            HandleDialogRequest(ref logsForm);
        }

        private void HandleDialogRequest<T>(ref T host) where T : Form, new()
        {
            if (host == null || host.IsDisposed)
            {
                host = new T();
                host.Owner = this;

                if (host.Handle == IntPtr.Zero)
                    Log.d("Forced handle to be created.");
            }

            if (!host.Visible)
            {
                host.Show();
                host.Focus();
            }
            else
            {
                host.Focus();
                return;
            }
        }

        private void OnProcessListViewItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (e.Item.Checked == (ProcessManager.Get(e.Item.Text).State != ProcessRunner.Status.Disabled))
                return;

            if (e.Item.Checked)
                ProcessManager.Get(e.Item.Text).RestoreState();
            else
                ProcessManager.Get(e.Item.Text).State = ProcessRunner.Status.Disabled;
        }

        private void OnMainDialogUpdateTimerTick(object sender, EventArgs e)
        {
            ProcessManager.Runners.ForEach(p => p.Monitor());
            metricsManager.Update();
            UpdateProcessList();

            CpuChart?.Points.DataBindY(metricsManager.CpuSamples);
            GpuChart?.Points.DataBindY(metricsManager.GpuSamples);
            RamChart?.Points.DataBindY(metricsManager.RamSamples);
        }

        #region StatusBar text feature

        public void SetStatusBarText(Control control, string text)
        {
            control.MouseEnter += (s, e) => { StatusText.Text = text; };
            control.MouseLeave += (s, e) => { ResetStatusBarText(); };
        }

        public void SetStatusBarText(ToolStripItem control, string text)
        {
            control.MouseEnter += (s, e) => { StatusText.Text = text; };
            control.MouseLeave += (s, e) => { ResetStatusBarText(); };
        }

        public void ResetStatusBarText()
        {
            StatusText.Text = Properties.Resources.StatusTextDefaultString;
        }

        private void SetupMainDialogStatusTexts()
        {
            ResetStatusBarText();
            SetStatusBarText(AddButton, "Add a new process to monitor.");
            SetStatusBarText(DeleteButton, "Delete selected processes.");
            SetStatusBarText(SettingsButton, "Adjust OpenRoC settings.");
            SetStatusBarText(LogsButton, "Open logging history window.");
            SetStatusBarText(AboutButton, "Read about OpenRoC project.");
            SetStatusBarText(ContextMenuAddButton, "Add a new process.");
            SetStatusBarText(ContextMenuEditButton, "Edit the process.");
            SetStatusBarText(ContextMenuDeleteButton, "Delete selected processes.");
            SetStatusBarText(ContextMenuDisableButton, "Disable selected processes.");
            SetStatusBarText(ContextMenuStart, "Run selected processes if they are stopped.");
            SetStatusBarText(ContextMenuStop, "Stop selected processes if they are running.");
            SetStatusBarText(ContextMenuShow, "Attempt to bring the main Window of the selected processes to top.");
            SetStatusBarText(MetricsChart, "Overall performance graph of this machine over past few seconds.");
        }

        #endregion

        #region Start minimized support

        protected override void SetVisibleCore(bool value)
        {
            if (!IsHandleCreated)
            {
                CreateHandle();
                base.SetVisibleCore(!Settings.Instance.IsStartMinimizedEnabled);
            }
            else
            {
                base.SetVisibleCore(value);
            }
        }

        #endregion

        #region Taskbar right-click context menu event callbacks

        private void OnTaskbarContextMenuToggleViewButtonClick(object sender, EventArgs e)
        {
            if (e is MouseEventArgs && (e as MouseEventArgs).Button != MouseButtons.Left)
                return;

            Visible = !Visible;

            if (Visible)
                Focus();
        }

        private void OnTaskbarContextMenuExitButtonClick(object sender, EventArgs e)
        {
            Close();
        }

        #endregion

        #region Process right-click context menu event callbacks

        private void openFolderInExplorerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (ProcessListView.SelectedItems.Count > 1 || ProcessListView.SelectedItems.Count == 0)
            {
                openFolderInExplorerToolStripMenuItem.Visible = false;
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
            {
                FileName = Path.GetDirectoryName(ProcessListView.SelectedItems[0].Name),
                UseShellExecute = true,
                Verb = "open"
            });
        }

        private void OnContextMenuEditButtonClick(object sender, EventArgs e)
        {
            if (ProcessListView.FocusedItem == null || ProcessListView.SelectedItems.Count == 0)
            {
                MessageBox.Show(
                    "Please select a Process to edit.",
                    "No Process selected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            ProcessRunner process = ProcessManager.Get(ProcessListView.FocusedItem.Text);

            if (editProcessForm == null || editProcessForm.IsDisposed)
            {
                editProcessForm = new ProcessDialog(process.ProcessOptions);
                editProcessForm.Owner = this;
            }

            if (!editProcessForm.Visible)
                editProcessForm.Show();
            else
                editProcessForm.Focus();
        }

        private void OnContextMenuDeleteButtonClick(object sender, EventArgs e)
        {
            if (ProcessListView.SelectedItems.Count == 0)
            {
                MessageBox.Show(
                    "Please select Processes to delete.",
                    "No Processes selected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            foreach (ListViewItem item in ProcessListView.SelectedItems)
                ProcessManager.Remove(item.Text);

            UpdateProcessList();
        }

        private void OnContextMenuDisableButtonClick(object sender, EventArgs e)
        {
            if (ProcessListView.SelectedItems.Count == 0)
            {
                MessageBox.Show(
                    "Please select Processes to disable.",
                    "No Processes selected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            foreach (ListViewItem item in ProcessListView.SelectedItems)
                ProcessManager.Get(item.Text).State = ProcessRunner.Status.Disabled;
        }

        private void OnContextMenuShowClick(object sender, EventArgs e)
        {
            if (ProcessListView.SelectedItems.Count == 0)
            {
                MessageBox.Show(
                    "Please select Processes to show.",
                    "No Processes selected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            foreach (ListViewItem item in ProcessListView.SelectedItems)
                ProcessManager.Get(item.Text).BringToFront();
        }

        private void OnContextMenuStopClick(object sender, EventArgs e)
        {
            if (ProcessListView.SelectedItems.Count == 0)
            {
                MessageBox.Show(
                    "Please select Processes to stop.",
                    "No Processes selected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            foreach (ListViewItem item in ProcessListView.SelectedItems)
                ProcessManager.Get(item.Text).Stop();
        }

        private void OnContextMenuStartClick(object sender, EventArgs e)
        {
            if (ProcessListView.SelectedItems.Count == 0)
            {
                MessageBox.Show(
                    "Please select Processes to start.",
                    "No Processes selected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            foreach (ListViewItem item in ProcessListView.SelectedItems)
                ProcessManager.Get(item.Text).Start();
        }

        #endregion

        #region Drag and drop file support

        private void OnProcessListViewDragDrop(object sender, DragEventArgs e)
        {
            string[] dragged_files = e.Data.GetData(DataFormats.FileDrop, false) as string[];

            if (dragged_files == null)
                return;

            foreach (string dragged_file in dragged_files)
            {
                if (!ProcessManager.Contains(dragged_file))
                {
                    ProcessOptions opts = new ProcessOptions();

                    opts.Path = dragged_file;
                    opts.WorkingDirectory = Path.GetDirectoryName(opts.Path);

                    ProcessManager.Add(opts);
                }
            }

            UpdateProcessList();
        }

        private void OnProcessListViewDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.Copy;
        }

        #endregion

        #region Disable auto-check feature of ListView on double-click

        private void OnProcessListViewMouseUp(object sender, MouseEventArgs e)
        {
            inhibitAutoCheck = false;
        }

        private void OnProcessListViewMouseDown(object sender, MouseEventArgs e)
        {
            inhibitAutoCheck = true;
        }

        private void OnProcessListViewItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (inhibitAutoCheck)
                e.NewValue = e.CurrentValue;
        }

        #endregion

        #region ScreenShot support

        public void TakeScreenShot()
        {
            if (!Directory.Exists(Program.ScreenShotDirectory))
                Directory.CreateDirectory(Program.ScreenShotDirectory);

            Log.i("ScreenShot queued for execution.");

            screenshotService.Accept(() =>
            {
                Log.i("ScreenShot is being taken...");

                using (var picture = Pranas.ScreenshotCapture.TakeScreenshot())
                {
                    string name = Path.Combine(
                        Program.ScreenShotDirectory,
                        string.Format("{0}.png", DateTime.Now.ToFileTime()));

                    picture.Save(name);

                    Log.i("ScreenShot is saved to: {0}", name);
                }
            });
        }

        #endregion

        private void DisposeAddedComponents()
        {
            screenshotService?.Dispose();
            metricsManager?.Dispose();
            ProcessManager?.Dispose();
            sensuInterface?.Dispose();
            editProcessForm?.Dispose();
            addProcessForm?.Dispose();
            settingsForm?.Dispose();
            aboutForm?.Dispose();
            logsForm?.Dispose();

            screenshotService = null;
            metricsManager = null;
            ProcessManager = null;
            sensuInterface = null;
            editProcessForm = null;
            addProcessForm = null;
            settingsForm = null;
            aboutForm = null;
            logsForm = null;
        }
    }
}