using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VoxtaBridge
{
    /// <summary>
    /// Simple key=value config next to the mod DLL (VoxtaBridge.cfg). Created with defaults and a
    /// random token on first start. Spike-level: per-player pairing comes later.
    /// </summary>
    public sealed class BridgeConfig
    {
        public int Port = 30016;
        public string BindAddress = "0.0.0.0";
        public string Token;
        public int SnapshotIntervalMs = 2000;
        public bool TransfersEnabled = true;
        public bool RequireOwnership = true;
        public int MaxItemsPerTransfer = 10000;
        /// <summary>Owned structures within this distance (entity position to entity position) are in transfer scope; 0 = docking only.</summary>
        public double TransferRangeMeters = 1000;
        /// <summary>Litres; giving to a player stops at this inventory volume (0 = no limit). The real capacity depends on armor/boosts, which the mod can't see.</summary>
        public double PlayerVolumeLimit = 1000;
        /// <summary>Virtual crafting needs a constructor block on the ship (docking group) that can make each recipe.</summary>
        public bool RequireConstructor = false;
        /// <summary>Micro jumps (RE2 Prototype Micro Jump Drive): on/off, jump length, own cooldown (RE2: 120 s), drive required.</summary>
        public bool MicroJumpEnabled = true;
        public double MicroJumpMinMeters = 1000;
        public double MicroJumpMaxMeters = 25000;
        public int MicroJumpCooldownSeconds = 60;
        /// <summary>No micro jump for this long after the ship was hit (shield drop or block damage); 0 = off.</summary>
        public int MicroJumpHitLockSeconds = 30;
        /// <summary>Aimed jumps at a planet or moon stop this far above its (estimated) surface.</summary>
        public double MicroJumpBodyClearanceMeters = JumpRules.DefaultBodyClearance;
        public bool MicroJumpRequireDrive = true;
        /// <summary>Jump distance per Refined Pentaxid in km; the cost is rounded up to whole Pentaxid (minimum 1).</summary>
        public double MicroJumpKmPerPentaxid = JumpRules.DefaultKmPerPentaxid;
        /// <summary>
        /// Quest recorder: log every change of the online players' dialogue variables, PDA chapters, dialogue steps and
        /// tokens (with where the player was) to VoxtaBridge.questlog.txt, to learn how quests and contracts are stored.
        /// </summary>
        public bool QuestLog = false;
        /// <summary>Combat probe: log game events, NPCs near players and kill statistics (read-only, for development).</summary>
        public bool CombatProbe = false;
        public string Path;

        /// <param name="create">false on playfield servers: read only, never write a token (the dedi owns the file).</param>
        public static BridgeConfig LoadOrCreate(string dir, Action<string> log, bool create = true)
        {
            var cfg = new BridgeConfig { Path = System.IO.Path.Combine(dir, "VoxtaBridge.cfg") };
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(cfg.Path))
            {
                foreach (var raw in File.ReadAllLines(cfg.Path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }

            if (values.TryGetValue("Port", out var p) && int.TryParse(p, out var port)) cfg.Port = port;
            if (values.TryGetValue("BindAddress", out var b) && b.Length > 0) cfg.BindAddress = b;
            if (values.TryGetValue("SnapshotIntervalMs", out var s) && int.TryParse(s, out var ms) && ms >= 250) cfg.SnapshotIntervalMs = ms;
            if (values.TryGetValue("Token", out var t) && t.Length >= 16) cfg.Token = t;
            if (values.TryGetValue("TransfersEnabled", out var te) && bool.TryParse(te, out var teb)) cfg.TransfersEnabled = teb;
            if (values.TryGetValue("RequireOwnership", out var ro) && bool.TryParse(ro, out var rob)) cfg.RequireOwnership = rob;
            if (values.TryGetValue("MaxItemsPerTransfer", out var mx) && int.TryParse(mx, out var mxi) && mxi > 0) cfg.MaxItemsPerTransfer = mxi;
            if (values.TryGetValue("TransferRangeMeters", out var tr) && double.TryParse(tr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var trd) && trd >= 0) cfg.TransferRangeMeters = trd;
            if (values.TryGetValue("PlayerVolumeLimit", out var pv) && double.TryParse(pv, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var pvd) && pvd >= 0) cfg.PlayerVolumeLimit = pvd;
            if (values.TryGetValue("RequireConstructor", out var rc) && bool.TryParse(rc, out var rcb)) cfg.RequireConstructor = rcb;
            if (values.TryGetValue("QuestLog", out var ql) && bool.TryParse(ql, out var qlb)) cfg.QuestLog = qlb;
            if (values.TryGetValue("CombatProbe", out var cp) && bool.TryParse(cp, out var cpb)) cfg.CombatProbe = cpb;
            double D(string k, double d) => values.TryGetValue(k, out var v) && double.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var x) && x >= 0 ? x : d;
            if (values.TryGetValue("MicroJumpEnabled", out var mje) && bool.TryParse(mje, out var mjeb)) cfg.MicroJumpEnabled = mjeb;
            cfg.MicroJumpMinMeters = D("MicroJumpMinMeters", cfg.MicroJumpMinMeters);
            cfg.MicroJumpMaxMeters = D("MicroJumpMaxMeters", cfg.MicroJumpMaxMeters);
            cfg.MicroJumpCooldownSeconds = (int)D("MicroJumpCooldownSeconds", cfg.MicroJumpCooldownSeconds);
            if (values.TryGetValue("MicroJumpRequireDrive", out var mjd) && bool.TryParse(mjd, out var mjdb)) cfg.MicroJumpRequireDrive = mjdb;
            cfg.MicroJumpHitLockSeconds = (int)D("MicroJumpHitLockSeconds", cfg.MicroJumpHitLockSeconds);
            cfg.MicroJumpBodyClearanceMeters = D("MicroJumpBodyClearanceMeters", cfg.MicroJumpBodyClearanceMeters);
            var kmPer = D("MicroJumpKmPerPentaxid", cfg.MicroJumpKmPerPentaxid);
            if (kmPer > 0) cfg.MicroJumpKmPerPentaxid = kmPer;

            if (cfg.Token == null && create)
            {
                cfg.Token = NewToken();
                try
                {
                    File.WriteAllText(cfg.Path,
                        "# VoxtaBridge config (generated). Keep the token secret.\n" +
                        $"Port={cfg.Port}\nBindAddress={cfg.BindAddress}\nSnapshotIntervalMs={cfg.SnapshotIntervalMs}\nToken={cfg.Token}\n" +
                        $"TransfersEnabled={cfg.TransfersEnabled}\nRequireOwnership={cfg.RequireOwnership}\nMaxItemsPerTransfer={cfg.MaxItemsPerTransfer}\n" +
                        $"TransferRangeMeters={cfg.TransferRangeMeters.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n" +
                        $"PlayerVolumeLimit={cfg.PlayerVolumeLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n" +
                        $"RequireConstructor={cfg.RequireConstructor}\n" +
                        $"QuestLog={cfg.QuestLog}\n" +
                        $"MicroJumpEnabled={cfg.MicroJumpEnabled}\nMicroJumpMinMeters={cfg.MicroJumpMinMeters}\nMicroJumpMaxMeters={cfg.MicroJumpMaxMeters}\n" +
                        $"MicroJumpCooldownSeconds={cfg.MicroJumpCooldownSeconds}\nMicroJumpRequireDrive={cfg.MicroJumpRequireDrive}\nMicroJumpHitLockSeconds={cfg.MicroJumpHitLockSeconds}\n" +
                        $"MicroJumpBodyClearanceMeters={cfg.MicroJumpBodyClearanceMeters.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n" +
                        $"MicroJumpKmPerPentaxid={cfg.MicroJumpKmPerPentaxid.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n",
                        new UTF8Encoding(false));
                    log($"wrote new config with a fresh token: {cfg.Path}");
                }
                catch (Exception e)
                {
                    log($"could not write {cfg.Path}: {e.Message} (token only valid until restart)");
                }
            }
            return cfg;
        }

        static string NewToken()
        {
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder();
            foreach (var x in bytes) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }
}
