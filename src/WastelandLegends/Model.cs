using System;
using System.Collections.Generic;
using UnityEngine;

namespace WastelandLegends
{
    // Everything here is saved (Json.cs, one string under the ES3 key "WastelandLegends" in the game's own save).
    // Positions are true world positions (scene position minus the MapMagic root), so floating-origin jumps don't move them.
    [Serializable]
    public class LegendWorld
    {
        public const int CurrentVersion = 3;

        public int version = CurrentVersion;
        public int nextId = 1;
        public List<LegendRecord> legends = new List<LegendRecord>();
        public List<NewsItem> news = new List<NewsItem>();
        public float simHour = -1f;       // game hours (day * 24 + time) simulated up to; -1 = not started
        public int nextRiseDay;           // no new legend before this day
        public int lastRoadDay = -1;      // the last day a legend rode with a patrol (one road encounter per day)
        public List<TrophyItem> trophies = new List<TrophyItem>();   // dropped signature weapons and their bonus
        public int lastHuntDay = -10;     // the last day a legend came for the player (one hunt per 2 days)
        public List<StashItem> stashes = new List<StashItem>();      // dead legends' camp stashes
        public string playerCar;          // the car the player last sat in (GameObject name)
        public float carSeenHour = -1f;   // game hours when the player was last with or near it

        public LegendRecord Alive()
        {
            foreach (var l in legends) if (l.state == LegendRecord.Alive) return l;
            return null;
        }

        public List<LegendRecord> Living()
        {
            var list = new List<LegendRecord>();
            foreach (var l in legends) if (l.state == LegendRecord.Alive) list.Add(l);
            return list;
        }

        public LegendRecord Find(int id)
        {
            foreach (var l in legends) if (l.id == id) return l;
            return null;
        }
    }

    [Serializable]
    public class LegendRecord
    {
        public const int Alive = 0, Dead = 1;

        public int id;
        public string name;
        public string title;
        public string body;            // raider prefab, e.g. "Flexa"
        public string strength;        // Traits id
        public List<string> moreStrengths = new List<string>();   // gained with fame (Feared, Infamous)
        public string weakness;
        public List<string> known = new List<string>();
        public bool locationKnown;     // its camp: told by a trader (M2); M1 legends were known from the start
        public int fame;
        public string lastDeed;        // the latest news line about it
        public int lastDeedDay;
        public float baseHealth;       // its raider type's health
        public float maxHealth;
        public float health;
        public int state;
        public string lairName;        // "Camp_11", for text
        public string lairPath;        // "MapMagic/Tile 1,-1/Objects/Camp_11(Clone)"
        public Vector3 lairWorld;
        public int bornDay;
        public int diedDay;
        public int bounty;
        public string markName;        // the mark item's GameObject name (its save key)
        public Vector3 markWorld;      // where the mark dropped
        public bool bountyPaid;
        public int encounters;

        // M3: what it remembers about the player, and coming back.
        public List<MemoryEntry> memory = new List<MemoryEntry>();
        public int grudge;
        public int deaths;
        public int returnDay = -1;     // the day it may come back; -1 = not coming back
        public float returnRoll;       // rolled at death, so reloading doesn't change the outcome

        // Its signature weapon: an ItemList prefab ("slamberg_500"), or "pack:<id>" for a Weapons Pack weapon.
        // Upgraded as its fame grows; dropped as a trophy when it dies.
        public string signature;
        public string trophyName;      // the dropped trophy's GameObject name
        public string stolenCar;       // the player's car it stole (M5, opt-in), until the player takes it back

        // Not saved: bench and F5 test legends live only in memory.
        [NonSerialized] public bool temp;

        public bool Has(string trait) { return strength == trait || weakness == trait || moreStrengths.Contains(trait); }
        public bool Knows(string trait) { return known.Contains(trait); }

        public List<string> Traits()
        {
            var list = new List<string> { strength };
            list.AddRange(moreStrengths);
            list.Add(weakness);
            return list;
        }

        public int Band { get { return Fame.Band(fame); } }
    }

    [Serializable]
    public class MemoryEntry
    {
        public const string Met = "met", Ran = "ran", Killed = "killed";

        public int day;
        public string place;
        public string weapon;     // the player's weapon it saw most, display name
        public string outcome;
        public int dealt;         // damage the player did to it
        public int taken;         // damage it did to the player
    }

    [Serializable]
    public class StashItem
    {
        public string legend;
        public string place;
        public Vector3 world;     // its camp's stand point
        public int band;          // the legend's fame band at death: how much is in it
        public bool laid;         // laid out already (once)
    }

    [Serializable]
    public class TrophyItem
    {
        public string item;       // the weapon item's GameObject name (its save key)
        public string legend;
        public string signature;
        public float bonus;       // 0.2 = +20 % damage while the player holds it
        public List<string> perks = new List<string>();   // Perks ids, rolled at the drop
    }

    // Extra trophy bonuses (user's choice, 2026-10-29): one at random per trophy, two from an Infamous legend.
    internal static class Perks
    {
        public const string Bleeding = "Bleeding", Searing = "Searing", Quick = "Quick", Skullsplitter = "Skullsplitter", Bloodthirst = "Bloodthirst";

        // Searing (real burning: the flamethrower's fire look and burnt corpse) only when the Weapons Pack offers
        // its Ignite call; the first "Searing" only bled the target and became Bleeding (user, 2026-10-29).
        public static List<string> Pool()
        {
            var pool = new List<string> { Bleeding, Quick, Skullsplitter, Bloodthirst };
            if (PackApi.CanIgnite()) pool.Add(Searing);
            return pool;
        }

        public static string Describe(string id)
        {
            switch (id)
            {
                case Bleeding: return "Bleeding: hits make it bleed, up to 3 times over";
                case Searing: return "Searing: sets what it hits on fire";
                case Quick: return "Quick: faster attacks";
                case Skullsplitter: return "Skullsplitter: head hits +50 %";
                case Bloodthirst: return "Bloodthirst: kills heal you";
                default: return id;
            }
        }
    }

    [Serializable]
    public class NewsItem
    {
        public int day;
        public int legendId;
        public string text;
    }

    internal static class Fame
    {
        public static readonly string[] Bands = { "Rumor", "Known", "Feared", "Infamous" };
        private static readonly float[] HealthScale = { 1f, 1.2f, 1.45f, 1.67f };   // x3 -> x5 with HealthMultiplier 3

        public static int Band(int fame) { return fame >= 100 ? 3 : fame >= 60 ? 2 : fame >= 25 ? 1 : 0; }

        public static float MaxHealth(LegendRecord r)
        {
            return r.baseHealth * Plugin.HealthMultiplier.Value * HealthScale[r.Band] * (r.Has("ThickHide") ? 1.5f : 1f);
        }

        public static int Bounty(int fame) { return Mathf.Clamp(100 + 5 * fame, 100, 600); }

        public static int RumorPrice(int fame) { return Mathf.Clamp(40 + (int)(1.5f * fame), 40, 200); }

        // A trophy's damage bonus from the fame band of the legend it came from (user's choices): guns +10 / +15 / +20 /
        // +25 %; melee +25 / +35 / +45 / +60 % (+10 % on a machete was nothing).
        public static float TrophyBonus(int band, bool melee)
        {
            band = Mathf.Clamp(band, 0, 3);
            return melee ? new[] { 0.25f, 0.35f, 0.45f, 0.60f }[band] : 0.10f + 0.05f * band;
        }
    }

    internal class Deed
    {
        public string Id, Title;
        public int Fame;
        public string[] Lines;   // {0} legend, {1} place

        public static readonly Deed[] All =
        {
            new Deed { Id = "bus", Fame = 8, Title = "the Bus-Burner", Lines = new[] { "{0} burned a Coyote trader bus near {1}.", "{0} robbed a trader convoy outside {1} and left nobody standing." } },
            new Deed { Id = "sack", Fame = 12, Title = "of the Ashes", Lines = new[] { "{0} sacked {1}. The fires are still burning.", "{0}'s gang tore through {1} last night." } },
            new Deed { Id = "raid", Fame = 5, Title = "the Road Raider", Lines = new[] { "{0} won a running fight on the road near {1}.", "{0} ran a scav car off the road near {1}." } },
            new Deed { Id = "butcher", Fame = 4, Title = "the Butcher", Lines = new[] { "{0} butchered a band of scavengers near {1}.", "Wanderers near {1} met {0}. None of them came back." } },
        };
    }

    internal class Trait
    {
        public string Id, Name, Effect, Rumor;
        public bool Strength;

        public static readonly Trait[] All =
        {
            new Trait { Id = "Ironhead", Name = "Ironhead", Strength = true, Effect = "welded helmet: head shots do half damage", Rumor = "wears a welded helmet. Shooting for the head is a waste of bullets" },
            new Trait { Id = "ThickHide", Name = "Thick Hide", Strength = true, Effect = "takes half again as much punishment", Rumor = "has a hide like boot leather. Bring a lot of ammo" },
            new Trait { Id = "Bruiser", Name = "Bruiser", Strength = true, Effect = "hits like a truck up close", Rumor = "hits like a truck. Don't let that one get close" },
            new Trait { Id = "GlassJaw", Name = "Glass Jaw", Strength = false, Effect = "head shots do half again as much damage", Rumor = "has a glass jaw. One good shot to the head and down they go" },
            new Trait { Id = "SoftBelly", Name = "Soft Belly", Strength = false, Effect = "body shots hurt it more", Rumor = "has a soft belly. Aim for the gut" },
            new Trait { Id = "Loudmouth", Name = "Loudmouth", Strength = false, Effect = "can't keep quiet: you hear it coming", Rumor = "never shuts up. You'll hear that one before you see it" },
        };

        public static Trait Get(string id)
        {
            foreach (var t in All) if (t.Id == id) return t;
            return null;
        }

        public static List<Trait> Of(bool strength)
        {
            var list = new List<Trait>();
            foreach (var t in All) if (t.Strength == strength) list.Add(t);
            return list;
        }
    }

    internal static class Names
    {
        private static readonly string[] Start = { "Rattle", "Grit", "Rust", "Spark", "Crank", "Scrap", "Sump", "Piston", "Axle", "Gasket", "Clutch", "Sprocket" };
        private static readonly string[] End = { "jaw", "tooth", "gut", "neck", "fist", "eye", "back", "skull", "knuckle", "belly" };
        private static readonly string[] Firsts = { "Old", "Mad", "Big", "Velma", "Sal", "Dusty", "Ma", "Two-Stroke" };

        // Titles come from deeds in the design (M2). Until then a legend starts with one about where it holds out.
        private static readonly string[] Titles = { "of the Scrapyard", "the Rust King", "of the Burning Barrels", "the Scrap Baron", "the Yard Dog" };

        public static string Name(System.Random rng)
        {
            string core = Start[rng.Next(Start.Length)] + End[rng.Next(End.Length)];
            return rng.Next(3) == 0 ? Firsts[rng.Next(Firsts.Length)] + " " + core : core;
        }

        public static string Title(System.Random rng) { return Titles[rng.Next(Titles.Length)]; }

        // The camps' names on the map (as in WW2's HUD/Icons file names).
        private static readonly string[] CampNames =
        {
            null, "the Container Home", "the Scrap Camp", "the Container Home", "the Fuel Camp", "the Raid Camp", "the Dog Boat",
            "Buzzgut's Base", "the Coyote Sentry", "the Scrapyard Sentry", "the Rip-Off Merchant Camp", "the Pipe Home",
            "the Brickstop Camp", "the Radio Tower", "the Scrapyard", "Slumtown", "the Bus Merchant",
        };

        public static string Camp(string id)
        {
            int n;
            if (id != null && id.StartsWith("Camp_") && int.TryParse(id.Substring(5), out n) && n > 0 && n < CampNames.Length) return CampNames[n];
            return id;
        }
    }

    internal static class Taunts
    {
        public static readonly string[] Sight =
        {
            "Fresh meat. I'll take your boots too.",
            "You walked into my yard. Bad idea.",
            "Another scav come to die in the scrap.",
            "Look at you. I'll hang your hide on the fence.",
        };

        public static readonly string[] Loud =
        {
            "I can smell you, scav!",
            "Come out, come out! I won't bite. Much.",
            "Heard you coming a mile off!",
        };

        public static readonly string[] Road =
        {
            "Nice wheels. I'll take them off your corpse.",
            "Pull over, scav! Or don't. More fun that way.",
            "This road's mine. Toll's everything you've got.",
            "Run them down, boys!",
        };

        // {0} its signature weapon.
        public static readonly string[] Signature =
        {
            "Say hello to my {0}, scav.",
            "Want to see what this {0} does to a face?",
            "Come closer. My {0} gets lonely.",
        };

        public static readonly string[] Hurt =
        {
            "That all you got?",
            "You'll pay for that!",
            "First blood's yours. Last blood's mine.",
        };

        // Lines from memory: {0} weapon, {1} place, {2} times met.
        public static readonly string[] AfterRan =
        {
            "Back again, {0}? You ran last time.",
            "Look who crawled back. Running suits you better.",
            "Last time you ran from {1}. Not this time.",
        };

        public static readonly string[] AfterDeath =
        {
            "You left me in the sand at {1}. Bad idea.",
            "Remember me? You should have made sure.",
            "Took more than your {0} to put me down.",
        };

        public static readonly string[] ByWeapon =
        {
            "Still carrying that {0}?",
            "I know that {0}. I'll take it off your corpse.",
        };

        public static readonly string[] Again =
        {
            "That's {2} times now, scav. Let's finish it.",
            "You again. You just don't learn.",
        };

        public static string Pick(string[] list, System.Random rng) { return list[rng.Next(list.Length)]; }

        public static readonly string[] Scars = { null, "Twice-Buried", "Thrice-Buried" };
    }
}
