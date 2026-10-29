# Valence User Guide

Valence is a read-only 3D map of a codebase. You point it at a folder you have already cloned; it reads every type and how they depend on each other, and draws the result as chemistry. Nothing in the folder is built, run or written.

This guide explains every menu item, button, panel, lens, filter, report and control, and how to use them to understand code you didn't write.

## Contents

- [Quick start](#quick-start)
- [Reading the scene](#reading-the-scene)
- [The window](#the-window)
- [Menus, item by item](#menus-item-by-item)
- [Lenses](#lenses)
- [The Boundary lens in depth](#the-boundary-lens-in-depth)
- [Filters and visibility](#filters-and-visibility)
- [Simulation and the flow chart](#simulation-and-the-flow-chart)
- [Insights reports](#insights-reports)
- [History: timeline and snapshots](#history-timeline-and-snapshots)
- [Workflows](#workflows)
- [Keyboard and mouse reference](#keyboard-and-mouse-reference)
- [Where Valence stores things](#where-valence-stores-things)
- [Troubleshooting and FAQ](#troubleshooting-and-faq)

---

## Quick start

### Opening something

Valence starts with the graph you had open last time, or with a small bundled demo graph the first time. It opens in full screen by default; press **F11** (or **View ▸ Full Screen**) to switch to a normal window.

- **File ▸ Open Source Folder…** (⌘O) picks a repository on disk. It is analyzed in the background with a progress dialog (phase, progress bar, current file, a log, and **Cancel**). When it finishes, the graph opens. The result is also saved to `~/.valence/graphs/<folder>-<hash>.valence.json`.
- **File ▸ Open Graph…** (⌘⇧O) opens a saved graph instantly, without analyzing again.
- **File ▸ Open Recent ▸** lists the last ten folders. Picking one opens its saved graph if there is one, otherwise analyzes it.
- **File ▸ Re-analyze** (⌘R) analyzes the current graph's folder again (after the code changed).

Languages understood today: C# (via Roslyn) and Python (via tree-sitter). No language runtime needs to be installed. Git is optional; with it you get churn, the "changed recently" glow, the Risk and Authors lenses and the timeline.

### What you are looking at

The scene is a molecule model of the code:

| On screen | In the code |
| --- | --- |
| Molecule (a cluster, labeled) | a project or top-level module |
| Atom (a glowing sphere) | a system: a C# namespace, a Python package or folder |
| Nucleon (small sphere inside an atom) | a class, interface, struct, enum, or a file of free functions |
| Red nucleon (proton) | a public type |
| Blue nucleon (neutron) | an internal or private type |
| Electrons orbiting an atom | one per system this atom is bonded with (up to 8 drawn) |
| Cyan bond | one-way dependency between two systems |
| Amber bond | two systems depend on each other: a cycle |
| Height | dependency layer: a system always sits above what it depends on |

### The first five things to try

1. Read the **Inspector** on the right. With nothing selected it shows the repo's name, branch and commit, counts (projects, systems, classes, bonds, cycles, members, member links) and the **Top risks** list. Click a risk to jump to that class.
2. **Click a system** (an atom). It opens: its classes spread out with labels, each class above the ones it uses, and ports on its rim lead to the systems it is bonded with. Click a class inside to select it; everything it links to lights up.
3. Press **⌘F** and type a class or system name. The Outline filters as you type; **Enter** selects the first class that matches.
4. Click the **Risk** tab above the view. Hot (yellow) classes change a lot and many classes depend on them: the ones to read carefully before touching.
5. Open **Insights ▸ Reading Guide**. It lists where to start reading, the entry points, the systems from the foundations up, hot spots, cycles and the biggest classes, and can be saved as Markdown.

Then press **▶** next to an entry point in the **Entry points** panel to watch, statically, what it ends up calling.

---

## Reading the scene

### Systems and classes

- **Size** of a nucleon is its lines of code (on a log scale, so one huge file doesn't dwarf everything).
- **Color** depends on the active lens (see [Lenses](#lenses)). With the default **Structure** lens, red = public, blue = internal or private.
- **Glow** (✦): a class changed in the 14 days before the graph was generated (needs git). The legend reminds you of this.
- **Bond thickness** is the number of class-level dependencies between the two systems. Pulses on a cyan bond travel from the dependent system to the one it depends on.
- **Height** is the dependency layer, computed after collapsing cycles. Systems in a cycle share a layer and are flagged in the Inspector ("part of a dependency cycle").
- Each project has its own hue; the project's name floats above its atoms, and its atoms' clouds and the Outline rows use the same tint.
- System names appear as you get closer (nearer on big graphs with more than 40 systems). Class names appear once a system is opened.

### Opening a system

Click an atom to open it. Its classes spread out into concentric rings per internal dependency layer, labeled, with the camera flying in. The opened system's bonds, ports and connected systems turn **yellow**; other systems dim.

- **Ports** sit on the rim, one per bonded system, labeled like `→ Shop.Data ×12` (this system depends on Shop.Data through 12 links), `← Web ×3` (Web depends on this one) or `⇄ Core  4 out · 2 in` (both ways). Click a port to jump to that system.
- **Class links** are drawn inside the opened system, colored by kind: inherits (red), implements (orange), contains (grey), has (yellow), creates (green), calls (blue), uses (purple), references (light grey). Links to classes in other systems go to the matching port. The checkboxes in the legend turn kinds on and off.
- Click empty space inside the open system to deselect the class but keep the system open. Click elsewhere, or press **Esc**, to back out.

### Hovering and selecting

- **Hover a class** to see a tooltip: name, kind and visibility, how many classes use it, how many it depends on, how many members it has, plus one line from the active lens (its risk score, what reaches it, its agent share, its inferred-link share, or what it does outside the program). Its direct links light up while you hover.
- **Hover a system** to see its class count and valence (how many systems it bonds with).
- **Click a class** to select it. It turns white; everything within **View ▸ Link Depth** hops (over the link kinds enabled in the legend) stays lit and labeled; the rest darkens. The Inspector shows its details and the **Methods** panel lists its methods.
- Links from the selected class to classes in other systems are drawn straight to those classes (which are labeled wherever they are), not just to the port.
- The Outline follows the selection, and the status bar shows `System ▸ Class`.

### Labels

**View ▸ Labels** (or press **L** to cycle):

- **All**: every label, regardless of zoom or pointer.
- **Near Mouse** (default): labels of the opened system appear as you zoom in and only around the pointer; the selected class and its neighbours are always labeled.
- **On Click**: only what you clicked (the selected class and the opened system) is labeled.

During a simulation this is overridden: only classes the flow has reached are labeled.

---

## The window

### Title bar

Logo, the menus (File, View, Navigate, Simulate, Insights, Timeline, Help) and, on the right, three pills: the repository name (hover for its path), the git branch and the short commit hash. The menus are drawn inside the window, so they are available in full screen.

### Toolbar (above the 3D view)

- **Structure**, **Changes**, **Dependencies**, **Risk**, **Boundary**: the lens tabs. Same choices as **View ▸ Color By** (which has four more). The Boundary tab cycles through three states; see [The Boundary lens in depth](#the-boundary-lens-in-depth).
- **Hide Unrelated**: a checkbox that only appears while a class or system is selected. Hides everything the selection doesn't link to. See [Filters and visibility](#filters-and-visibility).
- **Filters ▾**: the filter menu (usage filter, only differences, show everything, clear spotlight, code that reaches outside, count indirect effects).
- **Fit view**: frames everything (same as **F**).

### The 3D view

- The **legend** (top left; **View ▸ Legend** hides it) explains bonds, what the current lens's colors mean, and has the **Class links** checkboxes that enable or disable each link kind. Disabled kinds are not lit, not labeled and not drawn, and are ignored by Hide Unrelated.
- The **axis gizmo** (bottom left) turns with the camera: X red, Y green (up: the dependency layer), Z blue.
- The **view tools** (bottom right) pick what a left-drag does: **Select** (click selects, drag orbits; the default), **Orbit** (drag turns the view; clicks don't select), **Pan** (drag moves the view), **Zoom** (drag up or down to zoom). The **▾** next to them has **Zoom In**, **Zoom Out**, **Fit View** and **Reset Layout**.
- Right-drag or middle-drag always pans. The wheel (or pinch) zooms. A two-finger swipe orbits; with Shift it pans.
- **Double-click empty space** clears the selection and frames everything.
- **Shift-drag a system** (in the Select tool) moves it on its layer. Moves are saved per repository; **View ▸ Reset Layout** undoes them all. Height never changes: it always means the dependency layer.

### Outline (left)

A searchable tree of ★ Favorites, then projects → systems → classes, alphabetical, with class counts. **View ▸ Outline** hides it.

- The header has **Expand all** and **Collapse all** buttons.
- The filter box (**⌘F** focuses it) matches system and class names as you type; **Enter** selects the first matching class (or the first match of any kind).
- Click a system or class row to select it in 3D. Project rows can't be selected.
- The **eye** on the right of each row cycles its visibility: ● Shown → ◐ Quiet (faded) → ○ Hidden. Right-click a row for **★ Add to Favorites** / **☆ Remove from Favorites**, **Show**, **Quiet (faded)**, **Hide**, and **Show Everything**.
- Favorites are listed in their own section at the top and marked ★ in the tree; they are saved per repository.

### Entry points / Methods (right, top)

The panel above the Inspector (**View ▸ Methods** hides it) lists playable methods, each with a **▶** that simulates the flow from exactly that method.

- With **nothing selected**, it is titled **ENTRY POINTS** and lists every entry point the analysis found: Godot callbacks (`_Process`, `_Ready`, `_Input`…), `Main`, handlers of external events (`Button.Pressed → OnStart`), Python `__main__` blocks, routes, Celery tasks, click/Typer commands, signal receivers, Django views and game loops. Chips above the list show one trigger each with its count (`draw 12`); click one to show only that trigger, its ✕ or **Clear** to show all.
- With a **class selected**, it is titled **METHODS** and lists that class's methods, constructors, functions and properties.
- Typing in **Search all methods…** searches every method in the whole repository by `Class.method` or signature, whatever is selected. **Enter** plays the first result that can be played.
- Each row: **▶** (greyed out when nothing is recorded to simulate from there), the name (click it to select its class; hover for file and line), a tag (the trigger for entry points, else the member kind) and a **⋯** menu with **Simulate from Here**, **Select Class** and **Copy Location (file:line)**.
- The panel's own **⋯** menu has **Clear Search and Filter** and **Play First Result**. Lists show at most 300 rows; narrow them with a trigger or a search.

### Inspector (right, bottom)

**View ▸ Inspector** hides it. Its **⋯** menu has **Back to Summary**, **Copy Repo Path** and **Open Repo Folder** (opens the folder in your file manager).

- **Nothing selected**: the repository summary (name, path, branch @ commit), the count cards, the **Top risks** list (up to ten classes with their risk score and a bar; click to select) and a hint.
- **System selected**: class, proton and neutron counts, valence and layer, a cycle warning, the systems it depends on and is used by (click to jump; ⇄ marks a cycle, ×n the link count), how many entry points it holds, which outside effects its classes make, and its classes with their used-by/depends-on counts.
- **Class selected**: kind, visibility, full name, file and line, its system, external base type and interfaces, size and member counts, used by / depends on, git history (commits, lines churned, last change), the **Risk** line and what drives it, which triggers reach it, authorship and agent share, how many of its links are inferred, modules it imports from outside the repo, what it does outside the program (Boundary), its members (▶ marks entry points, ● tags mark outside effects), and its dependencies and dependents grouped by link kind (×n = weight, "(likely)" = inferred).
- **Comparing**, **simulating** and scrubbing a **timeline with the Boundary lens** replace the summary with their own text (see those sections).

Anything underlined is a link: click a class or system name to select it.

### Flow chart drawer

Appears under the 3D view when a simulation starts (if **Simulate ▸ Flow Chart** is on). Drag the gap above it to resize it. See [Simulation and the flow chart](#simulation-and-the-flow-chart).

### Filter bar and timeline bar

Two bars appear under the view when needed: **Filter: used by** (View ▸ Filter by Usage) and **Timeline** (Timeline ▸ Show Timeline). Each has a ✕ to put it away.

### Status bar

Left: messages (what just happened, how many classes a filter shows, hints). Middle: the current selection. Right: a reminder of the mouse controls.

### Resizing, hiding and full screen

- Drag the gap between a side panel and the view to resize it (180 to 900 pixels). Widths are remembered.
- **View ▸ Outline / Methods / Inspector / Legend** toggle the panels. When both right-hand panels are hidden the right column disappears entirely.
- **View ▸ Full Screen** (**F11**, **⌃⌘F**) toggles exclusive full screen; off, the window is maximized. The choice is remembered.
- Dialogs (Controls, About, Insights, Snapshots) close with **OK**, **Esc**, **Enter** or a click outside them.

---

## Menus, item by item

### File

| Item | What it does |
| --- | --- |
| Open Source Folder… (⌘O) | Analyze a folder (read-only) and open the result. |
| Open Graph… (⌘⇧O) | Open a saved `.valence.json` graph (defaults to `~/.valence/graphs`). |
| Open Recent ▸ | The last ten analyzed folders. Opens the saved graph if present, else analyzes. |
| Re-analyze (⌘R) | Analyze the current graph's folder again. Disabled when the folder isn't on this machine. |
| Take Snapshot (to compare later) | Save a copy of the current graph for **View ▸ Compare With Snapshot**. |
| Snapshots… | List, compare, rename and delete this repository's snapshots. |
| Quit | Exit. |

### View

| Item | What it does |
| --- | --- |
| Color By ▸ | Pick a lens: Protons / Neutrons, Kind, Churn, Coupling, Risk, Reach, Authors, Trust, Boundary. |
| Link Depth ▸ | 1 hop (direct links), 2 hops, 3 hops: how far from the selected class lighting, labels and Hide Unrelated reach. |
| Frame All (F) | Fly the camera to show everything. |
| Reset Layout | Forget every system you moved and lay the scene out again. |
| Quiet Fade ▸ | 5%, 10%, 15%, 25%, 40%: how visible quiet (and ghosted) items are. |
| Show Everything (clear hide/quiet) | Reset every hide and quiet choice for this repository. |
| Labels (L) ▸ | All, Near Mouse, On Click. |
| Brightness ▸ | 25% to 125%: atom clouds, bonds, electrons, nucleon glow and bloom together. |
| Compare With Snapshot ▸ | Pick a snapshot to compare with (newest first), or **Manage Snapshots…**. |
| Stop Comparing | Back to normal colors. Only enabled while comparing. |
| Only Show Differences | Hide everything that didn't change since the snapshot. Only enabled while comparing. |
| Filter by Usage (used-by count) | Show the usage filter bar under the view. |
| Outline, Methods, Inspector, Legend | Show or hide each panel. |
| Full Screen (F11) | Exclusive full screen on or off. |

### Navigate

| Item | What it does |
| --- | --- |
| Find… (⌘F) | Focus the Outline's filter box (shows the Outline if hidden). |
| Back (Esc) | Stop a running simulation; otherwise back out one level: deselect the class, then close the system. |
| Clear Selection | Deselect everything and show the repository summary. |

### Simulate

Everything here is static: it follows the recorded calls, nothing is executed. See [Simulation and the flow chart](#simulation-and-the-flow-chart).

| Item | What it does |
| --- | --- |
| Flow Downstream from Selection (D) | From the selected class, follow what it calls, creates, raises and triggers, hop by hop. |
| Flow Upstream into Selection (U) | The same links backwards: what ends up reaching the selected class. |
| Play Game Loop (frame, physics, draw) (G) | Play every frame, physics and draw entry point, tick after tick, repeating. Disabled when there are none. |
| Play Trigger ▸ | One item per trigger with its entry-point count, e.g. `request (4)`, `startup (2)`. |
| Pause / Resume (Space) | Pause or resume; after a finished single run, play it again. |
| Stop | Clear the simulation (so does Esc). |
| Speed ▸ | 0.25×, 0.5×, 1×, 2×, 4×. |
| Include Data Reads/Writes | Also pulse field and property reads and writes (off by default: control flow only). |
| Untouched (H) ▸ | Show, Ghost or Hide classes the flow hasn't reached. |
| Flow Chart | Show the flow chart drawer whenever a simulation has a chart (remembered). |

### Insights

Reports built from the graph, in a window with clickable classes, **Show Only These**, **Copy Markdown** and **Save as Markdown…**. See [Insights reports](#insights-reports).

| Item | Report |
| --- | --- |
| Blast Radius ▸ From the Selection | What depends on the selected class or system, and which entry points reach it. |
| Blast Radius ▸ From the Compared Changes | The same, starting from the classes added or changed since the compared snapshot. |
| Blast Radius ▸ From Recent Changes (14 days) | The same, starting from classes changed in the 14 days before the graph was made. |
| Unreached Code | Classes no entry point reaches. |
| Cycles | Pairs of systems that depend on each other, and the lighter direction to cut. |
| Coverage Matrix | Systems × triggers: how much of each system each trigger runs. |
| Abstractions | Interfaces with one or no implementation; calls that could run many. |
| System Metrics | Instability, abstractness and distance per system. |
| What Reaches Outside (Boundary) | Everything that touches the network, files, processes, environment, databases, native or dynamic code. |
| Outside Effects Over Time (timeline) | When each outside call was added or removed, by sampled commit. |
| Reading Guide | A tour of the repository for someone new to it. |

### Timeline

| Item | What it does |
| --- | --- |
| Build Timeline: 10 / 25 / 50 commits | Sample that many commits from the folder's git history, analyze each, save the result. |
| Show Timeline | Reopen the saved timeline for this repository. |
| Hide Timeline | Back to the current code. |

### Help

| Item | What it does |
| --- | --- |
| User Guide (F1) | This guide, with a contents list. Section links jump; web links open in your browser. |
| Controls | The key and mouse reference, in groups (Camera, Exploring, Simulate). |
| About Valence | What Valence is, the read-only promise, credits and license. |

### Smaller menus

- **Filters ▾** (toolbar): Filter by Usage (used-by count), Only Show Differences, Show Everything (clear hide/quiet), Clear Spotlight (a report's Show Only These), Only Code That Reaches Outside ▸ (Off, Anything Outside the Program, then one item per effect kind), Count Indirect Effects (through calls).
- **▾ in the view's corner**: Zoom In, Zoom Out, Fit View, Reset Layout.
- **Inspector ⋯**: Back to Summary, Copy Repo Path, Open Repo Folder.
- **Outline row, right-click**: ★ Add to Favorites / ☆ Remove from Favorites, Show, Quiet (faded), Hide, Show Everything.
- **Methods panel ⋯**: Clear Search and Filter, Play First Result. **Row ⋯**: Simulate from Here, Select Class, Copy Location (file:line).
- **Flow chart Export ▾**: Export SVG…, Export PNG…, Export Mermaid…, Copy as Mermaid.

---

## Lenses

A lens colors every class by one question. The toolbar tabs cover the five most used; **View ▸ Color By** has all nine. The legend changes with the lens. Most lenses use a heat scale: cold blue → magenta → hot yellow. Grey means the lens has nothing to say about that class.

### Structure (Protons / Neutrons)

Red = public, blue = internal or private (Python: `_name` is private; dunder names count as public). Use it to see a system's surface: how much of it is API versus implementation.

### Changes (Churn)

Hot = many lines changed in the churn window (90 days by default; the legend shows the number). Logarithmic, so a few monster commits don't flatten everything else. Needs git; otherwise the legend says "No git history in this graph". Question: where is the work happening right now?

### Dependencies (Coupling)

Hot = many classes depend on it (its used-by count, square-rooted so the long tail stays visible). Question: what will hurt most if it changes?

### Risk

Where the next change is most likely to hurt. Each class gets a score from 0 to 1:

- With git: the square root of (churn percentile × dependents percentile), scaled by size (0.75 to 1). A class needs both recent change and many dependents to score high.
- Without git: the square root of (dependents percentile × size percentile).

Percentiles are within the repository, so the score is relative. The Inspector's **Top risks** lists the ten highest; a selected class shows its score and what drives it, e.g. "Risk 0.86: changed 1,240 lines in 90 days (top 4%), used by 37 classes (top 2%), 940 lines (top 10%)". Hovering shows the score.

### Kind

Class (blue), interface (yellow), struct (green), enum (purple), record (teal), delegate (orange), module, i.e. a file of free functions (lime). Question: where are the interfaces, where is the data?

### Reach

What no entry point runs. Cool = reached from an entry point by following calls, creates and events; warm = not reached, but used by other unreached code; hot = not reached and nothing uses it. Hovering tells you which triggers reach the class. Grey everywhere means the graph has no entry points. Unreached is not proof of dead code: reflection, dependency injection, serialization, scene files, tests and framework callbacks are invisible to static analysis.

### Authors

Hot = most of the lines changed in the churn window came from commits by AI agents or bots (matched by author name, email or `Co-authored-by` trailer against markers such as `[bot]`, dependabot, renovate, github-actions, copilot, claude, codex, devin, cursor, aider, gemini). Cool = people. Grey = unchanged in the window. Hovering shows the share; the Inspector shows the number of authors and the top author.

### Trust

How much of a class's linking is inferred rather than certain. Hot = most of its links are "likely" (resolved through type hints, inferred types, virtual dispatch or framework conventions) or guesses. C# links resolved by the compiler are certain; Python has more inferred links. Grey = no links. Question: where should I double-check the map against the code?

### Boundary

What each class does outside the program. The next section covers it.

---

## The Boundary lens in depth

The Boundary lens shows the code that reaches outside the program: the places where a change can affect the world and where the world can affect the program.

### Kinds

Each class is colored by the most notable kind of outside call it makes, in this order:

| Kind | Color | Examples |
| --- | --- | --- |
| Network | blue | HTTP, sockets, websockets, mail, cloud SDKs |
| Database | purple | SQL and NoSQL clients, ORM queries |
| Writes files | orange | create, write, move, delete files and folders |
| Processes | red | start or stop processes, open with the OS shell, exit |
| Reads files | green | read files, list folders, check existence |
| Environment | yellow | environment variables, command-line arguments, the registry |
| Native code | pink | P/Invoke, marshalling, ctypes |
| Dynamic code | light grey | reflection, `eval`, call by name: what runs can't be seen statically |

### Direct and indirect

- **Bright, with a badge**: the class's own code makes the call. The badge under the class shows one icon per kind (up to three).
- **Dimmer**: the class only reaches the outside by calling code that does (a button handler that calls `Storage.Save` "writes files, through Storage"). Effects spread back up calls, creates and event wiring.
- **Dark**: stays inside the program.

**Filters ▾ ▸ Count Indirect Effects (through calls)** turns the indirect part off: then only classes that make the call themselves are colored, badged and matched by the filter. The setting is remembered.

### The three-click Boundary tab

1. First click: the lens turns on (colors and badges). The status bar says "click Boundary again to show only code that reaches outside".
2. Second click: only code that reaches outside is drawn (the same as **Filters ▾ ▸ Only Code That Reaches Outside ▸ Anything Outside the Program**).
3. Third click: the lens and the filter turn off and the lens you had before comes back.

Picking any other lens also clears the filter.

### The filter

**Filters ▾ ▸ Only Code That Reaches Outside ▸** has **Off**, **Anything Outside the Program**, and one item per kind. Choosing a kind turns the Boundary lens on if it isn't, hides every class without that effect (systems with no such class disappear too), and the status bar reports "Only code that reaches network (itself or through calls): 23 of 1,204 classes".

### Hover, Inspector and flow chart

- **Hover** a class: "Network, Writes files" for its own calls, "through calls: Database" for indirect ones, or "stays inside the program".
- **Inspector**, class selected, section **Reaches outside the program**: for each kind the class calls itself, the member, the API, how many times (×n), the line and "likely" when inferred. For kinds it only reaches through calls, the path: `Save → Storage.Write → … → System.IO.File.WriteAllText` (each step is a link). The member list tags members with ● net, ● db, ● write, ● proc, ● read, ● env, ● native, ● dyn.
- **Inspector**, system selected: "Reaches outside (classes making the call): ● network 3  ● writes files 1".
- **Flow chart**: each box that reaches outside gets an icon per kind in its bottom-right corner; hovering shows "reaches outside: Network". Exports keep them (Mermaid as `↗ net, write`, SVG as small tags).
- **Reports**: **Insights ▸ What Reaches Outside (Boundary)** and **Insights ▸ Outside Effects Over Time (timeline)**.

### The timeline view

Build a timeline (**Timeline ▸ Build Timeline**) and turn the Boundary lens on, then scrub:

- Each class is colored and badged by what it called outside **at that commit**.
- A class that **started** an outside call since the previous sample **glows**; one that **stopped** turns **red** (and stays visible even with the filter on).
- With nothing selected, the Inspector lists **Started reaching outside** and **Stopped** by class, with each call. The timeline label adds "· reaching outside 14 (+2 −1)".
- Because the timeline samples commits, a change happened "somewhere between the previous sample and this commit". Build with more commits for finer resolution.
- The timeline view only counts calls a class makes itself: past commits don't keep the call links needed to spread effects through callers.
- Timelines built before Valence recorded outside effects show "rebuild the timeline to see outside calls" in the bar: use **Timeline ▸ Build Timeline** again.

### What it can and can't detect

Calls are recognized from a catalog of known APIs (`src/Valence.Core/EffectCatalog.cs`): the .NET base libraries, Godot, common .NET packages (HTTP clients, database drivers, Entity Framework, AWS/Azure/Google SDKs, gRPC, RabbitMQ, Kafka…), the Python standard library and common packages (requests, httpx, aiohttp, boto3, sqlite3, psycopg, pymongo, redis, SQLAlchemy, pandas, torch, ctypes…).

- **C#**: calls are resolved by the compiler, so the declaring type and member are exact. Whether `new FileStream(...)`/`File.Open` reads or writes comes from the mode argument. `[DllImport]`/`[LibraryImport]` methods count as native. When a package's assemblies aren't in your local NuGet cache (Godot's, for instance), calls on those types are matched by the type's short name and marked "likely".
- **Python**: a call is classified by the dotted path it resolves to through imports (`requests.get`, `pathlib.Path().write_text`). Paths that go through an assignment or a type hint (`s = requests.Session(); s.get(...)`, `path: Path`) are "likely". `open`, `eval`, `exec`, `compile` and `__import__` count even though they are builtins; reading `os.environ[...]` or `sys.argv` counts as Environment; Django `Model.objects.filter(...)` counts as Database (likely). Raising `requests.HTTPError` is not a network call.
- **Missed by both**: calls through wrappers that live outside the repository, APIs the catalog doesn't know, and anything reached dynamically (which is why Dynamic code is a kind of its own: links out of such code may be missing).

---

## Filters and visibility

Everything below multiplies together: a class is drawn only if it passes every active filter, and a system disappears when none of its classes is drawn.

### Hide and quiet

The eye in each Outline row (or its right-click menu) sets a project, system or class to **Shown**, **Quiet** or **Hidden**. Hidden removes the item, everything inside it, and every bond, link, port, label, badge and pulse attached to it. Quiet draws it faded at the **View ▸ Quiet Fade** level. A class is as hidden as the most hidden of itself, its system and its project. Choices are saved per repository; the status bar counts them ("3 hidden, 2 quiet"); **View ▸ Show Everything** (also in Filters ▾ and the Outline's right-click menu) clears them all.

### Filter by Usage

**View ▸ Filter by Usage (used-by count)** opens the **Filter: used by** bar: a two-handle range over 0 to the highest used-by count in the graph. Only classes used by that many other classes stay; the dropdown picks **Hide others** or **Ghost others**; the count reads "1,234 of 5,000 classes". Useful ranges: 0–0 for classes nothing uses; the top of the range for the most depended-on. The ✕ turns it off.

### Hide Unrelated

Appears in the toolbar while something is selected. With a **class** selected it keeps the class and everything within **View ▸ Link Depth** hops of it over the link kinds enabled in the legend. With only a **system** open it keeps that system's classes and those of the systems it bonds with. It stays checked between selections and does nothing with nothing selected.

### Link depth and link kinds

**View ▸ Link Depth** (1, 2 or 3 hops) and the **Class links** checkboxes in the legend decide which neighbours light up and get labels around a selected class, and what Hide Unrelated keeps.

### Only Show Differences

While comparing with a snapshot, keeps only the classes added or changed since it (see [History](#history-timeline-and-snapshots)).

### Spotlight

Every Insights report has **Show Only These (N)**: only the report's classes are drawn until **Filters ▾ ▸ Clear Spotlight (a report's Show Only These)**. The status bar names the report the spotlight came from.

### Only Code That Reaches Outside

The Boundary filter; see [The Boundary lens in depth](#the-boundary-lens-in-depth).

### During a simulation and on a timeline

**Simulate ▸ Untouched** ghosts or hides classes the flow hasn't reached yet; a timeline frame only draws classes that existed at that commit.

---

## Simulation and the flow chart

The analysis records methods, properties, fields and events, the calls and reads/writes between them in source order, event and signal wiring, and entry points. The **Simulate** menu plays that back as a wave of pulses. Nothing is executed; it shows what the code *can* do, not a recording of a run. If a graph was made without method-level data, simulation asks you to re-analyze.

### Starting a simulation

- **▶ next to a method** (Entry points / Methods panel): flow downstream from exactly that method.
- **D** / **Simulate ▸ Flow Downstream from Selection**: from the selected class's members, what it reaches.
- **U** / **Simulate ▸ Flow Upstream into Selection**: what reaches the selected class.
- **G** / **Simulate ▸ Play Game Loop**: every `_Process`, `_PhysicsProcess` and `_Draw` (and their Python equivalents), tick after tick, repeating until stopped; the trail accumulates across ticks.
- **Simulate ▸ Play Trigger ▸**: every entry point of one trigger: `startup`, `frame`, `physics`, `input`, `draw`, `event`, `request`, `job`, `shutdown`.

### What you see

- Pulses travel the links hop by hop, in the order the calls are written. Colors: calls blue, creates green, raises orange, triggers pink, reads purple, writes yellow. Calls through interfaces, virtual methods, overrides and event wiring are "likely": smaller, dimmer pulses.
- A class turns **gold** for two seconds when a pulse lands on it, then returns to its lens color with a glow that fades with its heat; classes not reached yet are dimmed.
- Fired links stay drawn as a trail. All labels hide; a class is labeled the moment the flow reaches it, and its system's name is pinned.
- **Simulate ▸ Untouched (H)**: **Show** draws everything, **Ghost** fades what hasn't been reached (at the Quiet Fade level), **Hide** draws only the flow's path, classes appearing as pulses arrive. Remembered between sessions.
- The Inspector shows **Simulation**: the title, run or tick number, pulses per run, points reached, classes lit so far, a legend of pulse colors and the **busiest classes** (×visits). Very large flows are cut at 5,000 pulses and 18 hops and say so.
- The status bar: "Flow from Player._Process: 212 pulses, 87 points reached · Space pauses · H untouched · Esc stops".

### Controls

- **Space** pauses and resumes. After a single run has finished, Space plays it again.
- **Simulate ▸ Speed** sets the pace.
- **Simulate ▸ Stop** clears everything (colors, trail, labels). **Esc** also stops it, even with something selected; press Esc again to back out of the selection.
- **Simulate ▸ Include Data Reads/Writes** adds field and property traffic; a read pulses from the field to the reader.
- You can select, open systems, change lenses and move around while it plays.

### The flow chart

The drawer under the 3D view shows the same simulation as a flow chart: one box per method (or per class for big flows), laid out left to right by hop and top to bottom in the order things happen. Arrows are the calls, creates, reads/writes and events; dashed arrows are "likely" or data. Boxes get a colored bar in their project's hue and ▶ marks the start points.

- Boxes light up gold as the flow reaches them and arrows brighten as they fire; pulses in flight glow along the arrows.
- **Follow** keeps the newest step in view; panning or zooming turns it off. The **Fit** button shows the whole chart.
- **Group by class** draws one box per class instead of per method (automatic above 220 methods, and always for graphs without method-level data).
- **Scroll** or pinch to zoom (text appears once the zoom is big enough), **drag** to pan, **click a box** to select its class, **hover** for its full name, when it was reached and what it does outside the program.
- The subtitle reads "Flow from Player._Process · 48 methods, 71 links" and notes when the flow was cut short.
- Drag the gap above the drawer to resize it. ✕ closes it; **Simulate ▸ Flow Chart** brings it back. The last chart stays after the simulation stops, so you can still export it.

### Export

**Export ▾** on the drawer: **Export SVG…** (the chart exactly as laid out), **Export PNG…** (the whole chart rendered at up to 2×), **Export Mermaid…** (a `.mmd` flowchart file that GitHub, GitLab, Notion and Obsidian render), and **Copy as Mermaid** (paste it into a fenced `mermaid` code block in Markdown). Files default to `~/.valence/exports/` with a name made from the chart's title; a save dialog lets you change both.

---

## Insights reports

Each report opens in the **Insights** window. The summary is at the top, then sections with lists and tables. Class names are links: clicking one closes the window and selects the class. The buttons:

- **Show Only These (N)**: draw only the report's classes until **Filters ▾ ▸ Clear Spotlight**.
- **Copy Markdown**: the whole report as Markdown on the clipboard.
- **Save as Markdown…**: the same to a `.md` file (default folder `~/.valence/exports/`).

### Blast Radius

Starts from a set of changed classes and follows "who depends on this" outward, hop by hop.

- **From the Selection**: the selected class, or every class of the opened system.
- **From the Compared Changes**: the classes added or changed since the snapshot you are comparing with (needs **View ▸ Compare With Snapshot**).
- **From Recent Changes (14 days)**: classes last changed within two weeks of the graph's generation (needs git).

Sections: the changed classes; the classes that **depend on them directly** (with the link kinds and how many classes use each of them: re-test those first); classes **further out** by hop count; **systems affected**; and the **entry points that reach the change** (following calls backwards from the changed members). Spotlight: everything affected.

### Unreached Code

Which classes no entry point reaches, over calls, creates and event wiring. **Nothing uses it** (no class depends on it: most likely dead, or wired up by name) and **Used only by unreached code**. Read the "Read this first" note: reflection, dependency injection, serialization, scene files, tests and framework callbacks are invisible here; treat the lists as things to check. Spotlight: all unreached classes. The Reach lens shows the same thing in 3D.

### Cycles

One section per pair of systems that depend on each other, heaviest first: the link count in each direction, and a table of the class pairs in the lighter direction with their kinds and weights: moving or inverting those breaks the cycle. Spotlight: the classes involved.

### Coverage Matrix

A table of systems × triggers: how many of each system's classes each trigger's entry points reach, shaded by the share of the system. Systems reached by nothing are counted in the summary; systems reached only at startup or shutdown are listed. Use it to see which parts of the code a request, a frame or an event actually runs.

### Abstractions

Interfaces and abstract classes: **Single implementation** (a seam, or ceremony), **Most implemented**, **No implementation in the repo** (implemented elsewhere, unused, or found in ways the probe can't see), and **Wide dispatch**: calls that could run any of four or more implementations, where reading the code won't tell you what actually runs.

### System Metrics

Per system: how many systems depend on it (In), how many it depends on (Out), instability I = Out/(In+Out), abstractness A = share of interfaces and abstract classes, distance D = |A + I − 1|. Stable systems that many depend on should be abstract; unstable ones can be concrete. High D marks the exceptions: rigid concrete foundations, or abstractions nobody uses. Sorted furthest from balance first.

### What Reaches Outside (Boundary)

How many classes call out of the program themselves and how many reach it through calls; an **overview** per effect kind; **what each entry point touches** (a trigger × kind matrix: ● means the entry point or code it runs makes that kind of call); then one table per kind with the class, its system, the APIs called and the members and lines; and a note on how sure the detection is. Spotlight: the classes making calls themselves.

### Outside Effects Over Time (timeline)

Needs a timeline (**Timeline ▸ Build Timeline**) built with the current version. Lists how many outside calls were added and removed across the sampled commits, **when each kind of reach first appeared** (with the class and call), then one section per sampled commit with changes, newest first (date, short hash, subject, author, and a table of `+ added` / `− removed` rows with class, kind and API). Classes deleted since are named "(deleted since)". Spotlight: every class that gained or lost a call. To see a change in place, scrub the timeline to its frame with the Boundary lens on.

### Reading Guide

For code you didn't write: **Where to start** (entry points, the core classes most others use, then the systems from the foundations up), the **entry points** by trigger, **systems from the foundations up** (layer, class count, key classes), **hot spots** from the Risk lens, **cycles to know about**, the **biggest classes**, and classes **mostly changed by AI agents or bots** (worth a human read). Save it as Markdown and keep it next to the code.

---

## History: timeline and snapshots

### Timeline

**Timeline ▸ Build Timeline: 10 / 25 / 50 commits** reads the folder's local git history (the commits on the current branch's first-parent line that touch the folder), picks that many spread evenly from the first commit to the latest, exports each with `git archive` into a temporary folder, analyzes it with the same probes, and keeps for each commit which classes existed, how big they were and what they called outside. Nothing in the repository or its working copy is touched. The progress dialog shows each commit; **Cancel** stops it. The result is saved to `~/.valence/timelines/` and opens at the latest commit.

The **Timeline** bar under the view:

- **▶ / ❚❚** plays through history, one commit every 0.7 seconds. Playing from the end starts over at the first commit.
- The **slider** scrubs; the label reads `3/25 · 2026-01-02 · abc1234 · subject · 180 classes (+12 −3) · 4 since deleted (not drawn)`.
- Only classes that existed at that commit are drawn; classes new since the previous sample glow **green**. Classes that were later deleted aren't in today's layout, so they are counted instead of drawn.
- ✕ (or **Timeline ▸ Hide Timeline**) returns to the current code. **Timeline ▸ Show Timeline** reopens the saved timeline later without rebuilding.

With the **Boundary** lens on, the timeline shows when code started reaching outside; see [The Boundary lens in depth](#the-boundary-lens-in-depth). A commit that couldn't be analyzed (early history with no source the probes understand) shows as an empty frame.

### Snapshots and compare

1. **File ▸ Take Snapshot (to compare later)** saves the current graph to `~/.valence/snapshots/<repo>-<hash>/` named by date, time and commit.
2. After the code changes, **File ▸ Re-analyze**.
3. **View ▸ Compare With Snapshot ▸** and pick the snapshot. Added classes turn **green**, changed ones **amber** (their size, member count or outgoing links differ), the rest dim. The Inspector lists **Added**, **Changed** and **Removed** (removed classes are named only; they aren't in the scene). The status bar counts them.
4. **View ▸ Only Show Differences** (also in Filters ▾) hides everything that didn't change. **View ▸ Stop Comparing** returns to normal.

**File ▸ Snapshots…** (or **Compare With Snapshot ▸ Manage Snapshots…**) lists this repository's snapshots, newest first, with name or date, commit, size and "being compared" when active:

- **Compare** starts comparing (the button reads **Comparing** on the active one).
- **Rename** edits the name in place: Enter or leaving the field saves, Esc cancels, an empty name goes back to the date. Names live beside the files, so file names never change.
- **Delete** asks for a second click (**Confirm**) and moves the file to the system trash. Deleting the snapshot being compared stops the comparison.
- **Take Snapshot Now** saves another one.

**Insights ▸ Blast Radius ▸ From the Compared Changes** turns a comparison into a list of what to re-test.

---

## Workflows

### I just inherited this repo

1. **File ▸ Open Source Folder…**, then read the Inspector summary: how big is it, how many cycles, what the top risks are.
2. **Insights ▸ Reading Guide**, **Save as Markdown…**. Follow its "Where to start".
3. Look at the scene with **Structure**: tall stacks mean deep dependency chains; amber bonds are cycles. Open the lowest systems first: everything builds on them.
4. With nothing selected, scan the **Entry points** panel chips: what makes this program run (startup, requests, frames, events, jobs)? Press ▶ on one and watch where it goes; read the flow chart left to right.
5. Switch to **Dependencies** to find the core classes, then **Trust** to see where the map is inferred (Python) and should be read with care.
6. Right-click the systems you'll keep coming back to in the Outline and add them to **Favorites**; **Hide** generated code and vendored folders so they stop cluttering the view.

### What does this button end up doing?

1. Find the handler: in the **Entry points** panel pick the `event` chip, or type part of its name in **Search all methods…** (for Python routes pick `request`).
2. Press **▶**. The flow plays in source order; set **Simulate ▸ Untouched ▸ Hide** to see only the path.
3. Read the **flow chart**: boxes by hop, time top to bottom, dashed arrows where the probe is guessing (interfaces, events). Turn on **Group by class** for a higher-level picture.
4. Check the Inspector's **busiest classes**, and the boxes' outside-effect icons to see if the button writes files or hits the network.
5. **Export ▾ ▸ Copy as Mermaid** to paste the chart into a pull request or a doc.

### Where does this app touch the network or disk, and when was that added?

1. Click the **Boundary** tab. Bright classes with badges make the calls; dim ones reach them through calls.
2. Click **Boundary** again to show only that code, or use **Filters ▾ ▸ Only Code That Reaches Outside ▸ Network** for one kind.
3. Select a class to read the exact APIs, members and lines in the Inspector, or the path to the code that makes the call.
4. **Insights ▸ What Reaches Outside (Boundary)**: the matrix of which entry points end up on the network, the disk, a database or a process. Save it as Markdown for a review.
5. **Timeline ▸ Build Timeline: 25 commits**, keep the Boundary lens on and scrub: glowing classes started an outside call at that sample, red ones stopped; the Inspector lists the calls. **Insights ▸ Outside Effects Over Time** gives the same as a dated list with authors and commits.

### What will my change break?

1. Before changing, **File ▸ Take Snapshot**. Select the class you plan to change and run **Insights ▸ Blast Radius ▸ From the Selection** to see what depends on it and which entry points reach it.
2. Press **U** on it to watch, upstream, what calls it.
3. After changing, **File ▸ Re-analyze**, then **View ▸ Compare With Snapshot**: added green, changed amber. **Only Show Differences** isolates them.
4. **Insights ▸ Blast Radius ▸ From the Compared Changes** lists what to re-test first, including the entry points that reach the changes.

### What is risky to change?

1. **Risk** tab: hot classes change a lot *and* many depend on them. The Inspector's **Top risks** is the shortlist; a selected class explains its score.
2. **Changes** shows where work is happening; **Dependencies** shows what everything leans on; Risk is the overlap.
3. **Insights ▸ Cycles** shows the cheapest link to cut in each cycle; **Insights ▸ System Metrics** flags rigid concrete foundations.
4. **Authors** shows what agents or bots wrote recently; **Insights ▸ Reading Guide** lists the classes mostly changed by them.

### Finding dead code

**Insights ▸ Unreached Code**, or the **Reach** lens, then **View ▸ Filter by Usage** at 0–0 for classes nothing uses at all. Confirm by hand before deleting: dynamic wiring is invisible to Valence.

---

## Keyboard and mouse reference

| Input | Action |
| --- | --- |
| Drag / two-finger swipe | Orbit (Select and Orbit tools) |
| Right-drag / middle-drag / Shift + swipe | Pan |
| Wheel / pinch | Zoom |
| Click a system | Open it |
| Click a class | Select it |
| Click a port | Jump to that system |
| Click empty space | Clear the selection (inside the open system: only deselect the class) |
| Double-click empty space | Clear the selection and frame everything |
| Shift-drag a system | Move it (Select tool; saved per repo) |
| Hover a class or system | Tooltip; direct links light up |
| F | Frame everything |
| F1 | Open this guide |
| Esc | Stop a simulation; otherwise back out one level |
| ⌘F | Find (focus the Outline filter) |
| ⌘O / ⌘⇧O / ⌘R | Open source folder / open graph / re-analyze |
| F11, ⌃⌘F | Full screen on or off |
| L | Cycle labels: all → near mouse → on click |
| D / U | Flow downstream from / upstream into the selected class |
| G | Play the game loop |
| Space | Pause, resume or replay a simulation |
| H | Cycle untouched classes: show → ghost → hide |
| Enter (Outline filter) | Select the first match |
| Enter (method search) | Play the first result |

On Windows and Linux, ⌘ is Ctrl. **Help ▸ Controls** shows the same list inside the app.

In the flow chart: wheel or pinch zooms, drag pans, click a box selects its class. In the Snapshots dialog's name field: Enter saves, Esc cancels.

---

## Where Valence stores things

Nothing is ever written inside the folder you analyze. Valence writes only to:

| Location | What |
| --- | --- |
| `~/.valence/graphs/` | Analyzed graphs: `<folder>-<hash>.valence.json`. The hash of the full path keeps two clones with the same name apart. |
| `~/.valence/snapshots/<repo>-<hash>/` | Snapshots, named by date, time and commit; `names.json` holds the names you give them. |
| `~/.valence/timelines/` | Timelines: `<folder>-<hash>.timeline.json`. One per repository; building again replaces it. |
| `~/.valence/exports/` | Default folder for flow chart (SVG, PNG, Mermaid) and report (Markdown) exports. |
| Godot's user data folder for Valence | `viewer.json` (settings), `layouts/`, `visibility/`, `favorites/` (one file per repository). |

On macOS the Godot user data folder is `~/Library/Application Support/Godot/app_userdata/Valence/`.

What is remembered between sessions: the last graph, recent folders, which panels are shown and their widths, quiet fade, untouched mode, label mode, brightness, whether the flow chart drawer is on, full screen, and **Count Indirect Effects**. Per repository: moved systems, hide and quiet choices, favorites, snapshots and the timeline.

The read-only guarantee:

- The C# probe never runs MSBuild or `dotnet restore`; it reads `.csproj` files as XML and builds compilations in memory. Package assemblies are read from your local NuGet cache if present.
- The Python probe only parses; it never imports or runs code, so no `__pycache__` appears.
- Git is queried with read-only commands and `GIT_OPTIONAL_LOCKS=0`, so it doesn't even refresh the index. The timeline uses `git archive` into a temporary folder.
- Hidden folders, `bin`, `obj`, `node_modules`, `packages`, `dist`, `build`, `artifacts`, Python virtualenvs, `site-packages` and `__pycache__` are skipped; symlinked folders are not followed.

---

## Troubleshooting and FAQ

**The Boundary lens is all grey and the legend says "No outside effects in this graph".**
The graph was made by an older Valence. **File ▸ Re-analyze**.

**"This timeline was built before Valence recorded outside effects" / "rebuild the timeline to see outside calls".**
Run **Timeline ▸ Build Timeline** again; the old file is replaced.

**"This graph has no method-level data" / "No method data in this graph".**
Simulation, the flow chart, Reach and most reports need member links. Re-analyze with the current version.

**Simulate ▸ Play Trigger says "(no entry points; re-analyze the source)".**
Same cause, or the code has no entry points the probes recognize (see the list in [The window](#the-window)).

**Flow Downstream does nothing.**
Select a class first (the status bar says so). "X has no recorded calls or data links" means the class has nothing to follow; try **Include Data Reads/Writes**, or a neighbour.

**"No probe found source files it understands".**
The folder has no `.cs` or `.py` files outside skipped folders. Other languages are on the roadmap.

**No glow, no churn, no Risk with git, Authors grey.**
The folder isn't a git repository, or git isn't installed; the log says "not a git repository; skipping history". Risk then falls back to dependents × size.

**"This graph has no source folder on this machine to re-analyze."**
The graph was made elsewhere, or the folder moved. Open the folder again with **File ▸ Open Source Folder…**.

**"An analysis is already running…"**
Wait for it or press **Cancel** in the progress dialog. One analysis or timeline build at a time.

**C# types show as unresolved; some effects are "likely".**
A package's assemblies aren't in your local NuGet cache, so the compiler couldn't resolve them (the log notes "package X not in local NuGet cache"). Restore the solution once in your normal workflow (not through Valence), then re-analyze. Godot's own types are matched by name even without the cache.

**Python links look wrong or missing.**
Python is resolved statically: imports, `self` fields, type hints and constructor calls. Anything through an inferred type is "likely"; the **Trust** lens shows where. Decorators that register things by name, `getattr`, plugins loaded by string and monkey-patching are invisible. Files over 5 MB and files with syntax errors are skipped or parsed around (the log lists them).

**The view is crowded on a big repository.**
Hide generated code and vendored folders in the Outline; use **Filter by Usage**; select a class and check **Hide Unrelated**; use a report's **Show Only These**. System names appear only when you are closer on graphs with more than 40 systems. Very large flows are cut at 5,000 pulses; report tables show at most 300 rows.

**Labels are hard to read.**
Press **L** for **All**, or select the class you care about: only it and its neighbours are labeled. **View ▸ Brightness** dims the clouds and glow.

**I moved systems and want the original layout back.**
**View ▸ Reset Layout**.

**Some classes vanished.**
Check, in order: the status bar's "N hidden, M quiet" (**View ▸ Show Everything**), the usage filter bar's ✕, **Hide Unrelated**, **Filters ▾ ▸ Clear Spotlight**, **Only Code That Reaches Outside ▸ Off**, **Only Show Differences**, a shown timeline frame (✕ on the Timeline bar), and **Simulate ▸ Untouched ▸ Show**.

**Where did my exports go?**
`~/.valence/exports/` unless you chose another folder in the save dialog; the status bar prints the full path after each save.

**Is anything sent anywhere?**
No. Valence reads the folder and git locally and writes only to `~/.valence/` and its own settings folder.
