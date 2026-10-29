using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace WastelandLegends
{
    // A legend's body while it is in the world. The record is what gets saved; the body comes and goes.
    internal class Body
    {
        public LegendRecord R;
        public GameObject Root;
        public PlayMakerFSM Health, Detection;
        public Transform Head;
        public FsmFloat HeadDamage, BodyDamage;
        public float LastHealth;
        public float MeleeHit;          // what one of its melee hits writes into the player (positive)
        public bool Applied, Seen, LoudTaunted, HurtTaunted, Despawning;
        // This encounter (M3): damage both ways and the player's weapons, by hits.
        public float Dealt, Taken;
        public bool Combat;
        public readonly Dictionary<string, int> Weapons = new Dictionary<string, int>();
        // M4: a crew member of a WW2 patrol (never destroyed by us), and its car.
        public bool Road;
        public bool Hunt;   // M5: it came for the player (despawns like a camp body)
        public GameObject Car;
    }

    internal class Mark
    {
        public GameObject Item;
        public LegendRecord R;
        public int ChecksInBox;
    }

    internal class LegendTag : MonoBehaviour
    {
        internal Body Body;
        internal Runner Runner;

        private void OnDestroy()
        {
            if (Runner != null) Runner.OnBodyDestroyed(Body, transform.position);
        }
    }

    // M1 (design section 15): one legend, holding a Scrapyard camp. It rises once the grace days are over, its body
    // appears when the player comes near its camp and goes again when the player leaves, its traits work and are
    // learned in the fight, its mark pays a bounty, and all of it is saved with the game.
    internal partial class Runner : MonoBehaviour
    {
        internal static readonly string[] Bodies = { "Flexa", "Gunnar", "Spanna", "Lugnut", "Scrud", "Bolter" };

        internal LegendWorld World;
        internal readonly List<Body> bodies = new List<Body>();
        internal readonly List<Mark> marks = new List<Mark>();
        internal readonly System.Random rng = new System.Random();

        private float readyAt = float.MaxValue, nextTick, nextBoxCheck;
        private float lastNoCampLog;
        private FsmFloat playerHit;
        private string handoffPath;
        internal readonly Markers markers = new Markers();

        private void Awake()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == "WildWasteland") talkWindow = true;
            handoffPath = Path.Combine(Paths.GameRootPath, @"GameInternals\dumps\bench\handoff.json");
            // Hot reload (Dev Loader): the old copy left the world behind.
            if (Plugin.Dev && File.Exists(handoffPath))
            {
                try
                {
                    if ((DateTime.Now - File.GetLastWriteTime(handoffPath)).TotalSeconds < 120)
                    {
                        World = Parse(File.ReadAllText(handoffPath));
                        readyAt = Time.realtimeSinceStartup + 2f;
                        Plugin.Log.LogInfo("Took over " + World.legends.Count + " legend(s) from the previous build.");
                    }
                    File.Delete(handoffPath);
                }
                catch (Exception e) { Plugin.Log.LogWarning("Handoff not read: " + e.Message); }
            }
            // Loaded while a world is already running (Dev Loader, first copy with nothing to take over).
            if (World == null && SceneManager.GetActiveScene().name == "Game")
            {
                World = new LegendWorld();
                readyAt = Time.realtimeSinceStartup + 2f;
            }
        }

        private void OnDestroy() { SceneManager.sceneLoaded -= OnSceneLoaded; }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single) return;
            bodies.Clear();
            marks.Clear();
            notices.Clear();
            bonusUndo.Clear();
            bonusItem = null;
            trophies.Clear();
            if (scene.name == "Game")
            {
                // A new game stays empty; a loaded save fills this from its key (Persistence.BeforeLoad).
                World = new LegendWorld();
                readyAt = Time.realtimeSinceStartup + 15f;
            }
            else World = null;
        }

        // ---- Saving ----

        internal static LegendWorld Parse(string json)
        {
            LegendWorld w = null;
            if (!string.IsNullOrEmpty(json))
            {
                try { w = Json.Read<LegendWorld>(json); }
                catch (Exception e) { Plugin.Log.LogError("Saved legends could not be read (" + e.Message + "): " + json); }
            }
            if (w == null) w = new LegendWorld();
            if (w.legends == null) w.legends = new List<LegendRecord>();
            if (w.news == null) w.news = new List<NewsItem>();
            if (w.trophies == null) w.trophies = new List<TrophyItem>();
            if (w.stashes == null) w.stashes = new List<StashItem>();
            // 0.2.0 stored the "press T" hint inside the news text.
            foreach (var n in w.news)
            {
                int hint = n.text != null ? n.text.IndexOf(" The Coyote traders may know where", StringComparison.Ordinal) : -1;
                if (hint > 0) n.text = n.text.Substring(0, hint).Replace("is making a name", "has started making a name");
            }
            foreach (var l in w.legends)
            {
                if (l.known == null) l.known = new List<string>();
                if (l.moreStrengths == null) l.moreStrengths = new List<string>();
                if (l.memory == null) l.memory = new List<MemoryEntry>();
            }
            if (w.version < 2)
            {
                // M1 saves: the legend's camp was told from the start; fame and base health didn't exist.
                foreach (var l in w.legends)
                {
                    l.locationKnown = true;
                    l.baseHealth = l.maxHealth / (Plugin.HealthMultiplier.Value * (l.Has("ThickHide") ? 1.5f : 1f));
                }
            }
            if (w.version < 3)
            {
                // Before 0.5.0's rename, "Searing" was what is now Bleeding (real fire comes from the Weapons Pack).
                foreach (var t in w.trophies)
                    if (t.perks != null)
                        for (int i = 0; i < t.perks.Count; i++)
                            if (t.perks[i] == Perks.Searing) t.perks[i] = Perks.Bleeding;
            }
            w.version = LegendWorld.CurrentVersion;
            return w;
        }

        internal string SaveJson()
        {
            if (World == null) return null;
            foreach (var b in bodies)
                if (!b.R.temp && b.Root != null && b.Applied) b.R.health = Mathf.Max(1f, b.Health.FsmVariables.GetFsmFloat("Health").Value);
            return Json.Write(World);
        }

        internal void OnSaveLoaded(string json)
        {
            World = Parse(json);
            readyAt = Time.realtimeSinceStartup + 10f;
            Plugin.Log.LogInfo(json == null ? "This save has no legends yet." : "Loaded " + World.legends.Count + " legend(s) from the save.");
        }

        // Dev Loader: hand the world to the next copy and take this copy's bodies out of the game.
        internal void Shutdown()
        {
            try
            {
                string json = SaveJson();
                if (json != null) File.WriteAllText(handoffPath, json);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Handoff not written: " + e.Message); }
            foreach (var b in bodies.ToArray()) { if (b.Road) Release(b, false); else Despawn(b, false); }
            markers.Clear();
            UninstallTalk();
            RestoreBonus();
            foreach (var e in escorts) if (e != null) Destroy(e);
            escorts.Clear();
        }

        // The Enabled setting is off: the legends leave the world, the saved world stays as it is (and is still saved).
        private void PutAway()
        {
            if (putAway) return;
            putAway = true;
            foreach (var b in bodies.ToArray()) { if (b.Road) Release(b, false); else Despawn(b, false); }   // not "you ran"
            foreach (var e in escorts) if (e != null) Destroy(e);
            escorts.Clear();
            markers.Clear();
            RestoreBonus();
            journalOpen = false;
            Plugin.Log.LogInfo("Wasteland Legends switched off: legends put away, the save keeps them.");
        }

        // ---- Main loop ----

        private bool putAway;

        private void Update()
        {
            if (!Plugin.Enabled.Value) { PutAway(); return; }
            if (putAway)
            {
                // Switched back on: go on from now, without simulating the time it was off.
                putAway = false;
                if (World != null) World.simHour = -1f;
                Plugin.Log.LogInfo("Wasteland Legends switched back on.");
            }
            if (Pressed(Plugin.JournalKey.Value)) journalOpen = !journalOpen;
            if (Pressed(Plugin.PromoteKey.Value)) PromoteLookedAt();
            if (World != null) { WatchTraders(); TickTrophyBonus(); TickPerks(); }
            if (World != null && Time.realtimeSinceStartup >= readyAt && !Game.Peaceful()) { WatchPatrols(); WatchRoadBodies(); }
            if (trader != null && Pressed(Plugin.RumorKey.Value)) BuyRumor();

            foreach (var b in bodies.ToArray())
                if (b.Root != null && b.Applied) Watch(b);
            if (bodies.Count > 0) WatchPlayer();

            marks.RemoveAll(m => m.Item == null);
            trophies.RemoveAll(t => t.Item == null);
            if (marks.Count > 0 && Time.time >= nextBoxCheck) { nextBoxCheck = Time.time + 0.25f; CheckBounties(); }

            if (Time.realtimeSinceStartup >= nextTick) { nextTick = Time.realtimeSinceStartup + 1f; Tick(); }
        }

        private static bool Pressed(Key key)
        {
            if (key == Key.None) return false;
            try { var kb = Keyboard.current; return kb != null && kb[key].wasPressedThisFrame; }
            catch (Exception) { return false; }
        }

        private void Tick()
        {
            if (World == null || Time.realtimeSinceStartup < readyAt || Time.timeScale < 0.01f) return;
            var player = Game.Player();
            if (player == null) return;
            if (Game.Peaceful()) { foreach (var b in bodies.ToArray()) if (!b.R.temp) { if (b.Road) Release(b, false); else Despawn(b, false); } return; }

            TickTalk();
            Simulate();
            TickReturns();
            AssignSignatures();
            Populate();

            var living = World.Living();
            markers.Tick(living.FindAll(r => r.locationKnown));
            foreach (var r in living)
            {
                var body = BodyOf(r);
                float dist = Vector3.Distance(player.position, Game.ToScene(r.lairWorld));
                if (body == null && dist < Plugin.SpawnRange.Value) TrySpawn(r);
                else if (body != null && !body.Road && body.Root != null && Vector3.Distance(player.position, body.Root.transform.position) > Plugin.DespawnRange.Value) Despawn(body);
            }
            RelinkMarks();
            RelinkTrophies();
            TidyEscorts();
            LayOutStashes();
            WatchPlayerCar();
        }

        internal Body BodyOf(LegendRecord r)
        {
            foreach (var b in bodies) if (b.R == r) return b;
            return null;
        }

        // ---- Rising ----

        internal LegendRecord Rise()
        {
            var player = Game.Player();
            var camp = ChooseCamp();
            if (camp == null) return null;

            var bodyNames = new List<string>();
            foreach (var n in Bodies) if (Game.EnemyPrefab(n) != null) bodyNames.Add(n);
            if (bodyNames.Count == 0) { Plugin.Log.LogWarning("No raider prefab found for a legend body."); return null; }
            string bodyName = bodyNames[rng.Next(bodyNames.Count)];
            float baseHealth = Game.Health(Game.EnemyPrefab(bodyName)).Value;

            var strengths = Trait.Of(true);
            var weaknesses = Trait.Of(false);
            string strength = strengths[rng.Next(strengths.Count)].Id, weakness;
            do weakness = weaknesses[rng.Next(weaknesses.Count)].Id; while (strength == "Ironhead" && weakness == "GlassJaw");

            int campNumber;
            var r = new LegendRecord
            {
                id = World.nextId++,
                name = Names.Name(rng),
                title = "",   // titles come from deeds
                body = bodyName,
                baseHealth = baseHealth,
                strength = strength,
                weakness = weakness,
                lairName = Game.CampId(camp.name, out campNumber),
                lairPath = Game.PathOf(camp),
                lairWorld = Game.ToWorld(Game.CampStandPoint(camp)),
                bornDay = Game.Day(),
                bounty = Fame.Bounty(0),
                signature = Signatures.Pick(0, rng),
            };
            r.maxHealth = r.health = Fame.MaxHealth(r);
            World.legends.Add(r);

            Plugin.Log.LogInfo("Legend risen: " + r.name + " (" + bodyName + ", " + r.maxHealth + " health, " + strength + " / " + weakness
                + ") at " + r.lairPath + ", " + Vector3.Distance(player.position, camp.position).ToString("0") + " m away.");
            // The news itself is what traders repeat; the how-to goes only into the pop-up (a trader read the "press T"
            // hint out loud in the M2 play test).
            News(r, "A raider called " + r.name + " has started making a name out in the scrap.", false);
            Notice("News: a raider called " + r.name + " has started making a name out in the scrap. "
                + (talkWindow ? "Ask a Coyote trader where." : "Ask a Coyote trader where (" + Plugin.RumorKey.Value + " next to one)."));
            return r;
        }

        // Not on top of the player, not at the edge of the loaded world, not where another legend lives.
        internal Transform ChooseCamp()
        {
            var player = Game.Player();
            var camps = Game.ScrapyardCamps();
            if (player == null || camps.Count == 0)
            {
                if (Time.realtimeSinceStartup - lastNoCampLog > 60f) { lastNoCampLog = Time.realtimeSinceStartup; Plugin.Log.LogInfo("No scrapyard camp in the loaded terrain yet; a legend rises when one is."); }
                return null;
            }
            Transform camp = null, nearest = null;
            float best = float.MaxValue, nearestDist = float.MaxValue;
            foreach (var c in camps)
            {
                if (CampTaken(c)) continue;
                float d = Vector3.Distance(player.position, c.position);
                if (d < nearestDist) { nearest = c; nearestDist = d; }
                if (d >= 300f && d <= 2500f && d < best) { camp = c; best = d; }
            }
            if (camp == null) camp = nearest;
            if (camp == null && Time.realtimeSinceStartup - lastNoCampLog > 60f)
            {
                lastNoCampLog = Time.realtimeSinceStartup;
                Plugin.Log.LogInfo("Every loaded scrapyard camp has a legend; the next one waits for a free camp.");
            }
            return camp;
        }

        // ---- Bodies ----

        internal Body TrySpawn(LegendRecord r)
        {
            Vector3 stand = Game.ToScene(r.lairWorld);
            bool ground;
            Vector3 at = Game.ActorSpawn(stand, out ground);
            if (!ground) return null;   // the terrain there isn't built yet
            var prefab = Game.EnemyPrefab(r.body);
            if (prefab == null) return null;

            var player = Game.Player();
            var face = player != null ? player.position - at : Vector3.forward;
            face.y = 0f;
            var root = Instantiate(prefab, at, Quaternion.LookRotation(face.sqrMagnitude > 0.01f ? face : Vector3.forward));
            root.SetActive(true);
            // Not registered in ArrayList_Items: the game must not save it as a plain raider. The record is the save.
            root.name = prefab.name + "(Clone)" + Game.NextItemId();
            foreach (var fsm in root.GetComponents<PlayMakerFSM>()) fsm.SendEvent("Randomize");

            var b = Attach(r, root);
            Plugin.Log.LogInfo(r.name + " appears at " + r.lairName + " as " + root.name + " (" + r.health.ToString("0") + "/" + r.maxHealth.ToString("0") + ").");
            // WeaponType picks its gun (and writes the gun's damage) when the FSMs start, so scale after that.
            StartCoroutine(ApplyLater(b, 0.5f));
            return b;
        }

        private IEnumerator ApplyLater(Body b, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (b.Root != null) Apply(b);
        }

        private Body Attach(LegendRecord r, GameObject root)
        {
            var b = new Body { R = r, Root = root, Health = Game.Fsm(root, "Health"), Detection = Game.Fsm(root, "Detection") };
            var head = Game.HeadBodypart(root);
            b.Head = head != null ? head.transform : root.transform;
            if (head != null) b.HeadDamage = head.FsmVariables.GetFsmFloat("Damage");
            var bodypart = Game.Fsm(root, "Bodypart");
            if (bodypart != null) b.BodyDamage = bodypart.FsmVariables.GetFsmFloat("Damage");
            var tag = root.AddComponent<LegendTag>();
            tag.Body = b;
            tag.Runner = this;
            bodies.Add(b);
            return b;
        }

        // Health is set, not multiplied, so a legend comes back with the health it had when it last left.
        internal string Apply(Body b)
        {
            var r = b.R;
            var hp = b.Health.FsmVariables.GetFsmFloat("Health");
            if (!r.temp && r.baseHealth > 0f) r.maxHealth = Fame.MaxHealth(r);
            r.health = Mathf.Clamp(r.health, 1f, r.maxHealth);
            hp.Value = r.health;
            b.LastHealth = r.health;
            float melee = Plugin.DamageMultiplier.Value * (r.Has("Bruiser") ? 1.5f : 1f);
            string log = "melee " + Game.ScaleDamage(Game.Fsm(b.Root, "Damage"), melee) + ", ranged " + Game.ScaleDamage(Game.Fsm(b.Root, "Damage Ranged"), Plugin.DamageMultiplier.Value);
            b.MeleeHit = -Game.BaseMelee(b.Root);
            if (b.HeadDamage != null) b.HeadDamage.Value = 0f;
            if (b.BodyDamage != null) b.BodyDamage.Value = 0f;
            b.Applied = true;
            return log;
        }

        // remember: the player walked away (an encounter to remember); false for shutdown, peaceful mode and the like.
        internal void Despawn(Body b, bool remember = true)
        {
            b.Despawning = true;
            if (b.Root != null)
            {
                if (b.Applied) b.R.health = Mathf.Max(1f, b.Health.FsmVariables.GetFsmFloat("Health").Value);
                Plugin.Log.LogInfo(b.R.name + " leaves (" + b.R.health.ToString("0") + "/" + b.R.maxHealth.ToString("0") + ").");
                Destroy(b.Root);
            }
            bodies.Remove(b);
            if (remember && (b.Combat || b.Seen)) EndEncounter(b, b.Combat ? MemoryEntry.Ran : MemoryEntry.Met);
        }

        internal void OnBodyDestroyed(Body b, Vector3 position)
        {
            bodies.Remove(b);
            if (b.Despawning) return;
            // During OnDestroy the sibling PlayMakerFSM may already have dropped its Fsm (seen on quitting to the title).
            float hp = b.LastHealth;
            try { hp = b.Health.FsmVariables.GetFsmFloat("Health").Value; } catch (Exception) { }
            if (hp >= 1f)
            {
                if (!b.R.temp && b.Applied) b.R.health = hp;
                // WW2 bails a crew member out by building a new NPC on foot with the same health and deleting the
                // seated one (Patrol.BailOut), so the legend follows to the new body (M4 play test: it was lost).
                if (b.Road) StartCoroutine(FollowBailOut(b, position, hp));
                return;   // cleanup or scene change
            }
            Die(b, position);
        }

        internal void Die(Body b, Vector3 position)
        {
            var r = b.R;
            if (!r.temp)
            {
                r.state = LegendRecord.Dead;
                r.health = 0f;
                r.diedDay = Game.Day();
                r.bounty = Fame.Bounty(r.fame);
                World.nextRiseDay = Mathf.Max(World.nextRiseDay, r.diedDay + 2 + rng.Next(3));   // replaced 2-4 days later
                News(r, r.name + (string.IsNullOrEmpty(r.title) ? "" : " " + r.title) + " is dead.", false);
                EndEncounter(b, MemoryEntry.Killed);
                ScheduleReturn(r);
            }
            Plugin.Log.LogInfo(r.name + " died (fame " + r.fame + ", bounty " + r.bounty + ").");
            StartCoroutine(SpawnMark(r, position + Vector3.up * 0.6f));
            if (!r.temp) StartCoroutine(SpawnTrophy(r, position + Vector3.up * 0.8f + Vector3.right * 0.5f));
            QueueStash(r);
        }

        // ---- The fight ----

        private void Watch(Body b)
        {
            var r = b.R;
            var health = b.Health.FsmVariables.GetFsmFloat("Health");

            // Each Bodypart keeps the last hit in its Damage variable (the head's after its own multiplier). Read and
            // clear them to tell head hits from body hits; the game writes the variable again before every hit.
            float headHit = b.HeadDamage != null ? b.HeadDamage.Value : 0f;
            float bodyHit = b.BodyDamage != null ? b.BodyDamage.Value : 0f;
            if (b.HeadDamage != null) b.HeadDamage.Value = 0f;
            if (b.BodyDamage != null) b.BodyDamage.Value = 0f;

            string extra = "";
            if (health.Value >= 1f)
            {
                // Done after the hit, not through the head's FloatMultiply: WW2 Gunplay cancels that multiplier for its
                // bullets (Tracers.HeadFactor). A hit that already took it below 1 has started the death states.
                if (headHit < -0.001f && r.Has("Ironhead")) { health.Value -= headHit * 0.5f; extra = ", Ironhead gave back " + (-headHit * 0.5f).ToString("0.#"); Reveal(r, "Ironhead"); }
                if (headHit < -0.001f && r.Has("GlassJaw")) { health.Value += headHit * 0.5f; extra = ", Glass Jaw took " + (-headHit * 0.5f).ToString("0.#") + " more"; Reveal(r, "GlassJaw"); }
                if (bodyHit < -0.001f && r.Has("SoftBelly")) { health.Value += bodyHit * 0.3f; extra = ", Soft Belly took " + (-bodyHit * 0.3f).ToString("0.#") + " more"; Reveal(r, "SoftBelly"); }
            }
            // Trophy perks (TrophyPerks.cs) on a legend too.
            if (headHit < -0.001f || bodyHit < -0.001f) OnPlayerHit(b.Root, health, headHit, bodyHit);

            float hp = health.Value;
            if (Math.Abs(hp - b.LastHealth) > 0.01f)
            {
                string where = headHit < -0.001f ? "HEAD hit " + headHit.ToString("0.#") : bodyHit < -0.001f ? "body hit " + bodyHit.ToString("0.#") : "other";
                Plugin.Log.LogInfo(r.name + " health " + b.LastHealth.ToString("0.#") + " -> " + hp.ToString("0.#") + " (" + where + extra + ")");
                if (where != "other" && hp < b.LastHealth)
                {
                    b.Dealt += b.LastHealth - Mathf.Max(0f, hp);
                    b.Combat = true;
                    string weapon = CurrentWeapon();
                    int n;
                    b.Weapons.TryGetValue(weapon, out n);
                    b.Weapons[weapon] = n + 1;
                }
                b.LastHealth = hp;
            }

            // A legend without Thick Hide would be dead by now.
            if (r.Has("ThickHide") && r.maxHealth - hp > r.maxHealth / 1.5f) Reveal(r, "ThickHide");

            if (!b.HurtTaunted && hp < r.maxHealth * 0.5f && hp >= 1f) { b.HurtTaunted = true; Say(r, Taunts.Pick(Taunts.Hurt, rng)); }

            var player = Game.Player();
            if (!b.Seen && InLineOfSight(b)) Announce(b);
            else if (!b.Seen && !b.LoudTaunted && r.Has("Loudmouth") && player != null && Vector3.Distance(player.position, b.Root.transform.position) < 70f)
            {
                b.LoudTaunted = true;
                Say(r, Taunts.Pick(Taunts.Loud, rng));
                Reveal(r, "Loudmouth");
            }
        }

        private static bool InLineOfSight(Body b)
        {
            return b.Detection != null && b.Detection.Fsm.Initialized && b.Detection.ActiveStateName == "inLOS";
        }

        private void Announce(Body b)
        {
            b.Seen = true;
            if (!b.R.temp) b.R.encounters++;
            ShowCard(b.R);
            Say(b.R, MemoryTaunt(b.R) ?? FirstLine(b.R, Taunts.Sight));
        }

        // A first meeting: now and then it shows off its signature weapon.
        internal string FirstLine(LegendRecord r, string[] lines)
        {
            if (!string.IsNullOrEmpty(r.signature) && rng.Next(3) == 0)
                return string.Format(Taunts.Pick(Taunts.Signature, rng), Signatures.Name(r.signature));
            return Taunts.Pick(lines, rng);
        }

        // Bruiser shows itself on the first melee hit the player takes from it: that hit writes its own value.
        private void WatchPlayer()
        {
            if (playerHit == null)
            {
                var body = Game.Fsm(Game.Global("Player"), "Bodypart");
                if (body == null) return;
                playerHit = body.FsmVariables.GetFsmFloat("Damage");
            }
            float hit = playerHit.Value;
            if (Math.Abs(hit) < 0.001f) return;
            playerHit.Value = 0f;
            var player = Game.Player();

            // Whose hit: the nearest legend that has seen the player (its crew don't exist yet, so this is close enough).
            Body hitter = null;
            float near = 80f;
            foreach (var b in bodies)
            {
                if (b.Root == null || !b.Seen || b.R.temp) continue;
                float d = Vector3.Distance(player.position, b.Root.transform.position);
                if (d < near) { near = d; hitter = b; }
            }
            if (hitter != null) { hitter.Taken += Math.Abs(hit); hitter.Combat = true; }

            foreach (var b in bodies)
            {
                if (b.Root == null || !b.R.Has("Bruiser") || b.MeleeHit <= 0f) continue;
                if (Math.Abs(Math.Abs(hit) - b.MeleeHit) < 0.6f && Vector3.Distance(player.position, b.Root.transform.position) < 5f) Reveal(b.R, "Bruiser");
            }
        }

        internal void Reveal(LegendRecord r, string trait)
        {
            if (r.temp || r.Knows(trait)) return;
            r.known.Add(trait);
            var t = Trait.Get(trait);
            Notice("Learned: " + r.name + " - " + t.Name + ": " + t.Effect + ".");
            Plugin.Log.LogInfo("Revealed " + r.name + ": " + trait);
        }

        // ---- F5 / bench test legends (not saved) ----

        private void PromoteLookedAt()
        {
            var eyes = Game.Eyes();
            if (eyes == null) return;
            var hits = Physics.RaycastAll(eyes.position, eyes.forward, 80f, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, c) => a.distance.CompareTo(c.distance));
            foreach (var hit in hits)
            {
                var root = Game.EnemyRoot(hit.collider.transform);
                if (root == null) continue;
                Promote(root, "Ironhead", "Loudmouth");
                return;
            }
            Plugin.Log.LogInfo("Promote: no enemy in sight (look straight at one, within 80 m).");
        }

        internal Body Promote(GameObject root, string strength, string weakness)
        {
            foreach (var existing in bodies) if (existing.Root == root) return existing;
            var r = new LegendRecord
            {
                temp = true, name = Names.Name(rng), title = Names.Title(rng), body = Game.BaseName(root.name),
                strength = strength, weakness = weakness, bounty = Fame.Bounty(0),
            };
            r.maxHealth = r.health = Game.Health(root).Value * Plugin.HealthMultiplier.Value * (strength == "ThickHide" ? 1.5f : 1f);
            var b = Attach(r, root);
            string log = Apply(b);
            Plugin.Log.LogInfo("Test legend " + r.name + " from " + root.name + ": " + Game.Describe(root) + " (" + log + ")");
            if (InLineOfSight(b)) Announce(b);
            return b;
        }

        // ---- The mark and the bounty ----

        private IEnumerator SpawnMark(LegendRecord r, Vector3 at)
        {
            yield return null;
            GameObject item = null;
            try { item = Game.CreateRegisteredItem(Plugin.MarkItem.Value, at); }
            catch (Exception e) { Plugin.Log.LogError("Mark spawn failed: " + e); }
            if (item == null) yield break;
            if (!r.temp) { r.markName = item.name; r.markWorld = Game.ToWorld(item.transform.position); }
            marks.Add(new Mark { Item = item, R = r });
            Plugin.Log.LogInfo("Dropped " + r.name + "'s mark as " + item.name + ".");
            Notice(r.name + " is dead. Drop the mark in a trader's sell box for the " + r.bounty + " nut bounty.");
        }

        // The mark is a registered item, so the game saves and reloads it by name; find it again after a load.
        private void RelinkMarks()
        {
            foreach (var r in World.legends)
            {
                if (r.state != LegendRecord.Dead || r.bountyPaid || string.IsNullOrEmpty(r.markName)) continue;
                bool tracked = false;
                foreach (var m in marks) if (m.R == r && m.Item != null) tracked = true;
                if (tracked) continue;
                var item = GameObject.Find(r.markName);
                if (item == null) continue;
                string renamed = Game.FixOldName(item, Plugin.MarkItem.Value);
                if (renamed != null) { Plugin.Log.LogInfo("Mark " + r.markName + " renamed to " + renamed + " (the game's pattern)."); r.markName = renamed; }
                marks.Add(new Mark { Item = item, R = r });
            }
        }

        // Native traders only buy what is on their price board (economy.md), so a mark that has rested inside any
        // sell_trigger box for a second pays its bounty itself. While the Coyotes are hostile the game switches the
        // boxes off: then nothing is paid, and the player is told why (in the second play test it just did nothing).
        private float nextClosedNotice;

        private void CheckBounties()
        {
            foreach (var m in marks.ToArray())
            {
                if (m.Item == null) continue;
                bool open;
                var box = Game.SellBoxAt(m.Item.transform.position, out open);
                if (box != null && !open)
                {
                    m.ChecksInBox = 0;
                    if (Time.realtimeSinceStartup >= nextClosedNotice)
                    {
                        nextClosedNotice = Time.realtimeSinceStartup + 20f;
                        Notice(Game.CoyotesHostile()
                            ? "The Coyotes won't pay for " + m.R.name + "'s mark: they're angry with you for hurting one of theirs. Try again in a few minutes."
                            : "This trader isn't buying right now.");
                        Plugin.Log.LogInfo("Mark " + m.Item.name + " lies in a closed sell box (Coyotes hostile: " + Game.CoyotesHostile() + ").");
                    }
                    continue;
                }
                m.ChecksInBox = box != null ? m.ChecksInBox + 1 : 0;
                if (m.ChecksInBox >= 4) PayBounty(m);
            }
        }

        private void PayBounty(Mark m)
        {
            var money = Game.Money();
            if (money == null) { m.ChecksInBox = 0; return; }
            int before = money.Value;
            money.Value = before + m.R.bounty;
            Game.Unregister(m.Item);
            string itemName = m.Item.name;
            Destroy(m.Item);
            m.Item = null;
            if (!m.R.temp) m.R.bountyPaid = true;
            Notice("Bounty paid: " + m.R.bounty + " nuts for " + m.R.name + ".");
            Plugin.Log.LogInfo("Bounty for " + m.R.name + " paid: money " + before + " -> " + money.Value + " (" + itemName + " removed).");
        }
    }
}
