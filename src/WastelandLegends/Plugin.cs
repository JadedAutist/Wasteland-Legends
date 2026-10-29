using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.InputSystem;

namespace WastelandLegends
{
    internal enum ArrivalStyle { Card, Line, Off }
    internal enum ItemLabelStyle { LookAt, Always, Off }

    // Wasteland Legends (design: GameInternals/notes/wasteland-legends-design.md). Files:
    // Model (saved records, traits, names), Runner (rising, bodies, the fight, the mark), Hud (screen), Persistence
    // (the save key), Game (helpers), Bench (test commands, Dev Loader only).
    [BepInPlugin(Guid, "Wasteland Legends", Version)]
    public class Plugin : BaseUnityPlugin
    {
        // Published by JadedAutist (M6). The ID names the config file; saves use Persistence.Key, not this.
        internal const string Guid = "apocalypter.wastelandlegends";
        internal const string Version = "0.9.0";

        internal static ManualLogSource Log;
        internal static bool Dev;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<Key> JournalKey;
        internal static ConfigEntry<Key> PromoteKey;
        internal static ConfigEntry<Key> RumorKey;
        internal static ConfigEntry<int> GraceDays;
        internal static ConfigEntry<int> LivingLegends;
        internal static ConfigEntry<float> DeedChance;
        internal static ConfigEntry<bool> LegendsReturn;
        internal static ConfigEntry<bool> RoadAmbushes;
        internal static ConfigEntry<bool> Hunts;
        internal static ConfigEntry<bool> CarTheft;
        internal static ConfigEntry<float> RoadChance;
        internal static ConfigEntry<float> ReturnWithMark;
        internal static ConfigEntry<float> ReturnWithoutMark;
        internal static ConfigEntry<float> SpawnRange;
        internal static ConfigEntry<float> DespawnRange;
        internal static ConfigEntry<float> HealthMultiplier;
        internal static ConfigEntry<float> DamageMultiplier;
        internal static ConfigEntry<string> MarkItem;
        internal static ConfigEntry<ArrivalStyle> Arrival;
        internal static ConfigEntry<ItemLabelStyle> ItemLabels;

        internal static GameObject RunnerObject;
        internal static Runner Runner;

        private void Awake()
        {
            Start(Config, Logger, false);
        }

        // Shared by BepInEx (Awake) and the Dev Loader (DevEntry), which has no plugin instance.
        internal static void Start(ConfigFile cfg, ManualLogSource logger, bool dev)
        {
            Log = logger;
            Dev = dev;
            // Grouped as Apocasetter shows them (F6 > Mods).
            Enabled = cfg.Bind("1. General", "Enabled", true, "Off: legends are put away (no fights, news or hunts, trophy bonuses off). Their story stays in your save and goes on when you switch it back on.");
            LivingLegends = cfg.Bind("1. General", "LivingLegends", 4, new ConfigDescription("How many legends can be alive at once (one new one per day until then).", new AcceptableValueRange<int>(1, 8)));
            GraceDays = cfg.Bind("1. General", "GraceDays", 2, new ConfigDescription("No legend before this in-game day of a save.", new AcceptableValueRange<int>(0, 30)));
            DeedChance = cfg.Bind("1. General", "DeedChancePerHour", 0.08f, new ConfigDescription("Chance per in-game hour that a legend does something the wasteland talks about.", new AcceptableValueRange<float>(0f, 1f)));
            Arrival = cfg.Bind("1. General", "Arrival", ArrivalStyle.Card, "How a legend's arrival is shown: a big title card, a line of text, or nothing.");
            ItemLabels = cfg.Bind("1. General", "ItemLabels", ItemLabelStyle.LookAt, "Names over marks and trophy weapons on the ground: only the one you look at (within 6 m), all nearby, or none.");

            HealthMultiplier = cfg.Bind("2. Fights", "HealthMultiplier", 3f, new ConfigDescription("Legend health = its raider type's health x this (Thick Hide: x1.5 more).", new AcceptableValueRange<float>(1f, 10f)));
            DamageMultiplier = cfg.Bind("2. Fights", "DamageMultiplier", 1.3f, new ConfigDescription("Legend melee and gun damage x this (Bruiser: melee x1.5 more).", new AcceptableValueRange<float>(0.5f, 3f)));
            SpawnRange = cfg.Bind("2. Fights", "SpawnRange", 150f, new ConfigDescription("A legend appears at its camp when you come this close (m).", new AcceptableValueRange<float>(50f, 300f)));
            DespawnRange = cfg.Bind("2. Fights", "DespawnRange", 400f, new ConfigDescription("It leaves again when you are this far away (m); it keeps its wounds.", new AcceptableValueRange<float>(200f, 1000f)));
            MarkItem = cfg.Bind("2. Fights", "MarkItem", "old_knife", "Item a legend drops as its mark (an ItemList name).");

            LegendsReturn = cfg.Bind("3. Comebacks", "LegendsReturn", true, "A killed legend can come back 2-5 days later (never after a third death), with a scar title and the same traits.");
            ReturnWithMark = cfg.Bind("3. Comebacks", "ReturnChanceWithMark", 0.2f, new ConfigDescription("Chance it comes back when you took its mark (sold it, or carried it off).", new AcceptableValueRange<float>(0f, 1f)));
            ReturnWithoutMark = cfg.Bind("3. Comebacks", "ReturnChanceWithoutMark", 0.5f, new ConfigDescription("Chance it comes back when the mark was left lying where it fell.", new AcceptableValueRange<float>(0f, 1f)));

            RoadAmbushes = cfg.Bind("4. Roads and hunts", "RoadAmbushes", true, "With Wild Wasteland 2: a legend can ride with a raider patrol that comes your way (at most once a day).");
            RoadChance = cfg.Bind("4. Roads and hunts", "RoadChance", 0.5f, new ConfigDescription("Chance that a raider patrol you meet has a legend with it (plus its grudge against you / 200).", new AcceptableValueRange<float>(0f, 1f)));
            Hunts = cfg.Bind("4. Roads and hunts", "Hunts", true, "A legend with a big grudge against you comes looking for you (at most every 2 days; at night at your Home marker).");
            CarTheft = cfg.Bind("4. Roads and hunts", "CarTheft", false, "A legend may steal your car when you leave it more than 1.5 km away for 6+ in-game hours. It ends up at its camp.");

            JournalKey = cfg.Bind("5. Keys", "JournalKey", Key.L, "Opens and closes the Legends journal.");
            RumorKey = cfg.Bind("5. Keys", "RumorKey", Key.T, "Next to a Coyote trader (without Wild Wasteland 2's talk window): pay for a rumor about a legend.");
            // In Keys, not a tab of its own: Apocasetter's tab row only fits five (user's screenshot, 0.9.0 test).
            PromoteKey = cfg.Bind("5. Keys", "PromoteKey", Key.None, "For testing: makes the enemy you look at a temporary legend (not saved). None = off.");

            var go = new GameObject("WastelandLegends.Runner");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            Runner = go.AddComponent<Runner>();
            // The test bench reads commands from GameInternals\dumps\bench: development only, never in a release.
            if (dev) go.AddComponent<Bench>().Runner = Runner;
            RunnerObject = go;
            Persistence.Runner = Runner;
            Persistence.Install(dev ? Guid + ".dev." + DateTime.Now.Ticks : Guid);
            Log.LogInfo("Wasteland Legends " + Version + " loaded" + (Enabled.Value ? "" : " (switched off in its settings)") + ". Journal: " + JournalKey.Value + ".");
        }
    }

    // Entry for the Dev Loader (GameInternals/tools/DevLoader), which loads this DLL from GameInternals\build and
    // again after every rebuild.
    public static class DevEntry
    {
        private static ManualLogSource log;

        public static void DevStart()
        {
            log = BepInEx.Logging.Logger.CreateLogSource("Wasteland Legends (dev)");
            var cfg = new ConfigFile(System.IO.Path.Combine(Paths.ConfigPath, Plugin.Guid + ".cfg"), true);
            Plugin.Start(cfg, log, true);
        }

        // Undo what this copy added, so the next copy starts clean. The world is handed over in a file.
        public static void DevStop()
        {
            Persistence.Uninstall();
            Persistence.Runner = null;
            if (Plugin.Runner != null) Plugin.Runner.Shutdown();
            foreach (var tag in UnityEngine.Object.FindObjectsOfType<LegendTag>())
            {
                tag.Runner = null;
                UnityEngine.Object.Destroy(tag);
            }
            if (Plugin.RunnerObject != null) UnityEngine.Object.Destroy(Plugin.RunnerObject);
            Plugin.RunnerObject = null;
            Plugin.Runner = null;
            if (log != null) BepInEx.Logging.Logger.Sources.Remove(log);
        }
    }
}
