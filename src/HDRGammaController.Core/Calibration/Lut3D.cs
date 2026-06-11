using System;
using System.IO;
using System.Text;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// A 3D color lookup table for display calibration.
    /// Supports tetrahedral interpolation for accurate color correction.
    /// </summary>
    /// <remarks>
    /// The 3D LUT maps input RGB values to corrected output RGB values.
    /// Common sizes are 17x17x17 (4913 entries), 33x33x33 (35937 entries),
    /// or 65x65x65 (274625 entries).
    ///
    /// Larger LUTs provide more accuracy but consume more memory.
    /// For display calibration, 17x17x17 is typically sufficient.
    ///
    /// Reference: Adobe Cube LUT Specification
    /// </remarks>
    public class Lut3D
    {
        private readonly float[,,] _r;
        private readonly float[,,] _g;
        private readonly float[,,] _b;

        /// <summary>
        /// The size of each dimension (e.g., 17 for 17x17x17).
        /// </summary>
        public int Size { get; }

        /// <summary>
        /// Total number of LUT entries.
        /// </summary>
        public int EntryCount => Size * Size * Size;

        /// <summary>
        /// Domain minimum (typically 0.0).
        /// </summary>
        public float DomainMin { get; init; } = 0.0f;

        /// <summary>
        /// Domain maximum (typically 1.0).
        /// </summary>
        public float DomainMax { get; init; } = 1.0f;

        /// <summary>
        /// Creates a new 3D LUT with the specified size, initialized to identity (passthrough).
        /// </summary>
        public Lut3D(int size)
        {
            if (size < 2 || size > 256)
                throw new ArgumentOutOfRangeException(nameof(size), "LUT size must be between 2 and 256");

            Size = size;
            _r = new float[size, size, size];
            _g = new float[size, size, size];
            _b = new float[size, size, size];

            // Initialize to identity (no correction)
            for (int ri = 0; ri < size; ri++)
            {
                float r = ri / (float)(size - 1);
                for (int gi = 0; gi < size; gi++)
                {
                    float g = gi / (float)(size - 1);
                    for (int bi = 0; bi < size; bi++)
                    {
                        float b = bi / (float)(size - 1);
                        _r[ri, gi, bi] = r;
                        _g[ri, gi, bi] = g;
                        _b[ri, gi, bi] = b;
                    }
                }
            }
        }

        /// <summary>
        /// Sets a LUT entry at the specified indices.
        /// </summary>
        public void SetEntry(int rIndex, int gIndex, int bIndex, float r, float g, float b)
        {
            _r[rIndex, gIndex, bIndex] = r;
            _g[rIndex, gIndex, bIndex] = g;
            _b[rIndex, gIndex, bIndex] = b;
        }

        /// <summary>
        /// Gets a LUT entry at the specified indices.
        /// </summary>
        public (float R, float G, float B) GetEntry(int rIndex, int gIndex, int bIndex)
        {
            return (_r[rIndex, gIndex, bIndex], _g[rIndex, gIndex, bIndex], _b[rIndex, gIndex, bIndex]);
        }

        /// <summary>
        /// Looks up a color using trilinear interpolation.
        /// </summary>
        public (float R, float G, float B) LookupTrilinear(float r, float g, float b)
        {
            // Clamp inputs to valid range
            r = Math.Clamp(r, DomainMin, DomainMax);
            g = Math.Clamp(g, DomainMin, DomainMax);
            b = Math.Clamp(b, DomainMin, DomainMax);

            // Normalize to [0, size-1]
            float range = DomainMax - DomainMin;
            float rn = (r - DomainMin) / range * (Size - 1);
            float gn = (g - DomainMin) / range * (Size - 1);
            float bn = (b - DomainMin) / range * (Size - 1);

            // Get integer indices
            int r0 = (int)rn;
            int g0 = (int)gn;
            int b0 = (int)bn;

            int r1 = Math.Min(r0 + 1, Size - 1);
            int g1 = Math.Min(g0 + 1, Size - 1);
            int b1 = Math.Min(b0 + 1, Size - 1);

            // Get fractional parts
            float rf = rn - r0;
            float gf = gn - g0;
            float bf = bn - b0;

            // Trilinear interpolation
            // Interpolate along R axis
            float c000r = _r[r0, g0, b0], c000g = _g[r0, g0, b0], c000b = _b[r0, g0, b0];
            float c100r = _r[r1, g0, b0], c100g = _g[r1, g0, b0], c100b = _b[r1, g0, b0];
            float c010r = _r[r0, g1, b0], c010g = _g[r0, g1, b0], c010b = _b[r0, g1, b0];
            float c110r = _r[r1, g1, b0], c110g = _g[r1, g1, b0], c110b = _b[r1, g1, b0];
            float c001r = _r[r0, g0, b1], c001g = _g[r0, g0, b1], c001b = _b[r0, g0, b1];
            float c101r = _r[r1, g0, b1], c101g = _g[r1, g0, b1], c101b = _b[r1, g0, b1];
            float c011r = _r[r0, g1, b1], c011g = _g[r0, g1, b1], c011b = _b[r0, g1, b1];
            float c111r = _r[r1, g1, b1], c111g = _g[r1, g1, b1], c111b = _b[r1, g1, b1];

            // Interpolate along B axis
            float c00r = c000r + bf * (c001r - c000r);
            float c00g = c000g + bf * (c001g - c000g);
            float c00b = c000b + bf * (c001b - c000b);

            float c10r = c100r + bf * (c101r - c100r);
            float c10g = c100g + bf * (c101g - c100g);
            float c10b = c100b + bf * (c101b - c100b);

            float c01r = c010r + bf * (c011r - c010r);
            float c01g = c010g + bf * (c011g - c010g);
            float c01b = c010b + bf * (c011b - c010b);

            float c11r = c110r + bf * (c111r - c110r);
            float c11g = c110g + bf * (c111g - c110g);
            float c11b = c110b + bf * (c111b - c110b);

            // Interpolate along G axis
            float c0r = c00r + gf * (c01r - c00r);
            float c0g = c00g + gf * (c01g - c00g);
            float c0b = c00b + gf * (c01b - c00b);

            float c1r = c10r + gf * (c11r - c10r);
            float c1g = c10g + gf * (c11g - c10g);
            float c1b = c10b + gf * (c11b - c10b);

            // Interpolate along R axis
            float outR = c0r + rf * (c1r - c0r);
            float outG = c0g + rf * (c1g - c0g);
            float outB = c0b + rf * (c1b - c0b);

            return (outR, outG, outB);
        }

        /// <summary>
        /// Looks up a color using tetrahedral interpolation (more accurate than trilinear).
        /// </summary>
        /// <remarks>
        /// Tetrahedral interpolation divides each cube into 6 tetrahedra and
        /// interpolates within the appropriate tetrahedron. This is more
        /// geometrically accurate than trilinear interpolation.
        /// </remarks>
        public (float R, float G, float B) LookupTetrahedral(float r, float g, float b)
        {
            // Clamp inputs to valid range
            r = Math.Clamp(r, DomainMin, DomainMax);
            g = Math.Clamp(g, DomainMin, DomainMax);
            b = Math.Clamp(b, DomainMin, DomainMax);

            // Normalize to [0, size-1]
            float range = DomainMax - DomainMin;
            float rn = (r - DomainMin) / range * (Size - 1);
            float gn = (g - DomainMin) / range * (Size - 1);
            float bn = (b - DomainMin) / range * (Size - 1);

            // Get integer indices
            int r0 = (int)rn;
            int g0 = (int)gn;
            int b0 = (int)bn;

            int r1 = Math.Min(r0 + 1, Size - 1);
            int g1 = Math.Min(g0 + 1, Size - 1);
            int b1 = Math.Min(b0 + 1, Size - 1);

            // Get fractional parts
            float rf = rn - r0;
            float gf = gn - g0;
            float bf = bn - b0;

            // Get the 8 corner values
            var c000 = GetEntry(r0, g0, b0);
            var c100 = GetEntry(r1, g0, b0);
            var c010 = GetEntry(r0, g1, b0);
            var c110 = GetEntry(r1, g1, b0);
            var c001 = GetEntry(r0, g0, b1);
            var c101 = GetEntry(r1, g0, b1);
            var c011 = GetEntry(r0, g1, b1);
            var c111 = GetEntry(r1, g1, b1);

            // Determine which tetrahedron we're in and interpolate
            // There are 6 tetrahedra, determined by the relative ordering of rf, gf, bf
            float outR, outG, outB;

            if (rf > gf)
            {
                if (gf > bf)
                {
                    // rf > gf > bf: Tetrahedron 1
                    outR = c000.R + rf * (c100.R - c000.R) + gf * (c110.R - c100.R) + bf * (c111.R - c110.R);
                    outG = c000.G + rf * (c100.G - c000.G) + gf * (c110.G - c100.G) + bf * (c111.G - c110.G);
                    outB = c000.B + rf * (c100.B - c000.B) + gf * (c110.B - c100.B) + bf * (c111.B - c110.B);
                }
                else if (rf > bf)
                {
                    // rf > bf > gf: Tetrahedron 2
                    outR = c000.R + rf * (c100.R - c000.R) + bf * (c101.R - c100.R) + gf * (c111.R - c101.R);
                    outG = c000.G + rf * (c100.G - c000.G) + bf * (c101.G - c100.G) + gf * (c111.G - c101.G);
                    outB = c000.B + rf * (c100.B - c000.B) + bf * (c101.B - c100.B) + gf * (c111.B - c101.B);
                }
                else
                {
                    // bf > rf > gf: Tetrahedron 3
                    outR = c000.R + bf * (c001.R - c000.R) + rf * (c101.R - c001.R) + gf * (c111.R - c101.R);
                    outG = c000.G + bf * (c001.G - c000.G) + rf * (c101.G - c001.G) + gf * (c111.G - c101.G);
                    outB = c000.B + bf * (c001.B - c000.B) + rf * (c101.B - c001.B) + gf * (c111.B - c101.B);
                }
            }
            else
            {
                if (bf > gf)
                {
                    // bf > gf > rf: Tetrahedron 4
                    outR = c000.R + bf * (c001.R - c000.R) + gf * (c011.R - c001.R) + rf * (c111.R - c011.R);
                    outG = c000.G + bf * (c001.G - c000.G) + gf * (c011.G - c001.G) + rf * (c111.G - c011.G);
                    outB = c000.B + bf * (c001.B - c000.B) + gf * (c011.B - c001.B) + rf * (c111.B - c011.B);
                }
                else if (gf > rf)
                {
                    // gf > bf > rf: Tetrahedron 5
                    outR = c000.R + gf * (c010.R - c000.R) + bf * (c011.R - c010.R) + rf * (c111.R - c011.R);
                    outG = c000.G + gf * (c010.G - c000.G) + bf * (c011.G - c010.G) + rf * (c111.G - c011.G);
                    outB = c000.B + gf * (c010.B - c000.B) + bf * (c011.B - c010.B) + rf * (c111.B - c011.B);
                }
                else
                {
                    // gf > rf > bf: Tetrahedron 6
                    outR = c000.R + gf * (c010.R - c000.R) + rf * (c110.R - c010.R) + bf * (c111.R - c110.R);
                    outG = c000.G + gf * (c010.G - c000.G) + rf * (c110.G - c010.G) + bf * (c111.G - c110.G);
                    outB = c000.B + gf * (c010.B - c000.B) + rf * (c110.B - c010.B) + bf * (c111.B - c110.B);
                }
            }

            return (outR, outG, outB);
        }

        /// <summary>
        /// Default lookup method (uses tetrahedral interpolation).
        /// </summary>
        public (float R, float G, float B) Lookup(float r, float g, float b) => LookupTetrahedral(r, g, b);

        /// <summary>
        /// Applies the LUT to a LinearRgb value.
        /// </summary>
        public LinearRgb Apply(LinearRgb input)
        {
            var (r, g, b) = Lookup((float)input.R, (float)input.G, (float)input.B);
            return new LinearRgb(r, g, b);
        }

        #region Serialization

        /// <summary>
        /// Saves the LUT in Adobe Cube format.
        /// </summary>
        public void SaveAsCube(string path, string? title = null)
        {
            using var writer = new StreamWriter(path, false, Encoding.ASCII);

            writer.WriteLine($"# Created by HDR Gamma Controller");
            writer.WriteLine($"# {DateTime.UtcNow:O}");
            writer.WriteLine();

            if (!string.IsNullOrEmpty(title))
                writer.WriteLine($"TITLE \"{title}\"");

            writer.WriteLine($"LUT_3D_SIZE {Size}");
            writer.WriteLine($"DOMAIN_MIN {DomainMin:F6} {DomainMin:F6} {DomainMin:F6}");
            writer.WriteLine($"DOMAIN_MAX {DomainMax:F6} {DomainMax:F6} {DomainMax:F6}");
            writer.WriteLine();

            // Cube format: B changes fastest, then G, then R
            for (int ri = 0; ri < Size; ri++)
            {
                for (int gi = 0; gi < Size; gi++)
                {
                    for (int bi = 0; bi < Size; bi++)
                    {
                        writer.WriteLine($"{_r[ri, gi, bi]:F6} {_g[ri, gi, bi]:F6} {_b[ri, gi, bi]:F6}");
                    }
                }
            }
        }

        /// <summary>
        /// Loads a LUT from Adobe Cube format.
        /// </summary>
        public static Lut3D LoadFromCube(string path)
        {
            using var reader = new StreamReader(path);
            int size = 0;
            float domainMin = 0, domainMax = 1;
            var values = new System.Collections.Generic.List<(float r, float g, float b)>();

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith("TITLE"))
                    continue;

                if (line.StartsWith("LUT_3D_SIZE"))
                {
                    size = int.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
                }
                else if (line.StartsWith("DOMAIN_MIN"))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    domainMin = float.Parse(parts[1]);
                }
                else if (line.StartsWith("DOMAIN_MAX"))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    domainMax = float.Parse(parts[1]);
                }
                else
                {
                    // Data line
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        values.Add((
                            float.Parse(parts[0]),
                            float.Parse(parts[1]),
                            float.Parse(parts[2])
                        ));
                    }
                }
            }

            if (size == 0)
                throw new InvalidDataException("LUT_3D_SIZE not found in cube file");

            var lut = new Lut3D(size) { DomainMin = domainMin, DomainMax = domainMax };

            int index = 0;
            for (int ri = 0; ri < size; ri++)
            {
                for (int gi = 0; gi < size; gi++)
                {
                    for (int bi = 0; bi < size; bi++)
                    {
                        if (index < values.Count)
                        {
                            var v = values[index++];
                            lut.SetEntry(ri, gi, bi, v.r, v.g, v.b);
                        }
                    }
                }
            }

            return lut;
        }

        /// <summary>
        /// Serializes the LUT to a byte array for storage.
        /// </summary>
        public byte[] ToBytes()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // Header
            writer.Write((byte)'L');
            writer.Write((byte)'U');
            writer.Write((byte)'T');
            writer.Write((byte)'3');
            writer.Write((byte)1); // Version
            writer.Write(Size);
            writer.Write(DomainMin);
            writer.Write(DomainMax);

            // Data
            for (int ri = 0; ri < Size; ri++)
            {
                for (int gi = 0; gi < Size; gi++)
                {
                    for (int bi = 0; bi < Size; bi++)
                    {
                        writer.Write(_r[ri, gi, bi]);
                        writer.Write(_g[ri, gi, bi]);
                        writer.Write(_b[ri, gi, bi]);
                    }
                }
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Deserializes a LUT from a byte array.
        /// </summary>
        public static Lut3D FromBytes(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            // Verify header
            if (reader.ReadByte() != 'L' || reader.ReadByte() != 'U' ||
                reader.ReadByte() != 'T' || reader.ReadByte() != '3')
                throw new InvalidDataException("Invalid LUT3D header");

            byte version = reader.ReadByte();
            if (version != 1)
                throw new InvalidDataException($"Unsupported LUT3D version: {version}");

            int size = reader.ReadInt32();
            float domainMin = reader.ReadSingle();
            float domainMax = reader.ReadSingle();

            var lut = new Lut3D(size) { DomainMin = domainMin, DomainMax = domainMax };

            for (int ri = 0; ri < size; ri++)
            {
                for (int gi = 0; gi < size; gi++)
                {
                    for (int bi = 0; bi < size; bi++)
                    {
                        float r = reader.ReadSingle();
                        float g = reader.ReadSingle();
                        float b = reader.ReadSingle();
                        lut.SetEntry(ri, gi, bi, r, g, b);
                    }
                }
            }

            return lut;
        }

        #endregion

        /// <summary>
        /// Creates an identity LUT (no correction).
        /// </summary>
        public static Lut3D CreateIdentity(int size = 17) => new(size);
    }
}
