# Writing a Valence probe (language plugin)

A **probe** teaches Valence a language. It is a standalone program, written in whatever language
parses that language best, that reads a repository and writes a JSON description of its types and
their dependencies. The core (`valence`) runs probes as separate processes, merges their output,
adds git history and metrics, and writes the graph the viewer loads.

```
repo ──read-only──▶ probe(s) ──probe.json output──▶ valence core ──graph.json──▶ Godot viewer
```

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
`probes/` next to the CLI. A search path may be a probe directory or a folder of probe directories.

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
`fanIn`, `fanOut`, `commits`, `churn`, `lastChanged` (unix seconds).

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

## 5. The merged graph: `valence.graph/1`

The core's output adds `generator`, `repo` (`root`, `name`, `commit`, `branch`, `generatedAt`,
`churnWindowDays`), `probes`, and `moduleEdges` (node edges aggregated between leaf modules). The C#
types for both formats live in `src/Valence.Schema/Model.cs`. Bump the version suffix on breaking
changes; consumers reject versions they don't know.
