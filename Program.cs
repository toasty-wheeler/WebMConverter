using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WebMConverter
{
    #region Filters

    static class Filters
    {
        internal static CaptionFilter Caption = null;
        internal static CropFilter Crop = null;
        internal static DeinterlaceFilter Deinterlace = null;
        internal static DenoiseFilter Denoise = null;
        internal static DubFilter Dub;
        internal static FadeFilter Fade;
        internal static LevelsFilter Levels = null;
        internal static MultipleTrimFilter MultipleTrim = null;
        internal static OverlayFilter Overlay = null;
        internal static RateFilter Rate = null;
        internal static ResizeFilter Resize = null;
        internal static ReverseFilter Reverse = null;
        internal static RotateFilter Rotate;
        internal static SubtitleFilter Subtitle = null;
        internal static TrimFilter Trim = null;

        internal static void ResetFilters()
        {
            Caption = null;
            Crop = null;
            Deinterlace = null;
            Denoise = null;
            Dub = null;
            Fade = null;
            Levels = null;
            MultipleTrim = null;
            Overlay = null;
            Rate = null;
            Resize = null;
            Reverse = null;
            Rotate = null;
            Subtitle = null;
            Trim = null;
        }
    }
    
    #endregion

    static class Program
    {
        public static FFMSSharp.VideoSource VideoSource;
        public static FFMSSharp.ColorRange VideoColorRange;
        public static bool VideoInterlaced;
        public static string InputFile;
        public static FileType InputType;
        public static bool InputHasAudio;
        public static bool InputHasWeirdPixelFormat; // for something that xy-VSFilter can't render on
        public static string FileMd5;
        public static string AttachmentDirectory;
        public static Dictionary<int, Tuple<string, SubtitleType, string>> SubtitleTracks; // stream id, <tag:title OR codec_name, textsub/vobsub, extension>
        public static List<string> AttachmentList;

        /// <summary>
        /// Loads AviSynth into this process. AviSynth+ (32-bit) ships in Binaries\Win32 and needs only the C runtime that
        /// comes with Windows, so no separate install is required. Loading it by full path makes the live preview's
        /// [DllImport("avisynth")] bind to this copy; the bundled ffmpeg/ffprobe/ffplay pick it up on their own because
        /// it sits next to them. An installed AviSynth is only used as a fallback if the bundled copy is missing.
        /// </summary>
        internal static bool LoadAviSynth(out string error)
        {
            error = null;
            var bundled = System.IO.Path.Combine(Environment.CurrentDirectory, "Binaries", "Win32", "AviSynth.dll");
            if (System.IO.File.Exists(bundled))
            {
                if (NativeMethods.LoadLibraryEx(bundled, IntPtr.Zero, NativeMethods.LOAD_WITH_ALTERED_SEARCH_PATH) != IntPtr.Zero)
                    return true;
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            }
            if (NativeMethods.LoadLibrary("avisynth") != IntPtr.Zero)
                return true;
            error = error ?? new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // Bundled binaries are found relative to the working directory, which is not the exe folder when
            // started from a terminal, a shortcut or "Open with" on a file elsewhere.
            Environment.CurrentDirectory = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

            if (!LoadAviSynth(out string errorMessage))
            {
                MessageBox.Show(
                   $"Failed to load AviSynth: {errorMessage}.{Environment.NewLine}" +
                    "The bundled copy in Binaries\\Win32 seems to be missing or broken. " +
                    "I'll open the download page, go ahead and install it.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Process.Start("http://avisynth.nl/index.php/Main_Page#Official_builds");
                return;
            }

            // Upgrade Application Settings if applicable
            if (Properties.Settings.Default.TryUpgrade)
            {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.TryUpgrade = false;
                Properties.Settings.Default.Save();
            }

            Task.Factory.StartNew(VideoDownload.CheckEnabled);
            Task.Factory.StartNew(ShareXUpload.CheckEnabled);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
