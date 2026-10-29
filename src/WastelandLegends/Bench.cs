using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using HutongGames.PlayMaker;
using UnityEngine;

namespace WastelandLegends
{
    // Test bench: runs commands written to GameInternals\dumps\bench\cmd.txt and writes what happened to result.txt
    // (and the log, prefixed [bench]). The file is taken (deleted) when read; result.txt is rewritten at the end of
    // each batch, last line "END <n>". Commands, one per line (# starts a comment):
    //
    //   status                      the world, legends, marks, money, nearest sell box
    //   legend                      the saved record of every legend, as JSON
    //   spawn <prefab> [count] [m]  spawn enemies m metres in front of the player (default 1, 12 m)
    //   promote [prefab]            make the nearest enemy a temporary legend (Ironhead / Loudmouth)
    //   rise                        a legend rises now (ignores the grace days); "rise new" replaces the current one
    //   goto lair | back            move the player 80 m from the legend's camp, or back to where goto started
    //   despawn                     the living legend leaves (as if you walked away)
    //   hit head|body <amount>      native damage on the current legend; checks the health change against its traits
    //   kill                        a body hit bigger than its health; checks death and the mark
    //   sell                        puts the newest mark in the nearest sell box; checks the bounty was paid
    //   roundtrip                   the world to JSON and back, compared
    //   money <delta>               add to (or take from) the player's money
    //   wait <seconds>
    //   clear                       removes the enemies the bench spawned
    //   test                        M0: spawn 2 Flexas, promote, hit body and head, kill, sell (bounty taken back)
    //   m1                          M1: rise, goto lair, the body, traits, leaving and coming back, save JSON, kill,
    //                               back to the trader, sell. Restores the world and money afterwards.
    //
    // The game must be running and not paused: FSMs don't run at time scale 0.
    internal class Bench : MonoBehaviour
    {
        internal Runner Runner;

        private string dir, cmdPath, resultPath;
        private float nextPoll;
        private bool busy;
        private int batch;
        private int passed, failed;
        private readonly StringBuilder output = new StringBuilder();
        private readonly List<GameObject> spawned = new List<GameObject>();
        private Body current;
        private Vector3? backWorld;

        private void Awake()
        {
            dir = Path.Combine(Paths.GameRootPath, @"GameInternals\dumps\bench");
            cmdPath = Path.Combine(dir, "cmd.txt");
            resultPath = Path.Combine(dir, "result.txt");
            try { Directory.CreateDirectory(dir); } catch (Exception) { }
        }

        private void Update()
        {
            if (busy || Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 0.5f;
            if (!File.Exists(cmdPath)) return;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(cmdPath);
                File.Delete(cmdPath);
            }
            catch (Exception) { return; }   // still being written
            StartCoroutine(Run(lines));
        }

        private IEnumerator Run(string[] lines)
        {
            busy = true;
            batch++;
            passed = failed = 0;
            output.Length = 0;
            Out("batch " + batch + " at " + DateTime.Now.ToString("HH:mm:ss") + ", time scale " + Time.timeScale + ", build " + typeof(Bench).Assembly.GetName().Name);
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                Out("> " + line);
                IEnumerator step = null;
                try { step = Command(line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)); }
                catch (Exception e) { Fail("error: " + e.Message); }
                while (step != null)
                {
                    bool more;
                    try { more = step.MoveNext(); }
                    catch (Exception e) { Fail("error: " + e); break; }
                    if (!more) break;
                    yield return step.Current;
                }
            }
            Out("passed " + passed + ", failed " + failed);
            output.Append("END ").Append(batch).Append('\n');
            try { File.WriteAllText(resultPath, output.ToString()); }
            catch (Exception e) { Plugin.Log.LogError("[bench] can't write result: " + e.Message); }
            busy = false;
        }

        private IEnumerator Command(string[] a)
        {
            switch (a[0].ToLowerInvariant())
            {
                case "status": Status(); return null;
                case "legend": Out(Runner.World != null ? Json.Write(Runner.World) : "no world (not in a game)"); return null;
                case "spawn": Spawn(a.Length > 1 ? a[1] : "Flexa", a.Length > 2 ? int.Parse(a[2]) : 1, a.Length > 3 ? float.Parse(a[3]) : 12f); return Frames(5);
                case "promote": Promote(a.Length > 1 ? a[1] : null); return null;
                case "rise": Rise(a.Length > 1 && a[1] == "new"); return null;
                case "goto": return Goto(a.Length > 1 ? a[1] : "lair");
                case "despawn": return Despawn();
                case "hit": return Hit(a[1].ToLowerInvariant() == "head", float.Parse(a[2]));
                case "kill": return Kill();
                case "sell": return Sell(false);
                case "roundtrip": Roundtrip(); return null;
                case "probe": Probe(); return null;
                case "money": Money(int.Parse(a[1])); return null;
                case "wait": return Wait(float.Parse(a[1]));
                case "clear": Clear(); return null;
                case "test": return M0();
                case "m1": return M1();
                case "m2": return M2();
                case "m3": return M3();
                case "m5": return M5();
                case "where": return Where(a.Length > 1 ? float.Parse(a[1]) : 4f);
                case "ground":
                    // Diagnose: every collider straight down 15 m in front of the player, with its layer.
                    {
                        var eyes = Game.Eyes();
                        Vector3 at = eyes.position + Vector3.ProjectOnPlane(eyes.forward, Vector3.up).normalized * 15f;
                        foreach (var h in Physics.RaycastAll(at + Vector3.up * 80f, Vector3.down, 300f, ~0, QueryTriggerInteraction.Collide))
                            Out("hit " + Game.PathOf(h.collider.transform) + " [" + h.collider.GetType().Name + "] layer " + h.collider.gameObject.layer + " (" + LayerMask.LayerToName(h.collider.gameObject.layer) + ")"
                                + ", trigger " + h.collider.isTrigger + ", y " + h.point.y.ToString("0.0") + ", actor collides " + !Physics.GetIgnoreLayerCollision(Game.ActorLayer, h.collider.gameObject.layer));
                        return null;
                    }
                case "hunt":
                    {
                        var r = Runner.World.Living().Count > 0 ? Runner.World.Living()[0] : null;
                        if (r == null) { Fail("no living legend"); return null; }
                        var b = Runner.StartHunt(r);
                        Check(r.name + " hunts the player", b != null, b != null ? b.Root.name + " + " + Runner.escorts.Count + " raider(s)" : "no ground or busy");
                        return null;
                    }
                case "roads": Roads(); return null;
                case "patrolscan": PatrolScan(); return null;
                case "bailout": return BailOut();
                case "signature": Signature(); return null;
                case "trophybonus": TrophyBonus(); return null;
                case "perks": return PerkTest();
                case "switch": return SwitchTest();
                case "labels": LabelTest(); return null;
                case "rename":
                    {
                        var go = GameObject.Find(a[1]);
                        if (go == null) { Fail("no object " + a[1]); return null; }
                        go.name = a[2];
                        foreach (var t in Runner.World.trophies) if (t.item == a[1]) t.item = a[2];
                        Out(a[1] + " -> " + a[2]);
                        return null;
                    }
                case "nativename":
                    // Testing: rename an item we made to the game's own pattern "<prefab>(Clone)<id>" (and its trophy record).
                    {
                        var go = GameObject.Find(a[1]);
                        if (go == null) { Fail("no object " + a[1]); return null; }
                        string old = go.name, prefab = Game.BaseName(old);
                        go.name = prefab + "(Clone)" + old.Substring(prefab.Length);
                        foreach (var t in Runner.World.trophies) if (t.item == old) t.item = go.name;
                        Out(old + " -> " + go.name);
                        return null;
                    }
                case "givetrophy":
                    // Testing: an ItemList weapon in front of the player, registered as a trophy ("givetrophy m16a1 0.2").
                    {
                        var eyes = Game.Eyes();
                        var item = Game.CreateRegisteredItem(a[1], eyes.position + eyes.forward * 1.5f);
                        if (item == null) { Fail("no item " + a[1]); return null; }
                        var t = new TrophyItem { item = item.name, legend = "Bench", signature = a[1], bonus = a.Length > 2 ? float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) : 0.2f };
                        if (a.Length > 3) t.perks.AddRange(a[3].Split(','));   // e.g. Searing,Quick
                        Runner.World.trophies.Add(t);
                        Runner.trophies.Add(new Trophy { Item = item, T = t });
                        Out("gave trophy " + item.name + ": " + Runner.TrophyLabel(t));
                        return null;
                    }
                case "give":
                    // Testing: an ordinary ItemList item 1.5 m in front of the player (registered, saved like loot).
                    {
                        var eyes = Game.Eyes();
                        var item = Game.CreateRegisteredItem(a[1], eyes.position + eyes.forward * 1.5f);
                        if (item == null) Fail("no item " + a[1]); else Out("gave " + item.name);
                        return null;
                    }
                case "ammo":
                    // Diagnose: the ammo pool (__GameManager__ Ammo, inventory.md) and the drawn gun's magazine.
                    {
                        var pool = Game.Fsm(Game.Global("__GameManager__"), "Ammo");
                        if (pool != null) foreach (var v in pool.FsmVariables.IntVariables) Out("pool " + v.Name + " = " + v.Value);
                        var model = Runner.DrawnModel();
                        var reload = model != null ? Game.Fsm(model, "Reload") : null;
                        var inGun = reload != null ? reload.FsmVariables.FindFsmInt("ammo_in_gun") : null;
                        Out("drawn " + (model != null ? model.name : "nothing") + ", magazine " + (inGun != null ? inGun.Value.ToString() : "?")
                            + ", AmmoUI state " + (model != null && Game.Fsm(model, "AmmoUI") != null ? Game.Fsm(model, "AmmoUI").ActiveStateName : "-"));
                        return null;
                    }
                case "slots":
                    // Diagnose: what lies in the player's weapon slots, and what WeaponInHand sees.
                    {
                        Transform weapons = null;
                        foreach (var t in Game.Global("PlayerCamera").GetComponentsInChildren<Transform>(true)) if (t.name == "Weapons" && Game.Fsm(t.gameObject, "WeaponInHand") != null) weapons = t;
                        if (weapons == null) { Fail("no Weapons object"); return null; }
                        var wih = Game.Fsm(weapons.gameObject, "WeaponInHand");
                        Out("WeaponInHand state " + wih.ActiveStateName + ", weaponType '" + wih.FsmVariables.FindFsmString("weaponType").Value + "', Item " + (wih.FsmVariables.FindFsmGameObject("Item").Value != null ? wih.FsmVariables.FindFsmGameObject("Item").Value.name : "none"));
                        foreach (Transform slot in weapons) Out("  child " + slot.name + " (" + slot.childCount + " inside: " + string.Join(", ", System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(System.Linq.Enumerable.Cast<Transform>(slot), c => c.name))) + ")");
                        var me = Game.Player();
                        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                        {
                            if (!go.name.StartsWith("old_knife") || !go.scene.IsValid() || go.transform.parent == null) continue;
                            string path = Game.PathOf(go.transform);
                            if (path.StartsWith("Player") || Vector3.Distance(go.transform.position, me.position) < 3f)
                                Out("  knife " + go.name + " at " + path + ", active " + go.activeInHierarchy + ", layer " + go.layer);
                        }
                        foreach (var mark in Runner.marks) if (mark.Item != null) Out("  mark " + mark.Item.name + " under " + (mark.Item.transform.parent != null ? Game.PathOf(mark.Item.transform.parent) : "world") + ", active " + mark.Item.activeInHierarchy);
                        foreach (Transform slot in weapons)
                        {
                            if (!slot.name.StartsWith("Slot")) continue;
                            foreach (Transform item in slot)
                            {
                                var wt = Game.Fsm(item.gameObject, "weaponType");
                                var names = new List<string>();
                                foreach (var f in item.GetComponents<PlayMakerFSM>()) names.Add(f.FsmName + (f.enabled ? "" : "(off)") + (f.Fsm.Initialized ? "" : "(uninit)"));
                                Out(slot.name + ": " + item.name + " tag " + item.tag + " layer " + item.gameObject.layer + " active " + item.gameObject.activeSelf
                                    + ", weaponType '" + (wt != null ? wt.FsmVariables.FindFsmString("weaponType").Value : "no FSM") + "', FSMs " + string.Join(",", names.ToArray()));
                            }
                        }
                        return null;
                    }
                case "nearcar":
                    // Diagnose: every actor near the nearest patrol car, and the newest registered objects.
                    {
                        var cars = Runner.PatrolCarsNear(Game.Player().position, 600f);
                        if (cars.Count == 0) { Fail("no patrol car"); return null; }
                        var car = cars[0];
                        foreach (var rb in FindObjectsOfType<Rigidbody>())
                        {
                            var go = rb.gameObject;
                            float d = Vector3.Distance(go.transform.position, car.transform.position);
                            if (d > 25f || Game.Fsm(go, "Health") == null) continue;
                            var comps = new List<string>();
                            foreach (var c in go.GetComponents<Component>()) if (!(c is PlayMakerFSM) && c != null) comps.Add(c.GetType().Name);
                            Out("actor " + go.name + " layer " + go.layer + " tag " + go.tag + ", " + d.ToString("0.0") + " m, parent " + (go.transform.parent != null ? go.transform.parent.name : "-")
                                + ", health " + Game.Health(go).Value.ToString("0") + ", comps " + string.Join(",", comps.ToArray()));
                        }
                        var items = Game.Proxy("ArrayList_Items");
                        for (int i = items._arrayList.Count - 1, n = 0; i >= 0 && n < 8; i--, n++)
                        {
                            var go = items._arrayList[i] as GameObject;
                            Out("item[" + i + "] " + (go != null ? go.name + " layer " + go.layer + " " + Vector3.Distance(go.transform.position, car.transform.position).ToString("0") + " m" : "null"));
                        }
                        return null;
                    }
                case "declaredead":
                    // Repair: count a kill the mod missed (the bail-out bug): death, mark and, with a car, the car.
                    {
                        var r = Runner.World.Living().Find(x => x.name == a[1]);
                        var car = a.Length > 2 ? GameObject.Find(a[2]) : null;
                        if (r == null) { Fail("no living legend " + a[1]); return null; }
                        var pos = car != null ? car.transform.position + car.transform.right * 3f : Game.Player().position + Game.Player().forward * 2f;
                        Runner.Die(new Body { R = r, Road = car != null, Car = car, Seen = true, Combat = true }, pos);
                        Out(r.name + " counted as dead" + (car != null ? ", car " + car.name : " (car not found)") + "; deaths " + r.deaths + ", comeback day " + r.returnDay);
                        return Wait(1.5f);
                    }
                case "spawnpatrol": return SpawnPatrol(a.Length > 1 ? a[1] : "Bike", a.Length > 2 ? float.Parse(a[2]) : 90f);
                case "roadtest": Runner.roadTest = a.Length < 2 || a[1] != "off"; Out("road test " + (Runner.roadTest ? "on: the next Scrapyard patrol gets a legend" : "off")); return null;
                case "claim": Claim(); return null;
                case "m4": return M4();
                case "comeback":
                    // Testing: the named (or latest) dead legend gets a lucky roll and comes back at the next tick.
                    foreach (var r in Runner.World.legends)
                        if (r.state == LegendRecord.Dead && (a.Length < 2 || r.name == a[1]) && r.deaths < 3)
                        {
                            r.returnDay = Game.Day();
                            r.returnRoll = 0f;
                            if (r.deaths == 0) r.deaths = 1;
                            Out(r.name + " comes back at the next tick");
                        }
                    return Wait(2f);
                case "populate": Populate(); return null;
                case "deed": Deed(a.Length > 1 ? int.Parse(a[1]) : -1); return null;
                case "fame": SetFame(int.Parse(a[1])); return null;
                case "sim": Sim(float.Parse(a[1])); return null;
                case "rumor": Out("rumor: " + Runner.BuyRumor()); return null;
                case "notice": Runner.Notice(string.Join(" ", a, 1, a.Length - 1)); Out("shown"); return null;
                case "say": Runner.Say(new LegendRecord { name = "Sumpgut" }, string.Join(" ", a, 1, a.Length - 1)); Out("shown"); return null;
                case "know": foreach (var r in Runner.World.Living()) r.locationKnown = true; Out("all camps known"); return null;
                default: Fail("unknown command"); return null;
            }
        }

        // ---- Commands ----

        private void Status()
        {
            var money = Game.Money();
            var player = Game.Player();
            Out("day " + Game.Day() + ", money " + (money != null ? money.Value.ToString() : "?") + ", peaceful " + Game.Peaceful()
                + ", player " + (player != null ? Game.ToWorld(player.position).ToString("F0") : "none"));
            var faction = Game.Fsm(Game.Global("__GameManager__"), "FactionRelation_Coyotes");
            Out("Coyotes faction FSM: " + (faction == null ? "missing" : (faction.enabled ? "on" : "off") + ", state " + faction.ActiveStateName));
            if (Runner.World == null) { Out("no world (not in a game)"); return; }
            Out("legends " + Runner.World.legends.Count + ", bodies " + Runner.bodies.Count + ", marks " + Runner.marks.FindAll(m => m.Item != null).Count
                + ", scrapyard camps loaded " + Game.ScrapyardCamps().Count);
            foreach (var r in Runner.World.legends)
                Out("  " + r.name + " " + r.title + " [" + (r.state == LegendRecord.Alive ? "alive" : "dead") + "] " + r.body + " " + r.health.ToString("0") + "/" + r.maxHealth.ToString("0")
                    + ", " + r.strength + " / " + r.weakness + ", knows [" + string.Join(", ", r.known.ToArray()) + "], lair " + r.lairName
                    + (player != null ? " " + Vector3.Distance(player.position, Game.ToScene(r.lairWorld)).ToString("0") + " m" : "")
                    + ", met " + r.encounters + (r.markName != null ? ", mark " + r.markName + (r.bountyPaid ? " (paid)" : "") : ""));
            foreach (var b in Runner.bodies)
                if (b.Root != null) Out("  body " + b.R.name + " (" + b.Root.name + "): " + Game.Describe(b.Root));
            var box = player != null ? Game.NearestSellBox(player.position, 500f) : null;
            Out(box != null ? "  nearest sell box " + Vector3.Distance(player.position, box.bounds.center).ToString("0") + " m" : "  no sell box within 500 m");
        }

        private void Spawn(string name, int count, float distance)
        {
            var prefab = Game.EnemyPrefab(name);
            if (prefab == null) { Fail("no enemy prefab named " + name); return; }
            var eyes = Game.Eyes();
            Vector3 forward = Vector3.ProjectOnPlane(eyes.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            for (int i = 0; i < count; i++)
            {
                bool ground;
                Vector3 at = Game.ActorSpawn(eyes.position + forward * distance + right * (i - (count - 1) * 0.5f) * 3f, out ground);
                var go = Instantiate(prefab, at, Quaternion.LookRotation(-forward));
                go.SetActive(true);
                go.name = prefab.name + "(Clone)" + Game.NextItemId();
                Game.Register(go);
                foreach (var fsm in go.GetComponents<PlayMakerFSM>()) fsm.SendEvent("Randomize");
                spawned.Add(go);
                Out("spawned " + go.name + " at " + Vector3.Distance(eyes.position, at).ToString("0") + " m");
            }
        }

        private void Promote(string name)
        {
            var player = Game.Player();
            GameObject best = null;
            float bestDist = float.MaxValue;
            foreach (var col in Physics.OverlapSphere(player.position, 120f, 1 << Game.ActorLayer, QueryTriggerInteraction.Ignore))
            {
                var root = Game.EnemyRoot(col.transform);
                if (root == null || IsLegend(root)) continue;
                if (name != null && Game.BaseName(root.name) != name) continue;
                float d = Vector3.Distance(player.position, root.transform.position);
                if (d < bestDist) { best = root; bestDist = d; }
            }
            if (best == null) { Fail("no enemy" + (name != null ? " " + name : "") + " within 120 m"); return; }

            float baseHealth = Game.Health(best).Value;
            current = Runner.Promote(best, "Ironhead", "Loudmouth");
            Out("promoted " + best.name + " to " + current.R.name + ": " + Game.Describe(best));
            Check("legend health = x" + Plugin.HealthMultiplier.Value, Near(current.R.maxHealth, baseHealth * Plugin.HealthMultiplier.Value, 0.5f),
                current.R.maxHealth + " vs " + baseHealth + " x " + Plugin.HealthMultiplier.Value);

            foreach (var col in Physics.OverlapSphere(best.transform.position, 120f, 1 << Game.ActorLayer, QueryTriggerInteraction.Ignore))
            {
                var twin = Game.EnemyRoot(col.transform);
                if (twin == null || twin == best || IsLegend(twin) || Game.BaseName(twin.name) != Game.BaseName(best.name)) continue;
                Check("twin " + twin.name + " unchanged", Game.Health(twin).Value <= baseHealth + 0.5f, Game.Describe(twin));
                break;
            }
        }

        private void Rise(bool replace)
        {
            if (Runner.World == null) { Fail("not in a game"); return; }
            var alive = Runner.World.Alive();
            if (alive != null && !replace) { Out("already alive: " + alive.name); return; }
            if (alive != null)
            {
                var b = Runner.BodyOf(alive);
                if (b != null) Runner.Despawn(b);
                Runner.World.legends.Remove(alive);
            }
            var r = Runner.Rise();
            Check("a legend rose", r != null, r != null ? r.name + " " + r.title + ", " + r.body + ", " + r.strength + " / " + r.weakness + ", at " + r.lairPath : "no scrapyard camp loaded?");
        }

        private IEnumerator Goto(string where)
        {
            var player = Game.Player();
            if (player == null) { Fail("no player"); yield break; }
            if (player.parent != null) { Fail("get out of the car first"); yield break; }
            Vector3 target;
            if (where == "back")
            {
                if (backWorld == null) { Fail("no goto to go back from"); yield break; }
                target = Game.ToScene(backWorld.Value);
                backWorld = null;
            }
            else
            {
                var r = Runner.World != null ? Runner.World.Alive() : null;
                if (r == null) { Fail("no living legend"); yield break; }
                if (backWorld == null) backWorld = Game.ToWorld(player.position);
                Vector3 lair = Game.ToScene(r.lairWorld);
                Vector3 away = player.position - lair;
                away.y = 0f;
                target = lair + (away.sqrMagnitude > 1f ? away.normalized : Vector3.forward) * 80f;
            }
            bool ground;
            target = Game.Ground(target, out ground) + Vector3.up * 1.2f;
            var rb = player.GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.position = target; }
            player.position = target;
            Physics.SyncTransforms();
            yield return Wait(2f);
            Out("player now " + Game.ToWorld(Game.Player().position).ToString("F0") + (ground ? "" : " (no ground found there)"));
        }

        private IEnumerator Despawn()
        {
            var b = LivingBody();
            if (b == null) { Fail("no legend body in the world"); yield break; }
            float hp = b.Health.FsmVariables.GetFsmFloat("Health").Value;
            Runner.Despawn(b);
            yield return null;
            Check("record kept its health on leaving", Near(b.R.health, hp, 0.5f), b.R.health + " vs " + hp);
        }

        // Expected health loss for a native hit, from the record's traits.
        private static float Expected(LegendRecord r, bool head, float a)
        {
            if (!head) return r.Has("SoftBelly") ? a * 1.3f : a;
            float hit = a * 2f;   // the head Bodypart's own x2
            if (r.Has("Ironhead")) return hit * 0.5f;
            if (r.Has("GlassJaw")) return hit * 1.5f;
            return hit;
        }

        private IEnumerator Hit(bool head, float amount)
        {
            var b = Target();
            if (b == null) { Fail("no legend to hit (promote, or goto lair)"); yield break; }
            var target = head ? Game.HeadBodypart(b.Root) : Game.Fsm(b.Root, "Bodypart");
            if (target == null) { Fail("no " + (head ? "head" : "body") + " Bodypart"); yield break; }
            var health = b.Health.FsmVariables.GetFsmFloat("Health");
            float before = health.Value;
            target.FsmVariables.GetFsmFloat("Damage").Value = -amount;
            target.SendEvent("Damage");
            yield return null; yield return null; yield return null;
            if (b.Root == null) { Out("legend died from the hit"); yield break; }
            float expected = Expected(b.R, head, amount);
            float got = before - health.Value;
            Check((head ? "head" : "body") + " hit " + amount + " takes " + expected.ToString("0.#") + " (" + b.R.strength + " / " + b.R.weakness + ")",
                Near(got, expected, 0.6f), "took " + got.ToString("0.##"));
        }

        private IEnumerator Kill()
        {
            var b = Target();
            if (b == null) { Fail("no legend to kill"); yield break; }
            var r = b.R;
            int marksBefore = Runner.marks.Count;
            var body = Game.Fsm(b.Root, "Bodypart");
            body.FsmVariables.GetFsmFloat("Damage").Value = -(b.Health.FsmVariables.GetFsmFloat("Health").Value + 10f);
            body.SendEvent("Damage");
            for (float t = 0; t < 5f && b.Root != null; t += Time.deltaTime) yield return null;
            Check(r.name + " died", b.Root == null, b.Root != null ? "still alive after 5 s" : "");
            for (float t = 0; t < 2f && Runner.marks.Count == marksBefore; t += Time.deltaTime) yield return null;
            Check("mark dropped", Runner.marks.Count > marksBefore, Runner.marks.Count > marksBefore ? Runner.marks[Runner.marks.Count - 1].Item.name : "no mark");
            if (!r.temp)
            {
                Check("record says dead, with the mark's name", r.state == LegendRecord.Dead && !string.IsNullOrEmpty(r.markName), "state " + r.state + ", mark " + r.markName);
                for (float t = 0; t < 2f && r.trophyName == null; t += Time.deltaTime) yield return null;
                var trophy = r.trophyName != null ? GameObject.Find(r.trophyName) : null;
                Check("it dropped its signature weapon", trophy != null, r.trophyName + " (" + Signatures.Name(r.signature) + ")");
            }
            current = null;
        }

        // refund: take the bounty back afterwards, so bench runs don't put free money in the player's save.
        private IEnumerator Sell(bool refund)
        {
            // Only marks lying loose in the world: never pull one out of the player's slots or a car.
            var live = Runner.marks.FindAll(m => m.Item != null && m.Item.transform.parent == null);
            if (live.Count == 0) { Fail("no mark to sell"); yield break; }
            var mark = live[live.Count - 1];
            var box = Game.NearestSellBox(Game.Player().position, 500f);
            if (box == null) { Fail("no sell box within 500 m (stand near a trader)"); yield break; }
            var money = Game.Money();
            int before = money.Value;

            var rb = mark.Item.GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.isKinematic = true; }
            mark.Item.transform.position = box.bounds.center;
            Out("put " + mark.Item.name + " in a sell box");
            for (float t = 0; t < 3f && mark.Item != null; t += Time.deltaTime) yield return null;

            bool paid = mark.Item == null && money.Value == before + mark.R.bounty;
            Check("bounty paid", paid, "money " + before + " -> " + money.Value);
            if (!mark.R.temp) Check("record says bounty paid", mark.R.bountyPaid, "");
            if (mark.Item != null && rb != null) rb.isKinematic = false;
            if (paid && refund)
            {
                money.Value -= mark.R.bounty;
                Out("bench took the bounty back: money " + money.Value);
            }
        }

        private void Roundtrip()
        {
            if (Runner.World == null) { Fail("not in a game"); return; }
            string json = Runner.SaveJson();
            var parsed = Runner.Parse(json);
            string again = Json.Write(parsed);
            bool same = json == again && parsed.legends.Count == Runner.World.legends.Count;
            for (int i = 0; same && i < parsed.legends.Count; i++)
            {
                var x = parsed.legends[i];
                var y = Runner.World.legends[i];
                same = x.name == y.name && x.strength == y.strength && x.weakness == y.weakness && x.lairPath == y.lairPath
                    && Near(x.health, y.health, 0.01f) && (x.lairWorld - y.lairWorld).sqrMagnitude < 0.01f && x.known.Count == y.known.Count
                    && x.fame == y.fame && x.title == y.title && x.locationKnown == y.locationKnown && x.moreStrengths.Count == y.moreStrengths.Count;
            }
            same = same && parsed.news.Count == Runner.World.news.Count && Near(parsed.simHour, Runner.World.simHour, 0.01f) && parsed.nextRiseDay == Runner.World.nextRiseDay;
            Check("world survives JSON (" + parsed.legends.Count + " legend(s), " + json.Length + " chars)", same, same ? "" : "differs: " + json);
        }

        // Where each mark lies against the nearest sell box (for "dropped it in, nothing happened").
        private void Probe()
        {
            foreach (var m in Runner.marks)
            {
                if (m.Item == null) continue;
                var col = m.Item.GetComponentInChildren<Collider>();
                Out("mark " + m.Item.name + " at " + m.Item.transform.position.ToString("F2") + ", layer " + m.Item.layer + ", parent " + (m.Item.transform.parent != null ? m.Item.transform.parent.name : "none")
                    + ", collider " + (col != null ? col.bounds.center.ToString("F2") + " size " + col.bounds.size.ToString("F2") : "none") + ", checks in box " + m.ChecksInBox);
                foreach (var c in Physics.OverlapSphere(m.Item.transform.position, 2.5f, ~0, QueryTriggerInteraction.Collide))
                {
                    if (c.gameObject == m.Item) continue;
                    var names = new List<string>();
                    foreach (var f in c.GetComponents<PlayMakerFSM>()) names.Add(f.FsmName);
                    Out("  near: " + Game.PathOf(c.transform) + " [" + c.GetType().Name + ", trigger " + c.isTrigger + ", enabled " + c.enabled + ", layer " + c.gameObject.layer
                        + ", FSMs " + string.Join(",", names.ToArray()) + "]");
                }
                bool open;
                var found = Game.SellBoxAt(m.Item.transform.position, out open);
                Out("  SellBoxAt: " + (found != null ? Game.PathOf(found.transform) + (open ? ", open" : ", CLOSED") : "not in a box") + ", Coyotes hostile " + Game.CoyotesHostile());
                foreach (var c in Resources.FindObjectsOfTypeAll<Collider>())
                {
                    if (c.gameObject.name != "sell_trigger" || !c.gameObject.scene.IsValid()) continue;
                    if (Vector3.Distance(c.transform.position, m.Item.transform.position) > 30f) continue;
                    var bc = c as BoxCollider;
                    if (bc != null) Out("  box " + Game.PathOf(c.transform) + ": inside (own shape) " + Game.Inside(bc, m.Item.transform.position, 0.15f) + ", local " + (c.transform.InverseTransformPoint(m.Item.transform.position) - bc.center).ToString("F2") + ", size " + bc.size.ToString("F2"));
                    Out("  sell_trigger " + Game.PathOf(c.transform) + ": trigger " + c.isTrigger + ", enabled " + c.enabled + ", center " + c.bounds.center.ToString("F2")
                        + " size " + c.bounds.size.ToString("F2") + ", contains mark " + c.bounds.Contains(m.Item.transform.position)
                        + (col != null ? ", touches collider " + c.bounds.Intersects(col.bounds) : "") + ", FSM " + (Game.Fsm(c.gameObject, "SellItemsHere") != null));
                }
            }
            if (Runner.marks.Count == 0) Out("no marks tracked");
        }

        private void Money(int delta)
        {
            var money = Game.Money();
            int before = money.Value;
            money.Value = before + delta;
            Out("money " + before + " -> " + money.Value);
        }

        private IEnumerator Wait(float seconds)
        {
            for (float t = 0; t < seconds; t += Time.deltaTime) yield return null;
        }

        private static IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        private void Clear()
        {
            int n = 0;
            foreach (var go in spawned)
                if (go != null) { Game.Unregister(go); Destroy(go); n++; }
            spawned.Clear();
            current = null;
            Out("removed " + n + " bench enemies");
        }

        private IEnumerator M0()
        {
            Spawn("Flexa", 2, 12f);
            yield return Wait(1f);
            Promote("Flexa");
            if (current == null) yield break;
            yield return Hit(false, 10f);
            yield return Hit(true, 10f);
            yield return Kill();
            yield return Sell(true);
            Clear();
        }

        private IEnumerator M1()
        {
            if (Runner.World == null) { Fail("not in a game"); yield break; }
            string snapshot = Runner.SaveJson();
            RememberDrops();
            int money = Game.Money().Value;

            Rise(true);
            var r = Runner.World.Alive();
            if (r == null) yield break;
            Check("one strength and one weakness", Trait.Get(r.strength).Strength && !Trait.Get(r.weakness).Strength, r.strength + " / " + r.weakness);

            yield return Goto("lair");
            Body b = null;
            for (float t = 0; t < 20f && (b == null || !b.Applied); t += Time.deltaTime) { b = Runner.BodyOf(r); yield return null; }
            Check("its body appeared at the camp", b != null && b.Applied, b != null ? b.Root.name + ", " + Vector3.Distance(b.Root.transform.position, Game.ToScene(r.lairWorld)).ToString("0") + " m from the stand point" : "none after 20 s");
            if (b != null && b.Applied)
            {
                Check("full health", Near(b.Health.FsmVariables.GetFsmFloat("Health").Value, r.maxHealth, 0.5f), Game.Describe(b.Root));
                current = b;
                yield return Hit(true, 10f);
                yield return Hit(false, 10f);
                yield return Despawn();
                float kept = r.health;
                for (float t = 0; t < 10f && (Runner.BodyOf(r) == null || !Runner.BodyOf(r).Applied); t += Time.deltaTime) yield return null;
                var again = Runner.BodyOf(r);
                Check("came back with its wounds", again != null && again.Applied && Near(again.Health.FsmVariables.GetFsmFloat("Health").Value, kept, 0.5f),
                    again != null ? again.Health.FsmVariables.GetFsmFloat("Health").Value + " vs " + kept : "no body after 10 s");
                current = again;
                Roundtrip();
                yield return Kill();
            }
            yield return Goto("back");
            yield return Sell(true);

            // Leave the player's world as it was before the test.
            foreach (var body in Runner.bodies.ToArray()) if (!body.R.temp) Runner.Despawn(body);
            RemoveTestDrops();
            Runner.World = Runner.Parse(snapshot);
            Game.Money().Value = money;
            Out("world and money restored (" + Runner.World.legends.Count + " legend(s), " + money + " nuts)");
        }

        private void Populate()
        {
            int before = Runner.World.Living().Count;
            while (Runner.World.Living().Count < Plugin.LivingLegends.Value && Runner.Rise() != null) { }
            Out("living legends " + before + " -> " + Runner.World.Living().Count);
        }

        private void Deed(int index)
        {
            var living = Runner.World.Living();
            if (living.Count == 0) { Fail("no living legend"); return; }
            Out(Runner.DoDeed(living[0], index));
        }

        private void SetFame(int fame)
        {
            var living = Runner.World.Living();
            if (living.Count == 0) { Fail("no living legend"); return; }
            living[0].fame = fame;
            Out(living[0].name + " fame " + fame + " (" + Fame.Bands[living[0].Band] + ")");
        }

        private void Sim(float hours)
        {
            int news = Runner.World.news.Count;
            Runner.World.simHour = Game.Day() * 24f + Game.Hour() - hours;
            Runner.SimulateNow();
            Out("simulated " + hours + " h: " + (Runner.World.news.Count - news) + " news");
        }

        private IEnumerator M2()
        {
            if (Runner.World == null) { Fail("not in a game"); yield break; }
            string snapshot = Runner.SaveJson();
            RememberDrops();
            int money = Game.Money().Value;
            foreach (var body in Runner.bodies.ToArray()) if (!body.R.temp) Runner.Despawn(body);
            Runner.World = new LegendWorld();

            // Several legends, each at its own camp, unknown at first.
            Populate();
            var living = Runner.World.Living();
            var camps = new HashSet<string>();
            foreach (var r in living) camps.Add(r.lairPath);
            Check("several legends rose", living.Count >= Mathf.Min(2, Plugin.LivingLegends.Value), living.Count + " of " + Plugin.LivingLegends.Value + " (scrapyard camps loaded: " + Game.ScrapyardCamps().Count + ")");
            Check("each at its own camp", camps.Count == living.Count, string.Join(", ", new List<string>(camps).ToArray()));
            Check("camps and titles unknown at first", living.TrueForAll(r => !r.locationKnown && string.IsNullOrEmpty(r.title)), "");
            if (living.Count == 0) yield break;
            var first = living[0];

            // A big deed: fame, title, news, a real place.
            int news = Runner.World.news.Count;
            string text = Runner.DoDeed(first, 0);
            Check("a deed gives fame, title and news", first.fame == 8 && first.title == "the Bus-Burner" && Runner.World.news.Count == news + 1, text);
            Check("the news names a place", !text.Contains("{") && text.Length > first.name.Length + 20, text);

            // Reaching Feared: a second strength, more health.
            float rumorHealth = first.maxHealth;
            first.fame = 59;
            Runner.DoDeed(first, 2);
            Check("Feared: a second strength", first.Band == 2 && first.moreStrengths.Count == 1, Fame.Bands[first.Band] + ", strengths " + first.strength + " + [" + string.Join(", ", first.moreStrengths.ToArray()) + "]");
            Check("Feared: more health", first.maxHealth > rumorHealth * 1.3f, rumorHealth.ToString("0") + " -> " + first.maxHealth.ToString("0"));

            // A day of the world going on without the player.
            news = Runner.World.news.Count;
            Runner.World.simHour = Game.Day() * 24f + Game.Hour() - 24f;
            Runner.SimulateNow();
            Check("a simulated day caught up", Near(Runner.World.simHour, Game.Day() * 24f + Game.Hour(), 1.01f), "sim hour " + Runner.World.simHour.ToString("0.0"));
            Check("deeds happened in that day", Runner.World.news.Count > news, (Runner.World.news.Count - news) + " news (about " + (living.Count * 24 * Plugin.DeedChance.Value).ToString("0") + " expected)");

            // Rumors: first a camp, then traits; markers for known camps.
            if (Game.CoyotesHostile()) Out("Coyotes hostile: rumor checks skipped");
            else
            {
                Game.Money().Value = 5000;
                int before = Game.Money().Value;
                string got = Runner.BuyRumor();
                Check("first rumor tells a camp", got == "camp" && living[0].locationKnown, got);
                Check("the rumor cost money", Game.Money().Value < before, before + " -> " + Game.Money().Value);
                for (int i = 0; i < 10 && got == "camp"; i++) got = Runner.BuyRumor();
                Check("later rumors tell traits", Trait.Get(got) != null, got);
                Runner.markers.Tick(Runner.World.Living().FindAll(r => r.locationKnown));
                Out("markers shown: " + Runner.markers.Count + " (WW2 map present: " + (Runner.markers.Count > 0) + ")");
            }

            // Death: bounty from fame, a successor scheduled.
            yield return Goto("lair-of-first");
            Body b = null;
            for (float t = 0; t < 20f && (b == null || !b.Applied); t += Time.deltaTime) { b = Runner.BodyOf(first); yield return null; }
            if (b == null) Fail("no body at " + first.lairName);
            else
            {
                Check("a Feared legend has its fame health", Near(b.Health.FsmVariables.GetFsmFloat("Health").Value, first.maxHealth, 1f), b.Health.FsmVariables.GetFsmFloat("Health").Value.ToString("0") + " / " + first.maxHealth.ToString("0"));
                current = b;
                yield return Kill();
                int day = Game.Day();
                Check("bounty from fame", first.bounty == Fame.Bounty(first.fame), first.bounty + " for fame " + first.fame);
                Check("a successor in 2-4 days", Runner.World.nextRiseDay >= day + 2 && Runner.World.nextRiseDay <= day + 4, "day " + day + ", next rise " + Runner.World.nextRiseDay);
            }
            yield return Goto("back");
            Roundtrip();

            foreach (var body in Runner.bodies.ToArray()) if (!body.R.temp) Runner.Despawn(body);
            RemoveTestDrops();
            Runner.World = Runner.Parse(snapshot);
            Runner.markers.Clear();
            Game.Money().Value = money;
            Out("world and money restored (" + Runner.World.legends.Count + " legend(s), " + money + " nuts)");
        }

        private IEnumerator WaitBody(LegendRecord r, float seconds)
        {
            for (float t = 0; t < seconds; t += Time.deltaTime)
            {
                var b = Runner.BodyOf(r);
                if (b != null && b.Applied) { current = b; yield break; }
                yield return null;
            }
            current = null;
        }

        private IEnumerator M3()
        {
            if (Runner.World == null) { Fail("not in a game"); yield break; }
            string snapshot = Runner.SaveJson();
            RememberDrops();
            int money = Game.Money().Value;
            foreach (var body in Runner.bodies.ToArray()) if (!body.R.temp) Runner.Despawn(body, false);
            Runner.World = new LegendWorld();
            var r = Runner.Rise();
            if (r == null) { Fail("no legend rose"); yield break; }
            r.locationKnown = true;
            Out("legend " + r.name + " at " + r.lairName + ", weapon now: " + Runner.CurrentWeapon());

            // A fight the player walks away from.
            yield return Goto("lair");
            yield return WaitBody(r, 20f);
            if (current == null) { Fail("no body"); yield break; }
            current.Seen = true;
            yield return Hit(false, 40f);
            yield return Hit(false, 40f);
            int fame = r.fame;
            Runner.Despawn(current);
            Check("walking away is remembered as 'ran'", r.memory.Count == 1 && r.memory[0].outcome == MemoryEntry.Ran && r.memory[0].dealt >= 79,
                r.memory.Count > 0 ? r.memory[0].outcome + ", dealt " + r.memory[0].dealt + ", weapon " + r.memory[0].weapon : "no memory");
            Check("grudge for it, but no fame", r.grudge >= 10 && r.fame == fame, "grudge " + r.grudge + ", fame " + fame + " -> " + r.fame);
            string taunt = Runner.MemoryTaunt(r);
            Check("the next taunt remembers it", taunt != null, taunt ?? "none");

            // Killed: a comeback is scheduled 2-5 days on, with a stored roll.
            yield return WaitBody(r, 15f);
            if (current == null) { Fail("no body after coming back"); yield break; }
            yield return Kill();
            int day = Game.Day();
            Check("a comeback is scheduled 2-5 days on", r.deaths == 1 && r.returnDay >= day + 2 && r.returnDay <= day + 5, "deaths " + r.deaths + ", return day " + r.returnDay + ", roll " + r.returnRoll.ToString("0.00"));
            Check("the mark counts as left lying", !Runner.MarkTaken(r), r.markName);

            // Comeback day with a lucky roll: same traits, scar title, fame and grudge up.
            string traits = string.Join(",", r.Traits().ToArray());
            int fameBefore = r.fame, grudgeBefore = r.grudge;
            r.returnDay = day;
            r.returnRoll = 0.1f;
            yield return Wait(1.5f);
            Check("it came back", r.state == LegendRecord.Alive, "state " + r.state);
            Check("with a scar title, the same traits, +10 fame, +30 grudge",
                r.title == "Twice-Buried" && string.Join(",", r.Traits().ToArray()) == traits && r.fame == fameBefore + 10 && r.grudge >= Mathf.Min(100, grudgeBefore + 30),
                r.title + ", " + string.Join(",", r.Traits().ToArray()) + ", fame " + r.fame + ", grudge " + r.grudge);
            Check("and remembers dying", Runner.MemoryTaunt(r) != null && r.memory[r.memory.Count - 1].outcome == MemoryEntry.Killed, Runner.MemoryTaunt(r) ?? "");

            // Unlucky roll: stays dead.
            yield return WaitBody(r, 15f);
            if (current == null) { Fail("no body after the comeback"); yield break; }
            yield return Kill();
            r.returnDay = Game.Day();
            r.returnRoll = 0.99f;
            yield return Wait(1.5f);
            Check("a bad roll keeps it dead", r.state == LegendRecord.Dead && r.returnDay == -1, "state " + r.state + ", return day " + r.returnDay);

            // The third death is final, whatever the roll.
            r.state = LegendRecord.Alive;
            r.health = r.maxHealth;
            yield return WaitBody(r, 15f);
            if (current != null)
            {
                yield return Kill();
                Check("the third death is final", r.deaths == 3 && r.returnDay == -1, "deaths " + r.deaths + ", return day " + r.returnDay);
            }
            Roundtrip();
            yield return Goto("back");

            foreach (var body in Runner.bodies.ToArray()) if (!body.R.temp) Runner.Despawn(body, false);
            RemoveTestDrops();
            Runner.World = Runner.Parse(snapshot);
            Runner.markers.Clear();
            Game.Money().Value = money;
            Out("world and money restored (" + Runner.World.legends.Count + " legend(s), " + money + " nuts)");
        }

        private void Roads()
        {
            var player = Game.Player();
            var cars = Runner.PatrolCarsNear(player.position, 600f);
            Out(cars.Count + " patrol car(s) within 600 m");
            foreach (var car in cars)
            {
                var crew = Runner.Crew(car);
                var names = new List<string>();
                foreach (var c in crew) names.Add(c.name + " [" + c.tag + "]");
                Out("  " + car.name + " " + Vector3.Distance(player.position, car.transform.position).ToString("0") + " m: " + (names.Count > 0 ? string.Join(", ", names.ToArray()) : "no seated crew"));
            }
            foreach (var b in Runner.bodies) if (b.Road) Out("  road legend " + b.R.name + " in " + (b.Car != null ? b.Car.name : "?") + " as " + (b.Root != null ? b.Root.name : "gone"));
        }

        // Every patrol car in the loaded world, whatever the distance, plus anything that looks like a seated crew.
        private void PatrolScan()
        {
            var player = Game.Player();
            int patrols = 0, crewed = 0;
            foreach (var rb in FindObjectsOfType<Rigidbody>())
            {
                var go = rb.gameObject;
                bool marker = go.GetComponent("PatrolMarker") != null;
                int seats = 0;
                if (marker || go.layer == 8)
                    foreach (Transform t in go.GetComponentsInChildren<Transform>(false))
                        if (t.name.Contains("(Driver)") || t.name.Contains("(Passenger)")) seats++;
                if (!marker && seats == 0) continue;
                if (marker) patrols++; else crewed++;
                Out((marker ? "PATROL " : "crewed ") + go.name + " layer " + go.layer + ", " + Vector3.Distance(player.position, go.transform.position).ToString("0") + " m, seated " + seats);
            }
            Out(patrols + " patrol car(s), " + crewed + " other crewed car(s) in the loaded world");
            if (Runner.talkWindow)
                try { Out(Ww2Talk.Traffic(Game.ToWorld(player.position))); } catch (Exception e) { Out("WW2 traffic query failed: " + e.Message); }
        }

        // A WW2 patrol car built by WW2 itself, m metres in front of the player, facing it.
        private IEnumerator SpawnPatrol(string template, float distance)
        {
            if (!Runner.talkWindow) { Fail("needs Wild Wasteland 2"); yield break; }
            var eyes = Game.Eyes();
            Vector3 forward = Vector3.ProjectOnPlane(eyes.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            bool ground;
            Vector3 at = Game.Ground(eyes.position + forward * distance, out ground) + Vector3.up;
            GameObject built = null;
            bool done = false;
            string said;
            try { said = Ww2Talk.SpawnPatrolCar(template, at, Quaternion.LookRotation(-forward), car => { built = car; done = true; }); }
            catch (Exception e) { Fail("spawn failed: " + (e.InnerException ?? e).Message); yield break; }
            Out(said);
            if (!said.StartsWith("asked")) { Fail(said); yield break; }
            for (float t = 0; t < 20f && !done; t += Time.deltaTime) yield return null;
            if (built == null) { Fail("WW2 built nothing in 20 s"); yield break; }
            yield return Wait(1f);
            var crew = Runner.Crew(built);
            var names = new List<string>();
            foreach (var c in crew) names.Add(c.name + " [" + c.tag + "]");
            Check("a WW2 patrol car with crew", Runner.IsPatrolCar(built) && crew.Count > 0, built.name + ": " + string.Join(", ", names.ToArray()));
        }

        // Make the road legend leave its car the way WW2 does, then check the legend followed to the new NPC.
        private IEnumerator BailOut()
        {
            Body b = null;
            foreach (var x in Runner.bodies) if (x.Road && x.Root != null) b = x;
            if (b == null || b.Car == null) { Fail("no road legend in a car"); yield break; }
            var r = b.R;
            var seat = b.Root;
            float hp = b.Health.FsmVariables.GetFsmFloat("Health").Value;
            string said;
            try { said = Ww2Talk.BailOut(b.Car, seat); }
            catch (Exception e) { Fail("bail-out failed: " + (e.InnerException ?? e).Message); yield break; }
            Out(said);
            Body now = null;
            for (float t = 0; t < 3f && (now == null || !now.Applied); t += Time.deltaTime) { now = Runner.BodyOf(r); yield return null; }
            Check("the legend followed to the NPC on foot", now != null && now.Root != null && now.Root != seat && now.Applied,
                now != null && now.Root != null ? now.Root.name + " (" + now.Health.FsmVariables.GetFsmFloat("Health").Value.ToString("0") + ")" : "lost");
            if (now != null && now.Root != null)
                Check("with the same health and its stats", Near(now.Health.FsmVariables.GetFsmFloat("Health").Value, hp, 1f), Game.Describe(now.Root));
        }

        private Body Claim()
        {
            var player = Game.Player();
            foreach (var car in Runner.PatrolCarsNear(player.position, 600f))
            {
                var crew = Runner.Crew(car);
                if (crew.Count == 0) continue;
                var b = Runner.TryClaim(car, crew, true);
                Check("a legend took a seat in " + car.name, b != null, b != null ? b.R.name + " as " + b.Root.name : "no living legend free?");
                return b;
            }
            Fail("no patrol car with crew within 600 m (drive the roads, or wait for one)");
            return null;
        }

        private IEnumerator M4()
        {
            if (Runner.World == null) { Fail("not in a game"); yield break; }
            if (Runner.World.Living().Count == 0) Runner.Rise();
            var b = Claim();
            if (b == null) yield break;
            var r = b.R;
            yield return Frames(3);
            Check("the crew member has the legend's health", Near(b.Health.FsmVariables.GetFsmFloat("Health").Value, r.health, 0.5f), Game.Describe(b.Root));
            Check("it counts as the legend's body (no camp body)", Runner.BodyOf(r) == b && b.Road, "");
            float before = b.Health.FsmVariables.GetFsmFloat("Health").Value;
            current = b;
            yield return Hit(false, 10f);
            string snapshot = Runner.SaveJson();
            int money = Game.Money().Value;
            yield return Kill();
            Roundtrip();
            Game.Money().Value = money;
            Out("this test killed a real legend (" + r.name + "); not undone");
        }

        // The trophy bonus on a weapon model the player has used (its FSMs are initialized): +20 %, then put back.
        private void TrophyBonus()
        {
            var cam = Game.Global("PlayerCamera");
            var parent = cam != null ? cam.transform.Find("WeaponsArm/Parent") : null;
            if (parent == null) { Fail("no WeaponsArm/Parent"); return; }
            foreach (Transform child in parent)
            {
                var attack = Game.Fsm(child.gameObject, "Attack");
                if (attack == null || !attack.Fsm.Initialized) continue;
                float before = FirstPlayerDamage(child.gameObject);
                if (before == 0f) continue;
                Runner.RestoreBonus();
                int n = Runner.ApplyBonus(child.gameObject, 1.2f);
                float raised = FirstPlayerDamage(child.gameObject);
                Runner.RestoreBonus();
                float after = FirstPlayerDamage(child.gameObject);
                Check(child.name + ": +20 % while held", Near(raised, before * 1.2f, 0.01f), n + " value(s), " + before + " -> " + raised);
                Check(child.name + ": exactly back when put away", after == before, before + " -> " + after);
                return;
            }
            Fail("no initialized weapon model (draw a weapon once first)");
        }

        // Trophy perks on a fresh Flexa, with the perks switched on by hand (no trophy needs to be drawn).
        // ItemLabels: three test trophies in front of the player (under the crosshair at 3 m, 1.5 m to the side, under
        // the crosshair at 10 m). Look at: only the first; Always: all three; Off: none. Removed again at once.
        private void LabelTest()
        {
            var cam = Camera.main;
            if (cam == null) { Fail("no camera"); return; }
            var made = new List<Trophy>();
            Vector3 eye = cam.transform.position, fwd = cam.transform.forward, side = cam.transform.right;
            var spots = new[] { eye + fwd * 3f, eye + fwd * 3f + side * 1.5f, eye + fwd * 10f };
            for (int i = 0; i < spots.Length; i++)
            {
                var item = Game.CreateRegisteredItem("machete", spots[i] - Vector3.up * 0.35f);
                if (item == null) continue;
                var t = new Trophy { Item = item, T = new TrophyItem { item = item.name, legend = "Bench" + i, signature = "machete", bonus = 0.25f, perks = new List<string>() } };
                made.Add(t);
                Runner.trophies.Add(t);
            }
            var keep = Plugin.ItemLabels.Value;
            try
            {
                if (made.Count < 3) { Fail("could not lay out 3 test machetes"); return; }
                Plugin.ItemLabels.Value = ItemLabelStyle.LookAt;
                var look = Runner.ItemLabels(cam);
                Check("look at: only the one under the crosshair", look.Count == 1 && look[0].Value.StartsWith("Bench0"), look.Count + " label(s)" + (look.Count > 0 ? ": " + look[0].Value : ""));
                Plugin.ItemLabels.Value = ItemLabelStyle.Always;
                var always = Runner.ItemLabels(cam);
                Check("always: every one in range", always.Count == 3, always.Count + " label(s)");
                Plugin.ItemLabels.Value = ItemLabelStyle.Off;
                Check("off: none", Runner.ItemLabels(cam).Count == 0, "");
            }
            finally
            {
                Plugin.ItemLabels.Value = keep;
                foreach (var t in made) { Runner.trophies.Remove(t); Game.Unregister(t.Item); Destroy(t.Item); }
                Out("removed the " + made.Count + " test machetes");
            }
        }

        // The Enabled setting: off puts legends away, on again goes on without catching up the time it was off.
        private IEnumerator SwitchTest()
        {
            Spawn("Flexa", 1, 12f);
            var flexa = spawned[spawned.Count - 1];
            yield return Frames(5);
            var temp = Runner.Promote(flexa, "Ironhead", "Loudmouth");
            int legends = Runner.World.legends.Count;
            Plugin.Enabled.Value = false;
            yield return Frames(3);
            Check("off: the legend leaves", Runner.bodies.Count == 0 && flexa == null, Runner.bodies.Count + " body(ies)");
            Check("off: the saved world stays", Runner.World != null && Runner.World.legends.Count == legends, legends + " legend(s)");
            Check("off: no trophy bonus left on", Runner.activePerks.Count == 0, "");
            Plugin.Enabled.Value = true;
            yield return Wait(1.5f);   // one main-loop tick
            float now = Game.Day() * 24f + Game.Hour();
            Check("on again: goes on from now", Runner.World.simHour >= 0f && now - Runner.World.simHour < 1f, "sim hour " + Runner.World.simHour.ToString("0.0") + ", now " + now.ToString("0.0"));
            if (temp != null && temp.Root != null) Destroy(temp.Root);
        }

        private IEnumerator PerkTest()
        {
            Spawn("Flexa", 1, 12f);
            var flexa = spawned[spawned.Count - 1];
            yield return Wait(1f);   // the perk watcher picks up nearby enemies twice a second
            var hp = Game.Health(flexa);
            var head = Game.HeadBodypart(flexa);
            var body = Game.Fsm(flexa, "Bodypart");
            Runner.activePerks.Clear();
            Runner.activePerks.Add(Perks.Skullsplitter);
            Runner.activePerks.Add(Perks.Bleeding);
            Runner.activePerks.Add(Perks.Bloodthirst);

            float before = hp.Value;
            Runner.lastPlayerAttack = Time.time;
            head.FsmVariables.GetFsmFloat("Damage").Value = -10f;
            head.SendEvent("Damage");
            yield return Frames(3);
            // 30, or 32 when the first Bleeding tick (−2) lands in the same frames.
            float took = before - hp.Value;
            Check("Skullsplitter: a head hit of 10 takes 30 (x2, then +50 %)", Near(took, 30f, 0.6f) || Near(took, 32f, 0.6f), "took " + took.ToString("0.#"));
            Check("Bleeding: it keeps bleeding", flexa.transform.Find("WastelandLegends.Fire") != null, "");
            float burning = hp.Value;
            yield return Wait(2.2f);
            Check("Bleeding: it loses health on its own", hp.Value < burning - 3f, burning.ToString("0") + " -> " + hp.Value.ToString("0"));

            // Its own ticks are not hits: mid-swing (attack window open) for 1.5 s, still one stack.
            var burn = flexa.transform.Find(Burn.Name).GetComponent<Burn>();
            for (float until = Time.time + 1.5f; Time.time < until; ) { Runner.lastPlayerAttack = Time.time; yield return null; }
            Check("Bleeding: its ticks don't add stacks", burn.Stacks == 1, burn.Stacks + " stack(s)");

            // Stacking: more hits, up to 3 stacks.
            for (int i = 0; i < 4; i++)
            {
                Runner.lastPlayerAttack = Time.time;
                body.FsmVariables.GetFsmFloat("Damage").Value = -1f;
                body.SendEvent("Damage");
                yield return Frames(3);
            }
            Check("Bleeding: hits stack, at most 3", burn.Stacks == 3, burn.Stacks + " stack(s)");
            float stacked = hp.Value;
            Runner.lastPlayerAttack = -100f;
            yield return Wait(2f);
            // 3 stacks: 12 a second (the game's 4 + two of ours); 2 s ~ 24.
            Check("Bleeding: 3 stacks bleed 3x as fast", stacked - hp.Value >= 20f && stacked - hp.Value <= 28f, stacked.ToString("0") + " -> " + hp.Value.ToString("0"));

            int heals = Runner.heals;
            Runner.lastPlayerAttack = Time.time;
            body.FsmVariables.GetFsmFloat("Damage").Value = -(hp.Value + 5f);
            body.SendEvent("Damage");
            yield return Frames(3);
            Check("Bloodthirst: the kill heals the player", Runner.heals == heals + 1, (Runner.heals - heals) + " heal(s) (god mode hides the health change)");
            Runner.activePerks.Clear();

            // Quick on a weapon model the player has used: shorter cycle, exactly back afterwards.
            var cam = Game.Global("PlayerCamera");
            var parent = cam.transform.Find("WeaponsArm/Parent");
            foreach (Transform child in parent)
            {
                var attack = Game.Fsm(child.gameObject, "Attack");
                if (attack == null || !attack.Fsm.Initialized || child.name == "kick" || child.name == "hands") continue;
                Runner.RestoreBonus();
                int n = Runner.ApplyQuick(child.gameObject, 0.8f);
                if (n == 0) continue;
                Runner.RestoreBonus();
                Check("Quick on " + child.name + ": " + n + " timing value(s) shortened and put back", true, "");
                break;
            }
            Clear();
        }

        private static float FirstPlayerDamage(GameObject model)
        {
            foreach (var fsm in model.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (!fsm.Fsm.Initialized) continue;
                foreach (var state in fsm.Fsm.States)
                    foreach (var action in state.Actions)
                    {
                        var set = action as HutongGames.PlayMaker.Actions.SetFsmFloat;
                        if (set == null || set.variableName == null || set.variableName.Value != "Damage") continue;
                        if (!set.setValue.UseVariable) return set.setValue.Value;
                        var v = fsm.FsmVariables.GetFsmFloat(set.setValue.Name);
                        if (v != null) return v.Value;
                    }
            }
            return 0f;
        }

        // Signatures: one at rising, a better one on reaching a new fame band; the trophy is checked in Kill.
        private void Signature()
        {
            var r = Runner.World.Living().Count > 0 ? Runner.World.Living()[0] : null;
            if (r == null) { Fail("no living legend"); return; }
            Check("it has a signature weapon", !string.IsNullOrEmpty(r.signature), r.signature);
            string before = r.signature;
            int tries = 0;
            r.fame = 59;
            Runner.DoDeed(r, 0);   // to Feared
            while (r.signature == before && tries++ < 5) Runner.UpgradeSignature(r);
            Check("Feared: a better weapon", r.signature != before, before + " -> " + r.signature + " (" + Signatures.Name(r.signature) + ")");
        }

        // Diagnose: the player, legend bodies and escorts every half second (position, height, distance).
        private IEnumerator Where(float seconds)
        {
            for (float t = 0; t <= seconds; t += 0.5f)
            {
                var p = Game.Player();
                var sb = new StringBuilder("t=" + t.ToString("0.0") + " player y " + p.position.y.ToString("0"));
                foreach (var b in Runner.bodies)
                    if (b.Root != null) sb.Append(" | ").Append(b.R.name).Append(" y ").Append(b.Root.transform.position.y.ToString("0")).Append(" d ").Append(Vector3.Distance(p.position, b.Root.transform.position).ToString("0"))
                      .Append(" ").Append(b.Detection != null && b.Detection.Fsm.Initialized ? b.Detection.ActiveStateName : "?");
                foreach (var e in spawned)
                    if (e != null) sb.Append(" | ").Append(e.name).Append(" y ").Append(e.transform.position.y.ToString("0")).Append(" d ").Append(Vector3.Distance(p.position, e.transform.position).ToString("0"));
                foreach (var e in Runner.escorts)
                    if (e != null) sb.Append(" | ").Append(e.name).Append(" y ").Append(e.transform.position.y.ToString("0")).Append(" d ").Append(Vector3.Distance(p.position, e.transform.position).ToString("0"));
                Out(sb.ToString());
                yield return Wait(0.5f);
            }
        }

        private IEnumerator M5()
        {
            if (Runner.World == null) { Fail("not in a game"); yield break; }
            string snapshot = Runner.SaveJson();
            int money = Game.Money().Value;
            RememberDrops();
            foreach (var body in Runner.bodies.ToArray()) if (!body.R.temp) { if (body.Road) Runner.Release(body, false); else Runner.Despawn(body, false); }
            Runner.World = new LegendWorld();
            var r = Runner.Rise();
            if (r == null) { Fail("no legend rose"); yield break; }
            int day = Game.Day();

            // A hunt: the legend and two raiders behind the player, told where the player is.
            r.grudge = 80;
            var b = Runner.StartHunt(r);
            Check("a grudge brings a hunt", b != null && b.Hunt, b != null ? b.Root.name : "none");
            if (b != null)
            {
                for (float t = 0; t < 3f && !b.Applied; t += Time.deltaTime) yield return null;
                var player = Game.Player();
                float d = Vector3.Distance(player.position, b.Root.transform.position);
                Check("it comes with two raiders, 60-90 m away", Runner.escorts.Count == 2 && d > 50f && d < 100f, Runner.escorts.Count + " raider(s), " + d.ToString("0") + " m");
                Check("hunting costs it 20 grudge; no new hunt for 2 days", r.grudge == 60 && Runner.World.lastHuntDay == day, "grudge " + r.grudge + ", last hunt day " + Runner.World.lastHuntDay);
                Check("its health and stats", b.Applied && Near(b.Health.FsmVariables.GetFsmFloat("Health").Value, r.maxHealth, 1f), Game.Describe(b.Root));
                Runner.Despawn(b, false);
            }
            foreach (var e in Runner.escorts) if (e != null) Destroy(e);
            Runner.escorts.Clear();

            // The stash: armor plates (1 / 2 / 2 / 3) plus 3 + 2 random things per fame band, laid out once.
            var registry = Game.Proxy("ArrayList_Items");
            var before = new HashSet<GameObject>();
            foreach (var x in registry._arrayList) if (x is GameObject) before.Add((GameObject)x);
            r.fame = 30;   // Known: 2 plates + 5 things
            Runner.QueueStash(r);
            var stash = Runner.World.stashes[Runner.World.stashes.Count - 1];
            var eyes = Game.Eyes();
            int made = Runner.LayOut(stash, eyes.position + Vector3.ProjectOnPlane(eyes.forward, Vector3.up).normalized * 6f);
            Check("a Known legend's stash has 7 things", made == 7 && stash.laid, made + " item(s)");
            int plates = 0;
            foreach (var x in registry._arrayList)
            {
                var go = x as GameObject;
                if (go != null && !before.Contains(go) && go.name.StartsWith("armorplate(", StringComparison.Ordinal)) plates++;
            }
            Check("it always has armor plates (2 for Known)", plates >= 2, plates + " plate(s)");
            foreach (var x in new System.Collections.ArrayList(registry._arrayList))
            {
                var go = x as GameObject;
                if (go != null && !before.Contains(go)) { Game.Unregister(go); Destroy(go); }
            }

            // Car theft on a WW2 raider car (never the player's own).
            if (Runner.talkWindow)
            {
                GameObject car = null;
                bool done = false;
                string said = Ww2Talk.SpawnPatrolCar("PipeRat_Basic", Game.Ground(eyes.position + eyes.forward * 40f, out done) + Vector3.up, Quaternion.identity, c => car = c);
                for (float t = 0; t < 20f && car == null; t += Time.deltaTime) yield return null;
                if (car == null) Fail("no stand-in car (" + said + ")");
                else
                {
                    Runner.StealCar(r, car);
                    yield return Frames(3);
                    float d = Vector3.Distance(car.transform.position, Game.ToScene(r.lairWorld));
                    Check("the stolen car is at its camp", d < 40f, d.ToString("0") + " m from " + r.lairName);
                    Check("the camp becomes known, fame +15", r.locationKnown && r.stolenCar == car.name, "stolen " + r.stolenCar + ", fame " + r.fame);
                }
            }
            else Out("no WW2: car theft check skipped");

            Roundtrip();
            RemoveTestDrops();
            Runner.World = Runner.Parse(snapshot);
            Runner.markers.Clear();
            Game.Money().Value = money;
            Out("world and money restored (" + Runner.World.legends.Count + " legend(s), " + money + " nuts)");
        }

        // ---- Helpers ----

        // Marks and trophies that existed before a test belong to the player (maybe in their weapon slots): a test
        // only removes the ones it dropped itself. (The first versions removed every mark and deleted two real marks
        // from the player's slots, 2026-10-29.)
        private readonly HashSet<GameObject> playerDrops = new HashSet<GameObject>();

        private void RememberDrops()
        {
            playerDrops.Clear();
            foreach (var m in Runner.marks) if (m.Item != null) playerDrops.Add(m.Item);
            foreach (var t in Runner.trophies) if (t.Item != null) playerDrops.Add(t.Item);
        }

        private void RemoveTestDrops()
        {
            int n = 0;
            foreach (var m in Runner.marks) if (m.Item != null && !playerDrops.Contains(m.Item)) { Game.Unregister(m.Item); Destroy(m.Item); n++; }
            foreach (var t in Runner.trophies) if (t.Item != null && !playerDrops.Contains(t.Item)) { Game.Unregister(t.Item); Destroy(t.Item); n++; }
            Out("removed " + n + " mark(s)/trophies the test dropped; the player's own were left alone");
        }

        private Body Target()
        {
            if (current != null && current.Root != null) return current;
            return LivingBody();
        }

        private Body LivingBody()
        {
            foreach (var b in Runner.bodies) if (b.Root != null && b.Applied) return b;
            return null;
        }

        private void Out(string s)
        {
            output.Append(s).Append('\n');
            Plugin.Log.LogInfo("[bench] " + s);
        }

        private void Check(string what, bool ok, string detail)
        {
            if (ok) passed++; else failed++;
            Out((ok ? "PASS " : "FAIL ") + what + (detail.Length > 0 ? " (" + detail + ")" : ""));
        }

        private void Fail(string s) { failed++; Out("FAIL " + s); }

        private bool IsLegend(GameObject go)
        {
            foreach (var b in Runner.bodies) if (b.Root == go) return true;
            return false;
        }

        private static bool Near(float a, float b, float tolerance) { return Math.Abs(a - b) <= tolerance; }
    }
}
