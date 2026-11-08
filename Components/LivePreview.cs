using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Threading;
using System.Windows.Forms;

namespace WebMConverter.Components
{
    /// <summary>
    /// A panel that shows the output of the current AviSynth script, with seeking and playback.
    /// AviSynth runs on a dedicated background thread; the UI only ever sees finished bitmaps.
    /// </summary>
    public class LivePreview : UserControl
    {
        #region UI

        const string CanvasTip = "Scroll to step through frames, Space to play";

        readonly PreviewCanvas canvas;
        readonly Label labelInfo;
        readonly ToolTip toolTip;

        bool transportEnabled;

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
            canvas.KeyDown += Canvas_KeyDown;
            canvas.SeekRequested += (s, frame) => RequestFrame(frame, stopPlayback: true);
            canvas.PreviousClicked += (s, e) => Step(-1);
            canvas.PlayClicked += (s, e) => TogglePlay();
            canvas.NextClicked += (s, e) => Step(1);
            canvas.PopoutClicked += (s, e) => PopoutRequested?.Invoke(this, EventArgs.Empty);
            canvas.HoverTipChanged += (s, e) => toolTip.SetToolTip(canvas, canvas.HoverTip ?? CanvasTip);

            labelInfo = new Label
            {
                Dock = DockStyle.Top,
                Height = 18,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                ForeColor = SystemColors.GrayText,
            };

            group.Controls.Add(canvas);
            group.Controls.Add(labelInfo);
            Controls.Add(group);

            toolTip = new ToolTip();
            toolTip.SetToolTip(canvas, CanvasTip);

            ResumeLayout(false);

            SetTransportEnabled(false);
            canvas.Message = "Open a video to see a preview of your edits here.";

            renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "Live preview renderer" };
            renderThread.Start();
        }

        void SetPlayGlyph(bool isPlaying) => canvas.Playing = isPlaying;

        void SetTransportEnabled(bool enabled)
        {
            transportEnabled = enabled;
            canvas.TransportEnabled = enabled;
        }

        /// <summary>True while the user can interact with the ffplay button.</summary>
        public bool PopoutEnabled
        {
            get => canvas.PopoutEnabled;
            set => canvas.PopoutEnabled = value;
        }

        int LastFrame => Math.Max(0, frameCount - 1);

        void Canvas_MouseWheel(object sender, MouseEventArgs e)
        {
            if (!transportEnabled) return;
            Step(e.Delta > 0 ? -1 : 1);
        }

        void Canvas_KeyDown(object sender, KeyEventArgs e)
        {
            if (!transportEnabled) return;
            switch (e.KeyCode)
            {
                case Keys.Space: TogglePlay(); e.Handled = true; break;
                case Keys.Left: Step(-1); e.Handled = true; break;
                case Keys.Right: Step(1); e.Handled = true; break;
                case Keys.Home: RequestFrame(0, true); e.Handled = true; break;
                case Keys.End: RequestFrame(LastFrame, true); e.Handled = true; break;
            }
        }

        void Step(int delta)
        {
            int target = Math.Max(0, Math.Min(LastFrame, displayedFrame + delta));
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
            SetPlayGlyph(playing);
            wake.Set();
        }

        void UpdatePositionLabel()
        {
            if (frameCount <= 0)
            {
                canvas.TimeText = "";
                return;
            }
            var now = FrameToTime(displayedFrame);
            var total = FrameToTime(frameCount);
            canvas.TimeText = $"{FormatTime(now)} / {FormatTime(total)}  #{displayedFrame}";
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
            SetPlayGlyph(false);
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
            if (stopPlayback) SetPlayGlyph(false);
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
            PostToUI(() => SetPlayGlyph(false));
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

            if (displayedFrame > LastFrame) displayedFrame = LastFrame;
            canvas.SeekMaximum = LastFrame;
            canvas.SeekValue = displayedFrame;

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
            SetPlayGlyph(false);
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
                canvas.SeekValue = Math.Min(LastFrame, frame);
            UpdatePositionLabel();
        }

        #endregion

        /// <summary>
        /// Double-buffered surface that letterboxes the current frame and draws the playback controls
        /// over the bottom of the video. The controls fade to barely visible while the mouse is away from them.
        /// </summary>
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

                timeFont = new Font(FontFamily.GenericMonospace, 8.25f);
                buttonFont = new Font(SystemFonts.MessageBoxFont.FontFamily, 8.25f);
                fadeTimer.Tick += FadeTimer_Tick;
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

            #region Overlay controls

            enum Part { None, Seek, Previous, Play, Next, Popout }
            enum Glyph { Previous, Play, Pause, Next }

            public event EventHandler<int> SeekRequested;
            public event EventHandler PreviousClicked, PlayClicked, NextClicked, PopoutClicked, HoverTipChanged;

            const int BarHeight = 50;      // height of the control row at the bottom of the video
            const int ShadeExtra = 34;     // the dark gradient starts this far above the row
            const int NearDistance = 60;   // how far above the shade the mouse counts as "near"
            const float DimAlpha = 0.14f;  // opacity of the controls while the mouse is away
            const float FadeInStep = 0.16f, FadeOutStep = 0.045f; // per timer tick

            readonly System.Windows.Forms.Timer fadeTimer = new System.Windows.Forms.Timer { Interval = 15 };
            readonly Font timeFont, buttonFont;

            bool transportEnabled, popoutEnabled = true, playing, near;
            int seekMaximum, seekValue, dragValue = -1;
            string timeText = "";
            Part hover = Part.None, pressed = Part.None;
            float alpha = DimAlpha;

            public bool TransportEnabled { get => transportEnabled; set { transportEnabled = value; InvalidateOverlay(); } }
            public bool PopoutEnabled { get => popoutEnabled; set { popoutEnabled = value; InvalidateOverlay(); } }
            public bool Playing { get => playing; set { playing = value; InvalidateOverlay(); } }
            public int SeekMaximum { get => seekMaximum; set { seekMaximum = Math.Max(0, value); InvalidateOverlay(); } }
            public int SeekValue { get => seekValue; set { seekValue = value; InvalidateOverlay(); } }
            public string TimeText { get => timeText; set { timeText = value ?? ""; InvalidateOverlay(); } }

            /// <summary>Tooltip for the overlay control under the mouse, or null.</summary>
            public string HoverTip
            {
                get
                {
                    switch (hover)
                    {
                        case Part.Seek: return "Click or drag to seek (Home / End jump to the ends)";
                        case Part.Previous: return "Previous frame (Left arrow)";
                        case Part.Play: return "Play / pause (Space)";
                        case Part.Next: return "Next frame (Right arrow)";
                        case Part.Popout: return "Open the full preview with audio in ffplay";
                        default: return null;
                    }
                }
            }

            struct OverlayLayout
            {
                public Rectangle Shade, Previous, Play, Next, Popout, TrackHit, TimeText;
                public int TrackLeft, TrackRight, TrackY;
            }

            OverlayLayout GetLayout()
            {
                int w = ClientSize.Width, h = ClientSize.Height;
                int cx = w / 2, cy = h - BarHeight / 2;
                var l = new OverlayLayout
                {
                    Shade = new Rectangle(0, h - BarHeight - ShadeExtra, w, BarHeight + ShadeExtra),
                    Play = Circle(cx, cy, 18),
                    Previous = Circle(cx - 44, cy, 14),
                    Next = Circle(cx + 44, cy, 14),
                    Popout = new Rectangle(w - 12 - 56, cy - 12, 56, 24),
                    TrackLeft = 16,
                    TrackY = cy + 8,
                };
                // The seek bar fills the left side, up to the transport buttons.
                l.TrackRight = Math.Max(l.TrackLeft + 24, l.Previous.Left - 22);
                l.TimeText = new Rectangle(l.TrackLeft - 3, cy - 18, l.TrackRight - l.TrackLeft + 6, 16);
                l.TrackHit = Rectangle.FromLTRB(l.TrackLeft - 8, l.TrackY - 11, l.TrackRight + 8, l.TrackY + 11);
                return l;
            }

            static Rectangle Circle(int cx, int cy, int r) => new Rectangle(cx - r, cy - r, r * 2, r * 2);

            Part HitTest(OverlayLayout l, Point p)
            {
                if (l.Popout.Contains(p)) return Part.Popout;
                if (l.Play.Contains(p)) return Part.Play;
                if (l.Previous.Contains(p)) return Part.Previous;
                if (l.Next.Contains(p)) return Part.Next;
                if (l.TrackHit.Contains(p)) return Part.Seek;
                return Part.None;
            }

            bool IsEnabled(Part part)
            {
                switch (part)
                {
                    case Part.None: return false;
                    case Part.Popout: return popoutEnabled;
                    default: return transportEnabled;
                }
            }

            void InvalidateOverlay()
            {
                if (IsHandleCreated)
                    Invalidate(GetLayout().Shade);
            }

            void SetNear(bool value)
            {
                if (near == value) return;
                near = value;
                fadeTimer.Start();
            }

            void FadeTimer_Tick(object sender, EventArgs e)
            {
                float target = near ? 1f : DimAlpha;
                alpha = target > alpha ? Math.Min(target, alpha + FadeInStep) : Math.Max(target, alpha - FadeOutStep);
                if (alpha == target)
                    fadeTimer.Stop();
                InvalidateOverlay();
            }

            void SetHover(Part part)
            {
                if (hover == part) return;
                hover = part;
                Cursor = IsEnabled(part) ? Cursors.Hand : Cursors.Default;
                InvalidateOverlay();
                HoverTipChanged?.Invoke(this, EventArgs.Empty);
            }

            void SeekTo(OverlayLayout l, int x)
            {
                int value = 0;
                if (seekMaximum > 0)
                {
                    double t = (double)(x - l.TrackLeft) / Math.Max(1, l.TrackRight - l.TrackLeft);
                    value = (int)Math.Round(Math.Max(0, Math.Min(1, t)) * seekMaximum);
                }
                if (value == dragValue) return;
                dragValue = value;
                InvalidateOverlay();
                SeekRequested?.Invoke(this, value);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                var l = GetLayout();
                if (pressed == Part.Seek)
                    SeekTo(l, e.X);
                SetNear(pressed != Part.None || e.Y >= l.Shade.Top - NearDistance);
                SetHover(pressed != Part.None ? pressed : HitTest(l, e.Location));
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (pressed == Part.None)
                {
                    SetNear(false);
                    SetHover(Part.None);
                }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                Focus();
                if (e.Button != MouseButtons.Left) return;

                var l = GetLayout();
                var part = HitTest(l, e.Location);
                if (!IsEnabled(part)) return;

                pressed = part;
                Capture = true;
                if (part == Part.Seek)
                    SeekTo(l, e.X);
                InvalidateOverlay();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button != MouseButtons.Left || pressed == Part.None) return;

                var part = pressed;
                pressed = Part.None;
                Capture = false;
                var l = GetLayout();

                if (part == Part.Seek)
                {
                    if (dragValue >= 0) seekValue = dragValue; // avoid a jump back until the frame arrives
                    dragValue = -1;
                }
                else if (HitTest(l, e.Location) == part)
                {
                    switch (part)
                    {
                        case Part.Previous: PreviousClicked?.Invoke(this, EventArgs.Empty); break;
                        case Part.Play: PlayClicked?.Invoke(this, EventArgs.Empty); break;
                        case Part.Next: NextClicked?.Invoke(this, EventArgs.Empty); break;
                        case Part.Popout: PopoutClicked?.Invoke(this, EventArgs.Empty); break;
                    }
                }

                bool inside = ClientRectangle.Contains(e.Location);
                SetNear(inside && e.Y >= l.Shade.Top - NearDistance);
                SetHover(inside ? HitTest(l, e.Location) : Part.None);
                InvalidateOverlay();
            }

            Color Ink(float opacity) => Color.FromArgb(Math.Max(0, Math.Min(255, (int)(255 * alpha * opacity))), Color.White);

            void PaintOverlay(Graphics g)
            {
                var l = GetLayout();
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                // Dark gradient behind the controls so they read on bright video.
                var brushRect = new Rectangle(l.Shade.X, l.Shade.Y - 1, l.Shade.Width, l.Shade.Height + 2);
                using (var shade = new LinearGradientBrush(brushRect, Color.FromArgb(0, 0, 0, 0),
                    Color.FromArgb((int)(190 * alpha), 0, 0, 0), LinearGradientMode.Vertical))
                    g.FillRectangle(shade, l.Shade);

                // Seek bar (left)
                float dim = transportEnabled ? 1f : 0.4f;
                using (var track = new SolidBrush(Ink(0.3f * dim)))
                    g.FillRectangle(track, l.TrackLeft, l.TrackY - 2, l.TrackRight - l.TrackLeft, 4);
                if (transportEnabled)
                {
                    int value = dragValue >= 0 ? dragValue : seekValue;
                    float t = seekMaximum > 0 ? Math.Max(0, Math.Min(1, (float)value / seekMaximum)) : 0;
                    float x = l.TrackLeft + t * (l.TrackRight - l.TrackLeft);
                    using (var fill = new SolidBrush(Color.FromArgb((int)(255 * alpha), 70, 160, 255)))
                        g.FillRectangle(fill, l.TrackLeft, l.TrackY - 2, x - l.TrackLeft, 4);
                    float r = hover == Part.Seek || pressed == Part.Seek ? 7 : 5;
                    using (var thumb = new SolidBrush(Ink(1)))
                        g.FillEllipse(thumb, x - r, l.TrackY - r, r * 2, r * 2);
                }
                if (timeText.Length > 0)
                    using (var brush = new SolidBrush(Ink(0.9f)))
                    using (var format = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                        g.DrawString(timeText, timeFont, brush, l.TimeText, format);

                // Transport buttons (centre)
                DrawRoundButton(g, l.Previous, Part.Previous, Glyph.Previous, 10, false);
                DrawRoundButton(g, l.Play, Part.Play, playing ? Glyph.Pause : Glyph.Play, 14, true);
                DrawRoundButton(g, l.Next, Part.Next, Glyph.Next, 10, false);

                // ffplay (right)
                float popDim = popoutEnabled ? 1f : 0.4f;
                using (var path = RoundedRect(l.Popout, 6))
                {
                    if (popoutEnabled && (hover == Part.Popout || pressed == Part.Popout))
                        using (var back = new SolidBrush(Ink(pressed == Part.Popout ? 0.35f : 0.2f)))
                            g.FillPath(back, path);
                    using (var pen = new Pen(Ink(0.6f * popDim)))
                        g.DrawPath(pen, path);
                }
                using (var brush = new SolidBrush(Ink(popDim)))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("ffplay", buttonFont, brush, l.Popout, format);
            }

            void DrawRoundButton(Graphics g, Rectangle r, Part part, Glyph glyph, int size, bool primary)
            {
                bool enabled = IsEnabled(part);
                float back = primary ? 0.16f : 0;
                if (enabled && pressed == part) back = 0.4f;
                else if (enabled && hover == part) back = primary ? 0.32f : 0.22f;
                if (back > 0)
                    using (var brush = new SolidBrush(Ink(back)))
                        g.FillEllipse(brush, r);
                using (var brush = new SolidBrush(Ink(enabled ? 1f : 0.4f)))
                    DrawGlyph(g, brush, glyph, r.Left + r.Width / 2f, r.Top + r.Height / 2f, size);
            }

            static void DrawGlyph(Graphics g, Brush brush, Glyph glyph, float cx, float cy, float s)
            {
                PointF[] Triangle(float left, bool pointRight) => pointRight
                    ? new[] { new PointF(left, cy - s / 2f), new PointF(left + s * 0.85f, cy), new PointF(left, cy + s / 2f) }
                    : new[] { new PointF(left + s * 0.85f, cy - s / 2f), new PointF(left, cy), new PointF(left + s * 0.85f, cy + s / 2f) };
                switch (glyph)
                {
                    case Glyph.Play:
                        g.FillPolygon(brush, Triangle(cx - s * 0.35f, true));
                        break;
                    case Glyph.Pause:
                        g.FillRectangle(brush, cx - s * 0.4f, cy - s / 2f, s * 0.28f, s);
                        g.FillRectangle(brush, cx + s * 0.12f, cy - s / 2f, s * 0.28f, s);
                        break;
                    case Glyph.Previous:
                        g.FillRectangle(brush, cx - s * 0.6f, cy - s / 2f, 2, s);
                        g.FillPolygon(brush, Triangle(cx - s * 0.35f, false));
                        break;
                    case Glyph.Next:
                        g.FillPolygon(brush, Triangle(cx - s * 0.5f, true));
                        g.FillRectangle(brush, cx + s * 0.45f, cy - s / 2f, 2, s);
                        break;
                }
            }

            static GraphicsPath RoundedRect(Rectangle r, int radius)
            {
                int d = radius * 2;
                var path = new GraphicsPath();
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }

            #endregion

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
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                }

                string text = busy && message == null ? (image == null ? "Loading preview…" : null) : message;
                if (text != null)
                {
                    var box = Rectangle.Inflate(ClientRectangle, -12, -12);
                    box.Height -= BarHeight; // keep clear of the controls
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

                PaintOverlay(g);

                if (Focused)
                    ControlPaint.DrawFocusRectangle(g, ClientRectangle);
            }

            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    fadeTimer.Dispose();
                    timeFont.Dispose();
                    buttonFont.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
