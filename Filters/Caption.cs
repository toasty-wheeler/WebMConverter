using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using WebMConverter.Dialogs;
using static WebMConverter.Utility;

namespace WebMConverter
{
    public partial class CaptionForm : Form
    {
        readonly CaptionFilter InputFilter;
        public CaptionFilter GeneratedFilter;

        Point point;

        Size videoResolution;
        Point held;
        Point beforeheld;

        public CaptionForm(CaptionFilter filterToEdit = null)
        {
            InitializeComponent();
            Theme.Register(this);

            InputFilter = filterToEdit;

            previewFrame.Picture.Paint += new PaintEventHandler(this.previewPicture_Paint);
            previewFrame.Picture.MouseDown += new MouseEventHandler(this.previewPicture_MouseDown);
            previewFrame.Picture.MouseMove += new MouseEventHandler(this.previewPicture_MouseMove);
        }

        void CaptionForm_Load(object sender, EventArgs e)
        {
            if (InputFilter != null)
            {
                point = InputFilter.Placement;
                boxText.Text = InputFilter.Text;
                fontDialog1.Font = InputFilter.Style;
                colorDialogTextColor.Color = InputFilter.TextColor;
                colorDialogBorderColor.Color = InputFilter.BorderColor;
                numericBorderThickness.Value = InputFilter.BorderThickness;
            }

            if ((Owner as MainForm).SarCompensate)
            {
                videoResolution = new Size((Owner as MainForm).SarWidth, (Owner as MainForm).SarHeight);
            }
            else
            {
                FFMSSharp.Frame frame = Program.VideoSource.GetFrame(previewFrame.Frame);
                videoResolution = frame.EncodedResolution;
                previewFrame.GeneratePreview(true);
            }

            if ((Owner as MainForm).boxAdvancedScripting.Checked) return;

            if (Filters.Trim != null)
            {
                previewFrame.Frame = Filters.Trim.TrimStart;
                trimTimingToolStripMenuItem.Enabled = true;
            }
            if (Filters.MultipleTrim != null)
            {
                previewFrame.Frame = Filters.MultipleTrim.Trims[0].TrimStart;
                trimTimingToolStripMenuItem.Enabled = true;
            }
        }

        Point getBasePoint(PictureBox pictureBox)
        {
            int basex = pictureBox.Size.Width == pictureBox.ClientSize.Width ? 0 : pictureBox.Size.Width - pictureBox.ClientSize.Width / 2;
            int basey = pictureBox.Size.Height == pictureBox.ClientSize.Height ? 0 : pictureBox.Size.Height - pictureBox.ClientSize.Height / 2;

            return new Point(basex, basey);
        }

        float getPictureScale(PictureBox pictureBox) => (float)pictureBox.ClientSize.Width / videoResolution.Width;

        void previewPicture_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            var path = new GraphicsPath();

            var pictureBox = sender as PictureBox;

            var basePoint = getBasePoint(pictureBox);
            var scale = getPictureScale(pictureBox);

            var scaledsize = fontDialog1.Font.Size * scale;
            var scaledpoint = new Point(basePoint.X + (int)(point.X * scale), basePoint.Y + (int)(point.Y * scale));

            path.AddString(boxText.Text, fontDialog1.Font.FontFamily, (int)fontDialog1.Font.Style, scaledsize, scaledpoint, StringFormat.GenericDefault);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(new SolidBrush(colorDialogTextColor.Color), path);
            g.DrawPath(new Pen(new SolidBrush(colorDialogBorderColor.Color), (float)numericBorderThickness.Value * scale), path);
        }

        void previewPicture_MouseDown(object sender, MouseEventArgs e)
        {
            held = e.Location;
            beforeheld = point;

            previewPicture_MouseMove(sender, e);
        }

        void previewPicture_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.None)
                return;

            var scale = getPictureScale(previewFrame.Picture);

            point.X = beforeheld.X + (int)((e.X - held.X) / scale);
            point.Y = beforeheld.Y + (int)((e.Y - held.Y) / scale);

            previewFrame.Picture.Invalidate();
        }

        private void UpdateTextLayout(object sender, EventArgs e)
        {
            previewFrame.Picture.Invalidate();
        }

        private void fontToolStripMenuItem_Click(object sender, EventArgs e)
        {
            fontDialog1.ShowDialog();
            UpdateTextLayout(sender, e);
        }

        private void textColorToolStripMenuItem_Click(object sender, EventArgs e)
        {
            colorDialogTextColor.ShowDialog();
            UpdateTextLayout(sender, e);
        }

        private void borderColorToolStripMenuItem_Click(object sender, EventArgs e)
        {
            colorDialogBorderColor.ShowDialog();
            UpdateTextLayout(sender, e);
        }

        private void buttonConfirm_Click(object sender, EventArgs e)
        {
            GeneratedFilter = new CaptionFilter(point, boxText.Text, fontDialog1.Font, colorDialogTextColor.Color, colorDialogBorderColor.Color, (int)numericBorderThickness.Value);
            DialogResult = DialogResult.OK;
        }

        private void startToolStripMenuItem_Click(object sender, EventArgs e) => previewFrame.Frame = Filters.Trim.TrimStart;
        private void endToolStripMenuItem_Click(object sender, EventArgs e) => previewFrame.Frame = Filters.Trim.TrimEnd;

        private void frameToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var dialog = new InputDialog<int>("Frame", previewFrame.Frame))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    previewFrame.Frame = Math.Max(0, Math.Min(Program.VideoSource.NumberOfFrames - 1, dialog.Value)); // Make sure we don't go out of bounds.
                }
            }
        }

        private void timeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var dialog = new InputDialog<TimeSpan>("Time", FrameToTimeSpan(previewFrame.Frame)))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    int i = TimeSpanToFrame(dialog.Value);
                    i = Math.Max(0, Math.Min(Program.VideoSource.NumberOfFrames - 1, i)); // Make sure we don't go out of bounds.
                    previewFrame.Frame = i;
                }
            }
        }

        private const int arrowKeyIncrement = 1;
        private const int arrowKeyShiftIncrement = 10;
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Up:
                    point.Y -= arrowKeyIncrement;
                    break;
                case Keys.Left:
                    point.X -= arrowKeyIncrement;
                    break;
                case Keys.Right:
                    point.X += arrowKeyIncrement;
                    break;
                case Keys.Down:
                    point.Y += arrowKeyIncrement;
                    break;
                case Keys.Shift | Keys.Up:
                    point.Y -= arrowKeyShiftIncrement;
                    break;
                case Keys.Shift | Keys.Left:
                    point.X -= arrowKeyShiftIncrement;
                    break;
                case Keys.Shift | Keys.Right:
                    point.X += arrowKeyShiftIncrement;
                    break;
                case Keys.Shift | Keys.Down:
                    point.Y += arrowKeyShiftIncrement;
                    break;
                default:
                    return base.ProcessCmdKey(ref msg, keyData);
            }

            previewFrame.Picture.Invalidate();
            return true;
        }
    }

    public class CaptionFilter
    {
        public Point Placement { get; }
        public string Text { get; }
        public Font Style { get; }
        public Color TextColor { get; }
        public Color BorderColor { get; }
        public int BorderThickness { get; }

        string renderedCaptionFilename;

        public CaptionFilter(Point placement, string text, Font style, Color textColor, Color borderColor, int borderThickness)
        {
            Placement = placement;
            Text = text;
            Style = style;
            TextColor = textColor;
            BorderColor = borderColor;
            BorderThickness = borderThickness;
        }

        public void BeforeEncode(Size resolution)
        {
            // AviSynth's AVISource keeps the file open while a script uses it (e.g. the live preview), so every
            // distinct rendering gets its own file name and an existing one is reused instead of overwritten.
            string key = string.Join("|", Text, Style.FontFamily.Name, Style.Style, Style.Size, Placement.X, Placement.Y,
                TextColor.ToArgb(), BorderColor.ToArgb(), BorderThickness, resolution.Width, resolution.Height);
            string name;
            using (var sha = System.Security.Cryptography.SHA1.Create())
                name = "webmconverter-caption-" + BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key)), 0, 8).Replace("-", "") + ".avi";
            renderedCaptionFilename = Path.Combine(Path.GetTempPath(), name);
            if (File.Exists(renderedCaptionFilename))
                return;

            // Clean up earlier renderings; ones still open in AviSynth are left for next time
            foreach (var old in Directory.GetFiles(Path.GetTempPath(), "webmconverter-caption*.*"))
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }

            using (var renderedCaption = new Bitmap(resolution.Width, resolution.Height))
            using (var g = Graphics.FromImage(renderedCaption))
            {
                var path = new GraphicsPath();

                path.AddString(Text, Style.FontFamily, (int)Style.Style, Style.Size, Placement, StringFormat.GenericDefault);

                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(new SolidBrush(TextColor), path);
                g.DrawPath(new Pen(new SolidBrush(BorderColor), BorderThickness), path);

                // A one-frame uncompressed AVI, read by AviSynth's built-in AVISource (no ImageSeq plugin needed)
                Components.StillImageAvi.Write(renderedCaption, renderedCaptionFilename);
            }
        }

        public override string ToString()
        {
            var source = Components.StillImageAvi.Source(GetCompatiblePath(renderedCaptionFilename));
            return $"Overlay({source}, mask={source}.ShowAlpha(pixel_type=\"RGB32\"))";
        }
    }
}
