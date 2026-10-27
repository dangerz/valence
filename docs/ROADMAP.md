# Roadmap

Valence is static analysis only: point it at a folder you've already cloned, and it reads the code.
It never clones, builds, runs, records or writes to the repo.

## Done

1. **Usability**: traditional menu bar, progress dialog with cancel, outline tree, inspector, hover tooltips.
2. **Opened systems**: clicking an atom spreads its classes out with labels (layered by internal
   dependencies), with rim ports leading to connected systems.
3. **Member-level format**: methods/properties/fields/events, ordered calls, reads/writes, event
   wiring (`raises`/`triggers`), confidence on edges, entry points. The C# probe fills all of it,
   including Godot callbacks and signals.

4. **Simulation** (static): flow downstream/upstream from a selected class, the game loop
   (frame/physics/draw entry points, repeating), and any other trigger, with pause, speed, heat
   and a trail. Labels appear on zoom, or for the selection and everything connected to it.

## Next

- Godot scene files (`.tscn`): attached scripts and signal connections as wiring.
- Method-level selection (flow from one method rather than a whole class).
5. **Tree-sitter engine** (started): `Valence.Syntax` loads tree-sitter and its bundled grammars
   in-process (TreeSitter.DotNet), so new languages need no language runtimes installed. Each
   language adds its own name resolution, plus data-driven *framework packs* (entry points and
   event conventions).
6. **Python** (in progress, built in): packages/folders as atoms; classes and files with free
   functions as nucleons; bonds from statically resolved names (imports incl. relative and
   `__init__.py` re-exports, `self` fields, type hints, constructor calls). Member links in
   evaluation order (calls, `__init__` on construction, field and property reads/writes) and entry
   points: `if __name__ == "__main__":`, route decorators (Flask, FastAPI…), Celery tasks,
   click/Typer commands. Next: Django (urls → views, signals) and pygame packs, and calls through
   base-class methods fanning out to overrides.

## Languages (planned, roughly in this order)

| Language | Systems (atoms) from | First framework packs |
|----------|----------------------|------------------------|
| C# | projects / namespaces | Godot, .NET (done) |
| PHP | `composer.json` PSR-4 namespaces | Laravel, Symfony, WordPress |
| Python (in progress) | packages (`__init__.py`) / folders | Django, Flask/FastAPI, pygame |
| Java | Maven/Gradle modules / packages | Spring, Android |
| Ruby | gems / `lib` folders / Rails app dirs | Rails |
| Objective-C | Xcode targets / folders | UIKit/AppKit (app delegate, actions) |
| Swift | SwiftPM / Xcode targets / modules | SwiftUI, UIKit |
| Go | modules / packages | net/http, gin |
| JavaScript / TypeScript | `package.json` workspaces / folders | Node, Express, React |
| HTML | pages and templates | links, forms, scripts, and templates bound to back-end routes |

Each language gets a tree-sitter probe first (breadth), and can later gain a precision probe
backed by its own compiler/type checker, the way C# uses Roslyn.
