using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace WastelandLegends
{
    // A marker on Wild Wasteland 2's map and compass for every legend camp the player knows. WW2 is optional, so
    // everything goes through reflection on its public ModApi (WildWasteland.Core.ModApi.Get<IMapMarkers /
    // ICompassMarkers>), the way its own quests add their flags (Quests/MapLink.cs): add again whenever a marker is
    // gone, since WW2 drops markers between loads. Positions are scene positions, asked for every frame through a Func.
    internal class Markers
    {
        private const string Owner = "WastelandLegends";
        // An icon from WW2's HUD/Icons folder (file name without .png). There is no skull there: "skull" drew nothing.
        private const string Icon = "diamond";
        private static readonly Color Red = new Color(0.9f, 0.3f, 0.2f);

        private class Shown { public int Map, Compass; }

        private bool resolved;
        private MethodInfo getMap, getCompass;
        private Type mapType, compassType;
        private object mapApi, compassApi;
        private readonly Dictionary<LegendRecord, Shown> shown = new Dictionary<LegendRecord, Shown>();

        public void Tick(List<LegendRecord> camps)
        {
            if (!Resolve()) return;
            try
            {
                var map = getMap.Invoke(null, null);
                var compass = getCompass.Invoke(null, null);
                bool same = map == mapApi && compass == compassApi && shown.Count == camps.Count;
                foreach (var r in camps) if (!shown.ContainsKey(r)) same = false;
                if (!same) { Clear(); mapApi = map; compassApi = compass; }

                foreach (var r in camps)
                {
                    Shown s;
                    if (!shown.TryGetValue(r, out s)) { s = new Shown(); shown[r] = s; }
                    var record = r;
                    Func<Vector3> where = () => Game.ToScene(record.lairWorld);
                    if (map != null && !Exists(map, mapType, s.Map))
                        s.Map = (int)mapType.GetMethod("Add").Invoke(map, new object[] { Owner, where, Icon, Red, r.name + " - " + Names.Camp(r.lairName) });
                    if (compass != null && !Exists(compass, compassType, s.Compass))
                        s.Compass = (int)compassType.GetMethod("Add").Invoke(compass, new object[] { Owner, where, Icon, Red });
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Map markers off: " + e.Message);
                getMap = null;   // don't retry every second
            }
        }

        public void Clear()
        {
            try
            {
                if (mapApi != null) mapType.GetMethod("RemoveAll").Invoke(mapApi, new object[] { Owner });
                if (compassApi != null) compassType.GetMethod("RemoveAll").Invoke(compassApi, new object[] { Owner });
            }
            catch (Exception) { }
            shown.Clear();
        }

        public int Count { get { return shown.Count; } }

        private static bool Exists(object api, Type iface, int id)
        {
            return id != 0 && (bool)iface.GetMethod("Exists").Invoke(api, new object[] { id });
        }

        private bool Resolve()
        {
            if (resolved) return getMap != null && getCompass != null;
            resolved = true;
            Type modApi = null, map = null, compass = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "WildWasteland") continue;
                modApi = asm.GetType("WildWasteland.Core.ModApi");
                map = asm.GetType("WildWasteland.Core.IMapMarkers");
                compass = asm.GetType("WildWasteland.Core.ICompassMarkers");
            }
            if (modApi == null || map == null || compass == null) return false;
            mapType = map;
            compassType = compass;
            var get = modApi.GetMethod("Get", BindingFlags.Public | BindingFlags.Static);
            getMap = get.MakeGenericMethod(map);
            getCompass = get.MakeGenericMethod(compass);
            Plugin.Log.LogInfo("Wild Wasteland 2 found: the legend's camp gets a map and compass marker.");
            return true;
        }
    }
}
