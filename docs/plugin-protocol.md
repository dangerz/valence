# Writing a Valence probe (language plugin)

A **probe** teaches Valence a language. It is a standalone program, written in whatever language
parses that language best, that reads a repository and writes a JSON description of its types and
their dependencies. The core (`Valence.Core`, used by both the viewer and the `valence` CLI) runs
probes as separate processes, merges their output, adds git history and metrics, and produces the
graph the viewer draws.

```
repo ──read-only──▶ probe(s) ──probe output──▶ Valence.Core ──graph.json──▶ Godot viewer
```

**Built-in probes.** C# and Python ship built in: `Valence.Core.IProbe` is implemented in-process by
`CSharpProbe` and `PythonProbe` (tree-sitter, via `src/Valence.Syntax`), so the viewer can analyze a
folder without any separately built tools or language runtimes. The C# analyzer is still a normal
probe too (`probes/csharp`, `probe.json`), and its output is identical either way. When an external probe has the same `name` as a built-in one, the built-in wins.

## 1. The manifest: `probe.json`

Every probe directory contains a `probe.json`:

```json
{
  "schema": "valence.probe-manifest/1",
  "name": "csharp",
  "displayName": "C# (Roslyn)",
  "version": "0.1.0",
  "languages": ["csharp"],
  "extensions": [".cs"],
  "command": ["dotnet", "{dir}/Valence.Probe.CSharp.dll"]
}
```

| Field        | Meaning |
|--------------|---------|
| `name`       | Unique id. Earlier search paths win, so `--probes` can override a bundled probe. |
| `extensions` | The probe only runs if the repo contains at least one file with one of these. |
| `command`    | Executable plus leading arguments. `{dir}` is replaced with the probe directory. |

The core finds probes in, in order: each `--probes <dir>`, `$VALENCE_PROBES` (path-separated), and
`probes/` next to the running program. The viewer also checks `dist/probes/` in this repo. A search
path may be a probe directory or a folder of probe directories.

## 2. Invocation

```
<command...> --root <absolute repo path> --out <file>
```

* Working directory: the probe directory.
* Write the result to `--out`. Log progress to **stderr** (the core forwards it).
* Exit `0` on success, non-zero on failure. The default timeout is 10 minutes (`--timeout`).

## 3. Rules

1. **Read-only.** Never write inside `--root`: no build outputs, no restores, no caches, no lock
   files. (The C# probe builds Roslyn compilations in memory instead of running MSBuild for exactly
   this reason.)
2. **Ids are global.** Prefix node and module ids with the language, e.g. `cs:`, `py:`, `ts:`.
3. **Edges point at repo code only.** If a dependency target isn't declared in the repo, don't emit
   an edge; record notable external facts as node `attributes` instead (e.g. `externalBase`).
4. **Paths** are relative to `--root`, with forward slashes.
5. Prefer emitting **one edge per (source, target, kind)** with a `weight` = number of occurrences.

## 4. Output: `valence.probe/1`

```json
{
  "schema": "valence.probe/1",
  "probe": { "name": "csharp", "version": "0.1.0" },
  "modules": [
    { "id": "cs:project:Shop/Shop.csproj", "name": "Shop", "kind": "project", "language": "csharp", "path": "Shop" },
    { "id": "cs:project:Shop/Shop.csproj#Shop.Domain", "name": "Shop.Domain", "kind": "namespace",
      "parent": "cs:project:Shop/Shop.csproj", "language": "csharp" }
  ],
  "nodes": [
    { "id": "cs:Shop.Domain.Order", "name": "Order", "fullName": "Shop.Domain.Order", "kind": "class",
      "module": "cs:project:Shop/Shop.csproj#Shop.Domain", "visibility": "public", "language": "csharp",
      "file": "Shop/Domain/Order.cs", "line": 4, "files": ["Shop/Domain/Order.cs"],
      "metrics": { "loc": 190, "methods": 8, "fields": 4, "properties": 6 },
      "attributes": { "externalBase": "Some.Library.Entity" } }
  ],
  "edges": [
    { "source": "cs:Shop.Domain.Order", "target": "cs:Shop.Domain.Money", "kind": "references", "weight": 4 }
  ],
  "diagnostics": [
    { "level": "warning", "message": "could not read Foo.cs", "source": "csharp" }
  ]
}
```

### Modules

Modules form a tree through `parent`. The viewer draws:

* **leaf modules** (the ones nodes belong to) as **atoms**,
* **root modules** (no parent) as **molecules**, the clusters atoms gather into.

For C# that's project → namespace. For other languages pick what people think of as a "system":
package → module, workspace → crate, folder → subfolder.

Well-known `kind`s: `project`, `namespace`, `folder`. Others are allowed.

### Nodes

Type-level units: `class`, `struct`, `interface`, `enum`, `record`, `record-struct`, `delegate`
(others allowed). `visibility` is `public` for anything visible outside its module. Those render as
protons, everything else as neutrons.

`metrics` is a free-form `string → number` map. The viewer uses `loc` for size. The core adds
`fanIn`, `fanOut`, `commits`, `churn`, `lastChanged` (unix seconds), and from git authorship over
the churn window `authors`, `topAuthorShare` and `agentShare` (share of changed lines from commits an
AI agent or bot authored or co-authored), with the `topAuthor` attribute.

Well-known `attributes`: `abstract` (`"true"` for abstract classes, ABCs and protocols; interfaces
are already `kind: interface`), `externalImports` (comma-separated modules outside the repo that
the type's file imports), `externalBase` / `externalInterfaces` (C#).

### Edge kinds

`source` depends on `target`. Use the most specific kind that applies:

| Kind         | Meaning |
|--------------|---------|
| `inherits`   | base class |
| `implements` | interface implementation |
| `contains`   | `source` lexically contains nested type `target` (not a dependency) |
| `has`        | field / property of type `target` (composition) |
| `creates`    | instantiates `target` |
| `calls`      | invokes a method on `target` |
| `uses`       | reads/writes a field, property or event of `target` |
| `references` | any other mention of `target` (parameters, locals, generics, casts, `typeof`) |

Edges may carry `"confidence"`: omitted means **certain** (resolved by a compiler/type checker),
`"likely"` (type hints, imports, virtual dispatch, framework conventions), `"guess"` (name match
only). Dynamic languages will produce many guesses; the viewer draws them more faintly.

For dynamic languages, a name resolved statically through the language's own scoping and imports
(a class defined in the file, `from .models import User`) counts as certain; anything that depends on
an inferred type (`self.repo.save()`, a type-hinted parameter, `x = Foo(); x.run()`) or an approximate
module match is `likely`. The Python probe follows this.

### Members and flow (optional, additive)

For simulation the format goes below types. Each node may list its `members`:

```json
"members": [
  { "id": "cs:Shop.Services.OrderService.Place(Shop.Domain.Order)", "name": "Place", "kind": "method",
    "visibility": "public", "isStatic": false, "signature": "void Place(Order order)",
    "file": "Shop/Services/OrderService.cs", "line": 31, "attributes": {} }
]
```

Member kinds: `method`, `constructor`, `property`, `field`, `event` (C# events and Godot signals,
with `"signal": "true"`), and `function` for free functions. Languages with free functions
(Python, PHP, JS) put them on a node of kind `module` that represents the file/module.

`memberEdges` connect members, in source order:

```json
"memberEdges": [
  { "source": "cs:…OrderService.Place(…)", "target": "cs:…OrderRepository.Save(…)", "kind": "calls",
    "weight": 1, "order": 3, "line": 40 },
  { "source": "cs:…Player.TakeDamage(int)", "target": "cs:…Player.Died", "kind": "raises", "weight": 1, "order": 1, "line": 22 },
  { "source": "cs:…Player.Died", "target": "cs:…Hud.OnPlayerDied()", "kind": "triggers", "weight": 1, "order": 0, "line": 15,
    "confidence": "likely" }
]
```

| Kind       | Meaning |
|------------|---------|
| `calls`    | source invokes target (calls through interfaces/virtuals also fan out to each implementation, `likely`) |
| `creates`  | source constructs via target constructor |
| `reads`    | source reads target field/property |
| `writes`   | source assigns target field/property |
| `raises`   | source raises/emits target event or signal |
| `triggers` | target handler runs when source event/signal is raised (from `+=`, `Connect`, framework wiring) |

`order` is the position of the edge's first occurrence among the source member's edges, so a
simulation can play a method's calls in the order they're written.

`entryPoints` mark members something outside the code invokes:

```json
"entryPoints": [
  { "member": "cs:Game.Player._Process(double)", "trigger": "frame", "label": "Player._Process", "source": "godot" },
  { "member": "cs:Game.Hud.OnStartPressed()", "trigger": "event", "label": "Button.Pressed → OnStartPressed", "source": "csharp" }
]
```

Triggers: `frame`, `physics`, `input`, `draw`, `startup`, `shutdown`, `event`, `request`, `job`
(others allowed). Engine and framework conventions (Godot callbacks, Laravel routes, Django views…)
are how a probe finds them.

## 5. The merged graph: `valence.graph/1`

The core's output adds `generator`, `repo` (`root`, `name`, `commit`, `branch`, `generatedAt`,
`churnWindowDays`), `probes`, and `moduleEdges` (node edges aggregated between leaf modules), and
carries `memberEdges` and `entryPoints` through (dropping any that point at unknown members). The C#
types for both formats live in `src/Valence.Schema/Model.cs`. Bump the version suffix on breaking
changes; consumers reject versions they don't know. Optional additive fields don't bump it.

## 6. Progress

Built-in probes receive an `AnalysisContext` and report `(phase, fraction, detail)` plus honour
cancellation; the viewer shows this in its progress dialog. External probes report nothing beyond
their stderr log, so they appear as a single step.
