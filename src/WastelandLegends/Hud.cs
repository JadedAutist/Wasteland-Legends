using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace WastelandLegends
{
    // Everything drawn on screen: name tags, the arrival card, taunts, notices and the journal. IMGUI, hidden while the
    // pause menu is open (labels stayed up over it in the first test).
    internal partial class Runner
    {
        private class NoticeLine { public string Text; public float Until; }

        private readonly List<NoticeLine> notices = new List<NoticeLine>();
        private LegendRecord card;
        private float cardUntil;
        private string sayText;
        private float sayUntil;
        internal bool journalOpen;

        private GUIStyle tagStyle, cardName, cardTitle, sayStyle, noticeStyle, promptStyle, journalStyle, journalHead;
        private Canvas pauseMenu;

        internal void Notice(string text)
        {
            // The same message again (switching weapons quickly stacked four identical boxes): refresh it instead.
            foreach (var n in notices)
                if (n.Text == text) { n.Until = Time.realtimeSinceStartup + 6f + text.Length / 30f; return; }
            // Long enough to read: 6 s plus a second per 30 characters (a trader's rumor stays about 11 s).
            notices.Add(new NoticeLine { Text = text, Until = Time.realtimeSinceStartup + 6f + text.Length / 30f });
            if (notices.Count > 4) notices.RemoveAt(0);
        }

        internal void Say(LegendRecord r, string line)
        {
            sayText = r.name + ": \"" + line + "\"";
            sayUntil = Time.time + 6f;
            Plugin.Log.LogInfo("Taunt: " + sayText);
        }

        internal void ShowCard(LegendRecord r)
        {
            if (Plugin.Arrival.Value == ArrivalStyle.Off) return;
            if (Plugin.Arrival.Value == ArrivalStyle.Line) { Notice(r.name + " " + r.title + "!"); return; }
            card = r;
            cardUntil = Time.time + 3.5f;
        }

        // The pause menu shows MainMenu_Canvas and tweens the time scale to 0; Apocasetter and WW2 menus set 0.
        private bool Paused()
        {
            if (Time.timeScale < 0.01f) return true;
            if (pauseMenu == null)
            {
                var go = GameObject.Find("MainMenu_Canvas");
                if (go != null) pauseMenu = go.GetComponent<Canvas>();
            }
            return pauseMenu != null && pauseMenu.enabled;
        }

        private void OnGUI()
        {
            if (!Plugin.Enabled.Value || (World == null && bodies.Count == 0)) return;
            if (tagStyle == null || stylesForHeight != Screen.height) MakeStyles();
            var cam = Camera.main;
            if (cam == null || Paused()) return;

            foreach (var b in bodies)
                if (b.Root != null && b.Applied)
                {
                    float hp = b.Health.FsmVariables.GetFsmFloat("Health").Value;
                    Label(cam, b.Head.position + Vector3.up * 0.45f, b.R.name + "  " + hp.ToString("0") + "/" + b.R.maxHealth.ToString("0"), 60f);
                }
            foreach (var l in ItemLabels(cam)) Label(cam, l.Key, l.Value, float.MaxValue);

            stashLabels.RemoveAll(s => s.Item == null || s.Item.transform.parent != null);
            foreach (var s in stashLabels) Label(cam, s.Item.transform.position + Vector3.up * 0.6f, s.T.legend + "'s stash", 40f);

            if (card != null && Time.time < cardUntil)
            {
                float a = Mathf.Clamp01((cardUntil - Time.time) / 0.6f);
                Shadowed(new Rect(0, Screen.height * 0.22f, Screen.width, 70), card.name.ToUpperInvariant(), cardName, a);
                Shadowed(new Rect(0, Screen.height * 0.22f + 66, Screen.width, 40), Subtitle(card), cardTitle, a);
            }
            if (sayText != null && Time.time < sayUntil)
                Panel(sayText, sayStyle, Screen.height * 0.80f, true);

            // Messages: a dark panel on the left, below WW2's compass bar (they sat on top of it and the sky in the
            // M2 play test), beside the journal when it is open.
            notices.RemoveAll(n => Time.realtimeSinceStartup > n.Until);
            float x = journalOpen ? 20f + JournalWidth + 16f : 20f, y = Screen.height * 0.16f;
            float w = Mathf.Min(Screen.width * 0.42f, 640f * Scale);
            foreach (var n in notices)
            {
                var content = new GUIContent(n.Text);
                float h = noticeStyle.CalcHeight(content, w - 20f) + 12f;
                GUI.DrawTexture(new Rect(x, y, w, h), Backing);
                GUI.Label(new Rect(x + 10f, y + 6f, w - 20f, h - 12f), content, noticeStyle);
                y += h + 6f;
            }

            // Without WW2's talk window only, and only when there is something to buy.
            if (trader != null && World != null && !talkWindow)
            {
                string trait;
                var subject = RumorSubject(out trait);
                if (subject != null)
                    Panel("[" + Plugin.RumorKey.Value + "] Ask about legends (" + Fame.RumorPrice(subject.fame) + " nuts)", promptStyle, Screen.height * 0.70f, true);
            }

            if (journalOpen) Journal();
        }

        // Text sizes follow the screen: 1.0 at 1080 lines, never below 0.85.
        private static float Scale { get { return Mathf.Max(0.85f, Screen.height / 1080f); } }
        private float JournalWidth { get { return Mathf.Min(560f * Scale, Screen.width * 0.45f); } }

        private static Texture2D backing;

        private static Texture2D Backing
        {
            get
            {
                if (backing == null)
                {
                    backing = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                    backing.SetPixel(0, 0, new Color(0.08f, 0.06f, 0.05f, 0.72f));
                    backing.Apply();
                }
                return backing;
            }
        }

        // One line (or a few) of text, centred, on a dark panel just as wide as the text.
        private static void Panel(string text, GUIStyle style, float y, bool centred)
        {
            var content = new GUIContent(text);
            float max = Screen.width * 0.7f;
            float w = Mathf.Min(max, style.CalcSize(content).x + 28f);
            float h = style.CalcHeight(content, w - 28f) + 12f;
            float x = centred ? (Screen.width - w) * 0.5f : 20f;
            GUI.DrawTexture(new Rect(x, y, w, h), Backing);
            GUI.Label(new Rect(x + 14f, y + 6f, w - 28f, h - 12f), content, style);
        }

        private void Journal()
        {
            var sb = new StringBuilder();
            if (World == null || World.legends.Count == 0)
                sb.Append(Game.Day() < Plugin.GraceDays.Value ? "Nobody out here has made a name yet.\nGive it a day or two." : "No word of any legends yet.");
            else
            {
                var living = World.Living();
                living.Sort((a, b) => b.fame.CompareTo(a.fame));
                foreach (var r in living)
                {
                    sb.Append("<b>").Append(r.name).Append("</b> ").Append(r.title).Append("  <i>(").Append(Fame.Bands[r.Band]).Append(")</i>\n");
                    if (r.locationKnown) sb.Append("Holds ").Append(Names.Camp(r.lairName)).Append(", ").Append(Where(r.lairWorld)).Append('\n');
                    else sb.Append("Camp unknown: ask a Coyote trader (").Append(Plugin.RumorKey.Value).Append(")\n");
                    sb.Append("Strengths: ");
                    sb.Append(TraitName(r, r.strength));
                    foreach (var s in r.moreStrengths) sb.Append(", ").Append(TraitName(r, s));
                    sb.Append("   Weakness: ").Append(TraitName(r, r.weakness)).Append('\n');
                    if (!string.IsNullOrEmpty(r.lastDeed)) sb.Append("<i>").Append(r.lastDeed).Append("</i>\n");
                    sb.Append("Carries a ").Append(Signatures.Name(r.signature)).Append("   Bounty about ").Append(Fame.Bounty(r.fame)).Append(" nuts");
                    if (r.grudge > 0) sb.Append("   ").Append(r.grudge >= 60 ? "Wants you dead" : r.grudge >= 30 ? "Holds a grudge against you" : "Remembers you");
                    sb.Append('\n');
                    if (r.memory.Count > 0)
                    {
                        var m = r.memory[r.memory.Count - 1];
                        sb.Append("Last met day ").Append(m.day).Append(" at ").Append(m.place).Append(": ")
                          .Append(m.outcome == MemoryEntry.Ran ? "you walked away" : m.outcome == MemoryEntry.Killed ? "you killed it. It got up again." : "you crossed paths")
                          .Append('\n');
                    }
                    sb.Append('\n');
                }
                foreach (var r in World.legends)
                    if (r.state == LegendRecord.Dead)
                        sb.Append(r.name).Append(" - dead since day ").Append(r.diedDay).Append(r.bountyPaid ? ", bounty paid" : ", mark not sold yet (" + r.bounty + " nuts)")
                          .Append(r.returnDay >= 0 && !MarkTaken(r) ? ". Its mark still lies where it fell." : "")
                          .Append(r.trophyName != null ? ". Dropped its " + Signatures.Name(r.signature) + "." : "").Append('\n');
                if (World.news.Count > 0)
                {
                    sb.Append("\n<b>News</b>\n");
                    for (int i = World.news.Count - 1, n = 0; i >= 0 && n < 5; i--, n++)
                        sb.Append("Day ").Append(World.news[i].day).Append(": ").Append(World.news[i].text).Append('\n');
                }
            }

            string text = sb.ToString().TrimEnd();
            var content = new GUIContent(text);
            float w = JournalWidth, head = journalHead.lineHeight + 14f;
            float top = Screen.height * 0.16f;
            float h = Mathf.Min(Screen.height - top - 20f, journalStyle.CalcHeight(content, w - 28f) + head + 16f);
            var box = new Rect(20, top, w, h);
            GUI.DrawTexture(box, Backing);
            GUI.DrawTexture(box, Backing);
            GUI.Label(new Rect(box.x + 14, box.y + 8, w - 28, head), "WASTELAND LEGENDS", journalHead);
            GUI.Label(new Rect(box.x + 14, box.y + head, w - 28, h - head - 8), content, journalStyle);
        }

        private static string TraitName(LegendRecord r, string id)
        {
            var t = Trait.Get(id);
            if (t == null) return "?";
            return r.Knows(id) ? t.Name + " (" + t.Effect + ")" : "???";
        }

        // Under the name on the arrival card when a legend hasn't earned a title yet.
        private static string Subtitle(LegendRecord r)
        {
            if (!string.IsNullOrEmpty(r.title)) return r.title;
            string[] lines = { "a name on the wind", "known in these parts", "feared on every road", "infamous across the wasteland" };
            return lines[r.Band];
        }

        // "1.4 km north-east" from the player (+z is north on the map).
        private static string Where(Vector3 world)
        {
            var player = Game.Player();
            if (player == null) return "";
            Vector3 d = world - Game.ToWorld(player.position);
            d.y = 0f;
            float dist = d.magnitude;
            string[] dirs = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
            int i = Mathf.RoundToInt(Mathf.Repeat(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 360f) / 45f) % 8;
            return (dist >= 1000f ? (dist / 1000f).ToString("0.0") + " km " : dist.ToString("0") + " m ") + dirs[i];
        }

        // Labels over marks and trophies lying on the ground (in a bag they have a parent). A hoard at home was a wall of
        // text with every label up (user, 2026-10-29), so by default only the one under the crosshair, within 6 m, like
        // a pickup prompt; ItemLabels = Always shows all within 25 m (marks) / 20 m (trophies), Off none.
        internal List<KeyValuePair<Vector3, string>> ItemLabels(Camera cam)
        {
            var all = new List<KeyValuePair<Vector3, string>>();
            var ranges = new List<float>();
            if (Plugin.ItemLabels.Value == ItemLabelStyle.Off || cam == null) return all;
            foreach (var m in marks)
                if (m.Item != null && m.Item.transform.parent == null)
                {
                    all.Add(new KeyValuePair<Vector3, string>(m.Item.transform.position + Vector3.up * 0.3f, m.R.name + "'s mark - drop it in a trader's sell box: " + m.R.bounty + " nuts"));
                    ranges.Add(25f);
                }
            foreach (var t in trophies)
                if (t.Item != null && t.Item.transform.parent == null)
                {
                    all.Add(new KeyValuePair<Vector3, string>(t.Item.transform.position + Vector3.up * 0.35f, TrophyLabel(t.T)));
                    ranges.Add(20f);
                }

            var shown = new List<KeyValuePair<Vector3, string>>();
            bool lookAt = Plugin.ItemLabels.Value == ItemLabelStyle.LookAt;
            float best = float.MaxValue, aim = Screen.height * 0.06f;
            var centre = new Vector2(Screen.width / 2f, Screen.height / 2f);
            for (int i = 0; i < all.Count; i++)
            {
                Vector3 p = cam.WorldToScreenPoint(all[i].Key);
                if (p.z <= 0f || p.z > (lookAt ? 6f : ranges[i])) continue;
                if (!lookAt) { shown.Add(all[i]); continue; }
                float off = Vector2.Distance(new Vector2(p.x, p.y), centre);
                if (off < aim && off < best) { best = off; shown.Clear(); shown.Add(all[i]); }
            }
            return shown;
        }

        private void Label(Camera cam, Vector3 world, string text, float range)
        {
            Vector3 p = cam.WorldToScreenPoint(world);
            if (p.z <= 0f || p.z > range) return;
            Shadowed(new Rect(p.x - 300f, Screen.height - p.y - 12f, 600f, 24f), text, tagStyle, 1f);
        }

        private static void Shadowed(Rect r, string text, GUIStyle style, float alpha)
        {
            var color = style.normal.textColor;
            style.normal.textColor = new Color(0f, 0f, 0f, 0.8f * alpha);
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, style);
            style.normal.textColor = new Color(color.r, color.g, color.b, alpha);
            GUI.Label(r, text, style);
            style.normal.textColor = color;
        }

        private int stylesForHeight;

        private void MakeStyles()
        {
            stylesForHeight = Screen.height;
            float s = Scale;
            tagStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(17 * s), fontStyle = FontStyle.Bold, wordWrap = false };
            tagStyle.normal.textColor = new Color(1f, 0.78f, 0.3f);
            cardName = new GUIStyle(tagStyle) { fontSize = Mathf.RoundToInt(56 * s) };
            cardName.normal.textColor = new Color(0.95f, 0.35f, 0.2f);
            cardTitle = new GUIStyle(tagStyle) { fontSize = Mathf.RoundToInt(28 * s), fontStyle = FontStyle.Italic };
            cardTitle.normal.textColor = new Color(0.95f, 0.9f, 0.8f);
            sayStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(22 * s), wordWrap = true };
            sayStyle.normal.textColor = Color.white;
            noticeStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, fontSize = Mathf.RoundToInt(20 * s), wordWrap = true, richText = true };
            noticeStyle.normal.textColor = new Color(1f, 0.92f, 0.75f);
            promptStyle = new GUIStyle(sayStyle) { fontSize = Mathf.RoundToInt(21 * s), fontStyle = FontStyle.Bold };
            promptStyle.normal.textColor = new Color(1f, 0.78f, 0.3f);
            journalStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(17 * s), wordWrap = true, richText = true };
            journalStyle.normal.textColor = new Color(0.95f, 0.92f, 0.85f);
            journalHead = new GUIStyle(journalStyle) { fontSize = Mathf.RoundToInt(21 * s), fontStyle = FontStyle.Bold };
            journalHead.normal.textColor = new Color(0.95f, 0.55f, 0.25f);
        }
    }
}
