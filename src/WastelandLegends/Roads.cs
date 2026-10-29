using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace WastelandLegends
{
    // M4: legends on Wild Wasteland 2's raider patrols. Found by name only (the PatrolMarker component, crew
    // children named "<Prefab>(Driver)" / "(Passenger)"), so nothing here needs WW2's types; without WW2 there are
    // simply no patrols. WW2 owns the patrol: we never destroy its NPCs or cars. (A named-car reward was tried and
    // dropped: a name on an ordinary car wasn't special enough. Legends drop a signature weapon instead, Trophies.cs.)
    internal partial class Runner
    {
        private readonly HashSet<int> patrolsSeen = new HashSet<int>();
        private float nextPatrolCheck;
        internal bool roadTest;   // bench: claim the next patrol whatever the chance and the daily limit

        // ---- Finding patrols ----

        internal static bool IsPatrolCar(GameObject car)
        {
            return car != null && car.GetComponent("PatrolMarker") != null;
        }

        internal static List<GameObject> PatrolCarsNear(Vector3 at, float range)
        {
            var cars = new List<GameObject>();
            foreach (var col in Physics.OverlapSphere(at, range, 1 << 8, QueryTriggerInteraction.Ignore))
            {
                var rb = col.attachedRigidbody;
                var car = rb != null ? rb.gameObject : null;
                if (car != null && !cars.Contains(car) && IsPatrolCar(car)) cars.Add(car);
            }
            return cars;
        }

        // Its seated crew: Health/Detection NPCs under the car named (Driver) / (Passenger); the driver first.
        internal static List<GameObject> Crew(GameObject car)
        {
            var crew = new List<GameObject>();
            foreach (var fsm in car.GetComponentsInChildren<PlayMakerFSM>(false))
            {
                if (fsm.FsmName != "Health") continue;
                var npc = fsm.gameObject;
                if (crew.Contains(npc) || Game.Fsm(npc, "Detection") == null) continue;
                if (npc.name.Contains("(Driver)")) crew.Insert(0, npc);
                else if (npc.name.Contains("(Passenger)")) crew.Add(npc);
            }
            return crew;
        }

        // ---- Claiming one ----

        private void WatchPatrols()
        {
            if (!Plugin.RoadAmbushes.Value || Time.realtimeSinceStartup < nextPatrolCheck) return;
            nextPatrolCheck = Time.realtimeSinceStartup + 2f;
            var player = Game.Player();
            if (player == null) return;
            foreach (var car in PatrolCarsNear(player.position, 300f))
            {
                if (!patrolsSeen.Add(car.GetInstanceID())) continue;
                var crew = Crew(car);
                if (crew.Count == 0 || !crew[0].CompareTag("Scrapyard")) continue;
                TryClaim(car, crew, false);
            }
        }

        internal Body TryClaim(GameObject car, List<GameObject> crew, bool force)
        {
            foreach (var b in bodies) if (b.Road) return null;   // one road legend at a time
            int day = Game.Day();
            if (!force && !roadTest && World.lastRoadDay >= day) return null;

            LegendRecord pick = null;
            foreach (var r in World.Living())
            {
                if (BodyOf(r) != null) continue;
                if (pick == null || r.grudge > pick.grudge || (r.grudge == pick.grudge && r.fame > pick.fame)) pick = r;
            }
            if (pick == null) return null;
            float chance = Plugin.RoadChance.Value + pick.grudge / 200f;
            if (!force && !roadTest && rng.NextDouble() >= chance)
            {
                Plugin.Log.LogInfo("Patrol " + car.name + " passed by without a legend (chance " + chance.ToString("0.00") + ").");
                return null;
            }

            // The crew member with the legend's own body type if there is one, else the driver.
            GameObject npc = crew[0];
            foreach (var c in crew) if (Game.BaseName(c.name.Replace("(Driver)", "").Replace("(Passenger)", "")) == pick.body) { npc = c; break; }

            World.lastRoadDay = day;
            var body = Attach(pick, npc);
            body.Road = true;
            body.Car = car;
            string log = Apply(body);
            Plugin.Log.LogInfo(pick.name + " rides with patrol " + car.name + " as " + npc.name + " (" + pick.health.ToString("0") + "/" + pick.maxHealth.ToString("0") + ", " + log + ")");
            return body;
        }

        // Seated crews don't run their own sight checks, so a road legend announces itself by distance.
        private void WatchRoadBodies()
        {
            var player = Game.Player();
            if (player == null) return;
            foreach (var b in bodies.ToArray())
            {
                if (!b.Road || b.Root == null) continue;
                float d = Vector3.Distance(player.position, b.Root.transform.position);
                if (!b.Seen && d < 120f) AnnounceRoad(b);
                else if (d > 700f) Release(b);
            }
        }

        private void AnnounceRoad(Body b)
        {
            b.Seen = true;
            b.R.encounters++;
            ShowCard(b.R);
            Say(b.R, MemoryTaunt(b.R) ?? FirstLine(b.R, Taunts.Road));
        }

        // From WW2's CrewBailedOut event (Ww2Talk): the seated legend body is about to be deleted, onFoot replaces it.
        internal void OnCrewBailedOut(GameObject seated, GameObject onFoot)
        {
            Body old = null;
            foreach (var b in bodies) if (b.Root == seated) old = b;
            if (old == null) return;
            float hp = old.Health.FsmVariables.GetFsmFloat("Health").Value;
            // Unhook the old seat before WW2's Destroy reaches it, so its OnDestroy doesn't count a departure.
            old.Despawning = true;
            var tag = seated.GetComponent<LegendTag>();
            if (tag != null) { tag.Runner = null; Destroy(tag); }
            bodies.Remove(old);
            MoveTo(old, onFoot, hp);
        }

        private void MoveTo(Body old, GameObject npc, float hp, string how = "bailed out of")
        {
            old.R.health = hp;
            var b = Attach(old.R, npc);
            b.Road = true;
            b.Car = old.Car;
            b.Seen = old.Seen; b.LoudTaunted = old.LoudTaunted; b.HurtTaunted = old.HurtTaunted;
            b.Dealt = old.Dealt; b.Taken = old.Taken; b.Combat = old.Combat;
            foreach (var kv in old.Weapons) b.Weapons[kv.Key] = kv.Value;
            // Its WeaponType picks a gun when the FSMs start: scale after that, as for camp bodies.
            StartCoroutine(ApplyLater(b, 0.5f));
            Plugin.Log.LogInfo(old.R.name + " " + how + " " + (old.Car != null ? old.Car.name : "the car") + " as " + npc.name + " (" + hp.ToString("0") + ").");
        }

        // Fallback without the event. The NPC WW2 just built on foot for a bailed-out crew member (Patrol.BailOut): made from the car's passenger
        // prefab (so it can be another raider type), registered in ArrayList_Items at once, and given its health a
        // moment later by a LateHealth component. Found as the newest registered actor with LateHealth near the seat;
        // physics queries and the health value weren't reliable in the same frame (first try lost the legend).
        // WW2 "Return to car" (CrewReturn.Seat) is the reverse: a new seated NPC in the same car with the old health
        // (set at once), then the one on foot deleted. Its CrewReboarded event is never sent, so look in the car first.
        private System.Collections.IEnumerator FollowBailOut(Body old, Vector3 seat, float hp)
        {
            if (old.Car != null)
                foreach (var npc in Crew(old.Car))
                {
                    var h = Game.Health(npc);
                    if (npc.GetComponent<LegendTag>() != null || h == null || Math.Abs(h.Value - hp) > 1f) continue;
                    MoveTo(old, npc, hp, "climbed back into");
                    yield break;
                }
            yield return null;
            GameObject best = null;
            float bestDist = 15f;
            var items = Game.Proxy("ArrayList_Items");
            if (items != null && items._arrayList != null)
                for (int i = items._arrayList.Count - 1, n = 0; i >= 0 && n < 30; i--, n++)
                {
                    var npc = items._arrayList[i] as GameObject;
                    if (npc == null || npc.layer != Game.ActorLayer || npc.GetComponent<LegendTag>() != null) continue;
                    if (npc.GetComponent("LateHealth") == null || Game.Fsm(npc, "Health") == null) continue;
                    float d = Vector3.Distance(seat, npc.transform.position);
                    if (d < bestDist) { best = npc; bestDist = d; }
                }
            if (best == null) { Plugin.Log.LogInfo(old.R.name + " was lost in a bail-out (no new NPC on foot near the seat)."); yield break; }

            MoveTo(old, best, hp);
        }

        // Let go of a patrol NPC without touching it: WW2 keeps it (with its legend stats) until it cleans up.
        internal void Release(Body b, bool remember = true)
        {
            if (b.Root != null && b.Applied) b.R.health = Mathf.Max(1f, b.Health.FsmVariables.GetFsmFloat("Health").Value);
            b.Despawning = true;
            var tag = b.Root != null ? b.Root.GetComponent<LegendTag>() : null;
            if (tag != null) { tag.Runner = null; Destroy(tag); }
            bodies.Remove(b);
            Plugin.Log.LogInfo(b.R.name + " rides off (" + b.R.health.ToString("0") + "/" + b.R.maxHealth.ToString("0") + ").");
            if (remember && (b.Combat || b.Seen)) EndEncounter(b, b.Combat ? MemoryEntry.Ran : MemoryEntry.Met);
        }
    }
}
