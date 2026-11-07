using System;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

using WebMConverter.Components;
using static WebMConverter.Utility;

namespace WebMConverter
{
    // The live preview sits between the "Main" box and the option tabs, in a split area the user can resize.
    // A timer watches the script that would be encoded; when it changes (filters added, edited or removed,
    // processing options toggled, the advanced script edited) and then stays the same for one more tick,
    // the preview reloads it.
    public partial class MainForm
    {
        LivePreview livePreview;
        SplitContainer livePreviewSplit;
        ToolStripButton livePreviewToggle;
        Timer livePreviewTimer;

        string livePreviewLoadedSignature;   // what the preview currently shows
        string livePreviewCandidate;         // last signature seen by the timer (debounce)

        // Locals in InitializeComponent, so look them up
        TableLayoutPanel tableMainForm => (TableLayoutPanel)Controls["tableMainForm"];
        Control tabControlOptions => Controls.Find("tabControlOptions", true)[0];

        bool livePreviewOn;
        int livePreviewHeight = LivePreviewDefaultHeight;   // height of the preview area while shown
        int minimumHeightWithoutPreview;

        const int LivePreviewDefaultHeight = 380;
        const int LivePreviewMinimumHeight = 200;

        void SetupLivePreview()
        {
            SuspendLayout();

            var tabs = tabControlOptions;
            int tabsHeight = tabs.Height;

            livePreview = new LivePreview { Dock = DockStyle.Fill, MinimumSize = Size.Empty };
            livePreview.PopoutRequested += (s, e) => buttonPreview_Click(s, e);

            livePreviewSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                FixedPanel = FixedPanel.Panel2,   // resizing the window resizes the preview, not the tabs
                SplitterWidth = 6,
                Margin = Padding.Empty,
                TabStop = false,
            };
            livePreviewSplit.Panel1.Controls.Add(livePreview);

            // Move the tabs from row 1 of the main table into the bottom half of the split
            tableMainForm.Controls.Remove(tabs);
            livePreviewSplit.Panel2.Controls.Add(tabs);
            tableMainForm.Controls.Add(livePreviewSplit, 0, 1);

            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            minimumHeightWithoutPreview = Math.Max(MinimumSize.Height, 380);
            MinimumSize = new Size(MinimumSize.Width, minimumHeightWithoutPreview);

            // Start with the preview area collapsed at the tabs' original size; SetLivePreviewVisible grows the window.
            livePreviewSplit.Panel1Collapsed = true;
            livePreviewOn = false;

            livePreviewSplit.SplitterMoved += (s, e) =>
            {
                if (livePreviewOn) livePreviewHeight = livePreviewSplit.Panel1.Height;
                PositionIndexingOverlay();
            };
            livePreviewSplit.Panel2.SizeChanged += (s, e) => PositionIndexingOverlay();
            livePreviewSplit.SizeChanged += (s, e) => PositionIndexingOverlay();

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

            ResumeLayout(true);

            UpdateLivePreviewToggleText();
            if (Properties.Settings.Default.ShowLivePreview)
                SetLivePreviewVisible(true, save: false);
            PositionIndexingOverlay();
        }

        /// <summary>The "indexing" overlay is positioned absolutely over the option tabs; keep it there.</summary>
        void PositionIndexingOverlay()
        {
            if (livePreviewSplit == null) return;
            var panel = livePreviewSplit.Panel2;
            panelHideTheOptions.Bounds = RectangleToClient(panel.RectangleToScreen(panel.ClientRectangle));
        }

        void UpdateLivePreviewToggleText() =>
            livePreviewToggle.Text = livePreviewOn ? "Hide preview" : "Show preview";

        void SetLivePreviewVisible(bool visible, bool save = true)
        {
            if (livePreviewOn == visible)
                return;

            int tabsHeight = livePreviewSplit.Panel2.Height;
            livePreviewOn = visible;
            UpdateLivePreviewToggleText();

            if (visible)
            {
                int extra = livePreviewHeight + livePreviewSplit.SplitterWidth;
                if (WindowState == FormWindowState.Normal)
                {
                    // Grow the window downwards, but stay on screen
                    var area = Screen.FromControl(this).WorkingArea;
                    int room = area.Bottom - Bottom + Math.Max(0, Top - area.Top);
                    if (extra > room && room >= LivePreviewMinimumHeight + livePreviewSplit.SplitterWidth)
                        extra = room;
                    else if (extra > room)
                        extra = LivePreviewMinimumHeight + livePreviewSplit.SplitterWidth;
                    Height += extra;
                    if (Bottom > area.Bottom)
                        Top = Math.Max(area.Top, area.Bottom - Height);
                }
                livePreviewSplit.Panel1Collapsed = false;
                livePreviewSplit.Panel1MinSize = LivePreviewMinimumHeight;
                livePreviewSplit.Panel2MinSize = 150;
                livePreviewSplit.SplitterDistance = Math.Max(LivePreviewMinimumHeight,
                    livePreviewSplit.Height - tabsHeight - livePreviewSplit.SplitterWidth);
                MinimumSize = new Size(MinimumSize.Width, minimumHeightWithoutPreview + LivePreviewMinimumHeight);
            }
            else
            {
                livePreviewHeight = livePreviewSplit.Panel1.Height;
                int removed = livePreviewHeight + livePreviewSplit.SplitterWidth;
                livePreviewSplit.Panel1MinSize = 0;
                livePreviewSplit.Panel1Collapsed = true;
                MinimumSize = new Size(MinimumSize.Width, minimumHeightWithoutPreview);
                if (WindowState == FormWindowState.Normal)
                    Height = Math.Max(MinimumSize.Height, Height - removed);

                // Free the AviSynth environment while hidden; it reloads when shown again
                livePreview.Clear("");
                livePreviewLoadedSignature = livePreviewCandidate = null;
            }

            PositionIndexingOverlay();

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
