using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace WastelandLegends
{
    // A legend's signature weapon (user's idea after the M4 play test, replacing the named car): what the wasteland
    // knows it for, better as its fame grows, and dropped as a trophy when it dies. Native weapons from ItemList today;
    // Weapons Pack weapons through a small public spawn call once the pack has one (requested from Codex in the
    // pack's AGENTS.md, 2026-10-29), looked up by reflection so nothing depends on it.
    internal static class Signatures
    {
        // ItemList prefab names by fame band (Rumor, Known, Feared, Infamous); loot odds in spawning.md.
        private static readonly string[][] Tiers =
        {
            new[] { "machete", "pipe_wrench", "slamfire_shotgun", "22_pipe_revolver", "crossbow" },
            new[] { "rochester_m24_chopped", "9mm_borz_smg", "22_pipe_smg", "slamberg_500_chopped" },
            new[] { "slamberg_500", "rochester_m24", "akm_trash", "folk_17" },
            new[] { "akms", "m16a1", "redmark_m11" },
        };

        // Chance that a Feared or Infamous legend carries a Weapons Pack weapon, when the pack offers them.
        private const float PackChance = 0.35f;

        public static string Pick(int band, System.Random rng)
        {
            band = Mathf.Clamp(band, 0, Tiers.Length - 1);
            var pack = PackApi.WeaponIds();
            if (band >= 2 && pack.Length > 0 && rng.NextDouble() < PackChance) return "pack:" + pack[rng.Next(pack.Length)];
            var tier = Tiers[band];
            for (int tries = 0; tries < 8; tries++)
            {
                string pick = tier[rng.Next(tier.Length)];
                if (InItemList(pick)) return pick;
            }
            return "machete";
        }

        private static readonly string[] MeleeWeapons = { "machete", "pipe_wrench", "shiv", "old_knife", "blastlance_1", "blastlance_2" };

        public static bool IsMelee(string signature)
        {
            if (string.IsNullOrEmpty(signature)) return false;
            if (signature.StartsWith("pack:")) return signature != "pack:Flamethrower";
            return Array.IndexOf(MeleeWeapons, signature) >= 0;
        }

        public static string Name(string signature)
        {
            if (string.IsNullOrEmpty(signature)) return "weapon";
            if (signature.StartsWith("pack:")) return signature.Substring(5);
            return Runner.WeaponName(signature);
        }

        private static bool InItemList(string prefab)
        {
            var list = Game.Proxy("ItemList");
            if (list == null || list._arrayList == null) return true;   // can't check: trust the table
            foreach (var entry in list._arrayList)
            {
                var go = entry as GameObject;
                if (go != null && go.name == prefab) return true;
            }
            return false;
        }
    }

    // The Weapons Pack's public spawn call, if it exists: a type ChainsawPrototype.WeaponsPackApi with
    // static string[] WeaponIds and static bool TrySpawn(string, Vector3, Quaternion, out GameObject).
    internal static class PackApi
    {
        private static bool resolved;
        private static MethodInfo trySpawn, ignite;
        private static FieldInfo idsField;
        private static PropertyInfo idsProp;

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type api;
                try { api = asm.GetType("ChainsawPrototype.WeaponsPackApi"); } catch (Exception) { continue; }
                if (api == null) continue;
                trySpawn = api.GetMethod("TrySpawn", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(Vector3), typeof(Quaternion), typeof(GameObject).MakeByRefType() }, null);
                idsField = api.GetField("WeaponIds", BindingFlags.Public | BindingFlags.Static);
                idsProp = api.GetProperty("WeaponIds", BindingFlags.Public | BindingFlags.Static);
                ignite = api.GetMethod("Ignite", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject), typeof(float) }, null);
                Plugin.Log.LogInfo("Weapons Pack API: spawn " + (trySpawn != null ? "yes" : "no") + ", ignite " + (ignite != null ? "yes (Searing trophies)" : "no") + ".");
                return;
            }
        }

        public static bool CanIgnite() { Resolve(); return ignite != null; }

        // The flamethrower's own burning (fire look, damage, burnt corpse), from the pack.
        public static bool Ignite(GameObject actor, float seconds)
        {
            Resolve();
            if (ignite == null || actor == null) return false;
            try { var r = ignite.Invoke(null, new object[] { actor, seconds }); return !(r is bool) || (bool)r; }
            catch (Exception e) { Plugin.Log.LogWarning("Weapons Pack ignite failed: " + (e.InnerException ?? e).Message); return false; }
        }

        public static string[] WeaponIds()
        {
            Resolve();
            if (trySpawn == null) return new string[0];
            try
            {
                var ids = (idsField != null ? idsField.GetValue(null) : idsProp != null ? idsProp.GetValue(null, null) : null) as string[];
                return ids ?? new string[0];
            }
            catch (Exception) { return new string[0]; }
        }

        public static GameObject Spawn(string id, Vector3 at, Quaternion rotation)
        {
            Resolve();
            if (trySpawn == null) return null;
            var args = new object[] { id, at, rotation, null };
            try { return (bool)trySpawn.Invoke(null, args) ? args[3] as GameObject : null; }
            catch (Exception e) { Plugin.Log.LogWarning("Weapons Pack spawn of " + id + " failed: " + (e.InnerException ?? e).Message); return null; }
        }
    }

    internal class Trophy
    {
        public GameObject Item;
        public TrophyItem T;
    }

    internal partial class Runner
    {
        internal readonly List<Trophy> trophies = new List<Trophy>();   // trophies found in the world (labels, bonus)

        internal static string TrophyLabel(TrophyItem t)
        {
            string perks = t.perks != null && t.perks.Count > 0 ? ", " + string.Join(", ", t.perks.ToArray()) : "";
            return t.legend + "'s " + Signatures.Name(t.signature) + " (+" + Mathf.RoundToInt(t.bonus * 100f) + "% damage" + perks + ")";
        }

        internal List<string> RollPerks(int band)
        {
            var pool = Perks.Pool();
            var perks = new List<string>();
            for (int i = 0; i < (band >= 3 ? 2 : 1) && pool.Count > 0; i++)
            {
                int k = rng.Next(pool.Count);
                perks.Add(pool[k]);
                pool.RemoveAt(k);
            }
            return perks;
        }

        // Trophies are registered items, saved by the game by name: find them again after a load.
        private void RelinkTrophies()
        {
            foreach (var t in World.trophies)
            {
                bool tracked = false;
                foreach (var x in trophies) if (x.T == t && x.Item != null) tracked = true;
                if (tracked) continue;
                var go = GameObject.Find(t.item);
                if (go == null) continue;
                string renamed = t.signature != null && !t.signature.StartsWith("pack:") ? Game.FixOldName(go, t.signature) : null;
                if (renamed != null)
                {
                    Plugin.Log.LogInfo("Trophy " + t.item + " renamed to " + renamed + " (the game's pattern; its ammo count shows now).");
                    foreach (var r in World.legends) if (r.trophyName == t.item) r.trophyName = renamed;
                    t.item = renamed;
                }
                trophies.Add(new Trophy { Item = go, T = t });
            }
        }

        internal TrophyItem TrophyOf(GameObject item)
        {
            if (item == null || World == null) return null;
            foreach (var t in World.trophies) if (t.item == item.name) return t;
            return null;
        }

        // ---- The bonus while a trophy is drawn ----

        // Weapon damage is not on the item but on the drawn weapon's model (WeaponsArm/Parent/<weapon>, its Attack
        // FSM's SetFsmFloat into the target Bodypart), shared by every copy of that weapon. So while a trophy is in
        // hand its model's damage is raised, and the exact old values are put back when it is put away.
        private readonly List<KeyValuePair<FsmFloatRef, float>> bonusUndo = new List<KeyValuePair<FsmFloatRef, float>>();
        private GameObject bonusItem;
        private float nextBonusCheck;
        private readonly Dictionary<string, float> trophyNoticeAt = new Dictionary<string, float>();

        internal class FsmFloatRef { public HutongGames.PlayMaker.FsmFloat Value; }

        private void TickTrophyBonus()
        {
            if (Time.realtimeSinceStartup < nextBonusCheck) return;
            nextBonusCheck = Time.realtimeSinceStartup + 0.2f;
            GameObject held = HeldWeapon();
            if (held == bonusItem) return;
            RestoreBonus();
            var t = TrophyOf(held);
            if (t == null) return;
            var model = DrawnModel();
            if (model == null) return;
            int n = ApplyBonus(model, 1f + t.bonus);
            if (t.perks != null)
            {
                foreach (var p in t.perks) activePerks.Add(p);
                if (t.perks.Contains(Perks.Quick)) n += ApplyQuick(model, 0.8f);
            }
            bonusItem = held;
            Plugin.Log.LogInfo("Trophy drawn: " + TrophyLabel(t) + ", " + n + " damage value(s) on " + model.name);
            // A reminder the first time, then at most every 10 minutes per trophy (not on every weapon switch).
            float last;
            if (!trophyNoticeAt.TryGetValue(t.item, out last) || Time.realtimeSinceStartup - last > 600f)
            {
                trophyNoticeAt[t.item] = Time.realtimeSinceStartup;
                Notice(TrophyLabel(t));
            }
        }

        // The drawn weapon item: WeaponInHand's Item while its weaponType is set (cleared when holstered).
        internal GameObject HeldWeapon()
        {
            CurrentWeapon();   // finds the WeaponInHand FSM
            if (weaponInHand == null) return null;
            var type = weaponInHand.FsmVariables.FindFsmString("weaponType");
            var item = weaponInHand.FsmVariables.FindFsmGameObject("Item");
            return type != null && !string.IsNullOrEmpty(type.Value) && item != null ? item.Value : null;
        }

        // The drawn weapon's model, by the weaponType name (the object for "folk17" is folk_17, weapons.md). Not just
        // "the active child with an Attack FSM": the kick lives there too (the bench picked it).
        internal GameObject DrawnModel()
        {
            var cam = Game.Global("PlayerCamera");
            var parent = cam != null ? cam.transform.Find("WeaponsArm/Parent") : null;
            var type = weaponInHand != null ? weaponInHand.FsmVariables.FindFsmString("weaponType") : null;
            if (parent == null || type == null || string.IsNullOrEmpty(type.Value)) return null;
            var model = parent.Find(type.Value == "folk17" ? "folk_17" : type.Value);
            return model != null && Game.Fsm(model.gameObject, "Attack") != null ? model.gameObject : null;
        }

        internal int ApplyBonus(GameObject model, float mult)
        {
            int n = 0;
            foreach (var fsm in model.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (!fsm.Fsm.Initialized) continue;
                foreach (var state in fsm.Fsm.States)
                    foreach (var action in state.Actions)
                    {
                        var set = action as HutongGames.PlayMaker.Actions.SetFsmFloat;
                        if (set == null || set.variableName == null || set.variableName.Value != "Damage") continue;
                        var f = set.setValue.UseVariable ? fsm.FsmVariables.GetFsmFloat(set.setValue.Name) : set.setValue;
                        if (f == null) continue;
                        bool seen = false;
                        foreach (var u in bonusUndo) if (u.Key.Value == f) seen = true;
                        if (seen) continue;
                        bonusUndo.Add(new KeyValuePair<FsmFloatRef, float>(new FsmFloatRef { Value = f }, f.Value));
                        f.Value *= mult;
                        n++;
                    }
            }
            return n;
        }

        internal void RestoreBonus()
        {
            foreach (var u in bonusUndo) if (u.Key.Value != null) u.Key.Value.Value = u.Value;
            bonusUndo.Clear();
            foreach (var a in animatorUndo) if (a.Key != null) a.Key.speed = a.Value;
            animatorUndo.Clear();
            activePerks.Clear();
            bonusItem = null;
        }

        private readonly List<KeyValuePair<Animator, float>> animatorUndo = new List<KeyValuePair<Animator, float>>();

        // Quick: the swing cycle (melee: Attack's SetFloatValue {Time}, 0.4 s on the machete) or the pause between
        // shots (guns: the Wait in Attack's "wait" state, 0.0857 s on the M16) times factor, and the model's animators
        // sped up to match. Same exact undo as the damage.
        internal int ApplyQuick(GameObject model, float factor)
        {
            int n = 0;
            var attack = Game.Fsm(model, "Attack");
            if (attack != null && attack.Fsm.Initialized)
                foreach (var state in attack.Fsm.States)
                    foreach (var action in state.Actions)
                    {
                        HutongGames.PlayMaker.FsmFloat f = null;
                        var set = action as HutongGames.PlayMaker.Actions.SetFloatValue;
                        if (set != null && set.floatVariable != null && set.floatVariable.Name == "Time") f = set.floatValue;
                        var wait = action as HutongGames.PlayMaker.Actions.Wait;
                        if (wait != null && action.Enabled && state.Name == "wait") f = wait.time;
                        if (f == null || f.UseVariable) continue;
                        bonusUndo.Add(new KeyValuePair<FsmFloatRef, float>(new FsmFloatRef { Value = f }, f.Value));
                        f.Value *= factor;
                        n++;
                    }
            foreach (var anim in model.GetComponentsInChildren<Animator>(true))
            {
                animatorUndo.Add(new KeyValuePair<Animator, float>(anim, anim.speed));
                anim.speed /= factor;
            }
            return n;
        }

        // Legends from before signature weapons get one that fits their fame.
        private void AssignSignatures()
        {
            foreach (var r in World.legends)
                if (r.state == LegendRecord.Alive && string.IsNullOrEmpty(r.signature)) r.signature = Signatures.Pick(r.Band, rng);
        }

        // On reaching a new fame band: a better weapon, and the wasteland hears about it.
        internal void UpgradeSignature(LegendRecord r)
        {
            string old = r.signature;
            string better = Signatures.Pick(r.Band, rng);
            if (better == old) return;
            r.signature = better;
            News(r, "Word is " + r.name + " carries a " + Signatures.Name(better) + " now.", true);
        }

        private IEnumerator SpawnTrophy(LegendRecord r, Vector3 at)
        {
            yield return null;
            if (string.IsNullOrEmpty(r.signature)) yield break;
            GameObject item = null;
            try
            {
                var rot = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                item = r.signature.StartsWith("pack:") ? PackApi.Spawn(r.signature.Substring(5), at, rot) : null;
                if (item == null) item = Game.CreateRegisteredItem(r.signature.StartsWith("pack:") ? "machete" : r.signature, at);
            }
            catch (Exception e) { Plugin.Log.LogError("Trophy spawn failed: " + e); }
            if (item == null) yield break;
            r.trophyName = item.name;
            var t = new TrophyItem { item = item.name, legend = r.name, signature = r.signature,
                bonus = Fame.TrophyBonus(r.Band, Signatures.IsMelee(r.signature)), perks = RollPerks(r.Band) };
            World.trophies.Add(t);
            trophies.Add(new Trophy { Item = item, T = t });
            Plugin.Log.LogInfo("Dropped " + TrophyLabel(t) + " as " + item.name + ".");
            var lines = new List<string>();
            foreach (var p in t.perks) lines.Add(Perks.Describe(p));
            Notice(r.name + " dropped its " + Signatures.Name(r.signature) + ": +" + Mathf.RoundToInt(t.bonus * 100f) + "% damage in your hands"
                + (lines.Count > 0 ? "; " + string.Join("; ", lines.ToArray()) : "") + ".");
        }
    }
}
