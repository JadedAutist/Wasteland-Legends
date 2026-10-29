using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace WastelandLegends
{
    // Wild Wasteland 2's talk window, bail-out event, NPC AI and patrols, through the bridge DLL WastelandLegends.Ww2
    // (tools/WastelandLegends.Ww2/Bridge.cs), the only code compiled against WildWasteland.dll. No WW2 type may appear
    // in this DLL: without WW2, .NET then can't list its types, and the Dev Loader and HarmonyX failed on that (0.9.0
    // no-WW2 test). The bridge is loaded the first time it is needed, and only when Runner has seen WW2 loaded.
    internal static class Ww2Talk
    {
        private static Assembly bridge;
        private static Type bridgeType;
        private static readonly Dictionary<string, MethodInfo> methods = new Dictionary<string, MethodInfo>();

        private static Runner runner;
        private static Func<GameObject, IList<KeyValuePair<string, string>>> buttons;
        private static Action<string> click;
        private static Action<GameObject, GameObject> bail;

        // Next to WastelandLegends.dll in a release; the build folder under the Dev Loader (which loads from bytes).
        private static string BridgePath()
        {
            string dir = Plugin.Dev ? Path.Combine(Paths.GameRootPath, "GameInternals/build/WastelandLegends")
                : Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            return Path.Combine(dir, "WastelandLegends.Ww2.dll");
        }

        private static object Call(string name, params object[] args)
        {
            if (bridgeType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    if (asm.GetName().Name == "WastelandLegends.Ww2") bridge = asm;   // still loaded from an earlier copy
                // From bytes: LoadFrom keeps the file open while the game runs, and the build then can't replace it.
                if (bridge == null) bridge = Assembly.Load(File.ReadAllBytes(BridgePath()));
                bridgeType = bridge.GetType("WastelandLegends.Ww2.Bridge", true);
            }
            MethodInfo m;
            if (!methods.TryGetValue(name, out m)) methods[name] = m = bridgeType.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            if (m == null) throw new MissingMethodException("WastelandLegends.Ww2.Bridge", name);
            try { return m.Invoke(null, args); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        }

        internal static void Tick(Runner r)
        {
            if (runner != r)
            {
                runner = r;
                buttons = Buttons;
                click = Click;
                bail = (seated, onFoot) =>
                {
                    try { if (runner != null) runner.OnCrewBailedOut(seated, onFoot); }
                    catch (Exception ex) { Plugin.Log.LogWarning("Bail-out event: " + ex.Message); }
                };
            }
            if ((bool)Call("Tick", buttons, click, bail))
                Plugin.Log.LogInfo("Wild Wasteland 2 talk window: Coyote traders get 'news' and 'legends' answers.");
        }

        // Our buttons for a trader in the talk window (keys start with "legends-"), or none.
        private static IList<KeyValuePair<string, string>> Buttons(GameObject speaker)
        {
            if (runner == null || runner.World == null || !runner.IsTrader(speaker)) return null;
            var list = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("legends-news", "What's the news out there?") };
            string trait;
            var subject = runner.RumorSubject(out trait);
            if (subject != null) list.Add(new KeyValuePair<string, string>("legends-rumor", "Heard of any legends? (" + Fame.RumorPrice(subject.fame) + " nuts)"));
            return list;
        }

        private static void Click(string key)
        {
            if (runner == null) return;
            if (key == "legends-news") runner.TalkNews();
            else if (key == "legends-rumor") runner.TalkRumor();
        }

        internal static void Uninstall()
        {
            runner = null;
            if (bridgeType != null) Call("Uninstall");
        }

        internal static string Traffic(Vector3 world) { return (string)Call("Traffic", world); }

        internal static string SpawnPatrolCar(string template, Vector3 scene, Quaternion rotation, Action<GameObject> done)
        {
            return (string)Call("SpawnPatrolCar", template, scene, rotation, done);
        }

        internal static bool Inform(GameObject npc, GameObject target) { return (bool)Call("Inform", npc, target); }

        internal static string BailOut(GameObject car, GameObject pax) { return (string)Call("BailOut", car, pax); }

        internal static void Line(string text, bool player) { Call("Line", text, player); }
    }
}
