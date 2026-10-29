using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace WastelandLegends
{
    // Small helpers around the game's objects and FSMs (see GameInternals/notes).
    internal static class Game
    {
        public const int ActorLayer = 10;

        public static PlayMakerFSM Fsm(GameObject go, string name)
        {
            if (go == null) return null;
            foreach (var fsm in go.GetComponents<PlayMakerFSM>())
                if (fsm.FsmName == name) return fsm;
            return null;
        }

        public static GameObject Global(string name)
        {
            var v = FsmVariables.GlobalVariables.GetFsmGameObject(name);
            return v != null ? v.Value : null;
        }

        public static Transform Player()
        {
            var p = Global("Player");
            return p != null ? p.transform : null;
        }

        // The camera: where the player looks from (on foot and in cars).
        public static Transform Eyes()
        {
            return Camera.main != null ? Camera.main.transform : Player();
        }

        public static GameObject EnemyRoot(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                var go = t.gameObject;
                if (go.layer != ActorLayer) continue;
                if (Fsm(go, "Health") != null && Fsm(go, "Detection") != null) return go;
            }
            return null;
        }

        public static PlayMakerFSM HeadBodypart(GameObject root)
        {
            foreach (var fsm in root.GetComponentsInChildren<PlayMakerFSM>(true))
                if (fsm.FsmName == "Bodypart" && fsm.gameObject != root && fsm.gameObject.name.EndsWith("Head")) return fsm;
            return null;
        }

        public static FsmFloat Health(GameObject go)
        {
            var h = Fsm(go, "Health");
            return h != null ? h.FsmVariables.GetFsmFloat("Health") : null;
        }

        public static string BaseName(string name)
        {
            name = name.Replace("(Clone)", "").Trim();
            int end = name.Length;
            while (end > 0 && char.IsDigit(name[end - 1])) end--;
            return name.Substring(0, end).Trim();
        }

        // ---- World positions ----

        private static Transform mapMagic;

        // The terrain root moves with every floating-origin jump, so positions relative to it stay put (as WW2 does).
        public static Transform MapMagic()
        {
            if (mapMagic == null) { var go = GameObject.Find("MapMagic"); mapMagic = go != null ? go.transform : null; }
            return mapMagic;
        }

        public static Vector3 ToWorld(Vector3 scene) { var m = MapMagic(); return m != null ? scene - m.position : scene; }
        public static Vector3 ToScene(Vector3 world) { var m = MapMagic(); return m != null ? world + m.position : world; }

        public static Vector3 Ground(Vector3 at, out bool found)
        {
            RaycastHit hit;
            int mask = ~((1 << 6) | (1 << 9) | (1 << 10) | (1 << 2));   // not the player, items, actors, ignore-raycast
            found = Physics.Raycast(at + Vector3.up * 80f, Vector3.down, out hit, 300f, mask, QueryTriggerInteraction.Ignore);
            return found ? hit.point + Vector3.up * 0.1f : at;
        }

        // Where to place an enemy so it lands on the ground: its capsule is centred on the body, not the feet, and a
        // TerrainCollider only pushes things out from above, so a raider placed 0.1 m above open terrain started half
        // inside it and fell through (M5 hunts, 2026-10-29; camps worked by standing on floors and rocks).
        public static Vector3 ActorSpawn(Vector3 at, out bool found)
        {
            return Ground(at, out found) + Vector3.up * 1.2f;
        }

        // ---- Camps ----

        // Scrapyard raider camps in the loaded terrain tiles: MapMagic/Tile x,y/Objects/Camp_N(Clone) with a direct
        // child EnemySpawn_Scrapyard_V*. Only tiles near the player exist.
        public static List<Transform> ScrapyardCamps()
        {
            var camps = new List<Transform>();
            var root = MapMagic();
            if (root == null) return camps;
            foreach (Transform tile in root)
            {
                var objects = tile.Find("Objects");
                if (objects == null) continue;
                foreach (Transform poi in objects)
                {
                    if (!poi.name.StartsWith("Camp_")) continue;
                    foreach (Transform child in poi)
                        if (child.name.StartsWith("EnemySpawn_Scrapyard_V")) { camps.Add(poi); break; }
                }
            }
            return camps;
        }

        // "Camp_9(Clone)" and WW2 town copies like "Camp_3 (Towns 183166636/1)" -> "Camp_9" / "Camp_3"; 0 if not a camp.
        public static string CampId(string objectName, out int number)
        {
            number = 0;
            if (objectName == null || !objectName.StartsWith("Camp_")) return objectName;
            int end = 5;
            while (end < objectName.Length && char.IsDigit(objectName[end])) end++;
            if (end == 5) return objectName;
            number = int.Parse(objectName.Substring(5, end - 5));
            return objectName.Substring(0, end);
        }

        public static string PathOf(Transform t)
        {
            string s = t.name;
            for (t = t.parent; t != null; t = t.parent) s = t.name + "/" + s;
            return s;
        }

        // Where a legend stands at its camp: the first scrapyard spawn point's first child (the game spawns raiders on
        // those children), else the camp itself.
        public static Vector3 CampStandPoint(Transform camp)
        {
            foreach (Transform child in camp)
                if (child.name.StartsWith("EnemySpawn_Scrapyard_V"))
                {
                    var parent = child.parent;
                    foreach (Transform sibling in parent)
                        if (sibling != child && sibling.childCount > 0 && sibling.name.ToLowerInvariant().Contains("spawn")) return sibling.GetChild(0).position;
                    return child.position;
                }
            return camp.position;
        }

        // ---- Prefabs and registered objects ----

        private static readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();

        // Enemy prefabs are loaded assets (not in a scene); looked up once by name.
        public static GameObject EnemyPrefab(string name)
        {
            GameObject go;
            if (prefabs.TryGetValue(name, out go) && go != null) return go;
            go = null;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (candidate.name != name || candidate.scene.IsValid() || candidate.transform.parent != null) continue;
                if (Fsm(candidate, "Health") == null || Fsm(candidate, "Detection") == null) continue;
                go = candidate;
                break;
            }
            prefabs[name] = go;
            return go;
        }

        public static int NextItemId()
        {
            var fsm = Fsm(Global("itemNameID"), "itemNameID");
            fsm.SendEvent("Activate");
            return fsm.FsmVariables.GetFsmInt("intName").Value;
        }

        public static PlayMakerArrayListProxy Proxy(string reference)
        {
            var registry = Global("NewGO_ArrayList");
            if (registry == null) return null;
            foreach (var proxy in registry.GetComponents<PlayMakerArrayListProxy>())
                if (proxy.referenceName == reference) return proxy;
            return null;
        }

        public static void Register(GameObject go) { var items = Proxy("ArrayList_Items"); if (items != null) items.Add(go, "GameObject"); }
        public static void Unregister(GameObject go) { var items = Proxy("ArrayList_Items"); if (items != null) items.Remove(go, "GameObject"); }

        // The game names spawned objects "<prefab>(Clone)<itemNameID>" (Instantiate adds "(Clone)", the spawner appends
        // the id). WW2's gun HUD needs that pattern: weapons we named "<prefab><id>" showed no ammo count (2026-10-29).
        public static string NativeName(string prefab, int id) { return prefab + "(Clone)" + id; }

        // Our items from before that fix ("akm_trash930"): renamed to the pattern, returning the new name, or null.
        public static string FixOldName(GameObject go, string prefab)
        {
            if (go == null || string.IsNullOrEmpty(prefab) || go.name.Contains("(Clone)") || !go.name.StartsWith(prefab)) return null;
            int id;
            if (!int.TryParse(go.name.Substring(prefab.Length), out id)) return null;
            go.name = NativeName(prefab, id);
            return go.name;
        }

        // Spawn an ItemList prefab the way the game's own spawners do: unique name from itemNameID, registered in
        // ArrayList_Items (so it is saved and tidied with the rest), then sent Randomize.
        public static GameObject CreateRegisteredItem(string prefabName, Vector3 at)
        {
            var list = Proxy("ItemList");
            GameObject prefab = null;
            if (list != null && list._arrayList != null)
                foreach (var entry in list._arrayList)
                {
                    var go = entry as GameObject;
                    if (go != null && go.name == prefabName) { prefab = go; break; }
                }
            if (prefab == null) { Plugin.Log.LogWarning("Item '" + prefabName + "' is not in ItemList."); return null; }

            var item = UnityEngine.Object.Instantiate(prefab, at, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            item.name = NativeName(prefab.name, NextItemId());
            Register(item);
            foreach (var fsm in item.GetComponents<PlayMakerFSM>()) fsm.SendEvent("Randomize");
            return item;
        }

        // ---- Game state ----

        public static int Day()
        {
            var fsm = Fsm(Global("__GameManager__"), "DayTime");
            var day = fsm != null ? fsm.FsmVariables.GetFsmInt("Day") : null;
            return day != null ? day.Value : 0;
        }

        // Time of day in hours (8.5 = 08:30), from the same DayTime FSM.
        public static float Hour()
        {
            var fsm = Fsm(Global("__GameManager__"), "DayTime");
            var time = fsm != null ? fsm.FsmVariables.GetFsmFloat("Time") : null;
            return time != null ? time.Value : 0f;
        }

        // Peaceful mode is the PeacefulMode FSM running on __GameManager__ (it deletes every registered actor).
        public static bool Peaceful()
        {
            var fsm = Fsm(Global("__GameManager__"), "PeacefulMode");
            return fsm != null && fsm.enabled;
        }

        public static FsmInt Money() { return FsmVariables.GlobalVariables.GetFsmInt("Money"); }

        // Sell boxes are camp parts (Camp_N/Sell_Items/sell_trigger, .../Sell_Liquids/sell_trigger) or WW2 trader buses.
        // While the Coyotes are hostile the game switches their colliders off, and physics queries no longer see them, so
        // camp boxes are found by name in the loaded tiles; enabled ones nearby (buses) by physics.
        private static readonly List<BoxCollider> sellBoxes = new List<BoxCollider>();
        private static float sellBoxesAt = -100f;

        public static BoxCollider SellBoxAt(Vector3 p, out bool open)
        {
            open = false;
            if (Time.realtimeSinceStartup - sellBoxesAt > 10f)
            {
                sellBoxesAt = Time.realtimeSinceStartup;
                sellBoxes.Clear();
                var root = MapMagic();
                if (root != null)
                    foreach (Transform tile in root)
                    {
                        var objects = tile.Find("Objects");
                        if (objects == null) continue;
                        foreach (Transform poi in objects)
                            foreach (Transform part in poi)
                                if (part.name.StartsWith("Sell_Items")) CollectSellBoxes(part, 0);
                    }
            }
            var near = new List<BoxCollider>(sellBoxes);
            foreach (var col in Physics.OverlapSphere(p, 3f, ~0, QueryTriggerInteraction.Collide))
            {
                var box = col as BoxCollider;
                if (box != null && box.isTrigger && box.name == "sell_trigger" && !near.Contains(box)) near.Add(box);
            }
            foreach (var box in near)
            {
                if (box == null || (box.transform.position - p).sqrMagnitude > 25f || !Inside(box, p, 0.15f)) continue;
                open = box.enabled && box.gameObject.activeInHierarchy;
                return box;
            }
            return null;
        }

        private static void CollectSellBoxes(Transform t, int depth)
        {
            foreach (Transform child in t)
            {
                if (child.name == "sell_trigger") { var box = child.GetComponent<BoxCollider>(); if (box != null) sellBoxes.Add(box); }
                else if (depth < 2) CollectSellBoxes(child, depth + 1);
            }
        }

        // The box's own shape (works while the collider is switched off, when its bounds are empty).
        public static bool Inside(BoxCollider box, Vector3 p, float margin)
        {
            Vector3 local = box.transform.InverseTransformPoint(p) - box.center;
            Vector3 half = box.size * 0.5f;
            Vector3 scale = box.transform.lossyScale;
            float mx = margin / Mathf.Max(0.001f, Mathf.Abs(scale.x)), my = margin / Mathf.Max(0.001f, Mathf.Abs(scale.y)), mz = margin / Mathf.Max(0.001f, Mathf.Abs(scale.z));
            return Mathf.Abs(local.x) <= half.x + mx && Mathf.Abs(local.y) <= half.y + my && Mathf.Abs(local.z) <= half.z + mz;
        }

        // The game turns the Coyotes hostile for 600 s after you hurt one (WW2 also on its own hits).
        public static bool CoyotesHostile()
        {
            var fsm = Fsm(Global("__GameManager__"), "FactionRelation_Coyotes");
            return fsm != null && fsm.Fsm.Initialized && (fsm.ActiveStateName == "PlayerEnemy" || fsm.ActiveStateName == "CoyotesAttacked");
        }

        public static Collider NearestSellBox(Vector3 from, float range)
        {
            Collider best = null;
            float bestDist = range;
            foreach (var col in Physics.OverlapSphere(from, range, ~0, QueryTriggerInteraction.Collide))
            {
                if (!col.isTrigger || !col.enabled || col.gameObject.name != "sell_trigger") continue;
                if (Fsm(col.gameObject, "SellItemsHere") == null) continue;
                float d = Vector3.Distance(from, col.bounds.center);
                if (d < bestDist) { best = col; bestDist = d; }
            }
            return best;
        }

        // ---- Describing an enemy's combat values (for logs and the bench) ----

        public static string Describe(GameObject root)
        {
            var hp = Health(root);
            return "health " + (hp != null ? hp.Value.ToString("0.#") : "?") + ", melee " + FirstDamage(Fsm(root, "Damage"))
                + ", ranged " + FirstDamage(Fsm(root, "Damage Ranged"));
        }

        private static string FirstDamage(PlayMakerFSM fsm)
        {
            if (fsm == null) return "-";
            if (!fsm.Fsm.Initialized) return "uninit";
            foreach (var state in fsm.Fsm.States)
                foreach (var action in state.Actions)
                {
                    var set = action as SetFsmFloat;
                    if (set == null || set.variableName == null || set.variableName.Value != "Damage") continue;
                    if (!set.setValue.UseVariable) return set.setValue.Value.ToString();
                    var v = fsm.FsmVariables.GetFsmFloat(set.setValue.Name);
                    return "{" + set.setValue.Name + "} " + (v != null ? v.Value.ToString() : "?");
                }
            return "-";
        }

        // Multiplies the damage the enemy writes into its target's Bodypart (SetFsmFloat ... "Damage"): the fixed value
        // in the action (melee), or the FSM variable it reads (ranged: "Damage Value", set once by WeaponType when the
        // enemy spawns with its gun).
        public static string ScaleDamage(PlayMakerFSM fsm, float mult)
        {
            if (fsm == null) return "no FSM";
            if (!fsm.Fsm.Initialized) return "FSM not initialized, skipped";
            var sb = new System.Text.StringBuilder();
            var scaled = new HashSet<string>();
            foreach (var state in fsm.Fsm.States)
                foreach (var action in state.Actions)
                {
                    var set = action as SetFsmFloat;
                    if (set == null || set.variableName == null || set.variableName.Value != "Damage") continue;
                    if (set.setValue.UseVariable)
                    {
                        var v = fsm.FsmVariables.GetFsmFloat(set.setValue.Name);
                        if (v == null || !scaled.Add(set.setValue.Name)) continue;
                        float was = v.Value;
                        v.Value = was * mult;
                        sb.Append(state.Name).Append(": {").Append(set.setValue.Name).Append("} ").Append(was).Append(" -> ").Append(v.Value).Append("; ");
                        continue;
                    }
                    float old = set.setValue.Value;
                    set.setValue.Value = old * mult;
                    sb.Append(state.Name).Append(": ").Append(old).Append(" -> ").Append(set.setValue.Value).Append("; ");
                }
            return sb.Length > 0 ? sb.ToString().TrimEnd(' ', ';') : "no damage action";
        }

        public static float BaseMelee(GameObject root)
        {
            var fsm = Fsm(root, "Damage");
            if (fsm == null || !fsm.Fsm.Initialized) return 0f;
            foreach (var state in fsm.Fsm.States)
                foreach (var action in state.Actions)
                {
                    var set = action as SetFsmFloat;
                    if (set != null && set.variableName != null && set.variableName.Value == "Damage" && !set.setValue.UseVariable) return set.setValue.Value;
                }
            return 0f;
        }
    }
}
