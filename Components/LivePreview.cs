using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace WebMConverter.Components
{
    /// <summary>
    /// A side panel that shows the output of the current AviSynth script, with seeking and playback.
    /// AviSynth runs on a dedicated background thread; the UI only ever sees finished bitmaps.
    /// </summary>
    public class LivePreview : UserControl
    {
        #region UI

        readonly PreviewCanvas canvas;
        readonly TrackBar seekBar;
        readonly Button buttonPrevious, buttonPlay, buttonNext, buttonPopout;
        readonly Label labelPosition, labelInfo;
        readonly ToolTip toolTip;

        bool updatingSeekBar;

        /// <summary>Raised when the user asks for the full ffplay preview (with audio).</summary>
        public event EventHandler PopoutRequested;

        public LivePreview()
        {
            SuspendLayout();

            Padding = new Padding(3);
            MinimumSize = new Size(260, 200);

            var group = new GroupBox { Text = "Live preview", Dock = DockStyle.Fill, Padding = new Padding(6, 3, 6, 6) };

            canvas = new PreviewCanvas { Dock = DockStyle.Fill };
            canvas.MouseWheel += Canvas_MouseWheel;
            canvas.MouseClick += (s, e) => canvas.Focus();
            canvas.KeyDown += Canvas_KeyDown;

            labelInfo = new Label
            {
                Dock = DockStyle.Top,
                Height = 18,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                ForeColor = SystemColors.GrayText,
            };

            seekBar = new TrackBar
            {
                Dock = DockStyle.Bottom,
                TickStyle = TickStyle.None,
                AutoSize = false,
                Height = 26,
                Minimum = 0,
                Maximum = 0,
                SmallChange = 1,
                Enabled = false,
            };
            seekBar.ValueChanged += SeekBar_ValueChanged;

            var controls = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 30,
                ColumnCount = 5,
                RowCount = 1,
                Margin = Padding.Empty,
            };
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));

            buttonPrevious = MakeButton("◀❙", "Previous frame (Left arrow)");
            buttonPlay = MakeButton("▶", "Play / pause (Space)");
            buttonNext = MakeButton("❙▶", "Next frame (Right arrow)");
            buttonPopout = MakeButton("ffplay", "Open the full preview with audio in ffplay");
            buttonPrevious.Click += (s, e) => Step(-1);
            buttonNext.Click += (s, e) => Step(1);
            buttonPlay.Click += (s, e) => TogglePlay();
            buttonPopout.Click += (s, e) => PopoutRequested?.Invoke(this, EventArgs.Empty);

            labelPosition = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(FontFamily.GenericMonospace, 8.25f),
                AutoEllipsis = true,
            };

            controls.Controls.Add(buttonPrevious, 0, 0);
            controls.Controls.Add(buttonPlay, 1, 0);
            controls.Controls.Add(buttonNext, 2, 0);
            controls.Controls.Add(labelPosition, 3, 0);
            controls.Controls.Add(buttonPopout, 4, 0);

            group.Controls.Add(canvas);
            group.Controls.Add(labelInfo);
            group.Controls.Add(seekBar);
            group.Controls.Add(controls);
            Controls.Add(group);

            toolTip = new ToolTip();
            toolTip.SetToolTip(buttonPrevious, (string)buttonPrevious.Tag);
            toolTip.SetToolTip(buttonPlay, (string)buttonPlay.Tag);
            toolTip.SetToolTip(buttonNext, (string)buttonNext.Tag);
            toolTip.SetToolTip(buttonPopout, (string)buttonPopout.Tag);
            toolTip.SetToolTip(canvas, "Scroll to step through frames, Space to play");

            ResumeLayout(false);

            SetTransportEnabled(false);
            canvas.Message = "Open a video to see a preview of your edits here.";

            renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "Live preview renderer" };
            renderThread.Start();
        }

        static Button MakeButton(string text, string tip) => new Button
        {
            Text = text,
            Tag = tip,
            Dock = DockStyle.Fill,
            Margin = new Padding(1),
            FlatStyle = FlatStyle.System,
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 8.25f),
        };

        void SetTransportEnabled(bool enabled)
        {
            seekBar.Enabled = buttonPrevious.Enabled = buttonPlay.Enabled = buttonNext.Enabled = enabled;
        }

        /// <summary>True while the user can interact with the ffplay button.</summary>
        public bool PopoutEnabled
        {
            get => buttonPopout.Enabled;
            set => buttonPopout.Enabled = value;
        }

        void SeekBar_ValueChanged(object sender, EventArgs e)
        {
            if (updatingSeekBar)
                return;
            RequestFrame(seekBar.Value, stopPlayback: true);
        }

        void Canvas_MouseWheel(object sender, MouseEventArgs e)
        {
            if (!seekBar.Enabled) return;
            Step(e.Delta > 0 ? -1 : 1);
        }

        void Canvas_KeyDown(object sender, KeyEventArgs e)
        {
            if (!seekBar.Enabled) return;
            switch (e.KeyCode)
            {
                case Keys.Space: TogglePlay(); e.Handled = true; break;
                case Keys.Left: Step(-1); e.Handled = true; break;
                case Keys.Right: Step(1); e.Handled = true; break;
                case Keys.Home: RequestFrame(0, true); e.Handled = true; break;
                case Keys.End: RequestFrame(seekBar.Maximum, true); e.Handled = true; break;
            }
        }

        void Step(int delta)
        {
            int target = Math.Max(0, Math.Min(seekBar.Maximum, displayedFrame + delta));
            RequestFrame(target, stopPlayback: true);
        }

        void TogglePlay()
        {
            lock (sync)
            {
                playing = !playing;
                if (playing && displayedFrame >= frameCount - 1)
                    pendingFrame = 0; // restart from the beginning
            }
            buttonPlay.Text = playing ? "❙❙" : "▶";
            wake.Set();
        }

        void UpdatePositionLabel()
        {
            if (frameCount <= 0)
            {
                labelPosition.Text = "";
                return;
            }
            var now = FrameToTime(displayedFrame);
            var total = FrameToTime(frameCount);
            labelPosition.Text = $"{FormatTime(now)} / {FormatTime(total)}  #{displayedFrame}";
        }

        TimeSpan FrameToTime(int n) => fpsNum == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)n * fpsDen / fpsNum);

        static string FormatTime(TimeSpan t) =>
            t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.ff") : t.ToString(@"m\:ss\.ff");

        #endregion

        #region Public API (UI thread)

        /// <summary>Loads (or reloads) a script. The current position is kept when possible.</summary>
        public void LoadScript(string scriptPath)
        {
            lock (sync)
            {
                pendingScript = scriptPath;
                pendingLoad = true;
            }
            canvas.Busy = true;
            wake.Set();
        }

        /// <summary>Unloads the current script and shows <paramref name="message"/> instead.</summary>
        public void Clear(string message)
        {
            lock (sync)
            {
                pendingScript = null;
                pendingLoad = true;
                playing = false;
            }
            buttonPlay.Text = "▶";
            wake.Set();
            ShowMessage(message);
        }

        void ShowMessage(string message)
        {
            frameCount = 0;
            SetTransportEnabled(false);
            canvas.Image = null;
            canvas.Busy = false;
            canvas.Message = message;
            labelInfo.Text = "";
            UpdatePositionLabel();
        }

        void RequestFrame(int frame, bool stopPlayback)
        {
            lock (sync)
            {
                pendingFrame = frame;
                if (stopPlayback) playing = false;
            }
            if (stopPlayback) buttonPlay.Text = "▶";
            wake.Set();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (sync) quit = true;
                wake.Set();
                renderThread.Join(2000);
                canvas.Image?.Dispose();
                toolTip.Dispose();
            }
            base.Dispose(disposing);
        }

        #endregion

        #region Render thread

        readonly Thread renderThread;
        readonly object sync = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);

        // Shared state, guarded by sync
        string pendingScript;
        bool pendingLoad;
        int pendingFrame = -1;
        bool playing;
        bool quit;

        // UI-side copy of the loaded clip's properties
        int frameCount;
        uint fpsNum, fpsDen;
        int displayedFrame;

        // Latest finished frame, handed over to the UI. Only one UI callback is queued at a time.
        Bitmap handoffBitmap;
        int handoffFrame;
        bool handoffQueued;

        void RenderLoop()
        {
            AviSynthPreviewSource source = null;
            int current = 0;
            var clock = Stopwatch.StartNew();
            long playStartTicks = 0;
            int playStartFrame = 0, lastPlayed = -1;
            bool wasPlaying = false;

            try
            {
                while (true)
                {
                    bool load, play;
                    string script;
                    int frame;
                    lock (sync)
                    {
                        if (quit) break;
                        load = pendingLoad;
                        script = pendingScript;
                        pendingLoad = false;
                        frame = pendingFrame;
                        pendingFrame = -1;
                        play = playing;
                    }

                    if (load)
                    {
                        source?.Dispose();
                        source = null;
                        if (script != null)
                        {
                            try
                            {
                                source = new AviSynthPreviewSource(script);
                                current = Math.Min(current, source.FrameCount - 1);
                                if (frame < 0) frame = current;
                                var info = new ClipInfo(source);
                                PostToUI(() => OnClipLoaded(info));
                            }
                            catch (Exception ex)
                            {
                                string message = ex.Message;
                                PostToUI(() => OnClipFailed(message));
                            }
                        }
                        wasPlaying = false;
                    }

                    if (source == null)
                    {
                        wake.WaitOne();
                        continue;
                    }

                    if (play)
                    {
                        // Pace playback against the wall clock, dropping frames when rendering can't keep up.
                        double frameSeconds = source.FrameRate > 0 ? 1.0 / source.FrameRate : 1.0 / 25;
                        if (!wasPlaying || frame >= 0)
                        {
                            playStartFrame = frame >= 0 ? frame : current + 1;
                            if (playStartFrame >= source.FrameCount) playStartFrame = 0;
                            playStartTicks = clock.ElapsedTicks;
                            lastPlayed = playStartFrame - 1;
                            wasPlaying = true;
                        }

                        double elapsed = (double)(clock.ElapsedTicks - playStartTicks) / Stopwatch.Frequency;
                        int target = Math.Max(lastPlayed + 1, playStartFrame + (int)(elapsed / frameSeconds));
                        if (target >= source.FrameCount)
                        {
                            // Loop, like the ffplay preview does
                            target = playStartFrame = 0;
                            lastPlayed = -1;
                            playStartTicks = clock.ElapsedTicks;
                        }

                        if (!Render(source, target)) { StopPlaying(); continue; }
                        current = lastPlayed = target;

                        double due = (double)(current - playStartFrame + 1) * frameSeconds;
                        double now = (double)(clock.ElapsedTicks - playStartTicks) / Stopwatch.Frequency;
                        int waitMs = (int)((due - now) * 1000);
                        if (waitMs > 0)
                            wake.WaitOne(waitMs);
                        continue;
                    }

                    wasPlaying = false;

                    if (frame >= 0)
                    {
                        frame = Math.Max(0, Math.Min(source.FrameCount - 1, frame));
                        if (Render(source, frame))
                            current = frame;
                        continue; // check for newer requests before sleeping
                    }

                    wake.WaitOne();
                }
            }
            finally
            {
                source?.Dispose();
            }
        }

        void StopPlaying()
        {
            lock (sync) playing = false;
            PostToUI(() => buttonPlay.Text = "▶");
        }

        bool Render(AviSynthPreviewSource source, int frame)
        {
            Bitmap bitmap;
            try
            {
                bitmap = source.GetFrame(frame);
            }
            catch (Exception ex)
            {
                string message = ex.Message;
                PostToUI(() => OnClipFailed(message));
                return false;
            }

            bool queue;
            lock (sync)
            {
                handoffBitmap?.Dispose(); // the UI never picked it up; a newer frame replaces it
                handoffBitmap = bitmap;
                handoffFrame = frame;
                queue = !handoffQueued;
                handoffQueued = true;
            }
            if (queue)
                PostToUI(OnFrameReady);
            return true;
        }

        void PostToUI(Action action)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(action);
            }
            catch (InvalidOperationException) { } // handle destroyed while shutting down
        }

        sealed class ClipInfo
        {
            public readonly int Width, Height, FrameCount;
            public readonly uint FpsNum, FpsDen;
            public ClipInfo(AviSynthPreviewSource s)
            {
                Width = s.Width; Height = s.Height; FrameCount = s.FrameCount;
                FpsNum = s.FpsNumerator; FpsDen = s.FpsDenominator;
            }
        }

        #endregion

        #region UI callbacks

        void OnClipLoaded(ClipInfo info)
        {
            frameCount = info.FrameCount;
            fpsNum = info.FpsNum;
            fpsDen = info.FpsDen;

            updatingSeekBar = true;
            seekBar.Maximum = Math.Max(0, frameCount - 1);
            seekBar.LargeChange = Math.Max(1, frameCount / 20);
            if (displayedFrame > seekBar.Maximum) displayedFrame = seekBar.Maximum;
            seekBar.Value = displayedFrame;
            updatingSeekBar = false;

            SetTransportEnabled(true);
            canvas.Message = null;
            canvas.Busy = false;

            double fps = fpsDen == 0 ? 0 : (double)fpsNum / fpsDen;
            labelInfo.Text = $"Output: {info.Width}×{info.Height}  ·  {fps:0.###} fps  ·  {frameCount} frames  ·  {FormatTime(FrameToTime(frameCount))}";
            UpdatePositionLabel();
        }

        void OnClipFailed(string message)
        {
            message = message.Replace(@"\\?\", ""); // long-path prefix from GetCompatiblePath
            lock (sync) playing = false;
            buttonPlay.Text = "▶";
            ShowMessage(message);
            canvas.IsError = true;
        }

        void OnFrameReady()
        {
            Bitmap bitmap;
            int frame;
            lock (sync)
            {
                bitmap = handoffBitmap;
                frame = handoffFrame;
                handoffBitmap = null;
                handoffQueued = false;
            }
            if (bitmap == null)
                return;

            var old = canvas.Image;
            canvas.Image = bitmap;
            old?.Dispose();

            displayedFrame = frame;
            if (frameCount > 0)
            {
                updatingSeekBar = true;
                seekBar.Value = Math.Min(seekBar.Maximum, frame);
                updatingSeekBar = false;
            }
            UpdatePositionLabel();
        }

        #endregion

        /// <summary>Double-buffered surface that letterboxes the current frame.</summary>
        sealed class PreviewCanvas : Control
        {
            Bitmap image;
            string message;
            bool busy, isError;

            public PreviewCanvas()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
                BackColor = Color.FromArgb(24, 24, 24);
                ForeColor = Color.Gainsboro;
                TabStop = true;
            }

            public Bitmap Image { get => image; set { image = value; Invalidate(); } }
            public string Message { get => message; set { message = value; isError = false; Invalidate(); } }
            public bool Busy { get => busy; set { busy = value; Invalidate(); } }
            public bool IsError { get => isError; set { isError = value; Invalidate(); } }

            protected override bool IsInputKey(Keys keyData)
            {
                switch (keyData)
                {
                    case Keys.Left: case Keys.Right: case Keys.Space: case Keys.Home: case Keys.End:
                        return true;
                }
                return base.IsInputKey(keyData);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);

                if (image != null)
                {
                    float scale = Math.Min((float)ClientSize.Width / image.Width, (float)ClientSize.Height / image.Height);
                    int w = Math.Max(1, (int)(image.Width * scale));
                    int h = Math.Max(1, (int)(image.Height * scale));
                    var dest = new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);

                    g.InterpolationMode = scale < 1 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(image, dest);
                }

                string text = busy && message == null ? (image == null ? "Loading preview…" : null) : message;
                if (text != null)
                {
                    var box = Rectangle.Inflate(ClientRectangle, -12, -12);
                    using (var brush = new SolidBrush(isError ? Color.FromArgb(255, 140, 120) : ForeColor))
                    using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord })
                        g.DrawString(isError ? "Preview error\n\n" + text : text, Font, brush, box, format);
                }
                else if (busy)
                {
                    // Small "updating" badge over the stale frame
                    const string badge = "Updating…";
                    var size = g.MeasureString(badge, Font);
                    var r = new RectangleF(6, 6, size.Width + 8, size.Height + 4);
                    using (var back = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                        g.FillRectangle(back, r);
                    using (var brush = new SolidBrush(Color.White))
                        g.DrawString(badge, Font, brush, r.X + 4, r.Y + 2);
                }

                if (Focused)
                    ControlPaint.DrawFocusRectangle(g, ClientRectangle);
            }

            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        }
    }
}
