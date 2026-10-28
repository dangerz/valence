# AGENTS.md: contributing to Valence with an AI agent

This file is for AI coding agents (and the people driving them) who want to contribute to Valence.
Read all of it before changing anything. It covers what Valence is for, the rules that don't bend,
what to install and how to run it, how the code is laid out, how the existing work was done, and a step-by-step playbook for adding a
language (PHP is next; the playbook uses it as the worked example).

If anything here disagrees with what your maintainer tells you in chat, the maintainer wins.

---

## 1. What Valence is

AI agents now write a lot of code that people struggle to follow. Valence is a **read-only 3D map
of a codebase** that helps people get their bearings again. You point it at a folder you've already
cloned; it works out every type and how they depend on each other, and draws the result as
chemistry:

| On screen | In the code |
|---|---|
| Molecule | a project / top-level module |
| Atom | a system: a namespace (C#), a package or folder (Python) |
| Nucleon: proton (red) / neutron (blue) | a public / non-public type (class, interface, struct…) |
| Bond: ionic (one-way) / covalent (cycle) | dependencies between systems |
| Height | dependency layer: a system sits above what it depends on |

On top of the map: an outline, inspector, member-level schema, a **static simulation** (flow
downstream/upstream, the game loop, any trigger such as startup, requests, events), snapshots and
compare, a git timeline, usage filters, favorites and more. See `README.md` for the user's view.

**Languages are plugins ("probes").** C# (Roslyn) and Python (tree-sitter) are built in.
Roadmap order after that: PHP, Java, Ruby, Objective-C, Swift, Go, JavaScript/TypeScript, HTML
(`docs/ROADMAP.md`).

---

## 2. Rules that never bend

These are product guarantees. A change that breaks one will be rejected, however useful it is.

1. **Read-only.** Never write anything inside the analyzed repo: no build output, no restores, no
   caches, no lock files, no `__pycache__`. Parse files; never build, import or run them.
2. **Static only.** Valence never runs, records, instruments or traces the code it analyzes. The
   simulation is computed from source, not from execution.
3. **Never clone.** The user clones; Valence only gets pointed at a folder.
4. **No language runtimes for built-in probes.** A user analyzing PHP shouldn't need PHP installed.
   Built-in probes parse in-process (Roslyn for C#, tree-sitter for everything else).
5. **Output goes to `~/.valence/`** (graphs, snapshots, timelines) or Godot's `user://`. Never next
   to the analyzed code.
6. **The graph format is additive.** New optional fields are fine. Breaking changes bump the
   version suffix (`valence.graph/1` → `/2`) and need the maintainer's sign-off.
7. **Git is read-only too:** read-only commands with `GIT_OPTIONAL_LOCKS=0`; the timeline uses
   `git archive` into a temp folder.

---

## 3. Setup: what to install and how to run it

**Required**

| What | Why |
|---|---|
| **Godot 4.7.2, .NET edition** (the "Godot Engine - .NET" download, not the standard one) | The viewer is C#. `valence/Valence.csproj` pins `Godot.NET.Sdk/4.7.2`, so use exactly 4.7.2. |
| **.NET 8 SDK** (or newer that can target `net8.0`) | Every project targets `net8.0`; Godot uses the SDK to build the C#. |

**Optional**

| What | Why |
|---|---|
| **git** | Churn, the "changed recently" glow and the git timeline. Without it, analysis still works, just without history. |
| **Python 3** | Only to regenerate the app icon (`python3 tools/make_icon.py images/logo.png valence/images/icon.png 1024 --mark-only`) or the UI sheets (`tools/build_ui_sheets.py`, see its header). |
| An IDE with C# support (Rider, VS Code + C# Dev Kit, Visual Studio) | Opening `valence/Valence.sln` (viewer + everything it uses) or `Valence.Tools.sln` (CLI, core, probes). |

**Not needed:** Python, PHP or any other runtime for the languages Valence analyzes; Roslyn and
tree-sitter (with its native grammars for macOS, Windows and Linux) come in as NuGet packages
(`Microsoft.CodeAnalysis.CSharp`, `TreeSitter.DotNet`) and are restored automatically on the first
build. Nothing needs to be installed into the repos being analyzed.

**Run the viewer**

1. Open `valence/project.godot` in Godot 4.7.2 .NET. The first open imports assets and generates
   `.uid`/`.import` files; the first run builds the C# solution (the NuGet restore needs network).
2. Press Run (F5 / ⌘B). It opens the bundled demo graph (`valence/samples/demo.valence.json`).
3. **File ▸ Open Source Folder…** (⌘O) and pick a cloned repo. It's analyzed in the background
   with a progress dialog; the graph is saved to `~/.valence/graphs/<folder>-<hash>.valence.json` and
   reopens later via **File ▸ Open Graph…**.
4. Progress and summary lines are printed with a `[valence]` prefix in Godot's Output panel. That
   log is what the maintainer pastes back when testing a change.

If Godot reports it can't find .NET, install the .NET 8 SDK and restart Godot.

**Command line (optional)**

```bash
bash build.sh                                     # → dist/valence.dll (+ standalone dist/probes/csharp/)
dotnet dist/valence.dll analyze ~/code/SomeRepo   # → ~/.valence/graphs/SomeRepo-<hash>.valence.json
```

**Platforms:** developed and tested on macOS (Apple Silicon). The native tree-sitter libraries
also ship for Windows (x86/x64/arm64) and Linux (x64/arm64); those platforms haven't been tried
yet, so report what you find.

---

## 4. How to work here (process)

Valence was built by an agent working with a maintainer under these rules. Follow them unless your
maintainer says otherwise.

- **The maintainer builds, runs and tests. You write code.** Don't run `dotnet build`, Godot, the
  CLI or tests unless your maintainer explicitly asks you to. Write code carefully enough that it
  compiles first time (see section 8), then tell the maintainer exactly what to run and what they
  should see.
- **Commit and push only when asked.** Never on your own initiative.
- **Stage files by name.** No `git add -A` / `git add .`. Include the files Godot generates for new
  things: `*.cs.uid` for new scripts and `*.import` for new images. They appear only after the
  maintainer opens the project in Godot, so they often land in a follow-up commit.
- **Never rewrite history:** no amend, force-push, rebase, reset, clean, stash or branch deletion.
  `.claude/settings.json` denies most of these for Claude Code; follow the same rule with any tool.
- **Never delete files.** Move anything obsolete to `_to_delete/` and say so.
- **Prefer your file tools over shell chains.** Read, search and edit with dedicated tools; keep
  shell commands single and plain (no loops, no `VAR=value cmd` prefixes).
- **Read before you write.** Look at the surrounding code and match it. Check a file's current
  contents before editing it.
- **Report honestly.** If something is unverified (it always is until the maintainer builds it),
  say so. If the maintainer pastes a log, read it line by line and explain what it shows.
- **Ask before anything outward-facing** (publishing, posting, sending) or anything hard to undo.
- **License:** Valence is AGPL-3.0 with one additional term in `NOTICE`. Contributions are
  accepted under the same license. Never remove or alter the About dialog's credit ("By Roy El-Rayes
  and Claude") or its copyright/license notice, and don't add dependencies whose license isn't
  compatible with the AGPL (MIT, BSD, Apache-2.0 and LGPL are fine; ask about anything else).
- **Commit messages:** a short summary line, a blank line, then `-` bullets of what changed and
  why. Add your tool's attribution trailer if it uses one.

---

## 5. Repository map

```
README.md                user-facing overview, controls, read-only guarantees, license summary
AGENTS.md                this file
LICENSE                  GNU AGPL v3 (the official text; never edit it)
NOTICE                   copyright, and the AGPL section 7(b) term that keeps the About credit
build.sh                 optional: publishes the CLI and the standalone C# probe to dist/
Valence.Tools.sln        solution for the non-Godot projects (CLI, core, probes)
docs/plugin-protocol.md  THE contract: probe manifest, probe output, graph format, edge kinds
docs/ROADMAP.md          what's done, what's next, language order
images/logo.png          the original logo
tools/make_icon.py       rebuilds valence/images/icon.png from the logo (pure Python, --mark-only)
tools/build_ui_sheets.py rebuilds valence/images/ui/{icons,panels}.png from images/ui-source/
images/ui-source/        the original UI art: icon sheet and nine-patch panel sheet

src/Valence.Schema/      the JSON contract as C# classes (Model.cs) + serializer (ValenceJson.cs)
src/Valence.Core/        analysis engine used by viewer and CLI
  IProbe.cs              IProbe (built-in probes) + ExternalProbe (probe.json, separate process)
  RepoAnalyzer.cs        runs relevant probes, merges output, adds git, builds the graph
  GraphBuilder.cs        merge rules: dedupe nodes, aggregate edges, drop dangling, fan-in/out
  AnalysisContext.cs     logging, progress (Slice for sub-ranges), cancellation, throttling
  RepoFiles.cs           read-only file walking (skips hidden, build, symlinked folders)
  GitHistory.cs          read-only churn/commit metrics; Timeline.cs: git timeline builder
  ProbeRegistry.cs, ProbeRunner.cs, ProcessRunner.cs   external probe discovery and execution
src/Valence.Syntax/      tree-sitter engine: TreeSitterRuntime loads the native core + grammars
src/Valence.Cli/         optional `valence analyze <repo>` command line

probes/csharp/           C# probe (Roslyn): built in, also runnable standalone (probe.json)
probes/python/           Python probe (tree-sitter): built in. The reference for new languages:
  PythonProbe.cs         IProbe entry point
  PythonAnalyzer.cs      orchestrates pass 1 and pass 2, progress, summary log
  PythonModel.cs         what pass 1 records: Ref, Use, Scope, Binding, PyFile/PyClass/PyFunction…
  PythonProject.cs       shared state: ids, atoms/molecules, module naming + index, edge aggregation
  PythonExtractor.cs     pass 1: one walk per syntax tree (definitions, imports, uses, entry points)
  PythonResolver.cs      pass 2: name resolution → bonds, member links, wired entry points

valence/                 the Godot 4.7 (.NET, C#) viewer
  project.godot, Main.tscn, Valence.csproj, Valence.sln
  Scripts/Main.cs        orchestrator: loading, selection, visibility, colors, labels, simulation
  Scripts/Hud.cs         all 2D UI built in code: menu bar, panels, filter/timeline bars, dialogs
  Scripts/ValenceTheme.cs  the one UI theme (bordered panels, menus, buttons, inputs, tree)
  Scripts/ModalDialog.cs   in-app bordered dialogs (About, Controls); SnapshotsDialog.cs builds on it
  Scripts/UiAssets.cs    the UI sheets: Icon("name") (mipmapped atlas), Box(Patch.…) (nine-patches)
  Scripts/UiKit.cs       shared pieces: icon buttons, titles, search fields, badges, pills, cards
  Scripts/AxisGizmo.cs   the X/Y/Z gizmo in the 3D view's corner
  Scripts/*Panel.cs, ProgressOverlay.cs, PanelResizer.cs, RangeSlider.cs   UI pieces
  Scripts/GraphModel.cs  the loaded graph, indexed for the viewer
  Scripts/RiskScores.cs  the Risk lens: per-class score from churn, fan-in and size percentiles
  Scripts/Insights.cs    reach (Reach lens), trust (Trust lens) and the Insights menu's reports
  Scripts/Report.cs, ReportDialog.cs   a report model (BBCode + Markdown) and its window
  Scripts/FlowSimulator.cs, SimulationView.cs   static simulation
  Scripts/FlowChart.cs, FlowChartView.cs, FlowDrawer.cs   the simulation as a flow chart (layout,
                         drawing with live highlight, the drawer and SVG/PNG/Mermaid export)
  Scripts/Layout.cs, DependencyLayers.cs, AtomView.cs, NucleonRenderer.cs, BondRenderer.cs,
          DetailLines.cs, LabelLayer.cs, LabelProximity.cs, FocusView.cs, OrbitCamera.cs   3D view
  Scripts/Palette.cs     every color; Storage.cs: user:// persistence; Comparison.cs: compare +
                         SnapshotStore (save, list, rename via names.json, delete to trash)
  Scripts/AnalyzerLauncher.cs  runs the analysis in-process; registers the built-in probes
  Shaders/               atom cloud, bond flow, nucleon shaders
  images/logo.png (boot splash), images/icon.png (app icon), images/ui/ (icon + panel sheets)
  samples/demo.valence.json  the startup demo graph
```

---

## 6. Architecture

```
repo folder ──read-only──▶ probes ──ProbeOutput──▶ GraphBuilder ──Graph──▶ viewer
                           (C#, Python, …)        (merge, git metrics)    (~/.valence/graphs/*.json)
```

- **Probe output** (`valence.probe/1`): modules (a tree via `parent`), nodes (types, with members),
  edges (type-level), memberEdges (ordered, member-level), entryPoints, diagnostics. Full spec:
  `docs/plugin-protocol.md`. C# types: `src/Valence.Schema/Model.cs`.
- **Leaf modules are atoms, root modules are molecules.** Ids are global and language-prefixed
  (`cs:`, `py:`; use `php:` for PHP).
- **Edge kinds** (strongest first): `inherits`, `implements`, `contains`, `has`, `creates`,
  `calls`, `uses`, `references`. **Member edge kinds:** `calls`, `creates`, `reads`, `writes`,
  `raises`, `triggers`, with `order` = position of first occurrence among the source's edges.
- **Confidence:** omitted = certain; `likely` = through an inferred type, an approximate module
  match, virtual dispatch or framework convention; `guess` = name match only (avoid).
- **Entry point triggers:** `frame`, `physics`, `input`, `draw`, `startup`, `shutdown`, `event`,
  `request`, `job`.
- **Built-in probes** implement `IProbe` and run in-process with an `AnalysisContext`: report
  progress, honour cancellation, log a summary. They're registered in two places:
  `valence/Scripts/AnalyzerLauncher.cs` (`BuiltInProbes`) and `src/Valence.Cli/Program.cs`.
- **GraphBuilder drops** edges to unknown nodes, self-edges, and member edges/entry points whose
  members don't exist, so every member you link to must be listed on some node.

**Viewer state flow** (`Main.cs`): `ApplyVisibility` computes each node's opacity (hide/quiet ×
untouched-by-simulation × timeline presence × usage filter × only-differences while comparing). `RefreshSelection` drives
`ApplyLabels`, `ApplyColors` and `UpdateLines`. `NodeColor` branches: simulation → compare →
timeline → anchor/focus. All UI is built in code (no scenes beyond `Main.tscn`) and themed by
`ValenceTheme`. Settings live in `user://viewer.json`; layouts, visibility and favorites per repo
in `user://`.

---

## 7. The approach (how the existing work was done)

- **Small, complete steps.** Each change is a coherent unit the maintainer can build and judge.
  Big features go in phases (structure → bonds → flow → framework packs → docs), each one
  committed after the maintainer confirms it works.
- **Prove risky things first with a spike.** Native libraries, new runtimes, new APIs: write the
  smallest thing that proves it works on the maintainer's machine, with logging that makes success
  or failure obvious, before building on it. (Tree-sitter loading inside Godot was proven this way.)
- **Log a summary the maintainer can read.** Every probe ends with a few `language:` lines: counts
  (files, folders, types, members), how many imports/uses resolved inside vs outside the repo,
  bonds by kind, member links by kind, entry points by trigger, and timings. When something
  fails to load, log where you looked.
- **Read the API source before using a library.** If docs are thin, read the binding's source
  (that's how TreeSitter.DotNet's loading rules were found). Don't guess method names.
- **Match the surrounding code:** file-scoped namespaces, `<summary>` doc comments on types and
  non-obvious members, comments that say *why*, nullable enabled, small private helpers, `const`
  for tunables. Godot nodes are `partial` classes. Keep Godot APIs out of `src/` and `probes/`.
- **Keep the docs true.** When behaviour changes, update `README.md`, `docs/ROADMAP.md` and, for
  format or rule changes, `docs/plugin-protocol.md` in the same change.

---

**UI style** (match the existing panels: dark navy, thin blue nine-patch borders, cyan titles): build controls with `UiKit` and style them
with `UiAssets.Box(Patch.…)` nine-patches, not ad-hoc flat boxes; icons come from
`UiAssets.Icon("name")`. A new icon goes in `tools/build_ui_sheets.py` (drawn in the same light-cyan
stroke with a thin darker outline), appended to `ICON_NAMES` there and to `IconNames` in
`UiAssets.cs`, then the sheet is rebuilt.

## 8. Writing C# that compiles first time (pitfalls we've hit)

Code here is usually written without a compiler in the loop, so check these before handing over:

- **Name clashes with types.** A method named like a type in scope breaks `new Type(...)` (e.g. a
  method `Wiring` next to a record `Wiring`). A namespace named `Valence.TreeSitter` would shadow
  the bindings' `TreeSitter` namespace inside `Valence.*`; that's why the engine is
  `Valence.Syntax`.
- **Godot member clashes.** Don't name members `Show`, `Hide`, `VisibilityChanged`, `Name`… on
  classes deriving from Godot nodes; they collide with Godot's own.
- **Pattern/out variables leak** into the enclosing block (`if (x is not { } y) return;`). Use
  distinct names across a method, especially across `switch` sections.
- **No `yield` inside `try`/`catch`.** No local function with the same name as a variable.
- **Godot math:** `Vector3` has no static `Min/Max`; use component-wise math. Check named
  arguments against the real signature.
- **Tuple and conditional typing:** give `(null, false)` a target type (assign or return it).
- **Native libraries:** use `System.Runtime.InteropServices.NativeLibrary` fully qualified if a
  library brings its own `NativeLibrary`.
- **Wrapping labels in Godot need a width.** An autowrapping `Label` inside a popup with no width
  asks for one character per line (that's how the old About dialog went full screen). Give it
  `CustomMinimumSize.X`.
- **Solution files use CRLF** in places; edit them preserving line endings.

---

## 9. Adding a language (PHP as the worked example)

New languages are built-in, tree-sitter probes modelled on `probes/python/`. TreeSitter.DotNet
already ships the PHP grammar: `TreeSitterRuntime.Load("PHP")` loads
`runtimes/<rid>/native/libtree-sitter-php.*` with entry `tree_sitter_php`. No PHP install needed.

### The pattern: two passes

1. **Pass 1, extract** (one walk per syntax tree, tree freed immediately): create nucleons and
   members; record imports, bindings (parameters, locals, fields, globals, type hints) and every
   name use (`Use`: kind, name chain `Ref`, `Scope`, line) **in evaluation order**; record
   framework wiring by call/attribute/decorator shape.
2. **Pass 2, resolve** (across all files): evaluate each name chain the way the language looks
   names up, then emit a type-level bond (skipping self-bonds) and, inside a function, a member
   link with `order`. Memoize lookups with an in-progress sentinel so import cycles terminate;
   cap recursion depth. Anything that ends outside the repo bonds to nothing.

Copy `probes/python/` as the starting shape; don't contort Python's code to fit PHP.

### PHP phases

Each phase ends with the maintainer building, analyzing a real PHP repo, and pasting the log.
Commit after each phase only when asked.

**Phase 0: shared engine (one-time, also benefits every later language).** Move the
language-neutral pieces of `probes/python/` into `src/Valence.Syntax/` as you need them in both
languages: `Ref`/`Use`/`Scope`/`Binding`-style records, edge and member-edge aggregation with
per-source order (`PythonProject.AddEdge`/`AddMemberEdge`), id uniqueness, atom/molecule creation,
entry point helpers, the summary log. Start making framework rules data-driven (patterns in data
files rather than regexes in code). Extract only what both languages actually use, keep the
Python probe working and its output unchanged, and have the maintainer re-run a Python repo to
confirm.

**Phase 1: structure.** Register `PhpProbe` (extensions `.php`) in `AnalyzerLauncher.BuiltInProbes`
and the CLI; add the project to `valence/Valence.csproj`, `valence/Valence.sln`,
`src/Valence.Cli/Valence.Cli.csproj` and `Valence.Tools.sln`. Atoms: namespaces, mapped to
folders via `composer.json` `autoload.psr-4` (fall back to folders). Molecules: the composer
package or top-level namespace. Nucleons: classes, interfaces, traits, enums (`kind` accordingly);
files of plain functions become a `module` node, like Python. Members: methods (with
public/protected/private), properties (including constructor promotion), constants. Skip
`vendor/` and `node_modules/`. *Done when* atoms and nucleons appear with sensible counts.

**Phase 2: bonds.** `use` statements (with aliases and group uses), fully qualified and
namespace-relative names; `extends` → `inherits`, `implements` → `implements`, trait `use` →
`inherits` (or `has`, decide and document); typed properties → `has`; `new X` → `creates`; static
calls `X::m()` → `calls`; typed parameters/returns, `instanceof`, `catch`, `X::class` →
`references`; `$this->prop->m()` through typed properties or promoted constructor parameters →
`calls` (likely). `require`/`include` act as imports for procedural code. PHP names are explicit,
so most bonds should be certain. *Done when* the log shows most `use` imports resolving inside the
repo and atoms separate into layers.

**Phase 3: members and flow.** Member links in evaluation order: calls, `new` → `__construct`
(nearest in the class chain), property reads/writes, `parent::m()`, `self::`/`static::`; calls on
an interface or base type fan out to implementations/overrides (likely). Entry point: front
controllers (`index.php`, `public/index.php`) as `startup`, with their top-level code as a member.
*Done when* Simulate ▸ Play Trigger shows startup and plays through calls in order.

**Phase 4: framework packs.** Laravel: `Route::get('/x', [Controller::class, 'method'])` and
friends → `request`; artisan commands (`handle`) → `startup`; jobs (`handle`) → `job`;
events/listeners → `event`; middleware. Symfony: `#[Route]` attributes → `request`; console
commands; event subscribers. WordPress: `add_action`/`add_filter('hook', callback)` → `event`.
*Done when* each pack's entry points show up on a real app.

**Phase 5: wrap-up.** README (atoms row, the language paragraph, read-only notes), ROADMAP
(mark PHP done, note what's left), protocol doc if any rule changed.

### Checklist for any new language

- [ ] Probe registered in viewer and CLI; projects added to both solutions
- [ ] Never writes to the repo; skips vendor/dependency/build folders
- [ ] Ids prefixed with the language; all linked members exist on nodes
- [ ] Confidence follows the protocol (explicit names certain; inferred types `likely`)
- [ ] Progress via `AnalysisContext.Slice`, cancellation honoured, summary log lines
- [ ] Docs updated; maintainer has built it and pasted a log

---

## 10. Where things stand

- Done: viewer features listed in section 1, the C# probe, the tree-sitter engine, and Python
  (structure, bonds, member flow, entry points, Django/Flask/FastAPI/Celery/pygame packs). The
  Python framework and game rules haven't yet been exercised on a matching real repo; if you have
  one, a run and log are welcome.
- Next: PHP, starting with phase 0. Then the rest of the roadmap order.
