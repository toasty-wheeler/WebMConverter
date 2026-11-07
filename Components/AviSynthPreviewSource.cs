using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WebMConverter.Components
{
    /// <summary>
    /// Loads an AviSynth script in-process through the AviSynth C API and hands out frames as bitmaps.
    /// Not thread-safe: create, use and dispose it on a single thread.
    /// </summary>
    internal sealed class AviSynthPreviewSource : IDisposable
    {
        // AVS_Value is { short type; short array_size; union { void*, int, float, char* ... } } = 8 bytes on x86.
        // We pass and receive it as a 64-bit integer: low dword = type | array_size << 16, high dword = payload.
        // That matches both the stdcall argument layout and the EDX:EAX struct return of MSVC.
        const short TypeClip = (short)'c';
        const short TypeInt = (short)'i';
        const short TypeString = (short)'s';
        const short TypeArray = (short)'a';
        const short TypeError = (short)'e';

        const int AVISYNTH_INTERFACE_VERSION = 6; // 2.6; AviSynth+ accepts it too

        // CS_RGBA_TYPE | CS_BGR | CS_INTERLEAVED
        const int CS_BGR32 = (1 << 1) | (1 << 28) | (1 << 30);

        static class Native
        {
            const string Dll = "avisynth";
            const CallingConvention CC = CallingConvention.StdCall;

            [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr avs_create_script_environment(int version);
            [DllImport(Dll, CallingConvention = CC)] public static extern void avs_delete_script_environment(IntPtr env);
            [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr avs_get_error(IntPtr env);
            [DllImport(Dll, CallingConvention = CC)] public static extern long avs_invoke(IntPtr env, [MarshalAs(UnmanagedType.LPStr)] string name, long args, IntPtr argNames);
            [DllImport(Dll, CallingConvention = CC)] public static extern void avs_release_value(long value);
            [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr avs_take_clip(long value, IntPtr env);
            [DllImport(Dll, CallingConvention = CC)] public static extern void avs_release_clip(IntPtr clip);
            [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr avs_clip_get_error(IntPtr clip);
            [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr avs_get_video_info(IntPtr clip);
            [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr avs_get_frame(IntPtr clip, int n);
            [DllImport(Dll, CallingConvention = CC)] public static extern void avs_release_video_frame(IntPtr frame);
            [DllImport(Dll, CallingConvention = CC)] public static extern int avs_get_pitch_p(IntPtr frame, int plane);
            [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr avs_get_read_ptr_p(IntPtr frame, int plane);

            [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
            public static extern void CopyMemory(IntPtr dest, IntPtr src, IntPtr count);
        }

        IntPtr env;
        IntPtr clip;
        readonly List<IntPtr> allocations = new List<IntPtr>();

        public int Width { get; }
        public int Height { get; }
        public int FrameCount { get; }
        public uint FpsNumerator { get; }
        public uint FpsDenominator { get; }
        public double FrameRate => FpsDenominator == 0 ? 0 : (double)FpsNumerator / FpsDenominator;

        /// <param name="scriptPath">An ANSI-safe path (use Utility.GetCompatiblePath) to an .avs file.</param>
        public AviSynthPreviewSource(string scriptPath)
        {
            try
            {
                env = Native.avs_create_script_environment(AVISYNTH_INTERFACE_VERSION);
            }
            catch (DllNotFoundException)
            {
                throw new Exception("AviSynth is not installed (32-bit avisynth.dll not found).");
            }
            catch (EntryPointNotFoundException)
            {
                throw new Exception("The installed AviSynth is too old for the live preview.");
            }
            if (env == IntPtr.Zero)
                throw new Exception("Could not create an AviSynth script environment.");

            try
            {
                // Keep the frame cache small; this is a 32-bit process.
                Release(Invoke("SetMemoryMax", MakeInt(256)));

                long value = Invoke("Import", MakeString(scriptPath));
                ThrowIfError(value, "The script");
                if (TypeOf(value) != TypeClip)
                {
                    Native.avs_release_value(value);
                    throw new Exception("The script did not return a video clip.");
                }

                value = ConvertToRgb32(value);

                clip = Native.avs_take_clip(value, env);
                Native.avs_release_value(value);

                IntPtr vi = Native.avs_get_video_info(clip);
                Width = Marshal.ReadInt32(vi, 0);
                Height = Marshal.ReadInt32(vi, 4);
                FpsNumerator = (uint)Marshal.ReadInt32(vi, 8);
                FpsDenominator = (uint)Marshal.ReadInt32(vi, 12);
                FrameCount = Marshal.ReadInt32(vi, 16);
                int pixelType = Marshal.ReadInt32(vi, 20);

                if (Width <= 0 || Height <= 0 || FrameCount <= 0)
                    throw new Exception("The script has no video frames.");
                if (pixelType != CS_BGR32)
                    throw new Exception($"Could not convert the script output to RGB32 for the preview (pixel type 0x{pixelType:X}).");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        long ConvertToRgb32(long value)
        {
            int height = 0;
            IntPtr tmp = Native.avs_take_clip(value, env);
            try { height = Marshal.ReadInt32(Native.avs_get_video_info(tmp), 4); }
            finally { Native.avs_release_clip(tmp); }

            string matrix = height > 576 ? "Rec709" : "Rec601";

            long converted = Invoke("ConvertToRGB32", MakeArray(value, MakeString(matrix)), null, "matrix");
            if (TypeOf(converted) == TypeError)
            {
                // Probably a high bit depth source on AviSynth+: drop to 8 bits first.
                long eight = Invoke("ConvertBits", MakeArray(value, MakeInt(8)));
                if (TypeOf(eight) == TypeClip)
                {
                    converted = Invoke("ConvertToRGB32", MakeArray(eight, MakeString(matrix)), null, "matrix");
                    Native.avs_release_value(eight);
                }
                if (TypeOf(converted) == TypeError)
                    converted = Invoke("ConvertToRGB32", value);
            }

            Native.avs_release_value(value);
            ThrowIfError(converted, "Converting to RGB");
            return converted;
        }

        /// <summary>Renders frame <paramref name="n"/> into a new 32bpp bitmap. The caller owns the bitmap.</summary>
        public Bitmap GetFrame(int n)
        {
            if (clip == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(AviSynthPreviewSource));

            n = Math.Max(0, Math.Min(FrameCount - 1, n));

            IntPtr frame = Native.avs_get_frame(clip, n);
            if (frame == IntPtr.Zero)
            {
                IntPtr err = Native.avs_clip_get_error(clip);
                throw new Exception(err != IntPtr.Zero ? Marshal.PtrToStringAnsi(err) : $"AviSynth could not render frame {n}.");
            }

            try
            {
                int pitch = Native.avs_get_pitch_p(frame, 0);
                IntPtr src = Native.avs_get_read_ptr_p(frame, 0);

                var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
                var data = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    var rowBytes = new IntPtr(Width * 4);
                    for (int y = 0; y < Height; y++)
                    {
                        // AviSynth RGB is stored bottom-up.
                        IntPtr srcRow = src + (Height - 1 - y) * pitch;
                        IntPtr dstRow = data.Scan0 + y * data.Stride;
                        Native.CopyMemory(dstRow, srcRow, rowBytes);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
                return bitmap;
            }
            finally
            {
                Native.avs_release_video_frame(frame);
            }
        }

        public TimeSpan FrameToTime(int n) =>
            FpsNumerator == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)n * FpsDenominator / FpsNumerator);

        #region AVS_Value helpers

        static short TypeOf(long value) => (short)(value & 0xFFFF);
        static IntPtr PayloadOf(long value) => new IntPtr((int)(value >> 32));
        static long Make(short type, short arraySize, int payload) =>
            ((long)payload << 32) | ((long)(ushort)arraySize << 16) | (ushort)type;

        long MakeInt(int i) => Make(TypeInt, 0, i);

        long MakeString(string s)
        {
            IntPtr p = Marshal.StringToHGlobalAnsi(s);
            allocations.Add(p); // AviSynth may keep pointers into argument strings, so they live as long as the environment
            return Make(TypeString, 0, p.ToInt32());
        }

        long MakeArray(params long[] values)
        {
            IntPtr p = Marshal.AllocHGlobal(8 * values.Length);
            allocations.Add(p);
            for (int i = 0; i < values.Length; i++)
                Marshal.WriteInt64(p, i * 8, values[i]);
            return Make(TypeArray, (short)values.Length, p.ToInt32());
        }

        long Invoke(string function, long args, params string[] argNames)
        {
            IntPtr names = IntPtr.Zero;
            if (argNames != null && argNames.Length > 0)
            {
                names = Marshal.AllocHGlobal(IntPtr.Size * argNames.Length);
                allocations.Add(names);
                for (int i = 0; i < argNames.Length; i++)
                {
                    IntPtr name = IntPtr.Zero;
                    if (argNames[i] != null)
                    {
                        name = Marshal.StringToHGlobalAnsi(argNames[i]);
                        allocations.Add(name);
                    }
                    Marshal.WriteIntPtr(names, i * IntPtr.Size, name);
                }
            }
            return Native.avs_invoke(env, function, args, names);
        }

        static void Release(long value)
        {
            if (TypeOf(value) != TypeError)
                Native.avs_release_value(value);
        }

        static void ThrowIfError(long value, string what)
        {
            if (TypeOf(value) != TypeError)
                return;

            IntPtr message = PayloadOf(value);
            string text = message != IntPtr.Zero ? Marshal.PtrToStringAnsi(message) : "unknown error";
            throw new Exception($"{what} failed:{Environment.NewLine}{text}");
        }

        #endregion

        public void Dispose()
        {
            if (clip != IntPtr.Zero)
            {
                Native.avs_release_clip(clip);
                clip = IntPtr.Zero;
            }
            if (env != IntPtr.Zero)
            {
                Native.avs_delete_script_environment(env);
                env = IntPtr.Zero;
            }
            foreach (var p in allocations)
                Marshal.FreeHGlobal(p);
            allocations.Clear();
        }
    }
}
