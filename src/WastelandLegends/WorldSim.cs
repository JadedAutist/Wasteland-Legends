using System;
using System.Collections.Generic;
using UnityEngine;

namespace WastelandLegends
{
    // M2: several legends, their deeds while the player is elsewhere, fame and titles, news, and trader rumors.
    // Fame comes from a legend's own deeds in the world, never from beating the player (design section 2).
    internal partial class Runner
    {
        private static readonly int[] CoyoteCamps = { 5, 8, 10, 15, 16 };
        private static readonly string[] TraderTypes = { "Merchant", "Mechanic", "Organic_Mechanic" };
        private static readonly int[] DeedWeights = { 30, 15, 30, 25 };   // bus, sack, raid, butcher

        internal GameObject trader;        // a Coyote trader within reach, or null
        private float nextTraderCheck;

        // ---- Population ----

        private void Populate()
        {
            int day = Game.Day();
            if (day < Plugin.GraceDays.Value || day < World.nextRiseDay) return;
            if (World.Living().Count >= Plugin.LivingLegends.Value) return;
            if (Rise() != null) World.nextRiseDay = day + 1;
        }

        internal bool CampTaken(Transform camp)
        {
            foreach (var r in World.Living())
                if (r.lairPath == Game.PathOf(camp) || (Game.ToWorld(Game.CampStandPoint(camp)) - r.lairWorld).sqrMagnitude < 60f * 60f) return true;
            return false;
        }

        // ---- Deeds ----

        internal void SimulateNow() { Simulate(); }

        // One roll per living legend per in-game hour since the last tick (at most 48 after a long sleep).
        private void Simulate()
        {
            float now = Game.Day() * 24f + Game.Hour();
            if (World.simHour < 0f || now < World.simHour) { World.simHour = now; return; }   // first tick, or a new day count
            int steps = Mathf.Min(48, Mathf.FloorToInt(now - World.simHour));
            for (int i = 0; i < steps; i++)
            {
                World.simHour += 1f;
                MaybeStealCar();
                int day = Mathf.FloorToInt(World.simHour / 24f);
                bool newDay = Mathf.FloorToInt((World.simHour - 1f) / 24f) != day;
                foreach (var r in World.Living())
                {
                    if (newDay && r.grudge > 0) r.grudge = Math.Max(0, r.grudge - 2);
                    if (BodyOf(r) != null) continue;   // it is at home, fighting or waiting for the player
                    // Wounds heal by half its health per day away from the player (user's choice, M4 play test).
                    if (r.health < r.maxHealth) r.health = Mathf.Min(r.maxHealth, r.health + r.maxHealth * 0.5f / 24f);
                    if (newDay && r.lastDeedDay < day - 1 && r.fame > 0) r.fame--;   // a quiet day
                    if (rng.NextDouble() < Plugin.DeedChance.Value) DoDeed(r, -1);
                    MaybeHunt(r);
                }
            }
            if (steps == 48) World.simHour = now;
        }

        internal string DoDeed(LegendRecord r, int deedIndex)
        {
            if (deedIndex < 0)
            {
                int roll = rng.Next(100);
                deedIndex = 0;
                for (int sum = 0; deedIndex < DeedWeights.Length; deedIndex++) { sum += DeedWeights[deedIndex]; if (roll < sum) break; }
                deedIndex = Mathf.Min(deedIndex, Deed.All.Length - 1);
            }
            var deed = Deed.All[deedIndex];
            string text = string.Format(deed.Lines[rng.Next(deed.Lines.Length)], r.name, PlaceNear(r, deed.Id));
            int band = r.Band;
            r.fame += deed.Fame;
            r.lastDeed = text;
            r.lastDeedDay = Game.Day();
            if (deed.Fame >= 8 && r.title != deed.Title) r.title = deed.Title;
            News(r, text, deed.Fame >= 8);
            if (r.Band > band) BandUp(r, band);
            Plugin.Log.LogInfo("Deed: " + text + " (" + r.name + " fame " + r.fame + ", " + Fame.Bands[r.Band] + ")");
            return text;
        }

        private void BandUp(LegendRecord r, int oldBand)
        {
            float share = r.maxHealth > 0f ? r.health / r.maxHealth : 1f;
            string gained = null;
            if (r.Band >= 2 && r.moreStrengths.Count < r.Band - 1)
            {
                var options = new List<string>();
                foreach (var t in Trait.Of(true))
                    if (!r.Has(t.Id) && !(t.Id == "Ironhead" && r.weakness == "GlassJaw")) options.Add(t.Id);
                if (options.Count > 0) { gained = options[rng.Next(options.Count)]; r.moreStrengths.Add(gained); }
            }
            r.maxHealth = Fame.MaxHealth(r);
            r.health = Mathf.Max(1f, r.maxHealth * share);
            News(r, r.name + " is " + Fame.Bands[r.Band].ToLowerInvariant() + " now" + (gained != null ? ", and harder to kill than ever." : "."), true);
            UpgradeSignature(r);
            Plugin.Log.LogInfo(r.name + ": " + Fame.Bands[oldBand] + " -> " + Fame.Bands[r.Band] + (gained != null ? ", gained " + gained : "") + ", health " + r.health.ToString("0") + "/" + r.maxHealth.ToString("0"));
        }

        internal void News(LegendRecord r, string text, bool show)
        {
            World.news.Add(new NewsItem { day = Game.Day(), legendId = r != null ? r.id : 0, text = text });
            while (World.news.Count > 30) World.news.RemoveAt(0);
            if (show) Notice("News: " + text);
        }

        // A real place near the legend's home: a Coyote camp for trader deeds, any camp otherwise.
        private string PlaceNear(LegendRecord r, string deed)
        {
            bool coyote = deed == "bus" || deed == "sack" || deed == "raid";
            string best = null;
            float bestDist = 3000f;
            var root = Game.MapMagic();
            if (root != null)
                foreach (Transform tile in root)
                {
                    var objects = tile.Find("Objects");
                    if (objects == null) continue;
                    foreach (Transform poi in objects)
                    {
                        if (!poi.name.StartsWith("Camp_")) continue;
                        int n;
                        string id = Game.CampId(poi.name, out n);
                        if (n == 0 || (poi.position - Game.ToScene(r.lairWorld)).sqrMagnitude < 150f * 150f) continue;
                        if (coyote && Array.IndexOf(CoyoteCamps, n) < 0) continue;
                        float d = Vector3.Distance(Game.ToWorld(poi.position), r.lairWorld);
                        if (d < bestDist) { best = Names.Camp(id); bestDist = d; }
                    }
                }
            if (best != null) return best;
            return coyote ? "the trade road" : "the dunes";
        }

        // ---- Trader rumors ----

        private void WatchTraders()
        {
            if (Time.time < nextTraderCheck) return;
            nextTraderCheck = Time.time + 0.25f;
            trader = null;
            var player = Game.Player();
            if (player == null || World == null) return;
            foreach (var col in Physics.OverlapSphere(player.position, 3f, 1 << Game.ActorLayer, QueryTriggerInteraction.Ignore))
            {
                var root = Game.EnemyRoot(col.transform);
                if (root == null || !root.CompareTag("Coyotes") || Array.IndexOf(TraderTypes, Game.BaseName(root.name)) < 0) continue;
                trader = root;
                return;
            }
        }

        // What the next rumor would be about: the oldest living legend whose camp you don't know, then unknown traits.
        internal LegendRecord RumorSubject(out string trait)
        {
            trait = null;
            foreach (var r in World.Living()) if (!r.locationKnown) return r;
            foreach (var r in World.Living())
                foreach (var t in r.Traits())
                    if (!r.Knows(t)) { trait = t; return r; }
            return null;
        }

        // The T key (no WW2 talk window): the answer comes as a notice.
        internal string BuyRumor()
        {
            string line;
            string result = Rumor(out line);
            Notice(result == "hostile" ? line : "Trader: " + line);
            return result;
        }

        // Returns "camp", a trait id, or why not ("hostile", "nothing", "money"); line is what the trader says.
        internal string Rumor(out string line)
        {
            if (Game.CoyotesHostile()) { line = "The trader won't talk to you while the Coyotes are angry with you."; return "hostile"; }
            string trait;
            var r = RumorSubject(out trait);
            if (r == null) { line = "Nothing new out there. Go make some news yourself."; return "nothing"; }
            int price = Fame.RumorPrice(r.fame);
            var money = Game.Money();
            if (money == null || money.Value < price) { line = "Word costs " + price + " nuts, friend."; return "money"; }
            money.Value -= price;
            if (trait == null)
            {
                r.locationKnown = true;
                line = r.name + "? Holds " + Names.Camp(r.lairName) + ", " + Where(r.lairWorld) + " of here. " + Fame.Bounty(r.fame) + " nuts on that head.";
            }
            else
            {
                r.known.Add(trait);
                line = "They say " + r.name + " " + Trait.Get(trait).Rumor + ".";
            }
            line = "(" + price + " nuts) \"" + line + "\"";
            Plugin.Log.LogInfo("Rumor bought for " + price + " about " + r.name + ": " + (trait ?? "camp"));
            return trait ?? "camp";
        }

        internal bool IsTrader(GameObject speaker)
        {
            if (speaker == null) return false;
            var root = Game.EnemyRoot(speaker.transform) ?? speaker;
            return root.CompareTag("Coyotes") && (Array.IndexOf(TraderTypes, Game.BaseName(root.name)) >= 0 || root.name.IndexOf("Merchant", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ---- WW2's talk window (Ww2Talk.cs) ----

        internal void TalkRumor()
        {
            Ww2Talk.Line("Heard of any legends?", true);
            string line;
            Rumor(out line);
            Ww2Talk.Line(line, false);
        }

        internal void TalkNews()
        {
            Ww2Talk.Line("What's the news out there?", true);
            if (World.news.Count == 0) { Ww2Talk.Line("Quiet out there. Too quiet.", false); return; }
            for (int i = World.news.Count - 1, n = 0; i >= 0 && n < 3; i--, n++)
                Ww2Talk.Line((n == 0 ? "Word is, " : "And ") + LowerFirst(World.news[i].text) + (World.news[i].day < Game.Day() ? " (day " + World.news[i].day + ")" : ""), false);
        }

        private static string LowerFirst(string s)
        {
            // Names stay capitalised: only lower "A raider..." style openings.
            return s.StartsWith("A ") ? "a " + s.Substring(2) : s;
        }

        // WW2 present and its talk API usable: then rumors are asked in its window, not with the T key.
        internal bool talkWindow;
        private bool talkChecked;

        private void TickTalk()
        {
            if (!talkChecked)
            {
                talkChecked = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    if (asm.GetName().Name == "WildWasteland") talkWindow = true;
            }
            if (!talkWindow) return;
            try { Ww2Talk.Tick(this); }
            catch (Exception e)
            {
                talkWindow = false;
                Plugin.Log.LogWarning("WW2 talk window not available (" + e.GetType().Name + ": " + e.Message + "); rumors use the " + Plugin.RumorKey.Value + " key.");
            }
        }

        internal void UninstallTalk()
        {
            if (!talkWindow) return;
            try { Ww2Talk.Uninstall(); } catch (Exception) { }
        }
    }
}
