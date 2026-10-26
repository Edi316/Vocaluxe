#region license
// This file is part of Vocaluxe.
//
// Vocaluxe is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Vocaluxe is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with Vocaluxe. If not, see <http://www.gnu.org/licenses/>.
#endregion

using System;

namespace Vocaluxe.Lib.Draw
{
    enum EVideoFrameFormat
    {
        /// <summary>
        ///     Eine Ebene, 4 Byte pro Pixel (B, G, R, A)
        /// </summary>
        Bgra,
        /// <summary>
        ///     Drei Ebenen Y, U, V mit je 8 Bit, Chroma horizontal und vertikal halbiert
        /// </summary>
        Yuv420P
    }

    enum EVideoColorMatrix
    {
        Bt601,
        Bt709,
        Bt2020
    }

    /// <summary>
    ///     Beschreibt einen decodierten Videoframe in nicht verwaltetem Speicher.
    ///     Die Zeiger sind nur gueltig, solange der Erzeuger (Decoder) den Speicher haelt.
    /// </summary>
    readonly struct SVideoFrame
    {
        public readonly EVideoFrameFormat Format;
        public readonly int Width;
        public readonly int Height;
        public readonly EVideoColorMatrix ColorMatrix;
        public readonly bool FullRange;

        private readonly IntPtr _Plane0;
        private readonly IntPtr _Plane1;
        private readonly IntPtr _Plane2;
        private readonly int _Stride0;
        private readonly int _Stride1;
        private readonly int _Stride2;

        public SVideoFrame(EVideoFrameFormat format, int width, int height, IntPtr plane0, int stride0, IntPtr plane1, int stride1, IntPtr plane2, int stride2,
            EVideoColorMatrix colorMatrix, bool fullRange)
        {
            Format = format;
            Width = width;
            Height = height;
            _Plane0 = plane0;
            _Plane1 = plane1;
            _Plane2 = plane2;
            _Stride0 = stride0;
            _Stride1 = stride1;
            _Stride2 = stride2;
            ColorMatrix = colorMatrix;
            FullRange = fullRange;
        }

        public int PlaneCount => Format == EVideoFrameFormat.Yuv420P ? 3 : 1;

        public IntPtr GetPlane(int plane)
        {
            return plane switch
            {
                0 => _Plane0,
                1 => _Plane1,
                2 => _Plane2,
                _ => throw new ArgumentOutOfRangeException(nameof(plane))
            };
        }

        public int GetStride(int plane)
        {
            return plane switch
            {
                0 => _Stride0,
                1 => _Stride1,
                2 => _Stride2,
                _ => throw new ArgumentOutOfRangeException(nameof(plane))
            };
        }

        /// <summary>
        ///     Breite der Ebene in Pixeln
        /// </summary>
        public int GetPlaneWidth(int plane)
        {
            return plane == 0 ? Width : (Width + 1) / 2;
        }

        /// <summary>
        ///     Hoehe der Ebene in Zeilen
        /// </summary>
        public int GetPlaneHeight(int plane)
        {
            return plane == 0 ? Height : (Height + 1) / 2;
        }

        /// <summary>
        ///     Nutzbare Bytes pro Zeile der Ebene
        /// </summary>
        public int GetPlaneRowBytes(int plane)
        {
            return Format == EVideoFrameFormat.Bgra ? 4 * Width : GetPlaneWidth(plane);
        }
    }

    static class CVideoFrameHelper
    {
        /// <summary>
        ///     Kopiert eine Bildebene zeilenweise unter Beruecksichtigung unterschiedlicher Zeilenlaengen (Stride/Pitch)
        /// </summary>
        public static unsafe void CopyPlane(IntPtr src, int srcStride, IntPtr dst, int dstPitch, int rowBytes, int rows)
        {
            var s = (byte*)src;
            var d = (byte*)dst;
            if (srcStride == rowBytes && dstPitch == rowBytes)
            {
                var size = (long)rowBytes * rows;
                Buffer.MemoryCopy(s, d, size, size);
                return;
            }

            for (var y = 0; y < rows; y++)
            {
                Buffer.MemoryCopy(s, d, rowBytes, rowBytes);
                s += srcStride;
                d += dstPitch;
            }
        }

        /// <summary>
        ///     Liefert die Koeffizienten fuer die Umrechnung YUV -> RGB als 3 Zeilen (R, G, B) zu je 4 Werten:
        ///     Farbe = Zeile * (Y, U, V, 1). Die Eingangswerte sind auf 0..1 normiert (8 Bit / 255).
        /// </summary>
        public static float[] GetYuvToRgbRows(EVideoColorMatrix matrix, bool fullRange)
        {
            double kr, kb;
            switch (matrix)
            {
                case EVideoColorMatrix.Bt709:
                    kr = 0.2126;
                    kb = 0.0722;
                    break;
                case EVideoColorMatrix.Bt2020:
                    kr = 0.2627;
                    kb = 0.0593;
                    break;
                default:
                    kr = 0.299;
                    kb = 0.114;
                    break;
            }

            var kg = 1 - kr - kb;

            // Begrenzter Bereich (TV): Y 16..235, UV 16..240
            var yScale = fullRange ? 1.0 : 255.0 / 219.0;
            var yOffset = fullRange ? 0.0 : -16.0 / 255.0 * yScale;
            var cScale = fullRange ? 1.0 : 255.0 / 224.0;
            const double cCenter = 128.0 / 255.0;

            var rv = 2 * (1 - kr) * cScale;
            var gu = -2 * kb * (1 - kb) / kg * cScale;
            var gv = -2 * kr * (1 - kr) / kg * cScale;
            var bu = 2 * (1 - kb) * cScale;

            return new[]
            {
                (float)yScale, 0f, (float)rv, (float)(yOffset - rv * cCenter),
                (float)yScale, (float)gu, (float)gv, (float)(yOffset - (gu + gv) * cCenter),
                (float)yScale, (float)bu, 0f, (float)(yOffset - bu * cCenter)
            };
        }
    }
}
