<p align="center">
  <a href="https://github.com/slobodaapl/GatherBuddyAscended">
    <img src="images/gba.png" alt="GatherBuddy Ascended" width="240">
  </a>
</p>

<h1 align="center">GatherBuddy Ascended</h1>

[![Latest release](https://img.shields.io/github/v/release/slobodaapl/GatherBuddyAscended?style=for-the-badge)](https://github.com/slobodaapl/GatherBuddyAscended/releases/latest)
![GitHub downloads](https://img.shields.io/github/downloads/slobodaapl/GatherBuddyAscended/total?style=for-the-badge)
[![License](https://img.shields.io/github/license/slobodaapl/GatherBuddyAscended?style=for-the-badge)](LICENSE)

GatherBuddy Ascended is a Dalamud plugin for Final Fantasy XIV gathering and crafting automation.
It continues from [GatherBuddy Reborn](https://github.com/FFXIV-CombatReborn/GatherBuddyReborn),
preserves its established gathering workflows, and provides a home for deeper crafting integration,
quality-of-life work, and future features beyond the Reborn baseline.

## Current features

### Gathering

- Automated BTN/MIN gathering and navigation.
- Resource lists and queued gathering goals.
- Timed-node, weather, fishing, and spearfishing tools.
- Vendor, marketboard, retainer, and material-source support used by crafting workflows.

[vnavmesh](https://github.com/awgil/ffxiv_navmesh) is required for automated navigation.

### Crafting

- Crafting lists, material planning, consumable handling, repairs, and queue execution.
- **Donatello** is the default crafting solver. Standard Solver, Raphael, Progress Only, and user
  macros remain available.
- Donatello combines Raphael's global optimization with live-state replanning. It reacts to expert
  craft conditions, current CP/durability/progress/quality, active effects, combos, and specialist
  charges, then optimizes the complete remaining craft.

### Donatello benchmark

Each craft is simulated on the same plugin path the game uses. Raphael produces an initial
plan and plays it through to the end. Donatello starts from that same plan and searches from
the live state when the craft's condition leaves Normal, keeping a replacement only when it
is strictly better (higher quality, then fewer steps, then shorter duration).

The corpus is ten random regular recipes in each level band from 1–10 through 91–100, using
crafter stats from that band, plus ten dedicated level-100 regular crafts and ten expert
crafts in each of the 80–90, 91–99, and level-100 bands. Both solvers see the same action
and condition rolls.

![Donatello improvement rate by recipe level](images/donatello-effectiveness.png)

| Level | Better | Tied | Win rate |
| --- | ---: | ---: | ---: |
| 1–10 | 5 | 5 | 50% |
| 11–20 | 6 | 4 | 60% |
| 21–30 | 3 | 7 | 30% |
| 31–40 | 5 | 5 | 50% |
| 41–50 | 4 | 6 | 40% |
| 51–60 | 4 | 6 | 40% |
| 61–70 | 8 | 2 | 80% |
| 71–80 | 6 | 4 | 60% |
| 81–90 | 7 | 3 | 70% |
| 91–100 | 8 | 2 | 80% |
| 100 | 10 | 0 | 100% |
| Expert 80–90 | 10 | 0 | 100% |
| Expert 90–99 | 10 | 0 | 100% |
| Expert 100 | 10 | 0 | 100% |

Regular recipes improve more often at higher levels. Expert crafts are the diamonds on the
graph.

## Direction

GatherBuddy Ascended continues beyond GatherBuddy Reborn with Donatello and ongoing quality-of-life improvements.

## Installing

Add this URL under `/xlsettings` → **Experimental** → **Custom Plugin Repositories**:

```text
https://slobodaapl.github.io/GatherBuddyAscended/pluginmaster.json
```

GatherBuddy Ascended will then appear in the Dalamud Plugin Installer. Install and configure
[vnavmesh](https://github.com/awgil/ffxiv_navmesh) for automated navigation.

## Building

Clone recursively so all pinned dependencies, including Donatello, are present:

```text
git clone --recurse-submodules git@github.com:slobodaapl/GatherBuddyAscended.git
```

Release CI builds the .NET plugin plus the pinned native `donatello_ffi.dll`, then packages
them as `GatherBuddyAscended.zip`.

## Contributing

1. Fork the repository and clone it recursively.
2. Keep changes scoped and preserve unrelated behavior.
3. Build and test affected .NET and Rust components.
4. Open a pull request against `main` with behavior, evidence, and known limitations described.

## Attribution and acknowledgements

GatherBuddy Ascended builds on substantial prior work. Attribution does not imply endorsement:

- Contributors to Dalamud and FFXIVLauncher.
- [Artisan](https://github.com/PunishXIV/Artisan): Taurenkey, pksage, Limiana, and contributors.
- [GatherBuddy](https://github.com/Ottermandias/GatherBuddy): Ottermandias and contributors.
- [GatherBuddy Reborn](https://github.com/FFXIV-CombatReborn/GatherBuddyReborn): the Combat Reborn team and contributors.
- [Raphael XIV](https://github.com/KonaeAkira/raphael-rs): KonaeAkira and contributors; foundation of the pinned Donatello solver fork.
- [vnavmesh](https://github.com/awgil/ffxiv_navmesh): awgil, xanderscore, and contributors.
