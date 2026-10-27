# Valence

**A read-only 3D map of a codebase.** When AI agents write most of the code, it gets hard to keep a
mental model of what's in the repo and how it fits together. Valence reads a repository, never
writing to it, works out every type and how they depend on each other, and draws the result as
chemistry:

| On screen | In the code |
|-----------|-------------|
| **Molecule** | a project (top-level module) |
| **Atom** | a system: a namespace (C#), a package or folder (Python) |
| **Proton** (red nucleon) | a public class/interface/struct (Python: a class, or a file of free functions) |
| **Neutron** (blue nucleon) | an internal/private type (Python: `_name` by convention) |
| Nucleon size | lines of code |
| **Electrons** | one per system this atom bonds with (its *valence*) |
| **Ionic bond** (cyan) | one-way dependency; pulses flow from dependent → dependency |
| **Covalent bond** (amber) | two systems depend on each other: a cycle |
| Bond thickness | number of class-level dependencies |
| **Height** | dependency layer: a system always sits above what it depends on |
| ✦ Glow | changed in the 14 days before the snapshot (git) |

Click an atom to **open** it: its classes spread out with labels, stacked so each class sits above
the classes it uses, and **ports** on the rim ("→ Shop.Data ×12") lead to the systems it's bonded
with. Hover a class to see its direct links; click it to select it and light up everything it
touches (View ▸ Link Depth for 2–3 hops). Or skip the 3D hunting and pick anything from the
**Outline** on the left. Shift-drag atoms to arrange things your way. Moves are saved per repo, and
height stays tied to the dependency layer so the picture keeps its meaning.

Labels stay out of the way: class and system names appear as you zoom in. Select a class (click it
or find it with ⌘F) and only it and everything it connects to are labeled, wherever they are.

**Favorites.** Right-click anything in the Outline ▸ ★ Add to Favorites; favorites are listed at
the top of the Outline and saved per repo. Lists throughout are alphabetical.

**Hide and quiet.** Every row in the Outline has a visibility button on its left (● shown,
◐ quiet, ○ hidden; click to cycle, right-click for options). Hiding a project, system or class
removes it, everything inside it, and every bond, link, port, label and pulse attached to it.
Quiet still draws it, faded; pick how faded with View ▸ Quiet Fade. Choices are saved per repo;
View ▸ Show Everything resets them.

### Simulate

The analysis records methods, properties, fields and events, the calls and reads/writes between
them in source order, event and signal wiring, and entry points (Godot `_Process`, `_Ready`,
`Main`, event handlers). The **Simulate** menu plays that statically. Nothing is run:

* **Methods panel** (right, above the Inspector): the selected class's methods, or every entry
  point when nothing is selected. Press **▶** next to a method (e.g. `World._Process`) to simulate
  the flow from exactly that method. Its search box searches every method in the repo (Enter plays
  the first match; click a name to select its class).
* Each class turns **gold** for two seconds when the flow reaches it, then returns to normal.
* **D / U**: flow *downstream* from the selected class (what it reaches) or *upstream* into it
  (what reaches it). Pulses travel the links hop by hop, in the order the calls are written.
* While a simulation runs, all labels hide; a class is labeled the moment the flow reaches it.
* **Simulate ▸ Untouched** (or **H** to cycle): *Show* everything, *Ghost* what the flow hasn't
  reached (uses the Quiet Fade level), or *Hide* it so only the flow's path is drawn, appearing as
  pulses arrive.

Side panels can be resized by dragging their borders; widths are remembered.
**View ▸ Brightness** turns the atom clouds, bonds, electrons and bloom up or down.
**View ▸ Filter by Usage** adds a range slider over each class's used-by count (how many other
classes depend on it): drag the two handles to show only classes in that range, e.g. 0–0 for
classes nothing uses, or the top of the range for the most depended-on ones. Others are hidden or
ghosted; systems with nothing in range disappear.
**View ▸ Labels** (or **L**): *All* shows every label, *Near Mouse* (default) only around the
pointer, *On Click* only the selected class and opened system. A running simulation overrides
this: only classes the flow has reached are labeled.

### History

* **Timeline ▸ Build Timeline (10 / 25 / 50 commits)** reads the folder's local git history,
  exports each sampled commit with `git archive` into a temp folder (nothing in the repo or its
  working copy is touched), and analyzes it. Then drag the timeline slider, or press ▶, to watch
  the code grow: classes appear as they're added, new ones glow green. Classes that were later
  deleted can't be drawn (they aren't in today's layout) and are counted instead. Saved in
  `~/.valence/timelines/`; Timeline ▸ Show Timeline reopens it.
* **File ▸ Take Snapshot** saves the current graph to `~/.valence/snapshots/`. After the code
  changes, File ▸ Re-analyze and **View ▸ Compare With Snapshot**: added classes turn green, changed
  ones amber, the rest dim, and the inspector lists added, changed and removed classes.
  **View ▸ Only Show Differences** (enabled while comparing) hides everything that didn't change.
  **File ▸ Snapshots…** lists a repo's snapshots to compare, rename or delete (moved to the trash).
* **G**: play the **game loop**: every `_Process` / `_PhysicsProcess` / `_Draw`, tick after tick.
* **Simulate ▸ Play Trigger**: startup, input, event handlers, web requests…
* **Flow chart**: a drawer under the 3D view shows the simulation as a flow chart (one box per
  method, or per class for big flows), left to right by hop, in the order things happen. Boxes light
  up gold as the flow reaches them; click one to select its class; scroll to zoom, drag to pan.
  **Export** saves it as SVG, PNG or Mermaid, or copies the Mermaid for a markdown file.
  Simulate ▸ Flow Chart turns the drawer on or off; drag the gap above it to resize it.
* Space pauses/resumes, Simulate ▸ Speed changes pace, Esc stops. Classes heat up as the flow
  passes through them; the inspector lists the busiest. Calls through interfaces/virtuals and
  event wiring show as smaller "likely" pulses.

The demo graph includes four sample web requests, so **Play Trigger ▸ request** works right away.
Roadmap: [docs/ROADMAP.md](docs/ROADMAP.md). Contributing with an AI agent (e.g. adding a language):
[AGENTS.md](AGENTS.md).

## Layout

```
src/Valence.Schema/   shared JSON contract (C# model of the probe + graph formats)
src/Valence.Core/     analysis engine: runs probes, merges, adds git churn (used by viewer + CLI)
src/Valence.Cli/      `valence` command-line front end (optional)
src/Valence.Syntax/   tree-sitter engine (bindings + bundled native grammars) for in-process probes
probes/csharp/        C# probe (Roslyn): built in, and also runnable standalone
probes/python/        Python probe (tree-sitter): built in
tools/make_icon.py    rebuilds the app icon from the logo
tools/build_ui_sheets.py  rebuilds the UI icon and panel sheets from images/ui-source/
valence/              Godot 4.7 (.NET) viewer
docs/plugin-protocol.md   how to write a probe for another language
AGENTS.md             guide for AI agents contributing (rules, structure, adding a language)
```

Languages are plugins ("probes"). C# (Roslyn) and Python (tree-sitter) are built in and run
in-process, with no language runtimes needed. Any other language can be a separate program that
speaks a small JSON protocol, written in whatever language parses its target best. See
[docs/plugin-protocol.md](docs/plugin-protocol.md).

**Python** is resolved statically, the way Python looks names up: imports (relative, aliased,
`__init__.py` re-exports), `self` fields, type hints and constructor calls. Bonds through an
inferred type are marked "likely". Simulation entry points: `if __name__ == "__main__":`, routes
(Flask, FastAPI, Django `urls.py`, DRF routers), signals (`@receiver`, `.connect(handler)`), Celery
tasks, click/Typer commands, Django management commands, and game loops (pygame loops, arcade/pyglet
windows, sprites, Pygame Zero).

## Quick start

Requirements: **Godot 4.7.2, .NET edition** (not the standard build; the project pins
`Godot.NET.Sdk/4.7.2`) and the **.NET 8 SDK**. Optional: git (history, glow, timeline). No runtimes
are needed for the languages being analyzed; Roslyn and tree-sitter come in as NuGet packages on the
first build. More detail for contributors in [AGENTS.md](AGENTS.md#3-setup-what-to-install-and-how-to-run-it).

Open `valence/project.godot` in Godot and run it. It starts with a small demo graph. Click
**Open source folder…** and pick a repo: it's analyzed in the background (read-only) and the
result opens when done. The graph is also saved to `~/.valence/graphs/<folder>.valence.json`, which
**Open graph…** can reopen instantly later.

### Command line (optional)

```bash
bash build.sh                                   # → dist/valence.dll (+ standalone dist/probes/csharp/)
dotnet dist/valence.dll analyze ~/code/SomeRepo # → ~/.valence/graphs/SomeRepo.valence.json
```

CLI options:

```
valence analyze <repo> [--out <file>] [--probes <dir>]... [--churn-days <n>] [--no-git] [--timeout <min>]
valence probes  [--probes <dir>]...         # list discovered probes
```

The viewer also accepts `-- --graph <file>` on the Godot command line.

## Read-only guarantees

* The Python probe only parses files (tree-sitter); it never imports or runs them, so no
  `__pycache__` is written.
* The C# probe never runs MSBuild/`dotnet restore` (which would write `obj/`). It parses `.csproj`
  files as XML and builds Roslyn compilations in memory. NuGet/Godot assemblies are read from the
  local NuGet cache if present, so external base types (e.g. `Node3D`) resolve.
* Git is queried with read-only commands (`rev-parse`, `log`) and `GIT_OPTIONAL_LOCKS=0`, so it
  doesn't refresh the index.
* Output goes to `~/.valence/graphs/` unless you pass `--out`. Viewer layouts go to Godot's
  `user://layouts/`.

## Viewer controls

The toolbar above the 3D view switches lenses (**Structure**: public vs internal, **Changes**: git
churn, **Dependencies**: coupling; the same as View ▸ Color By), opens **Filters** and **Fit view**.
With a class or system selected, **Hide Unrelated** appears there too: everything the selection
doesn't link to (within View ▸ Link Depth hops) is hidden until you turn it off.
The buttons in the view's corner pick what a left-drag does: **select** (click selects, drag orbits),
**orbit** (clicks don't select), **pan** or **zoom**; ▾ has zoom steps, fit and reset layout.

| Input | Action |
|-------|--------|
| Drag / two-finger swipe | orbit |
| Right-drag / Shift+swipe | pan |
| Wheel / pinch | zoom |
| Click atom | open the system (labeled classes, rim ports, inspector) |
| Hover a class | tooltip + its direct links |
| Click a class | select it; links light up to View ▸ Link Depth hops |
| Click a port | jump to that system |
| Shift-drag atom | move it (saved per repo) |
| Double-click empty space, **F** | frame everything |
| **F11** / **⌃⌘F** | full screen on/off (View ▸ Full Screen; remembered) |
| **Esc** | back out one level |
| **⌘F** / Outline filter | find a class or system by name |
| **⌘O** / **⌘⇧O** / **⌘R** | open source folder / open graph / re-analyze |

## License

Copyright (C) 2026 Roy El-Rayes. Valence is free software under the
[GNU Affero General Public License v3](LICENSE), with one additional term (section 7(b), in
[NOTICE](NOTICE)): every copy and modified version must keep the About dialog's attribution,
"By Roy El-Rayes and Claude".

In short: you may use Valence for anything (including at work), study it, change it and share it.
If you share a modified version, or run one as a service others use over a network, you must
publish its source under the same license and keep the attribution. It comes with no warranty.
Contributions are accepted under the same license.
