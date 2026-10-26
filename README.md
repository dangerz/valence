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

Click an atom to focus it and see its class-level links. Click a nucleon to inspect one class and
everything it touches. Shift-drag atoms to arrange things your way. Moves are saved per repo,
and height stays tied to the dependency layer so the picture keeps its meaning.

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
| Click atom | focus system (info panel + class links) |
| Click nucleon (in focused atom) | inspect class |
| Shift-drag atom | move it (saved per repo) |
| Double-click empty space, **F** | frame everything |
| **Esc** | back out one level |
| Search box + Enter | find class/system (Enter again = next match) |
