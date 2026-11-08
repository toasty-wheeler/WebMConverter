using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WebMConverter.Components
{
    /// <summary>
    /// Writes a still image as a one-frame, uncompressed 32-bit (BGRA) AVI, which AviSynth's built-in AVISource reads
    /// with its alpha channel intact. This replaces ImageSource, which needs the ImageSeq plugin (DevIL + OpenMP runtime).
    /// AviSynth's Overlay keeps showing the last frame of a shorter overlay clip, so one frame covers the whole video.
    /// </summary>
    static class StillImageAvi
    {
        const int Fps = 24;

        static readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Converts an image file (anything GDI+ can open) into a temp AVI and returns its path.
        /// The result is cached for as long as the source file does not change.
        /// </summary>
        public static string FromImageFile(string imagePath)
        {
            var info = new FileInfo(imagePath);
            string key = info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            lock (cache)
            {
                if (cache.TryGetValue(key, out string cached) && File.Exists(cached))
                    return cached;

                string target = Path.Combine(Path.GetTempPath(), "webmconverter-overlay-" + Hash(key) + ".avi");
                using (var image = Image.FromFile(imagePath))
                using (var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        g.Clear(Color.Transparent);
                        g.DrawImage(image, 0, 0, image.Width, image.Height);
                    }
                    Write(bitmap, target);
                }
                cache[key] = target;
                return target;
            }
        }

        /// <summary>Writes <paramref name="bitmap"/> as a one-frame uncompressed BGRA AVI.</summary>
        public static void Write(Bitmap bitmap, string path)
        {
            int width = bitmap.Width, height = bitmap.Height;
            int stride = width * 4;
            int frameSize = stride * height;

            var frame = new byte[frameSize];
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                // DIBs are stored bottom-up
                for (int y = 0; y < height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, frame, (height - 1 - y) * stride, stride);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            const int avihSize = 56, strhSize = 56, strfSize = 40;
            int strlSize = 4 + (8 + strhSize) + (8 + strfSize);
            int hdrlSize = 4 + (8 + avihSize) + (8 + strlSize);
            int moviSize = 4 + 8 + frameSize;
            const int idx1Size = 16;
            int riffSize = 4 + (8 + hdrlSize) + (8 + moviSize) + (8 + idx1Size);

            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(Fcc("RIFF")); w.Write(riffSize); w.Write(Fcc("AVI "));

                w.Write(Fcc("LIST")); w.Write(hdrlSize); w.Write(Fcc("hdrl"));

                w.Write(Fcc("avih")); w.Write(avihSize);
                w.Write(1000000 / Fps);      // dwMicroSecPerFrame
                w.Write(frameSize * Fps);    // dwMaxBytesPerSec
                w.Write(0);                  // dwPaddingGranularity
                w.Write(0x10);               // dwFlags = AVIF_HASINDEX
                w.Write(1);                  // dwTotalFrames
                w.Write(0);                  // dwInitialFrames
                w.Write(1);                  // dwStreams
                w.Write(frameSize);          // dwSuggestedBufferSize
                w.Write(width);
                w.Write(height);
                w.Write(0); w.Write(0); w.Write(0); w.Write(0); // dwReserved

                w.Write(Fcc("LIST")); w.Write(strlSize); w.Write(Fcc("strl"));

                w.Write(Fcc("strh")); w.Write(strhSize);
                w.Write(Fcc("vids"));
                w.Write(Fcc("DIB "));        // fccHandler: uncompressed
                w.Write(0);                  // dwFlags
                w.Write((short)0);           // wPriority
                w.Write((short)0);           // wLanguage
                w.Write(0);                  // dwInitialFrames
                w.Write(1);                  // dwScale
                w.Write(Fps);                // dwRate
                w.Write(0);                  // dwStart
                w.Write(1);                  // dwLength (frames)
                w.Write(frameSize);          // dwSuggestedBufferSize
                w.Write(-1);                 // dwQuality
                w.Write(0);                  // dwSampleSize
                w.Write((short)0); w.Write((short)0); w.Write((short)width); w.Write((short)height); // rcFrame

                w.Write(Fcc("strf")); w.Write(strfSize); // BITMAPINFOHEADER
                w.Write(strfSize);
                w.Write(width);
                w.Write(height);             // positive = bottom-up
                w.Write((short)1);           // biPlanes
                w.Write((short)32);          // biBitCount
                w.Write(0);                  // biCompression = BI_RGB
                w.Write(frameSize);          // biSizeImage
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);

                w.Write(Fcc("LIST")); w.Write(moviSize); w.Write(Fcc("movi"));
                w.Write(Fcc("00db")); w.Write(frameSize); w.Write(frame);

                w.Write(Fcc("idx1")); w.Write(idx1Size);
                w.Write(Fcc("00db"));
                w.Write(0x10);               // AVIIF_KEYFRAME
                w.Write(4);                  // offset of the chunk, relative to the 'movi' fourcc
                w.Write(frameSize);
            }
        }

        /// <summary>The AviSynth expression for a clip of the AVI written by this class.</summary>
        public static string Source(string aviPath) => $"AVISource(\"{aviPath}\", audio=false, pixel_type=\"RGB32\")";

        static int Fcc(string s) => s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24;

        static string Hash(string s)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                return BitConverter.ToString(bytes, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
