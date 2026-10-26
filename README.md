# Valence

**A read-only 3D map of a codebase.** When AI agents write most of the code, it gets hard to keep a
mental model of what's in the repo and how it fits together. Valence reads a repository, never
writing to it, works out every type and how they depend on each other, and draws the result as
chemistry:

| On screen | In the code |
|-----------|-------------|
| **Molecule** | a project (top-level module) |
| **Atom** | a system: a namespace (leaf module) |
| **Proton** (red nucleon) | a public class/interface/struct |
| **Neutron** (blue nucleon) | an internal/private type |
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
  the flow from exactly that method.
* **D / U**: flow *downstream* from the selected class (what it reaches) or *upstream* into it
  (what reaches it). Pulses travel the links hop by hop, in the order the calls are written.
* While a simulation runs, all labels hide; a class is labeled the moment the flow reaches it.
* **Simulate ▸ Untouched** (or **H** to cycle): *Show* everything, *Ghost* what the flow hasn't
  reached (uses the Quiet Fade level), or *Hide* it so only the flow's path is drawn, appearing as
  pulses arrive.

Side panels can be resized by dragging their borders; widths are remembered.
* **G**: play the **game loop**: every `_Process` / `_PhysicsProcess` / `_Draw`, tick after tick.
* **Simulate ▸ Play Trigger**: startup, input, event handlers, web requests…
* Space pauses/resumes, Simulate ▸ Speed changes pace, Esc stops. Classes heat up as the flow
  passes through them; the inspector lists the busiest. Calls through interfaces/virtuals and
  event wiring show as smaller "likely" pulses.

The demo graph includes four sample web requests, so **Play Trigger ▸ request** works right away.
Roadmap: [docs/ROADMAP.md](docs/ROADMAP.md).

## Layout

```
src/Valence.Schema/   shared JSON contract (C# model of the probe + graph formats)
src/Valence.Core/     analysis engine: runs probes, merges, adds git churn (used by viewer + CLI)
src/Valence.Cli/      `valence` command-line front end (optional)
probes/csharp/        C# probe (Roslyn): built in, and also runnable standalone
valence/              Godot 4.7 (.NET) viewer
docs/plugin-protocol.md   how to write a probe for another language
```

Languages are plugins ("probes"). C# is built in and runs in-process. Any other language is a
separate program that speaks a small JSON protocol, so each can be written in whatever language
parses its target best. See [docs/plugin-protocol.md](docs/plugin-protocol.md).

## Quick start

Requirements: .NET 8 SDK, Godot 4.7 (.NET build).

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

* The C# probe never runs MSBuild/`dotnet restore` (which would write `obj/`). It parses `.csproj`
  files as XML and builds Roslyn compilations in memory. NuGet/Godot assemblies are read from the
  local NuGet cache if present, so external base types (e.g. `Node3D`) resolve.
* Git is queried with read-only commands (`rev-parse`, `log`) and `GIT_OPTIONAL_LOCKS=0`, so it
  doesn't refresh the index.
* Output goes to `~/.valence/graphs/` unless you pass `--out`. Viewer layouts go to Godot's
  `user://layouts/`.

## Viewer controls

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
| **Esc** | back out one level |
| **⌘F** / Outline filter | find a class or system by name |
| **⌘O** / **⌘⇧O** / **⌘R** | open source folder / open graph / re-analyze |
