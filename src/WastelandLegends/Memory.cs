using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace WastelandLegends
{
    // M3: what a legend remembers about the player, its grudge, the taunts that come from it, and coming back after
    // death (design sections 6 and 7, decision 2). A legend's memory changes what it says, never its stats.
    internal partial class Runner
    {
        private PlayMakerFSM weaponInHand;

        // ---- The player's weapon ----

        // WeaponInHand keeps the drawn weapon's weaponType string; empty with nothing drawn (weapons.md).
        internal string CurrentWeapon()
        {
            if (weaponInHand == null)
            {
                var cam = Game.Global("PlayerCamera");
                if (cam != null)
                    foreach (var fsm in cam.GetComponentsInChildren<PlayMakerFSM>(true))
                        if (fsm.FsmName == "WeaponInHand") { weaponInHand = fsm; break; }
            }
            var type = weaponInHand != null ? weaponInHand.FsmVariables.FindFsmString("weaponType") : null;
            return type != null && !string.IsNullOrEmpty(type.Value) ? WeaponName(type.Value) : "bare hands";
        }

        internal static string WeaponName(string type)
        {
            switch (type)
            {
                case "old_knife": return "knife";
                case "22_pipe_pistol": return "pipe pistol";
                case "22_pipe_revolver": return "pipe revolver";
                case "22_pipe_smg": return "pipe SMG";
                case "borz_smg": case "9mm_borz_smg": return "Borz";
                case "slamfire_shotgun": return "slamfire";
                case "blastlance_1": case "blastlance_2": return "blast lance";
                case "rochester_m24": case "rochester_m24_chopped": return "Rochester";
                case "slamberg_500": case "slamberg_500_chopped": return "Slamberg";
                case "folk17": case "folk_17": return "Folk-17";
                case "m16a1": return "M16";
                case "akm_trash": case "akms": case "akm_drum": return "AK";
                case "redmark_m11": case "redmark_m11_scoped": return "Redmark";
                default: return type.Replace('_', ' ');
            }
        }

        // ---- Encounters ----

        internal void EndEncounter(Body b, string outcome)
        {
            var r = b.R;
            if (r.temp) return;
            string weapon = "bare hands";
            int most = 0;
            foreach (var kv in b.Weapons) if (kv.Value > most) { most = kv.Value; weapon = kv.Key; }
            if (most == 0) weapon = CurrentWeapon();

            var m = new MemoryEntry
            {
                day = Game.Day(), place = Names.Camp(r.lairName), weapon = weapon, outcome = outcome,
                dealt = Mathf.RoundToInt(b.Dealt), taken = Mathf.RoundToInt(b.Taken),
            };
            r.memory.Add(m);
            while (r.memory.Count > 8) r.memory.RemoveAt(0);

            if (outcome == MemoryEntry.Ran)
            {
                // Grudge and news only, no fame: a legend's fame comes from its own deeds, never from the player
                // (design section 2; the +4 fame for this was dropped before release, user 2026-10-29).
                r.grudge = Mathf.Clamp(r.grudge + 10 + (b.Dealt >= r.maxHealth / 4f ? 10 : 0), 0, 100);
                News(r, r.name + " says you ran from " + m.place + ".", true);
            }
            Plugin.Log.LogInfo(r.name + " remembers: " + outcome + " at " + m.place + ", your " + weapon + ", dealt " + m.dealt + ", took " + m.taken + "; grudge " + r.grudge);
        }

        // The arrival line when it knows you; null on a first meeting.
        internal string MemoryTaunt(LegendRecord r)
        {
            if (r.memory.Count == 0) return null;
            var last = r.memory[r.memory.Count - 1];
            MemoryEntry death = null;
            foreach (var m in r.memory) if (m.outcome == MemoryEntry.Killed) death = m;
            string[] lines;
            MemoryEntry about = last;
            if (last.outcome == MemoryEntry.Killed) lines = Taunts.AfterDeath;
            else if (last.outcome == MemoryEntry.Ran) lines = Taunts.AfterRan;
            else if (death != null && rng.Next(2) == 0) { lines = Taunts.AfterDeath; about = death; }
            else lines = rng.Next(2) == 0 ? Taunts.ByWeapon : Taunts.Again;

            // "Back again, bare hands?" reads wrong: without a weapon, only lines that don't name one.
            var usable = new List<string>();
            foreach (var l in lines) if (about.weapon != "bare hands" || !l.Contains("{0}")) usable.Add(l);
            if (usable.Count == 0) usable.Add(Taunts.Again[1]);
            return string.Format(usable[rng.Next(usable.Count)], about.weapon, about.place, r.memory.Count + 1);
        }

        // ---- Death and coming back ----

        internal void ScheduleReturn(LegendRecord r)
        {
            r.deaths++;
            if (Plugin.LegendsReturn.Value && r.deaths < 3)
            {
                r.returnDay = r.diedDay + 2 + rng.Next(4);   // 2-5 days later
                r.returnRoll = (float)rng.NextDouble();
            }
            else r.returnDay = -1;
        }

        // Taken: the bounty was paid, or the mark no longer lies where it dropped. Looked up in the save registry,
        // which also holds items the game has switched off far away.
        internal bool MarkTaken(LegendRecord r)
        {
            if (r.bountyPaid || string.IsNullOrEmpty(r.markName)) return true;
            var items = Game.Proxy("ArrayList_Items");
            if (items == null || items._arrayList == null) return true;
            foreach (var entry in items._arrayList)
            {
                var go = entry as GameObject;
                if (go != null && go.name == r.markName)
                    return (Game.ToWorld(go.transform.position) - r.markWorld).sqrMagnitude > 30f * 30f;
            }
            return true;
        }

        private void TickReturns()
        {
            int day = Game.Day();
            foreach (var r in World.legends.ToArray())
            {
                if (r.state != LegendRecord.Dead || r.returnDay < 0 || day < r.returnDay) continue;
                bool taken = MarkTaken(r);
                float chance = taken ? Plugin.ReturnWithMark.Value : Plugin.ReturnWithoutMark.Value;
                Plugin.Log.LogInfo(r.name + ": comeback check, mark " + (taken ? "taken" : "left") + ", chance " + chance + ", roll " + r.returnRoll.ToString("0.00"));
                if (r.returnRoll < chance) Return(r);
                else r.returnDay = -1;
            }
        }

        internal void Return(LegendRecord r)
        {
            // An unsold mark of a legend that walked back in is just a knife now.
            marks.RemoveAll(m => m.R == r);

            // Its camp, unless another legend took it meanwhile.
            foreach (var other in World.Living())
                if (other.lairPath == r.lairPath || (other.lairWorld - r.lairWorld).sqrMagnitude < 60f * 60f)
                {
                    var camp = ChooseCamp();
                    if (camp != null) { int n; r.lairName = Game.CampId(camp.name, out n); r.lairPath = Game.PathOf(camp); r.lairWorld = Game.ToWorld(Game.CampStandPoint(camp)); }
                    break;
                }

            r.state = LegendRecord.Alive;
            r.returnDay = -1;
            r.markName = null;
            r.bountyPaid = false;
            r.title = Taunts.Scars[Mathf.Clamp(r.deaths, 0, Taunts.Scars.Length - 1)] ?? r.title;
            int band = r.Band;
            r.fame += 10;
            r.grudge = Mathf.Clamp(r.grudge + 30, 0, 100);
            r.maxHealth = Fame.MaxHealth(r);
            r.health = r.maxHealth;
            if (r.Band > band) BandUp(r, band);
            r.health = r.maxHealth;
            News(r, "Someone saw " + r.name + " at " + Names.Camp(r.lairName) + ". Walking.", true);
            Plugin.Log.LogInfo(r.name + " came back (" + r.title + ", death " + r.deaths + ", fame " + r.fame + ", grudge " + r.grudge + ")");
        }
    }
}
