using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using WildWasteland.Core;

namespace WastelandLegends.Ww2
{
    // Everything Wasteland Legends does with Wild Wasteland 2's own types, in its own DLL. Wasteland Legends loads it
    // only when WW2 is there, and calls it by reflection with plain values and delegates: no WW2 type may appear in
    // WastelandLegends.dll, or .NET can't list that DLL's types without WW2 (the Dev Loader and HarmonyX failed on it
    // in the 0.9.0 no-WW2 test). No reference to WastelandLegends.dll either: the Dev Loader renames that on every
    // reload, while this DLL stays loaded (a change here needs a game restart).
    //
    // Talk window ([F] Talk to ...): WW2's Quests module rebuilds the buttons and then lets one optional provider append
    // more: ModApi's ISkillMerchantChoices slot, which WW2's Perks module fills (Quests/Plugin.cs SyncTalkUi). We put a
    // decorator into that slot: it calls the original first (Perks keeps its buttons), then adds ours. The slot is
    // checked every second: if WW2 puts its own provider back, it is wrapped again; on shutdown the original is restored.
    public static class Bridge
    {
        private static Choices installed;
        private static Action<GameObject, GameObject> onBail;
        private static ModEventHandler<WildWasteland.Core.Events.CrewBailedOut> bailHandler;

        // buttons(speaker) gives our (key, text) pairs for that speaker, or null; click(key) runs one.
        // bail(seated, onFoot): WW2 replaced a seated crew member with a new NPC on foot (Patrol.BailOut).
        // Returns true when the decorator was put in just now.
        public static bool Tick(Func<GameObject, IList<KeyValuePair<string, string>>> buttons, Action<string> click, Action<GameObject, GameObject> bail)
        {
            WatchBailOuts(bail);
            var current = ModApi.Get<ISkillMerchantChoices>();
            if (installed != null && current == installed) { installed.Buttons = buttons; installed.Click = click; return false; }
            bool fresh = installed == null;
            if (fresh) installed = new Choices();
            installed.Buttons = buttons;
            installed.Click = click;
            // Never wrap another copy of ourselves - unwrap it.
            while (current != null && current.GetType().FullName == typeof(Choices).FullName && current != installed)
                current = ((Choices)current).Inner;
            installed.Inner = current;
            ModApi.Provide<ISkillMerchantChoices>(ModApi.OwnerOf<ISkillMerchantChoices>(), installed);
            return fresh;
        }

        // WW2 sends CrewBailedOut while the seated one still exists: the legend moves over right then.
        // Owner null: we are not a WW2 module.
        private static void WatchBailOuts(Action<GameObject, GameObject> bail)
        {
            onBail = bail;
            if (bailHandler != null) return;
            bailHandler = (ref WildWasteland.Core.Events.CrewBailedOut e) =>
            {
                var seated = Entities.Find(e.Passenger);
                var onFoot = Entities.Find(e.Npc);
                if (onBail != null && seated != null && onFoot != null) onBail(seated, onFoot);
            };
            ModEvents.Subscribe<WildWasteland.Core.Events.CrewBailedOut>(null, bailHandler);
        }

        public static void Uninstall()
        {
            if (bailHandler != null)
            {
                try { ModEvents.Unsubscribe<WildWasteland.Core.Events.CrewBailedOut>(null, bailHandler); } catch (Exception) { }
                bailHandler = null;
            }
            onBail = null;
            if (installed == null) return;
            try
            {
                if (ModApi.Get<ISkillMerchantChoices>() == installed)
                    ModApi.Provide<ISkillMerchantChoices>(ModApi.OwnerOf<ISkillMerchantChoices>(), installed.Inner);
            }
            catch (Exception) { }
            installed.Buttons = null;
            installed.Click = null;
            installed = null;
        }

        // Bench: what WW2's traffic says about itself (journeys anywhere, patrols near a true world position).
        public static string Traffic(Vector3 world)
        {
            var patrols = ModApi.Get<IPatrols>();
            if (patrols == null) return "no IPatrols (Patrols module off?)";
            return "WW2 journeys " + patrols.JourneyCount + ", patrols within 1 km " + patrols.PatrolCount(world, 1000f) + ", within 5 km " + patrols.PatrolCount(world, 5000f)
                + ", vehicle combat " + patrols.VehicleCombatActive + ", Scrapyard hostile " + patrols.IsPlayerHostile("Scrapyard");
        }

        // Bench only: WW2's own single-car patrol spawn (PatrolsModule.SpawnPoi, internal; the POI encounters use it),
        // with a template from CarTemplate.Park. Internal API, reached by reflection: may break with any WW2 update.
        public static string SpawnPatrolCar(string template, Vector3 scene, Quaternion rotation, Action<GameObject> done)
        {
            Assembly ww2 = typeof(ModApi).Assembly;
            var moduleType = ww2.GetType("WildWasteland.Modules.Patrols.PatrolsModule");
            var templateType = ww2.GetType("WildWasteland.Modules.Patrols.CarTemplate");
            if (moduleType == null || templateType == null) return "WW2 patrol types not found";
            var module = moduleType.GetField("Instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetValue(null);
            var park = templateType.GetField("Park", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetValue(null) as Array;
            if (module == null || park == null) return "Patrols module not running";

            object pick = null;
            var names = new List<string>();
            foreach (var t in park)
            {
                var nameField = templateType.GetField("Name", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var nameProp = templateType.GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                string name = nameField != null ? nameField.GetValue(t) as string : nameProp != null ? nameProp.GetValue(t, null) as string : null;
                names.Add(name);
                if (string.Equals(name, template, StringComparison.OrdinalIgnoreCase)) pick = t;
            }
            if (pick == null) return "no template '" + template + "'; have: " + string.Join(", ", names.ToArray());
            var spawn = moduleType.GetMethod("SpawnPoi", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            spawn.Invoke(module, new object[] { pick, scene, rotation, done });
            return "asked WW2 to build '" + template + "'";
        }

        // Hunts: tell WW2's NPC AI who the hunters are after (public INpcApi), so they come for the player.
        public static bool Inform(GameObject npc, GameObject target)
        {
            var api = ModApi.Get<INpcApi>();
            if (api == null || npc == null || target == null) return false;
            api.Inform(npc, target);
            api.Remember(npc, target, 300f);
            return true;
        }

        // Bench only: WW2's own bail-out (Patrol.BailOut(car, pax, marker), internal static), the call its crews use
        // when they leave a car in a fight: a new NPC on foot with the same health, the seated one deleted.
        public static string BailOut(GameObject car, GameObject pax)
        {
            Assembly ww2 = typeof(ModApi).Assembly;
            var patrolType = ww2.GetType("WildWasteland.Modules.Patrols.Patrol");
            var markerType = ww2.GetType("WildWasteland.Modules.Patrols.PatrolMarker");
            if (patrolType == null || markerType == null) return "WW2 patrol types not found";
            var marker = car.GetComponent(markerType);
            if (marker == null) return "no PatrolMarker on " + car.name;
            var bail = patrolType.GetMethod("BailOut", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null,
                new[] { typeof(GameObject), typeof(GameObject), markerType }, null);
            if (bail == null) return "no BailOut(car, pax, marker)";
            bail.Invoke(null, new object[] { car, pax, marker });
            return "bailed out " + pax.name;
        }

        // Say a line in the open talk window: as the trader, or as "You: ...".
        public static void Line(string text, bool player)
        {
            var api = ModApi.Get<IQuestApi>();
            object talk = api != null ? api.Conversation : null;
            if (talk == null) return;
            var say = talk.GetType().GetMethod(player ? "You" : "Say", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
            if (say != null) say.Invoke(talk, new object[] { text });
        }

        internal sealed class Choices : ISkillMerchantChoices
        {
            public ISkillMerchantChoices Inner;
            public Func<GameObject, IList<KeyValuePair<string, string>>> Buttons;
            public Action<string> Click;

            public void Append(GameObject speaker, IList<QuestDialogueButton> choices)
            {
                try { if (Inner != null) Inner.Append(speaker, choices); }
                catch (Exception e) { Debug.LogWarning("Wasteland Legends: WW2 merchant choices: " + e.Message); }

                // Called every frame while the window is open: replace our buttons, don't stack them.
                for (int i = choices.Count - 1; i >= 0; i--)
                    if (choices[i] != null && choices[i].Key != null && choices[i].Key.StartsWith("legends-", StringComparison.Ordinal)) choices.RemoveAt(i);
                var ours = Buttons != null ? Buttons(speaker) : null;
                if (ours == null) return;
                foreach (var b in ours)
                {
                    string key = b.Key;
                    choices.Add(new QuestDialogueButton { Key = key, Text = b.Value, Click = () => { if (Click != null) Click(key); } });
                }
            }

            public bool ResetWanted(GameObject station)
            {
                return Inner != null && Inner.ResetWanted(station);
            }
        }
    }
}
