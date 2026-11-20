using System;

namespace VoxtaBridge
{
    /// <summary>
    /// Micro jump rules (game-free, unit-tested). Mirrors RE2's Prototype Micro Jump Drive where it makes sense
    /// (cost 1 Pentaxid per started MicroJumpKmPerPentaxid km (default 5), minimum 1; cooldown MicroJumpCooldownSeconds (default 60 s); locked after a hit; nothing near the landing point) and the
    /// measurements of 2026-11-18 (planet/moon radius by size class; stop at radius + 500 m).
    /// </summary>
    public static class JumpRules
    {
        public const double DefaultKmPerPentaxid = 5;
        /// <summary>Refined Pentaxid (RE2's own micro jump dialogue pays with this item).</summary>
        public const string PentaxidItem = "PentaxidCrystal";
        public const float Buffer = 500f;          // stop this far outside a target of known size
        public const float UnknownClearance = 2000f; // targets/obstacles of unknown size (asteroids, unvisited structures)

        /// <summary>
        /// Refined Pentaxid for a jump: distance / km per Pentaxid, rounded up, at least 1 (RE2's
        /// dialogue rounds down). A tiny tolerance keeps float noise (10000.001 m) from costing one more.
        /// </summary>
        public static int Cost(float distanceMeters, double kmPerPentaxid = DefaultKmPerPentaxid)
        {
            if (kmPerPentaxid <= 0) kmPerPentaxid = DefaultKmPerPentaxid;
            return Math.Max(1, (int)Math.Ceiling(distanceMeters / (kmPerPentaxid * 1000) - 1e-6));
        }

        /// <summary>Planet/moon radius in space: 244.5 m × 2^size class (5 bodies, classes 2–4, measured on the HUD).</summary>
        public static float PlanetRadius(int sizeClass) => 244.5f * (float)Math.Pow(2, sizeClass);

        /// <summary>How far from a target's centre the jump stops.</summary>
        public static float StopDistance(float? radius) => radius.HasValue ? radius.Value + Buffer : UnknownClearance;

        /// <summary>Default clearance above a planet's or moon's surface (500 m was too close).</summary>
        public const float DefaultBodyClearance = 2000f;

        /// <summary>How far from a planet's or moon's centre the jump stops: radius + clearance (MicroJumpBodyClearanceMeters).</summary>
        public static float BodyStopDistance(float radius, float clearance) => radius + (clearance >= 0 ? clearance : DefaultBodyClearance);

        /// <summary>null if the jump length is allowed, else the reason.</summary>
        public static string CheckRange(float distance, float min, float max)
        {
            if (distance < min) return $"The jump would be {Km(distance)}; the drive needs at least {Km(min)}.";
            if (distance > max) return $"The jump would be {Km(distance)}; the drive reaches at most {Km(max)}.";
            return null;
        }

        /// <summary>Seconds until the next jump is allowed (0 = now).</summary>
        /// <summary>
        /// Was the ship hit between two samples? Shield dropped while it stayed on (switching it off is no hit), or the
        /// structure's DamageLevel rose (blocks damaged). Repairs and shield recharge are no hits.
        /// </summary>
        public static bool IsHit(int prevShield, bool prevShieldOn, float prevDamage, int shield, bool shieldOn, float damage) =>
            (prevShieldOn && shieldOn && shield < prevShield) || damage > prevDamage + 1e-6f;

        public static int CooldownLeft(DateTime? lastJumpUtc, DateTime nowUtc, int cooldownSeconds)
        {
            if (!lastJumpUtc.HasValue || cooldownSeconds <= 0) return 0;
            var left = lastJumpUtc.Value.AddSeconds(cooldownSeconds) - nowUtc;
            return left <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(left.TotalSeconds);
        }

        /// <summary>RE2's dialogue refuses jumps into the M31 core playfields.</summary>
        public static bool IsForbiddenPlayfield(string playfield) =>
            playfield != null && (playfield.Contains("Central BH of M31") || playfield.Contains("Accretion Disk"));

        public static string Km(float meters) => meters >= 1000
            ? (meters / 1000f).ToString(meters >= 10000 ? "0.#" : "0.##", System.Globalization.CultureInfo.InvariantCulture) + " km"
            : Math.Round(meters).ToString(System.Globalization.CultureInfo.InvariantCulture) + " m";
    }
}
