using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace WastelandLegends
{
    // Trophy perks that act on hits: Skullsplitter (head hits +50 %), Bloodthirst (a kill heals the player +10) and
    // Bleeding (the target keeps bleeding for a few seconds; Searing, real fire, comes from the Weapons Pack). Only while a trophy with that perk is drawn.
    //
    // A hit is seen the way legend traits see it (Runner.Watch): each Bodypart keeps the last hit in its Damage
    // variable, which is read and cleared. Nearby enemies are polled each frame; legends are handled in Watch. A hit
    // counts as the player's when the drawn weapon's Attack FSM was firing in the last 0.6 s.
    internal partial class Runner
    {
        private class Target
        {
            public GameObject Root;
            public FsmFloat Health, Head, Body;
        }

        internal readonly HashSet<string> activePerks = new HashSet<string>();
        private readonly List<Target> targets = new List<Target>();
        private float nextTargetScan;
        internal float lastPlayerAttack = -100f;

        private void TickPerks()
        {
            if (activePerks.Count == 0) return;
            var model = DrawnModel();
            var attack = model != null ? Game.Fsm(model, "Attack") : null;
            if (attack != null && attack.Fsm.Initialized)
            {
                string s = attack.ActiveStateName;
                if (s == "fire" || s == "hit" || s == "wait" || s == "getLayer" || s == "actorHitSound") lastPlayerAttack = Time.time;
            }

            if (Time.time >= nextTargetScan)
            {
                nextTargetScan = Time.time + 0.5f;
                targets.Clear();
                var player = Game.Player();
                if (player != null)
                    foreach (var col in Physics.OverlapSphere(player.position, 120f, 1 << Game.ActorLayer, QueryTriggerInteraction.Ignore))
                    {
                        var root = Game.EnemyRoot(col.transform);
                        if (root == null || root.GetComponent<LegendTag>() != null) continue;
                        bool known = false;
                        foreach (var t in targets) if (t.Root == root) known = true;
                        if (known) continue;
                        var head = Game.HeadBodypart(root);
                        var body = Game.Fsm(root, "Bodypart");
                        targets.Add(new Target
                        {
                            Root = root, Health = Game.Health(root),
                            Head = head != null ? head.FsmVariables.GetFsmFloat("Damage") : null,
                            Body = body != null ? body.FsmVariables.GetFsmFloat("Damage") : null,
                        });
                    }
            }

            foreach (var t in targets)
            {
                if (t.Root == null || t.Health == null) continue;
                float head = t.Head != null ? t.Head.Value : 0f, body = t.Body != null ? t.Body.Value : 0f;
                if (Math.Abs(head) < 0.001f && Math.Abs(body) < 0.001f) continue;
                if (t.Head != null) t.Head.Value = 0f;
                if (t.Body != null) t.Body.Value = 0f;
                OnPlayerHit(t.Root, t.Health, head, body);
            }
        }

        internal bool PlayerAttackedRecently() { return Time.time - lastPlayerAttack < 0.6f; }

        // After the game applied the hit (health already lowered).
        internal void OnPlayerHit(GameObject root, FsmFloat health, float headHit, float bodyHit)
        {
            if (activePerks.Count == 0 || !PlayerAttackedRecently() || health == null) return;
            // A bleed tick (the game's own or a stack's, both −2 on the body) is not a new hit: it must not add a stack.
            if (Math.Abs(headHit) < 0.001f && Math.Abs(bodyHit + Burn.Tick) < 0.01f && root.transform.Find(Burn.Name) != null) return;
            bool wasAlive = health.Value - headHit - bodyHit >= 1f;
            if (activePerks.Contains(Perks.Skullsplitter) && headHit < -0.001f && health.Value >= 1f) health.Value += headHit * 0.5f;
            if (activePerks.Contains(Perks.Bleeding) && health.Value >= 1f) Bleed(root, 4f);
            if (activePerks.Contains(Perks.Searing) && health.Value >= 1f) PackApi.Ignite(root, 4f);
            if (activePerks.Contains(Perks.Bloodthirst) && wasAlive && health.Value < 1f) HealPlayer(10f);
        }

        internal int heals;   // bench: god mode puts the player's health back at once, so count the heals

        internal void HealPlayer(float amount)
        {
            var hp = Game.Health(Game.Global("Player"));
            if (hp == null) return;
            hp.Value = Mathf.Min(100f, hp.Value + amount);
            heals++;
        }

        // ---- Bleeding ----

        // Damage over time through the game's own fire damage: an NPC takes −2 every 0.5 s while a trigger tagged "Fire"
        // touches its FireDamageCollider (FireDamage FSM). That shows as blood, not flames: in play it read as bleeding,
        // which the user liked, so it is the Bleeding perk (real burning is Searing, from the Weapons Pack). The trigger
        // has its own kinematic Rigidbody: colliders on the FireDamageCollider's own Rigidbody wouldn't trigger it.
        // Each hit adds a stack of 4 s, up to 3 (user, 2026-10-29). The FireDamage FSM is one loop that a second
        // trigger can't speed up, so the first stack is the game's own and stacks 2-3 tick themselves (Burn).
        internal void Bleed(GameObject root, float seconds)
        {
            var existing = root.transform.Find(Burn.Name);
            if (existing != null) { var keep = existing.GetComponent<Burn>(); if (keep != null) keep.Add(seconds); return; }
            var fireCol = root.transform.Find("FireDamageCollider");
            var go = new GameObject(Burn.Name);
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = fireCol != null ? fireCol.localPosition : Vector3.up;
            go.tag = "Fire";
            if (fireCol != null && !Physics.GetIgnoreLayerCollision(fireCol.gameObject.layer, fireCol.gameObject.layer)) go.layer = fireCol.gameObject.layer;
            var sphere = go.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.radius = 0.8f;
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            var burn = go.AddComponent<Burn>();
            burn.Add(seconds);
        }
    }

    internal class Burn : MonoBehaviour
    {
        internal const string Name = "WastelandLegends.Fire";
        internal const float Tick = 2f;   // as the game's FireDamage: −2 every 0.5 s
        internal const int MaxStacks = 3;

        private class Stack { public float Until, NextTick; }
        private readonly List<Stack> stacks = new List<Stack>();

        internal int Stacks { get { return stacks.Count; } }

        // A new stack, or at the cap a fresh timer on the oldest one.
        internal void Add(float seconds)
        {
            if (stacks.Count >= MaxStacks) { stacks.Sort((a, b) => a.Until.CompareTo(b.Until)); stacks[0].Until = Time.time + seconds; return; }
            stacks.Add(new Stack { Until = Time.time + seconds, NextTick = Time.time + 0.5f });
        }

        private void Update()
        {
            stacks.RemoveAll(s => Time.time >= s.Until);
            if (stacks.Count == 0) { Destroy(gameObject); return; }
            // The longest-lasting stack is the game's own (the Fire trigger stays while any stack does); the others tick here.
            stacks.Sort((a, b) => b.Until.CompareTo(a.Until));
            for (int i = 1; i < stacks.Count; i++)
                if (Time.time >= stacks[i].NextTick)
                {
                    stacks[i].NextTick += 0.5f;
                    Hurt();
                }
        }

        private void Hurt()
        {
            var root = transform.parent;
            var body = root != null ? Game.Fsm(root.gameObject, "Bodypart") : null;
            var health = root != null ? Game.Health(root.gameObject) : null;
            if (body == null || health == null || health.Value < 1f) return;
            body.FsmVariables.GetFsmFloat("Damage").Value = -Tick;
            body.SendEvent("Damage");
        }
    }
}
