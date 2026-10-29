using System;
using HarmonyLib;
using HutongGames.PlayMaker;

namespace WastelandLegends
{
    // Saves the legend world as one string in the game's own save file, during the game's own save step, the way the
    // Weapons Pack stores its records (tested working under WW2's save system, mod-conflicts.md): prefixes on
    // ES3PlayMaker Save/Load, only for Save_NewGO_ArrayList's "newGOArray_Items" key, writing with that action's own
    // settings so the key lands in whatever file the game (or WW2) is writing. The prefixes only observe; they never
    // skip the game's action.
    internal static class Persistence
    {
        public const string Key = "WastelandLegends";

        internal static Runner Runner;
        private static Harmony harmony;

        public static void Install(string id)
        {
            harmony = new Harmony(id);
            harmony.Patch(AccessTools.Method(typeof(ES3PlayMaker.Save), "Enter"), prefix: new HarmonyMethod(typeof(Persistence), nameof(BeforeSave)));
            harmony.Patch(AccessTools.Method(typeof(ES3PlayMaker.Load), "Enter"), prefix: new HarmonyMethod(typeof(Persistence), nameof(BeforeLoad)));
        }

        public static void Uninstall()
        {
            if (harmony != null) harmony.UnpatchSelf();
            harmony = null;
        }

        private static bool ItemsArray(FsmStateAction action, FsmString key)
        {
            return key != null && key.Value == "newGOArray_Items" && action.Fsm != null && action.Fsm.Name == "Save_NewGO_ArrayList";
        }

        private static void BeforeSave(ES3PlayMaker.Save __instance)
        {
            if (Runner == null || !ItemsArray(__instance, __instance.key)) return;
            try
            {
                var settings = __instance.GetSettings();
                string json = Runner.SaveJson();
                if (json == null) return;
                ES3.Save<string>(Key, json, settings);
                Plugin.Log.LogInfo("Saved " + Runner.World.legends.Count + " legend(s) with " + settings.path + ".");
            }
            catch (Exception e) { Plugin.Log.LogError("Saving legends failed: " + e); }
        }

        private static void BeforeLoad(ES3PlayMaker.Load __instance)
        {
            if (Runner == null || !ItemsArray(__instance, __instance.key)) return;
            try
            {
                var settings = __instance.GetSettings();
                Runner.OnSaveLoaded(ES3.KeyExists(Key, settings) ? ES3.Load<string>(Key, settings) : null);
            }
            catch (Exception e) { Plugin.Log.LogError("Loading legends failed: " + e); }
        }
    }
}
