using System;
using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace WastelandLegends
{
    // M5: a legend with a grudge comes for the player; a dead legend's camp stash; the opt-in car theft
    // (design: "M5 plan").
    internal partial class Runner
    {
        private static readonly string[] Escorts = { "Sprokka", "Scraffa", "Lugnut", "Scrud" };

        // Stash items by fame band (ItemList names, checked against the list when laid out).
        private static readonly string[][] StashTiers =
        {
            new[] { "ammo_box_22", "bandage_1", "dogfood_can", "Water_Can_Plastic", "cigar", "Repairbox_Small" },
            new[] { "ammo_box_9mm", "ammo_box_12gauge", "first_aid_1", "Repairbox_Medium", "armorplate", "sanity_pills", "turbo_littlebee" },
            new[] { "ammo_box_762mm", "ammo_box_556mm", "Repairbox_Large", "bodyarmor_type_2", "turbo_warwhistle", "supercharger_5", "Toolbox" },
            new[] { "ammo_box_big_762mm", "ammo_box_big_556mm", "bodyarmor_type_4", "supercharger_7", "turbo_bigbrutus", "wheel_5_armored" },
        };

        internal readonly List<GameObject> escorts = new List<GameObject>();
        internal readonly List<Trophy> stashLabels = new List<Trophy>();

        // ---- Hunts ----

        // Called once per simulated hour for a legend away from its camp (WorldSim.Simulate).
        private void MaybeHunt(LegendRecord r)
        {
            if (!Plugin.Hunts.Value || r.grudge < 40) return;
            int day = Game.Day();
            if (day < Plugin.GraceDays.Value || day < World.lastHuntDay + 2) return;
            foreach (var b in bodies) if (b.Hunt) return;
            var player = Game.Player();
            if (player == null || Vector3.Distance(player.position, Game.ToScene(r.lairWorld)) < 500f) return;
            if (rng.NextDouble() >= r.grudge / 400f) return;
            StartHunt(r);
        }

        internal Body StartHunt(LegendRecord r)
        {
            var player = Game.Player();
            var eyes = Game.Eyes();
            if (player == null || eyes == null || BodyOf(r) != null) return null;
            var prefab = Game.EnemyPrefab(r.body);
            if (prefab == null) return null;

            // Near the Home marker at night it comes there; otherwise from behind the player.
            Vector3 home = Vector3.zero;
            float hour = Game.Hour();
            bool night = hour >= 21f || hour < 5f;
            Vector3 origin = player.position;
            bool atHome = night && HomeMarker(out home) && Vector3.Distance(player.position, home) < 150f;
            if (atHome) origin = home;
            Vector3 back = -Vector3.ProjectOnPlane(eyes.forward, Vector3.up).normalized;
            if (back.sqrMagnitude < 0.01f) back = Vector3.back;
            back = Quaternion.Euler(0f, (float)(rng.NextDouble() * 60.0 - 30.0), 0f) * back;
            bool ground;
            Vector3 at = Game.ActorSpawn(origin + back * (60f + (float)rng.NextDouble() * 30f), out ground);
            if (!ground) return null;

            var root = Instantiate(prefab, at, Quaternion.LookRotation(-back));
            root.SetActive(true);
            root.name = Game.NativeName(prefab.name, Game.NextItemId());
            foreach (var fsm in root.GetComponents<PlayMakerFSM>()) fsm.SendEvent("Randomize");
            var b = Attach(r, root);
            b.Hunt = true;
            StartCoroutine(ApplyLater(b, 0.5f));

            for (int i = 0; i < 2; i++)
            {
                var ep = Game.EnemyPrefab(Escorts[rng.Next(Escorts.Length)]);
                if (ep == null) continue;
                Vector3 side = Vector3.Cross(Vector3.up, back) * (i == 0 ? 3f : -3f);
                var e = Instantiate(ep, Game.ActorSpawn(at + side + back * 2f, out ground), Quaternion.LookRotation(-back));
                e.SetActive(true);
                e.name = Game.NativeName(ep.name, Game.NextItemId());
                foreach (var fsm in e.GetComponents<PlayMakerFSM>()) fsm.SendEvent("Randomize");
                escorts.Add(e);
            }
            StartCoroutine(SendHunters(b));

            World.lastHuntDay = Game.Day();
            r.grudge = Mathf.Max(0, r.grudge - 20);
            Notice(atHome ? "Footsteps outside, in the dark. " + r.name + " has come for you." : "Engines behind you. " + r.name + " has come for you.");
            Plugin.Log.LogInfo(r.name + " hunts the player" + (atHome ? " at the Home marker" : "") + ", " + Vector3.Distance(player.position, at).ToString("0") + " m away, with " + escorts.Count + " raider(s).");
            return b;
        }

        // After the FSMs started: point them at the player (WW2's AI), or wake them with a scratch (health loss wakes
        // Detection, enemies.md).
        private IEnumerator SendHunters(Body b)
        {
            yield return new WaitForSeconds(1f);
            var player = Game.Global("Player");
            var hunters = new List<GameObject>(escorts);
            if (b.Root != null) hunters.Add(b.Root);
            foreach (var h in hunters)
            {
                if (h == null) continue;
                bool told = false;
                if (talkWindow) try { told = Ww2Talk.Inform(h, player); } catch (Exception) { }
                if (!told)
                {
                    var part = Game.Fsm(h, "Bodypart");
                    if (part != null) { part.FsmVariables.GetFsmFloat("Damage").Value = -0.01f; part.SendEvent("Damage"); }
                }
            }
        }

        // Escorts are ours (not registered): gone when their legend has left and the player is far.
        private void TidyEscorts()
        {
            escorts.RemoveAll(e => e == null);
            bool hunting = false;
            foreach (var b in bodies) if (b.Hunt) hunting = true;
            if (hunting) return;
            var player = Game.Player();
            foreach (var e in escorts.ToArray())
                if (player == null || Vector3.Distance(player.position, e.transform.position) > Plugin.DespawnRange.Value) { escorts.Remove(e); Destroy(e); }
        }

        internal static bool HomeMarker(out Vector3 at)
        {
            at = Vector3.zero;
            var items = Game.Proxy("ArrayList_Items");
            if (items == null || items._arrayList == null) return false;
            foreach (var entry in items._arrayList)
            {
                var go = entry as GameObject;
                if (go == null || !go.name.StartsWith("mapHomeIcon")) continue;
                at = go.transform.position - Vector3.up * 200f;   // spawned 200 m above the player (world.md)
                return true;
            }
            return false;
        }

        // ---- Stash ----

        internal void QueueStash(LegendRecord r)
        {
            if (r.temp) return;
            World.stashes.Add(new StashItem { legend = r.name, place = Names.Camp(r.lairName), world = r.lairWorld, band = r.Band });
            News(r, "With " + r.name + " dead, the stash at " + Names.Camp(r.lairName) + " is unguarded.", true);
        }

        private void LayOutStashes()
        {
            var player = Game.Player();
            if (player == null) return;
            foreach (var s in World.stashes)
            {
                if (s.laid) continue;
                Vector3 at = Game.ToScene(s.world);
                if (Vector3.Distance(player.position, at) > 150f) continue;
                bool ground;
                Game.Ground(at, out ground);
                if (!ground) continue;
                LayOut(s, at);
            }
        }

        internal int LayOut(StashItem s, Vector3 at)
        {
            var tier = StashTiers[Mathf.Clamp(s.band, 0, StashTiers.Length - 1)];
            // Armor plates are always in it, on top of the random things: players patch their armor after a big
            // fight (user, 2026-10-29). 1 / 2 / 2 / 3 by band.
            int plates = StashPlates(s.band);
            int count = plates + 3 + 2 * s.band, made = 0;
            GameObject first = null;
            for (int i = 0; i < count; i++)
            {
                float angle = i * 2.4f;
                bool ground;
                Vector3 p = Game.Ground(at + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (1f + i * 0.25f), out ground) + Vector3.up * 0.4f;
                var item = Game.CreateRegisteredItem(i < plates ? "armorplate" : tier[rng.Next(tier.Length)], p);
                if (item == null) continue;
                if (first == null) first = item;
                made++;
            }
            s.laid = true;
            if (first != null) stashLabels.Add(new Trophy { Item = first, T = new TrophyItem { legend = s.legend, signature = "stash" } });
            Plugin.Log.LogInfo("Laid out " + s.legend + "'s stash at " + s.place + ": " + made + " item(s).");
            Notice(s.legend + "'s stash: " + made + " things worth taking.");
            return made;
        }

        internal static int StashPlates(int band) { return 1 + (Mathf.Max(band, 0) + 1) / 2; }

        // ---- Car theft (opt-in) ----

        // The car the player sits in now (the player is parented into it, vehicles.md), else null.
        internal static GameObject CarOfPlayer()
        {
            var player = Game.Player();
            for (var t = player != null ? player.parent : null; t != null; t = t.parent)
            {
                var rb = t.GetComponent<Rigidbody>();
                if (rb != null && t.gameObject.layer == 8) return t.gameObject;
            }
            return null;
        }

        private GameObject playerCar;

        private void WatchPlayerCar()
        {
            var car = CarOfPlayer();
            float now = Game.Day() * 24f + Game.Hour();
            if (car != null)
            {
                if (World.playerCar != car.name) { World.playerCar = car.name; }
                playerCar = car;
                World.carSeenHour = now;
                foreach (var r in World.legends)
                    if (r.stolenCar == car.name)
                    {
                        r.stolenCar = null;
                        r.grudge = Mathf.Clamp(r.grudge + 10, 0, 100);
                        News(r, "You took your car back from " + r.name + ".", true);
                    }
                return;
            }
            if (playerCar == null && !string.IsNullOrEmpty(World.playerCar)) playerCar = GameObject.Find(World.playerCar);
            var player = Game.Player();
            if (playerCar != null && player != null && Vector3.Distance(player.position, playerCar.transform.position) < 1500f) World.carSeenHour = now;
        }

        // Once per simulated hour (WorldSim.Simulate).
        private void MaybeStealCar()
        {
            if (!Plugin.CarTheft.Value || playerCar == null || World.carSeenHour < 0f) return;
            float now = Game.Day() * 24f + Game.Hour();
            if (now - World.carSeenHour < 6f || CarOfPlayer() != null) return;
            Vector3 home;
            if (HomeMarker(out home) && Vector3.Distance(home, playerCar.transform.position) < 300f) return;
            if (playerCar.GetComponent<ConfigurableJoint>() != null) return;   // a trailer is hitched
            Vector3 carWorld = Game.ToWorld(playerCar.transform.position);
            foreach (var r in World.Living())
            {
                if (BodyOf(r) != null || r.stolenCar != null || (r.lairWorld - carWorld).magnitude > 3000f) continue;
                if (rng.NextDouble() < 0.2) { StealCar(r, playerCar); return; }
            }
        }

        internal void StealCar(LegendRecord r, GameObject car)
        {
            Vector3 stand = Game.ToScene(r.lairWorld);
            bool ground;
            Vector3 at = Game.Ground(stand + new Vector3(12f, 0f, 8f), out ground) + Vector3.up * 1.5f;
            var rb = car.GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.position = at; }
            car.transform.position = at;
            Physics.SyncTransforms();
            r.stolenCar = car.name;
            r.locationKnown = true;
            int band = r.Band;
            r.fame += 15;
            if (r.Band > band) BandUp(r, band);
            News(r, r.name + " stole your car and parked it at " + Names.Camp(r.lairName) + ".", true);
            Plugin.Log.LogInfo(r.name + " stole the player's car " + car.name + " to " + r.lairName + ".");
        }
    }
}
