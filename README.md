# Wasteland Legends

**Beta 0.9.0** for Apocalypter (BepInEx 5)

**Download:** [Nexus Mods](https://www.nexusmods.com/apocalypter/mods/96) · [GitHub releases](https://github.com/JadedAutist/Wasteland-Legends/releases) · or in-game with Apocasetter (F6, then Mods)

Some Scrapyard raiders grow into named **legends**. A legend has a home camp and its own strengths and weak spots, and the wasteland talks about what it does. It also remembers you. Kill one and you get its bounty, a trophy weapon and the stash at its camp. It may still come back, scarred and angrier.

## What happens in your game

- **Legends rise.** After the first couple of in-game days, a raider at one of the Scrapyard camps becomes a legend, and more follow until there are four (you can change the number). Each one has a name, a home camp, one strength and one weakness.
- **They make their own name.** Off-screen, legends burn trader buses, sack settlements, run cars off the road and butcher scavengers. That earns them fame, and fame brings titles, more health, extra strengths, better weapons and bigger bounties. The fame levels are **Rumor**, **Known**, **Feared** and **Infamous**.
- **News travels.** Coyote traders pass on the news and, for a price, a rumor about a legend: where its camp is, or what it's good or bad at.
- **They remember you.** A legend taunts you on sight. It remembers whether you ran, what you hit it with, and whether you killed it before. A grudge builds up against you.
- **They come for you.** A legend with a big grudge hunts you down with two raiders. With Wild Wasteland 2, a legend can also ride with a raider patrol on the road.
- **They come back.** A killed legend can return 2 to 5 days later with a scar and a new title, but the same strengths and weaknesses. It's less likely if you took its mark, and never happens after a third death.

## Fighting a legend

- A big title card announces it (you can make it a single line or turn it off). Its name tag shows its health.
- Its **strengths** make it tougher: **Ironhead** (head shots do half damage), **Thick Hide** (much more health), **Bruiser** (hits very hard up close).
- Its **weaknesses** are what you exploit: **Glass Jaw** (head shots hurt more), **Soft Belly** (body shots hurt more), **Loudmouth** (you hear it coming).
- You learn its traits by fighting it or by buying rumors. Everything you know is in the **journal (L)**.

## Rewards

- **The mark.** Every legend drops one. Sell it at a Coyote trader's sell box for the bounty, from 100 nuts up to 600 for the most famous.
- **A trophy weapon.** Its signature weapon, named after it ("Scrapjaw's AK"), with a damage bonus while you hold it. Melee trophies get +25 % to +60 %, guns +10 % to +25 %. Each trophy also has one random perk, or two from an Infamous legend:
  - **Bleeding**: each hit makes the target bleed for 4 seconds (4 damage a second). Hits stack up to 3 times, for 12 a second
  - **Quick**: faster attacks
  - **Skullsplitter**: head hits do 50 % more
  - **Bloodthirst**: each kill heals you
- **The stash.** With the legend dead, its camp's stash is unguarded: armor plates every time, plus ammo, medicine, repair kits, armor or car parts. The more famous the legend, the bigger the stash.

## Settings

Everything can be changed in Apocasetter (F6, then Mods), or in `BepInEx\config\apocalypter.wastelandlegends.cfg`.

| Group | Setting | Default | What it does |
|---|---|---|---|
| General | Enabled | on | Off puts all legends away. Their story stays in your save and goes on when you switch it back on. |
| | LivingLegends | 4 | How many can be alive at once (1–8) |
| | GraceDays | 2 | No legends before this in-game day |
| | DeedChancePerHour | 0.08 | How busy legends are off-screen |
| | Arrival | Card | Big title card, a line of text, or nothing |
| | ItemLabels | LookAt | Names over marks and trophies on the ground: only the one you look at, all nearby, or none |
| Fights | HealthMultiplier | 3 | Legend health compared with a normal raider |
| | DamageMultiplier | 1.3 | Legend damage compared with a normal raider |
| | SpawnRange / DespawnRange | 150 / 400 m | When a legend shows up at its camp, and when it leaves again |
| | MarkItem | old_knife | The item it drops as its mark |
| Comebacks | LegendsReturn | on | Killed legends can come back |
| | ReturnChanceWithMark / WithoutMark | 0.2 / 0.5 | Chance of a comeback if you took its mark / left it |
| Roads and hunts | RoadAmbushes | on | Legends ride with raider patrols (needs Wild Wasteland 2) |
| | RoadChance | 0.5 | How often a raider patrol has a legend with it |
| | Hunts | on | Legends with a grudge come looking for you |
| | CarTheft | **off** | A legend may steal your car if you leave it far away for a long time |
| Keys | JournalKey | L | The Legends journal |
| | RumorKey | T | Buy a rumor at a trader, if you don't have Wild Wasteland 2 |

## With other mods

- **Wild Wasteland 2** (optional, recommended): adds road ambushes, map and compass markers for known camps, and lets you ask traders about legends and news in its talk window. Without it, legends still live at their camps, and you buy rumors next to a trader with T.
- **Apocalypter Weapons Pack** (optional): when a future version of the pack adds the right hook, legends can carry its weapons as trophies, and a **Searing** perk sets enemies on fire like the flamethrower.
- It doesn't replace or change any game files. Legends are ordinary raiders made stronger, so other AI mods keep working on them.

## Install

1. You need BepInEx 5 for Apocalypter (most Apocalypter mods do). Apocasetter (F6, then Mods) can install this mod for you.
2. To install by hand, unpack the zip into the Apocalypter game folder (the one with `Apocalypter.exe`). You should end up with `BepInEx\plugins\WastelandLegends\WastelandLegends.dll`.
3. Start a new game or load a save. Legends start rising after the first couple of in-game days of that save.

**Uninstall:** delete the `WastelandLegends` folder. Your save keeps a small Wasteland Legends entry that the game ignores. Reinstall and your legends are back.

## Beta notes

This is the first public version, so the numbers may still change. If something breaks, please send `BepInEx\LogOutput.log`. Every Wasteland Legends line in it starts with `Wasteland Legends`.

**Ideas wanted.** This beta is here to hear from you: new traits, deeds, rewards, taunts, balance. Post them on the Nexus page or open a GitHub issue. Tell me what felt too easy, too hard or too much.

Made by JadedAutist.
