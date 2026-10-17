using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KesasInstall;

namespace AetherfrontInstallUI
{
    public static class InstallWindow
    {
        public static int Run(string helperRoot, string transactionPath, string kind, bool headless)
        {
            if (headless)
            {
                try
                {
                    using (var completion = Task.Run(() => NativeInstall.Run(helperRoot, transactionPath, kind)).GetAwaiter().GetResult())
                    {
                        if (completion == null) throw new InvalidOperationException("Installation returned no completion result.");
                    }
                    return 0;
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine("Installation failed: " + error.GetBaseException().Message);
                    return 1;
                }
            }

            int exitCode = 1;
            var uiThread = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    using (var window = new ProgressWindow(helperRoot, transactionPath, kind))
                    {
                        Application.Run(window);
                        exitCode = window.ExitCode;
                    }
                }
                catch (Exception error)
                {
                    exitCode = 1;
                    Console.Error.WriteLine("Installer window failed: " + error.GetBaseException().Message);
                    MessageBox.Show("The installer window could not finish. Keep KSP closed.\r\nSee APF/APFUpdater.log for installation details.",
                        "Aetherfront installation", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            uiThread.Join();
            return exitCode;
        }

        private sealed class ProgressWindow : Form
        {
            private readonly string helperRoot;
            private readonly string transactionPath;
            private readonly string kind;
            private readonly Label heading;
            private readonly Label status;
            private readonly Label elapsed;
            private readonly Label notice;
            private readonly ProgressBar progress;
            private readonly Button restart;
            private readonly Button close;
            private readonly System.Windows.Forms.Timer timer;
            private readonly Stopwatch clock = new Stopwatch();
            private Task<NativeInstall.Completion> worker;
            private Task restartWorker;
            private NativeInstall.Completion completion;
            private bool busy = true;
            private bool resourcesDisposed;
            public int ExitCode { get; private set; }
            private bool CanRestart
            {
                get { return completion != null && !string.Equals(kind, "rollback", StringComparison.OrdinalIgnoreCase); }
            }

            internal ProgressWindow(string helperRoot, string transactionPath, string kind)
            {
                this.helperRoot = helperRoot;
                this.transactionPath = transactionPath;
                this.kind = kind;
                ExitCode = 1;
                Text = "Aetherfront installation";
                StartPosition = FormStartPosition.CenterScreen;
                AutoScaleMode = AutoScaleMode.Font;
                Font = SystemFonts.MessageBoxFont;
                ClientSize = new Size(640, 320);
                MinimumSize = new Size(540, 320);
                MaximizeBox = false;

                heading = new Label { AutoSize = true, Text = "Installing and verifying files...", UseMnemonic = false };
                status = new Label { Dock = DockStyle.Fill, Text = "Waiting for installation to start...", UseMnemonic = false };
                elapsed = new Label { AutoSize = true, Text = "Elapsed: 00:00:00", UseMnemonic = false };
                notice = new Label { AutoSize = true, Text = "Keep KSP closed until installation is complete.", UseMnemonic = false };
                progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
                restart = new Button { AutoSize = true, Text = "Restart KSP", Enabled = false };
                close = new Button { AutoSize = true, Text = "Close", Enabled = false };
                restart.Click += RestartClicked;
                close.Click += (sender, args) => Close();
                AcceptButton = restart;
                CancelButton = close;

                var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                buttons.Controls.Add(close);
                buttons.Controls.Add(restart);
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 6 };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(heading, 0, 0);
                layout.Controls.Add(status, 0, 1);
                layout.Controls.Add(progress, 0, 2);
                layout.Controls.Add(elapsed, 0, 3);
                layout.Controls.Add(notice, 0, 4);
                layout.Controls.Add(buttons, 0, 5);
                Controls.Add(layout);
                timer = new System.Windows.Forms.Timer { Interval = 200 };
                timer.Tick += Poll;
            }

            protected override void OnShown(EventArgs args)
            {
                base.OnShown(args);
                clock.Start();
                worker = Task.Run(() => NativeInstall.Run(helperRoot, transactionPath, kind));
                timer.Start();
            }

            protected override void OnFormClosing(FormClosingEventArgs args)
            {
                base.OnFormClosing(args);
                if (busy) args.Cancel = true;
            }

            private void Poll(object sender, EventArgs args)
            {
                var time = clock.Elapsed;
                elapsed.Text = "Elapsed: " + ((long)time.TotalHours).ToString("00") + ":" + time.Minutes.ToString("00") + ":" + time.Seconds.ToString("00");
                if (restartWorker != null)
                {
                    if (!restartWorker.IsCompleted) return;
                    timer.Stop();
                    busy = false;
                    try
                    {
                        restartWorker.GetAwaiter().GetResult();
                        Close();
                    }
                    catch (Exception error)
                    {
                        ShowFailure("KSP could not be restarted.", error);
                    }
                    return;
                }

                string current = NativeInstall.Status;
                if (!string.IsNullOrWhiteSpace(current)) status.Text = current;
                if (worker == null || !worker.IsCompleted) return;
                timer.Stop();
                clock.Stop();
                busy = false;
                try
                {
                    completion = worker.GetAwaiter().GetResult();
                    if (completion == null) throw new InvalidOperationException("Installation returned no completion result.");
                    ExitCode = 0;
                    heading.Text = CanRestart ? "Installation complete" : "Restore complete";
                    status.Text = CanRestart
                        ? "All installation checks passed. Click Restart KSP when ready, or Close to leave KSP closed."
                        : "Recovery completed. Close this window; KSP has not been started.";
                    notice.Text = "KSP will not start automatically.";
                    progress.Style = ProgressBarStyle.Blocks;
                    progress.Value = 100;
                    restart.Enabled = CanRestart;
                    close.Enabled = true;
                    AcceptButton = CanRestart ? restart : close;
                    if (CanRestart) restart.Focus();
                    else close.Focus();
                }
                catch (Exception error)
                {
                    ShowFailure("Installation failed. KSP was not restarted.", error);
                }
            }

            private void RestartClicked(object sender, EventArgs args)
            {
                if (busy || !CanRestart || ExitCode != 0 || restartWorker != null) return;
                busy = true;
                restart.Enabled = close.Enabled = false;
                heading.Text = "Starting KSP...";
                status.Text = "Restart requested. Please wait.";
                restartWorker = Task.Run(() => completion.Restart());
                timer.Start();
            }

            private void ShowFailure(string message, Exception error)
            {
                ExitCode = 1;
                busy = false;
                heading.Text = "Installation needs attention";
                string detail = error.GetBaseException().Message;
                if (detail.Length > 600) detail = detail.Substring(0, 600) + "...";
                status.Text = message + "\r\n" + detail + "\r\nSee APF/APFUpdater.log for details.";
                notice.Text = "Keep KSP closed until the failure is resolved.";
                progress.Style = ProgressBarStyle.Blocks;
                progress.Value = 0;
                restart.Enabled = false;
                close.Enabled = true;
                close.Focus();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && !resourcesDisposed)
                {
                    resourcesDisposed = true;
                    if (timer != null)
                    {
                        timer.Stop();
                        timer.Dispose();
                    }
                    // Never release installation resources while a worker is still using them.
                    if (worker != null && completion == null)
                    {
                        try { completion = worker.GetAwaiter().GetResult(); }
                        catch (Exception) { }
                    }
                    if (restartWorker != null)
                    {
                        try { restartWorker.GetAwaiter().GetResult(); }
                        catch (Exception) { }
                    }
                    if (completion != null)
                    {
                        completion.Dispose();
                        completion = null;
                    }
                }
                base.Dispose(disposing);
            }
        }
    }
}
