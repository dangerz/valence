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

## Next

4. **Simulation**
   - *Flow from selection*: pick a class or method; pulses travel downstream (what it reaches) or
     upstream (what reaches it) along member edges, in call order, hop by hop.
   - *Triggers*: play a `frame` tick (every `_Process`/`_PhysicsProcess`), a `startup`, an `event`
     handler chain, or later a web `request`, and watch the flow through the codebase. Heat
     accumulates on the busiest paths. Virtual/interface calls fan out as "maybe" links.
   - Godot scene files (`.tscn`): attached scripts and signal connections as wiring.
5. **Tree-sitter engine**: one embedded parser library with bundled grammars so new languages run
   in-process with no language runtimes installed, plus per-language name resolution and
   data-driven *framework packs* (entry points and event conventions).

## Languages (planned, roughly in this order)

| Language | Systems (atoms) from | First framework packs |
|----------|----------------------|------------------------|
| C# | projects / namespaces | Godot, .NET (done) |
| PHP | `composer.json` PSR-4 namespaces | Laravel, Symfony, WordPress |
| Python | packages (`__init__.py`, `pyproject.toml`) | Django, Flask/FastAPI, pygame |
| Java | Maven/Gradle modules / packages | Spring, Android |
| Ruby | gems / `lib` folders / Rails app dirs | Rails |
| Objective-C | Xcode targets / folders | UIKit/AppKit (app delegate, actions) |
| Swift | SwiftPM / Xcode targets / modules | SwiftUI, UIKit |
| Go | modules / packages | net/http, gin |
| JavaScript / TypeScript | `package.json` workspaces / folders | Node, Express, React |
| HTML | pages and templates | links, forms, scripts, and templates bound to back-end routes |

Each language gets a tree-sitter probe first (breadth), and can later gain a precision probe
backed by its own compiler/type checker, the way C# uses Roslyn.
