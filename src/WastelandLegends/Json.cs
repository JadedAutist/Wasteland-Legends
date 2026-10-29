using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace WastelandLegends
{
    // A small JSON writer/reader for the saved records. JsonUtility wrote LegendWorld without its list of
    // LegendRecords (2026-10-28, also after making them public), most likely because Unity's serializer doesn't know
    // types from an assembly loaded at runtime. This handles exactly what the records use: public instance fields of
    // string, bool, int, float, Vector3, List<T> and nested classes. [NonSerialized] fields are skipped.
    internal static class Json
    {
        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        public static T Read<T>(string json) where T : new()
        {
            int i = 0;
            object tree = Parse(json, ref i);
            return (T)Convert(tree, typeof(T));
        }

        // ---- Writing ----

        private static void WriteValue(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is int) { sb.Append(((int)v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is float) { sb.Append(((float)v).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is Vector3)
            {
                var p = (Vector3)v;
                sb.Append("{\"x\":").Append(p.x.ToString("R", CultureInfo.InvariantCulture))
                  .Append(",\"y\":").Append(p.y.ToString("R", CultureInfo.InvariantCulture))
                  .Append(",\"z\":").Append(p.z.ToString("R", CultureInfo.InvariantCulture)).Append('}');
                return;
            }
            var list = v as IList;
            if (list != null)
            {
                sb.Append('[');
                for (int i = 0; i < list.Count; i++) { if (i > 0) sb.Append(','); WriteValue(sb, list[i]); }
                sb.Append(']');
                return;
            }
            sb.Append('{');
            bool first = true;
            foreach (var f in Fields(v.GetType()))
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, f.Name);
                sb.Append(':');
                WriteValue(sb, f.GetValue(v));
            }
            sb.Append('}');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static IEnumerable<FieldInfo> Fields(Type t)
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (!f.IsNotSerialized && !f.IsInitOnly) yield return f;
        }

        // ---- Reading: text -> Dictionary / List / string / double / bool / null ----

        private static object Parse(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{')
            {
                var obj = new Dictionary<string, object>();
                i++;
                Skip(s, ref i);
                if (s[i] == '}') { i++; return obj; }
                while (true)
                {
                    Skip(s, ref i);
                    string key = ParseString(s, ref i);
                    Skip(s, ref i);
                    Expect(s, ref i, ':');
                    obj[key] = Parse(s, ref i);
                    Skip(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    Expect(s, ref i, '}');
                    return obj;
                }
            }
            if (c == '[')
            {
                var arr = new List<object>();
                i++;
                Skip(s, ref i);
                if (s[i] == ']') { i++; return arr; }
                while (true)
                {
                    arr.Add(Parse(s, ref i));
                    Skip(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    Expect(s, ref i, ']');
                    return arr;
                }
            }
            if (c == '"') return ParseString(s, ref i);
            if (s.Substring(i).StartsWith("true")) { i += 4; return true; }
            if (s.Substring(i).StartsWith("false")) { i += 5; return false; }
            if (s.Substring(i).StartsWith("null")) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        private static string ParseString(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            i++;
            return sb.ToString();
        }

        private static void Skip(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException("expected '" + c + "' at " + i);
            i++;
        }

        // ---- Tree -> typed objects (unknown keys are ignored, missing ones keep their defaults) ----

        private static object Convert(object v, Type t)
        {
            if (v == null) return t.IsValueType ? Activator.CreateInstance(t) : null;
            if (t == typeof(string)) return v as string;
            if (t == typeof(bool)) return v is bool && (bool)v;
            if (t == typeof(int)) return v is double ? (int)Math.Round((double)v) : 0;
            if (t == typeof(float)) return v is double ? (float)(double)v : 0f;
            if (t == typeof(Vector3))
            {
                var d = v as Dictionary<string, object>;
                return d == null ? Vector3.zero : new Vector3(Num(d, "x"), Num(d, "y"), Num(d, "z"));
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var list = (IList)Activator.CreateInstance(t);
                var items = v as List<object>;
                if (items != null) foreach (var item in items) list.Add(Convert(item, t.GetGenericArguments()[0]));
                return list;
            }
            var obj = Activator.CreateInstance(t);
            var dict = v as Dictionary<string, object>;
            if (dict == null) return obj;
            foreach (var f in Fields(t))
            {
                object raw;
                if (dict.TryGetValue(f.Name, out raw)) f.SetValue(obj, Convert(raw, f.FieldType));
            }
            return obj;
        }

        private static float Num(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is double ? (float)(double)v : 0f;
        }
    }
}
