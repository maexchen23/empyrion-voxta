using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoxtaBridge
{
    /// <summary>
    /// A structure's bounding box with its world→block transform, measured on the playfield server
    /// (<c>GlobalToStructPos</c> at three probe points) so the dedi can test a fresher player position against it.
    /// Structures don't move while players walk on them, so a box a few seconds old is still right.
    /// </summary>
    public sealed class LiveBox
    {
        public int PlayerId, Id;
        public bool Own;
        public string Name, Type, Owner;
        /// <summary>Entity position (world).</summary>
        public double Px, Py, Pz;
        /// <summary>Block coordinates of the entity position.</summary>
        public double Ox, Oy, Oz;
        /// <summary>Row-major 3×3: block = O + M · (world − P).</summary>
        public double[] M = new double[9];
        /// <summary>Block bounds (y already with the +128 offset).</summary>
        public int MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        public long Volume => (long)(MaxX - MinX + 1) * (MaxY - MinY + 1) * (MaxZ - MinZ + 1);

        public (double x, double y, double z) ToBlock(double x, double y, double z)
        {
            double dx = x - Px, dy = y - Py, dz = z - Pz;
            return (Ox + M[0] * dx + M[1] * dy + M[2] * dz,
                    Oy + M[3] * dx + M[4] * dy + M[5] * dz,
                    Oz + M[6] * dx + M[7] * dy + M[8] * dz);
        }

        /// <summary>Same test as the playfield server's Inside(): block position within the bounds ± margin blocks.</summary>
        public bool Contains(double x, double y, double z, int margin)
        {
            var (bx, by, bz) = ToBlock(x, y, z);
            bx = Math.Round(bx); by = Math.Round(by); bz = Math.Round(bz);
            return bx >= MinX - margin && bx <= MaxX + margin && by >= MinY - margin && by <= MaxY + margin && bz >= MinZ - margin && bz <= MaxZ + margin;
        }

        public double DistanceTo(double x, double y, double z) => Math.Sqrt((x - Px) * (x - Px) + (y - Py) * (y - Py) + (z - Pz) * (z - Pz));

        static string Clean(string s) => (s ?? "").Replace("|", "/").Replace("\n", " ");
        static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
        static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        /// <summary>One line: player|own|id|name|type|owner|P|O|M|min|max (vectors comma-separated).</summary>
        public string Encode() =>
            string.Join("|", PlayerId.ToString(CultureInfo.InvariantCulture), Own ? "1" : "0", Id.ToString(CultureInfo.InvariantCulture),
                Clean(Name), Clean(Type), Clean(Owner),
                $"{F(Px)},{F(Py)},{F(Pz)}", $"{F(Ox)},{F(Oy)},{F(Oz)}", string.Join(",", M.Select(F)),
                $"{MinX},{MinY},{MinZ}", $"{MaxX},{MaxY},{MaxZ}");

        public static LiveBox Parse(string line)
        {
            var p = line.Split('|');
            if (p.Length != 11) return null;
            try
            {
                var pos = p[6].Split(',').Select(D).ToArray();
                var o = p[7].Split(',').Select(D).ToArray();
                var m = p[8].Split(',').Select(D).ToArray();
                var min = p[9].Split(',').Select(int.Parse).ToArray();
                var max = p[10].Split(',').Select(int.Parse).ToArray();
                if (pos.Length != 3 || o.Length != 3 || m.Length != 9 || min.Length != 3 || max.Length != 3) return null;
                return new LiveBox
                {
                    PlayerId = int.Parse(p[0]), Own = p[1] == "1", Id = int.Parse(p[2]), Name = p[3], Type = p[4], Owner = p[5].Length > 0 ? p[5] : null,
                    Px = pos[0], Py = pos[1], Pz = pos[2], Ox = o[0], Oy = o[1], Oz = o[2], M = m,
                    MinX = min[0], MinY = min[1], MinZ = min[2], MaxX = max[0], MaxY = max[1], MaxZ = max[2],
                };
            }
            catch (FormatException) { return null; }
        }

        /// <summary>
        /// Where a player is by a position: inside a structure that isn't theirs (the smallest box, no margin), at one of
        /// their own (the smallest box with <paramref name="ownMargin"/>), and their own structures by distance.
        /// </summary>
        public static (LiveBox visiting, LiveBox standing, List<(LiveBox box, double distance)> own) Locate(
            IEnumerable<LiveBox> boxes, double x, double y, double z, int ownMargin = 3)
        {
            var list = boxes.ToList();
            var visiting = list.Where(b => !b.Own && b.Contains(x, y, z, 0)).OrderBy(b => b.Volume).FirstOrDefault();
            var standing = list.Where(b => b.Own && b.Contains(x, y, z, ownMargin)).OrderBy(b => b.Volume).FirstOrDefault();
            var own = list.Where(b => b.Own).Select(b => (b, b.DistanceTo(x, y, z))).OrderBy(t => t.Item2).ToList();
            return (visiting, standing, own);
        }
    }
}
