using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// Playfield server: remembers when each SV/CV was last hit, for the micro jump's "under attack" lock (like the
    /// game's own "We are under attack! Warp not possible for {0}s"; that timer is not exposed to mods).
    /// Samples every ship once a second: a hit is a shield drop while the shield stays on, or a rise in DamageLevel
    /// (see <see cref="JumpRules.IsHit"/>). Hits before the playfield server loaded the ship are not known.
    /// </summary>
    public partial class BridgeMod
    {
        sealed class HitSample
        {
            public int Shield;
            public bool ShieldOn;
            public float Damage;
            public DateTime? LastHitUtc;
        }

        static readonly Dictionary<int, HitSample> hitSamples = new Dictionary<int, HitSample>();
        static DateTime nextHitSample = DateTime.MinValue;

        /// <summary>PF main thread, every tick (samples once a second).</summary>
        static void SampleHits(DateTime now)
        {
            if (now < nextHitSample) return;
            nextHitSample = now.AddSeconds(1);
            var seen = new HashSet<int>();
            foreach (var pf in playfields.ToList())
            {
                Dictionary<int, IEntity> entities;
                try { entities = pf.Entities; } catch { continue; }
                if (entities == null) continue;
                foreach (var e in entities.Values)
                {
                    try
                    {
                        if (e.Type != EntityType.SV && e.Type != EntityType.CV) continue;
                        var s = e.Structure;
                        if (s == null || !s.IsReady) continue;
                        int shield = s.ShieldLevel; bool on = s.IsShieldActive; float damage = s.DamageLevel;
                        seen.Add(e.Id);
                        if (hitSamples.TryGetValue(e.Id, out var prev))
                        {
                            if (JumpRules.IsHit(prev.Shield, prev.ShieldOn, prev.Damage, shield, on, damage))
                            {
                                if (prev.LastHitUtc == null || (now - prev.LastHitUtc.Value).TotalSeconds > 10)
                                    Log($"hit watch: {e.Name} ({e.Id}) hit: shield {prev.Shield}→{shield}, damage {prev.Damage:0.####}→{damage:0.####}");
                                prev.LastHitUtc = now;
                            }
                            prev.Shield = shield; prev.ShieldOn = on; prev.Damage = damage;
                        }
                        else hitSamples[e.Id] = new HitSample { Shield = shield, ShieldOn = on, Damage = damage };
                    }
                    catch { }
                }
            }
            foreach (var id in hitSamples.Keys.Where(id => !seen.Contains(id)).ToList()) hitSamples.Remove(id);
        }

        static DateTime? LastHit(int entityId) => hitSamples.TryGetValue(entityId, out var s) ? s.LastHitUtc : null;
    }
}
