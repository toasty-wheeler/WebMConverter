using System;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

using WebMConverter.Components;
using static WebMConverter.Utility;

namespace WebMConverter
{
    // The live preview docked on the right side of the main window.
    // A timer watches the script that would be encoded; when it changes (filters added, edited or removed,
    // processing options toggled, the advanced script edited) and then stays the same for one more tick,
    // the preview reloads it.
    public partial class MainForm
    {
        LivePreview livePreview;
        Splitter livePreviewSplitter;
        ToolStripButton livePreviewToggle;
        Timer livePreviewTimer;

        string livePreviewLoadedSignature;   // what the preview currently shows
        string livePreviewCandidate;         // last signature seen by the timer (debounce)

        // A local in InitializeComponent, so look it up
        Control tableMainForm => Controls["tableMainForm"];

        bool livePreviewOn;
        int minimumWidthWithoutPreview;

        const int LivePreviewDefaultWidth = 460;
        const int LivePreviewMinimumExtra = 280;

        void SetupLivePreview()
        {
            SuspendLayout();

            livePreview = new LivePreview { Dock = DockStyle.Right, Width = LivePreviewDefaultWidth };
            livePreview.PopoutRequested += (s, e) => buttonPreview_Click(s, e);

            livePreviewSplitter = new Splitter { Dock = DockStyle.Right, Width = 5, MinSize = livePreview.MinimumSize.Width, MinExtra = 640 };

            // Docking runs from the last control in the collection to the first, so adding these last
            // puts the preview on the far right, the splitter next to it and the main layout in what remains.
            Controls.Add(livePreviewSplitter);
            Controls.Add(livePreview);

            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            minimumWidthWithoutPreview = MinimumSize.Width;
            MinimumSize = new Size(minimumWidthWithoutPreview + LivePreviewMinimumExtra, Math.Max(MinimumSize.Height, 420));
            ClientSize = new Size(ClientSize.Width + livePreview.Width + livePreviewSplitter.Width, ClientSize.Height + 60);
            livePreviewOn = true;

            // The "indexing" overlay is positioned absolutely over the options tabs; keep it there now that the window resizes.
            tableMainForm.SizeChanged += (s, e) => PositionIndexingOverlay();
            PositionIndexingOverlay();

            livePreviewToggle = new ToolStripButton
            {
                Alignment = ToolStripItemAlignment.Right,
                DisplayStyle = ToolStripItemDisplayStyle.Text,
            };
            livePreviewToggle.Click += (s, e) => SetLivePreviewVisible(!livePreviewOn);
            toolStripStatusLabel.Owner.Items.Add(livePreviewToggle);

            livePreviewTimer = new Timer { Interval = 300 };
            livePreviewTimer.Tick += LivePreviewTimer_Tick;
            livePreviewTimer.Start();

            SetLivePreviewVisible(Properties.Settings.Default.ShowLivePreview, save: false);

            ResumeLayout(true);
        }

        void PositionIndexingOverlay()
        {
            panelHideTheOptions.Bounds = new Rectangle(
                tableMainForm.Left, tableMainForm.Top + 85,
                tableMainForm.Width, Math.Max(0, tableMainForm.Height - 87));
        }

        void SetLivePreviewVisible(bool visible, bool save = true)
        {
            bool changed = livePreviewOn != visible;
            livePreviewOn = visible;

            livePreview.Visible = livePreviewSplitter.Visible = visible;
            livePreviewToggle.Text = visible ? "Hide preview ▸" : "◂ Show preview";

            if (changed && WindowState == FormWindowState.Normal)
            {
                // Grow/shrink the window instead of squeezing the options
                int delta = livePreview.Width + livePreviewSplitter.Width;
                if (visible)
                {
                    Width += delta;
                    MinimumSize = new Size(minimumWidthWithoutPreview + LivePreviewMinimumExtra, MinimumSize.Height);
                }
                else
                {
                    MinimumSize = new Size(minimumWidthWithoutPreview, MinimumSize.Height);
                    Width -= delta;
                }
            }

            if (!visible)
            {
                // Free the AviSynth environment while hidden; it reloads when shown again
                livePreview.Clear("");
                livePreviewLoadedSignature = livePreviewCandidate = null;
            }

            if (save)
            {
                Properties.Settings.Default.ShowLivePreview = visible;
                Properties.Settings.Default.Save();
            }
        }

        void LivePreviewTimer_Tick(object sender, EventArgs e)
        {
            if (!livePreviewOn)
                return;

            livePreview.PopoutEnabled = buttonPreview.Enabled;

            string message = null, signature;
            try
            {
                signature = GetLivePreviewSignature(out message);
            }
            catch (Exception ex)
            {
                signature = null;
                message = ex.Message;
            }

            if (signature == livePreviewLoadedSignature)
            {
                livePreviewCandidate = signature;
                return;
            }

            if (signature != livePreviewCandidate)
            {
                // Changed since last tick: wait until it settles (e.g. while typing in the script box)
                livePreviewCandidate = signature;
                return;
            }

            livePreviewLoadedSignature = signature;

            if (signature == null)
            {
                livePreview.Clear(message);
                return;
            }

            try
            {
                livePreview.LoadScript(WriteLivePreviewScript());
            }
            catch (Exception ex)
            {
                livePreview.Clear(ex.Message);
            }
        }

        /// <summary>
        /// A string that changes whenever the previewed script would change, or null when there is nothing to preview.
        /// </summary>
        string GetLivePreviewSignature(out string message)
        {
            message = null;

            if (Program.InputFile == null)
            {
                message = "Open a video to see a preview of your edits here.";
                return null;
            }

            if (indexing || !textBoxIn.Enabled)
            {
                message = "Waiting for the video to finish loading…";
                return null;
            }

            if (Program.InputType == FileType.Avisynth)
                return "avs|" + Program.InputFile + "|" + File.GetLastWriteTimeUtc(Program.InputFile).Ticks;

            if (!buttonPreview.Enabled && !buttonGo.Enabled)
            {
                message = "Waiting for the video to finish loading…";
                return null;
            }

            string processing = boxAdvancedScripting.Checked
                ? textBoxProcessingScript.Text
                : BuildProcessingScript(renderCaption: false);

            // The caption is rendered to an image whose path never changes, so track the filter object too
            int caption = Filters.Caption == null ? 0 : RuntimeHelpers.GetHashCode(Filters.Caption);

            return BuildAvisynthScript(textBoxIn.Text, processing) + "\n#caption " + caption;
        }

        /// <summary>Writes the current script to a temporary file and returns an AviSynth-safe path to it.</summary>
        string WriteLivePreviewScript()
        {
            if (Program.InputType == FileType.Avisynth)
                return GetCompatiblePath(Program.InputFile);

            string processing = boxAdvancedScripting.Checked
                ? textBoxProcessingScript.Text
                : BuildProcessingScript(renderCaption: true);

            string avsFileName = GetTemporaryFile();
            WriteAvisynthScript(avsFileName, textBoxIn.Text, processing);
            return GetCompatiblePath(avsFileName);
        }

        void ShutdownLivePreview()
        {
            livePreviewTimer?.Stop();
            livePreview?.Dispose(); // stops the render thread and releases AviSynth before temp files are deleted
        }
    }
}
