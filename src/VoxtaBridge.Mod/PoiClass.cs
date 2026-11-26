using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoxtaBridge
{
    /// <summary>Rough POI categories, most specific first.</summary>
    public enum PoiKind
    {
        Unknown,
        /// <summary>Has trader NPCs (harmless if the faction is friendly, like a major faction base if hostile).</summary>
        Trading,
        /// <summary>Abandoned dungeon: no or trivial external defenses, looted on foot; often an info terminal at the entrance.</summary>
        Abandoned,
        /// <summary>Major faction POI with strong external defenses: don't approach with a carrier or mobile base.</summary>
        MajorFaction,
        /// <summary>Small faction POI (defense tower, depot, aux building): usually beatable with a small vessel.</summary>
        MinorFaction,
        /// <summary>Wreck, ruin, junk, debris: no defenses, often no enemies.</summary>
        Salvage,
        /// <summary>Asteroid, deposit, harvestable resource.</summary>
        Resource,
        /// <summary>Decoration or invisible marker (billboard, deco module, dummy, territory marker): not a real target.</summary>
        Decorative,
        /// <summary>Civilian / neutral structure without defenses.</summary>
        Civilian,
    }

    /// <summary>What is known about one POI: from its prefab (header + block data) and its spawn/live faction.</summary>
    public sealed class PoiFacts
    {
        public string Prefab, Group, Display, Tag;
        public int SizeX, SizeY, SizeZ;
        public int TurretTypes;
        /// <summary>Turret blocks from the prefab's control panel group "cpgTurret" (-1 = no such group); sentry guns (anti-personnel, weak) are separate.</summary>
        public int Turrets = -1, Sentries;
        /// <summary>Best turret measure: the real count if known, else the number of turret block types.</summary>
        public int TurretScore => Turrets >= 0 ? Turrets : TurretTypes;
        public int Traders;
        public string PoiDialogue;
        public bool HasGin;
        /// <summary>Trader mission dialogues the prefab's NPCs start (<c>TD_X_Start</c>), for the trade missions.</summary>
        public List<string> TradeDialogues = new List<string>();
        /// <summary>Dialogues from <see cref="TraderIndex.WatchDialogues"/> that the prefab's NPCs or consoles use.</summary>
        public List<string> WatchedDialogues = new List<string>();
        /// <summary>Faction id (Factions.ecf / Entities.facgroup), -1 unknown, 255 none.</summary>
        public int Faction = -1;
        public string FactionName;

        /// <summary>Copy with the live faction (prefab facts are shared; the faction comes from the entity).</summary>
        public PoiFacts WithFaction(int faction)
        {
            var c = (PoiFacts)MemberwiseClone();
            c.Faction = faction;
            return c;
        }

        public long Volume => (long)Math.Max(1, SizeX) * Math.Max(1, SizeY) * Math.Max(1, SizeZ);
        public int MinSide => Math.Min(SizeX <= 0 ? 1 : SizeX, Math.Min(SizeY <= 0 ? 1 : SizeY, SizeZ <= 0 ? 1 : SizeZ));
    }

    /// <summary>
    /// Classifies POIs into <see cref="PoiKind"/> (game-free, unit-tested). Rules, in order: resources and decoration
    /// first (never targets), then traders, abandoned dungeons, salvage, and faction POIs split into major / minor by
    /// group name, turret types and size. Signals checked against prod's 7,238 POIs (2026-11-18): group names
    /// (prefab header property 7) carry most of it (`…Deco`, `…Dummy…`, `…Defense…`, `…Aux…`, `SpaceJunk`, `Ruins`…),
    /// faction ids 8 Alien (dungeons), 68 Eden_Wreck, 71 Eden_Abandoned, 75 Eden_Resource, 255 none.
    /// </summary>
    public static class PoiClassifier
    {
        static readonly Regex Resource = new Regex(@"Asteroid|Harvest|Deposit|Resource|Meteor", RegexOptions.IgnoreCase);
        static readonly Regex Decorative = new Regex(@"Deco|Dummy|Territory|Audio|JumpExit|Jump_?Exit|Plume|Billboard|Sign\b|Marker|TalonFarm$|Field$", RegexOptions.IgnoreCase);
        static readonly Regex Abandoned = new Regex(@"Abandoned|Necro|Dungeon", RegexOptions.IgnoreCase);
        static readonly Regex Salvage = new Regex(@"Junk|Wreck|Debri|Ruin|Bones|Crash|Treasure|Cave|Derelict|Scrap", RegexOptions.IgnoreCase);
        static readonly Regex Major = new Regex(@"Main|Command|DroneBase|\bBase$|HQ|Starport|Spaceport|Shipyard|Station|Factory|Military|Fortress|Citadel|Space(T\d)?$|SpaceT\d|Capital|Home", RegexOptions.IgnoreCase);
        static readonly Regex Minor = new Regex(@"Defen[cs]e|Aux|Tower|Outpost|Satellite|Depot|Guard|Turret|Bunker|Camp|Pad|Relay|Array", RegexOptions.IgnoreCase);

        /// <summary>Turrets (without sentry guns) from which a faction POI counts as heavily defended.</summary>
        public const int HeavyTurrets = 10;

        /// <summary>Factions that aren't real owners (no defenses implied): none, wreck, abandoned, resource, the territory pseudo factions.</summary>
        static bool NoOwner(int f) => f == 255 || f == 68 || f == 71 || f == 75 || (f >= 64 && f <= 67) || f == 70 || f == 74;

        public static (PoiKind kind, string why) Classify(PoiFacts p)
        {
            var g = p.Group ?? "";
            var names = $"{g} {p.Prefab} {p.Display}";
            if (p.Faction == 75 || Resource.IsMatch(g) || (string.IsNullOrEmpty(g) && Resource.IsMatch(names))) return (PoiKind.Resource, "resource");
            if (Decorative.IsMatch(g) || Regex.IsMatch(p.Display ?? "", "Billboard|Dummy", RegexOptions.IgnoreCase))
                return (PoiKind.Decorative, "decorative group/name");
            if (p.Traders > 0) return (PoiKind.Trading, $"{p.Traders} trader(s)");
            if (p.Volume <= 8 && p.TurretScore == 0) return (PoiKind.Decorative, "tiny marker");
            if (p.MinSide <= 1 && p.TurretScore == 0) return (PoiKind.Decorative, "flat (1 block thick)");
            if (p.Faction == 71 || Abandoned.IsMatch(g) || (p.PoiDialogue != null && p.PoiDialogue.IndexOf("Abandoned", StringComparison.OrdinalIgnoreCase) >= 0))
                return (PoiKind.Abandoned, p.PoiDialogue != null ? $"abandoned, terminal {p.PoiDialogue}" : "abandoned");
            if (p.Faction == 68 || Salvage.IsMatch(g)) return (PoiKind.Salvage, "wreck/ruin/junk");
            if (p.PoiDialogue != null && p.Faction == 8) return (PoiKind.Abandoned, $"dungeon, terminal {p.PoiDialogue}");
            if (NoOwner(p.Faction) || p.Faction == 8) return p.TurretScore > 0 ? (PoiKind.MinorFaction, "unowned with turrets") : (PoiKind.Salvage, "no owner");
            if (p.Faction == 15 && p.TurretScore <= 1) return (PoiKind.Civilian, "civilian, (almost) no turrets");

            // owned by a real faction
            // real turret count when the prefab has a "cpgTurret" group (sentry guns don't count), else turret types
            var bigTurrets = p.Turrets >= 0 ? p.Turrets >= HeavyTurrets : p.TurretTypes >= 6;
            var big = p.Volume >= 40L * 40 * 40;
            var defense = Regex.IsMatch(g, "Defen[cs]e|Sentry|Turret|Guard", RegexOptions.IgnoreCase);
            if (Minor.IsMatch(g) && (defense || !Major.IsMatch(g)))
                return bigTurrets && big ? (PoiKind.MajorFaction, "aux/defense but heavily armed and large") : (PoiKind.MinorFaction, "defense/aux group");
            if (Major.IsMatch(g) || bigTurrets || big) return (PoiKind.MajorFaction, Major.IsMatch(g) ? "main/base group" : bigTurrets ? (p.Turrets >= 0 ? $"{p.Turrets} turrets" : $"{p.TurretTypes} turret types") : "large");
            return p.TurretScore > 0 ? (PoiKind.MinorFaction, "small, armed") : (PoiKind.Civilian, "small, unarmed");
        }

        /// <summary>Without prefab facts (base-game or procedural blueprints like AsteroidVoxel…): classify by blueprint and entity name.</summary>
        public static (PoiKind kind, string why) ClassifyByName(string blueprint, string name, int faction) =>
            Classify(new PoiFacts { Prefab = blueprint, Group = blueprint, Display = name, Faction = faction, SizeX = 10, SizeY = 10, SizeZ = 10 });

        /// <summary>One-word label for the LLM (short to save context).</summary>
        public static string Label(PoiKind k)
        {
            switch (k)
            {
                case PoiKind.Trading: return "trading";
                case PoiKind.Abandoned: return "abandoned dungeon";
                case PoiKind.MajorFaction: return "major faction base";
                case PoiKind.MinorFaction: return "small faction outpost";
                case PoiKind.Salvage: return "wreck/salvage";
                case PoiKind.Resource: return "resource";
                case PoiKind.Decorative: return "decoration";
                case PoiKind.Civilian: return "civilian";
                default: return "unknown";
            }
        }

        /// <summary>Kinds the player would call a destination (micro jump targets, "nearest POI"): not resources or decoration.</summary>
        public static bool IsRealTarget(PoiKind k) => k != PoiKind.Decorative && k != PoiKind.Resource;
    }
}
