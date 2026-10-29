# Valence agent reference: how everything works and where

This is the technical companion to [AGENTS.md](../AGENTS.md). Read that first: it holds the
rules that never bend (section 2), setup (3), process (4), the repository map (5), a short
architecture (6), the approach (7), C# pitfalls (8) and the playbook for adding a language (9).
None of that is repeated here. This file goes deeper: which code implements each feature, how data
flows from a probe to a pixel, the data model, the viewer's internals, the probes' internals,
step-by-step recipes for the usual extensions, the invariants that must survive any change, and
what is known to be missing. The user's view of the same features is
[valence/help/HELP.md](../valence/help/HELP.md).

Paths are relative to the repository root. "Main" is `valence/Scripts/Main.cs`, "Hud" is
`valence/Scripts/Hud.cs`. Line numbers are not given because they drift; method names are.

---

## 1. Feature catalogue

Each row names the user-facing feature, the code that implements it, and the data it reads.

### Loading and analysis

| Feature | Implemented by | Reads |
|---|---|---|
| Startup graph (`--graph` arg, last graph, demo) | `Main.StartupGraph`, `Main._Ready`; `ViewerSettings.LastGraph`; `Main.DemoGraph` = `res://samples/demo.valence.json` | `user://viewer.json` |
| File ▸ Open Source Folder… / Re-analyze / Open Recent | `Hud.PopupOpenSource` (native `FileDialog`), `Main.AnalyzeSourceFolder`, `Main.Reanalyze`, `Main.OpenRecent`; worker thread via `Task.Run` → `AnalyzerLauncher.Analyze`; results marshalled through `Main._mainThread` (a `ConcurrentQueue<Action>` drained in `_Process`) | the folder (read-only), `~/.nuget/packages` |
| Progress dialog with Cancel | `ProgressOverlay` (`Begin`, `SetProgress`, `AppendLog`, `Finish`, `Fail`); `AnalysisContext` (`Report`, `Slice`, throttled to ~10/s) ; cancellation via `CancellationTokenSource` in `Main._analysis` | |
| File ▸ Open Graph… | `Hud.PopupOpenGraph` (defaults to `~/.valence/graphs`), `Main.LoadGraph` → `GraphIO.Load` (`Storage.cs`), `GraphModel.Build`, `Layout.Run`, scene (re)build | `*.valence.json` |
| Title bar pills (repo, branch, commit) | `Hud.SetRepoInfo`, `UiKit.Pill` | `Graph.Repo` |

### The scene

| Feature | Implemented by | Reads |
|---|---|---|
| Molecules / atoms / nucleons / bonds | `GraphModel.Build` (model), `Layout.Run` (`PackNucleus`, `SolveHorizontal`, `LayerHeight`), `DependencyLayers.Compute` (Tarjan SCC → layer + `InCycle`), `AtomView` (cloud, orbits, electrons, name label), `NucleonRenderer` (one `MultiMesh`, color = rgb, glow = alpha), `BondRenderer` (one cylinder per bond; `Style` sets `bond_color`, `highlight`, `two_way`, `flow_sign`), shaders in `valence/Shaders/` | `Graph.Nodes`, `Graph.Edges`, `Graph.Modules`, `loc` metric |
| Project hue, labels | `Palette.MoleculeHue` (FNV-1a `Palette.Hash` → HSV); molecule `Label3D` in `Main.LoadGraph` / `PlaceMoleculeLabel`; atom label range `Main._atomLabelRange` (70, or 35 above 40 systems) | |
| Opening a system (expanded layout, ports) | `Main.FocusAtom` → `BeginOpening` → `Layout.Expand` (rings per internal layer) → `FocusView.Open` (ports, one per bond; `PortData`), animated in `Main._Process` → `ApplyOpenAmount`; `Main.UpdateLines` → `DetailLines` for class links | `BondData.WeightFrom`, `NodeData.Outgoing/Incoming` |
| Selection, lit set, dimming | `Main.SelectNode`, `Main.ClearSelection`, `Main.Back`, `Main.SelectionChanged` → `RefreshSelection` (BFS to `_linkDepth` over `_enabledKinds`, fills `_lit`), `AtomView.SetState`, `BondRenderer.SetFocus` | `GraphModel.Neighbors` |
| Hover tooltips | `Main.UpdateHover` / `TooltipText` / `BoundaryTooltip`, `Hud.ShowTooltip`; picking in `Main.PickAtom/PickNode/PickPort/RaySphere` | |
| Labels (All / Near Mouse / On Click) | `Main.ApplyLabels`, `Main.SetLabelMode`, `LabelLayer` (`SetOpened`, `SetPinned`, `SetMode`, `SetSimulation`, `Reveal`), `LabelProximity.Factor`, `AtomView.SetLabelPinned`, `FocusView.SetPinnedAtoms` | `ViewerSettings.Labels` |
| Camera (orbit, pan, zoom, fly, frame) | `OrbitCamera`; `Main.FrameAll`; input in `Main._UnhandledInput` / `HandleMouseButton` / `HandleMouseMotion` / `HandleKey`; view tools `ViewTool` (Hud enum) | |
| Shift-drag a system, Reset Layout | `Main.MoveAtom`, `Main.PinAtom` → `LayoutStore.Save`; `Main.ResetLayout` | `user://layouts/<hash>.json` (X/Z only) |
| Axis gizmo | `AxisGizmo` (purely visual, `MouseFilter = Ignore`) | camera basis |
| Legend | `Hud.BuildLegend`, `Hud.SetLegend(mode, hasGit, churnDays, hasEffects, indirect, timelineEffects)`; `Main.UpdateLegend`; edge-kind checkboxes raise `Hud.EdgeKindToggled` → `Main._enabledKinds` | `Palette.EdgeKindOrder`, `Boundary.Kinds` |
| Brightness | `Main.ApplyBrightness` (glow, bloom, shader params, electron emission) | `ViewerSettings.Brightness` |
| Full screen | `Main.ApplyFullscreen` (`ExclusiveFullscreen` vs `Maximized`), `Main.IsFullscreen` | `ViewerSettings.Fullscreen` (default true) |

### Panels

| Feature | Implemented by | Reads |
|---|---|---|
| Outline (tree, filter, eyes, favorites, context menu) | `OutlinePanel` (`Rebuild`, `BuildFavorites`, `Reveal`, `ChooseFirstMatch`, `OnItemSelected`, `OnItemMouseSelected`, `OnContextMenu`); events `ItemChosen`, `ItemVisibilityChanged`, `ShowAllRequested`, `FavoriteToggled`; Main: `OnLink`, `SetVisibility`, `ShowEverything`, `ToggleFavorite` | `GraphModel`, `VisibilityStore`, `FavoritesStore` |
| Entry points / Methods panel | `MethodsPanel` (`ShowEntryPoints`, `ShowMembers`, `RenderChips`, `ShowSearch`, `PlayFirstResult`, `AddRow`, `OnRowMenu`); `Main.CanPlay` (member has out-links in `FlowGraph`); `Main.StartFromMember` | `Graph.EntryPoints`, `GraphModel.EntryPointsByMember`, `MemberInfo` |
| Inspector summary (cards, Top risks) | `Hud.ShowRepoSummary`, `Hud.BuildSummary`, `Hud.BuildTopRisks` / `FillTopRisks`, `Hud.OnInspectorMenu` (Copy Repo Path, Open Repo Folder via `OS.ShellOpen`) | `GraphModel.TopRisks`, counts |
| Inspector details | `InfoText.ForNode` (+ `AppendRisk`, `AppendInsights`, `AppendBoundary`, `AppendMembers`, `AppendEdges`), `InfoText.ForAtom`, `ForSimulation`, `ForComparison`, `ForBoundaryFrame`; BBCode `[url=node:<id>]` / `[url=atom:<id>]` handled by `Hud.LinkClicked` → `Main.OnLink` | `NodeData`, metrics, `Boundary` |
| Status bar | `Hud.SetStatus`, `Hud.SetSelection` | |
| Panel resizing / visibility | `PanelResizer`, `Hud.ResizePanel`, `Hud.TogglePanel`, `Hud.UpdateRightColumn`; `Hud.PanelWidthsChanged` / `PanelsChanged` → `ViewerSettings` | `OutlineWidth`, `RightWidth`, `Show*` |
| Dialogs (Controls, About, Insights, Snapshots) | `ModalDialog` (OK / Esc / Enter / click outside closes; swallows keys while open), `Hud.BuildDialogs`, `Hud.AddHelpGroup` | |
| User Guide (Help ▸ User Guide, F1) | `HelpDialog` (`BuildGuide`, `ShowGuide`, contents `ItemList`, `#anchor` / web link handling, search: `Search`, `Step`, `GoToMatch`, Esc/⌘F in `_Input`); `Markdown.Render(markdown, highlight)` turns the guide's Markdown subset into BBCode, records each heading's paragraph for `ScrollToParagraph` and each line's plain text as a `MarkdownBlock` for search, and highlights the search term outside BBCode tags; `Hud.Guide`, `Main.HandleKey` (F1) | `res://help/HELP.md` (shipped via the export presets' `include_filter`) |

### Lenses

All lenses are `ColorMode` values (`Palette.cs`), colored in `Main.NodeColor`, legend text in `Hud.SetLegend`, menu in `Hud.ColorModes`, toolbar tabs in `Hud.Lenses`.

| Lens | Color source | Computed by |
|---|---|---|
| Structure (`Visibility`) | `NodeData.IsPublic` → `Palette.Proton` / `Neutron` | probe `visibility` |
| Kind | `Palette.NodeKind(kind)` | probe `kind` |
| Changes (`Churn`) | `Palette.Heat(log(1+churn)/log(1+MaxChurn))` | `GraphBuilder.AddGitMetrics` (`churn` metric), `GraphModel.MaxChurn` |
| Dependencies (`Coupling`) | `Palette.Heat(sqrt(FanIn/MaxFanIn))` | `GraphModel.ConnectEdges` (distinct non-`contains` sources) |
| Risk | `Palette.Heat(node.Risk.Score)` | `RiskScores.Compute` (percentile ranks; `TopRisks`) |
| Reach | reached / used-by-unreached / unused | `Insights.ComputeReach` (`ReachByTrigger`, `Reached`) |
| Authors | `Palette.Heat(agentShare)`, grey when churn = 0 | `GitHistory.LoadChurn` (`IsAgent`, `AgentMarkers`) → `GraphBuilder.AddGitMetrics` |
| Trust | `Palette.Heat(InferredShare)`, grey when no links | `Insights.ComputeTrust` |
| Boundary | `Main.BoundaryColor` (direct kind color; indirect lerped toward `NoData`; `NoData.Darkened` when none) | `Boundary.Compute`; badges `EffectBadges` |

### Filters and visibility

| Feature | Implemented by | State |
|---|---|---|
| Hide / quiet / show (per item, inherited project → system → class) | `Main.ApplyVisibility` (`OpacityOf`, `Most`), `Main.StateOf`, `Main.SetVisibility`, `Main.ShowEverything`, `VisibilityStore` | `Main._visibility`, `Main._quietFade` |
| View ▸ Filter by Usage | `Hud.BuildFilterBar`, `RangeSlider`, `Hud.RaiseUsageFilter` → `Main.UsageFilterChanged` handler; `Main.PassesUsageFilter`, `UsageFactor`, `UpdateUsageFilterCount`; range reset in `Hud.SetUsageFilterRange` on load | `_usageFilterActive/_usageMin/_usageMax/_usageGhost` |
| Hide Unrelated | `Hud._hideUnrelated` checkbox (shown by `Hud.SetHasSelection`), `Main.RelatedToSelection` (BFS over `_enabledKinds`, `includeHidden: true`, skipping user-hidden) | `Main._hideUnrelated` |
| Only Show Differences | `Hud.OnMenu(ViewOnlyDifferences)` (kept in sync in View and Filters menus), `Main.ShowsAsDifference` | `Main._onlyDifferences`, `_comparison` |
| Spotlight (report's Show Only These) | `ReportDialog.SpotlightRequested` → Main handler sets `_spotlight`; `Hud.SpotlightCleared`; `Hud.SetSpotlightActive` enables Clear Spotlight | `Main._spotlight`, `_spotlightTitle` |
| Only Code That Reaches Outside | `Hud._boundaryMenu` (ids `BoundaryFilterOff/Any/Kind+i`), `Hud.BoundaryFilterChanged` → `Main.SetBoundaryFilter`, `Main.PassesBoundaryFilter`, `Main.BoundaryEffects` | `Main._boundaryFilter` (`EffectFlags`) |
| Count Indirect Effects | `Hud.FilterBoundaryIndirect` check item → `Main.BoundaryIndirectChanged` handler | `Main._boundaryIndirect`, `ViewerSettings.BoundaryIndirect` |
| Untouched during simulation | `Main.UntouchedFactor`, `Main.SetUntouchedMode` | `Main._untouched`, `ViewerSettings.Untouched` |
| Timeline presence | `Main._timelinePresent` in `ApplyVisibility` | frame `Nodes` keys |

### Simulation and flow chart

| Feature | Implemented by | Reads |
|---|---|---|
| Flow graph (member level, class-level fallback) | `FlowGraph.For(model)` (`FlowSimulator.cs`), cached in `Main._flowGraph` | `Graph.MemberEdges`, else `NodeData.Outgoing` |
| Plan (what pulses when) | `FlowSimulator.Build(graph, starts, direction, includeData, title)` → `FlowPlan` (`Events`, `Reached`, `Truncated`, `Duration`); constants `Travel`, `SiblingGap`, `StartStagger`, `MaxDepth` = 18, `MaxEvents` = 5000 | |
| Start points | `Main.StartFlow` (D/U; `FlowGraph.StartPointsFor`), `Main.StartTrigger` (game loop = frame+physics+draw, `loop: true`; Play Trigger), `Main.StartFromMember` (▶), all → `Main.BeginSimulation` | `Graph.EntryPoints` |
| Playback (pulses, trail, heat, gold flash) | `SimulationView` (`Play`, `Pause`, `Resume`, `Stop`, `_Process`, `RebuildTrace`, `PulsePosition`), events `NodeReached` → `Main.OnNodeReached` (heat, visits, flash, `RevealLabel`), `Finished` → `Main.OnSimulationFinished` (loop replays with `keepTrace: true`); colors in `Main.NodeColor` simulation branch; `Main._Process` decays heat and flash and calls `ApplyColors` every 0.05 s | `Palette.FlowKind` |
| Pause / resume / replay / stop / speed / include data | `Main.TogglePause`, `Main.StopSimulation`, `Hud.SetSimulationState` (renames Pause ↔ Resume), `SimulationView.Speed`, `Main._includeData` | |
| Flow chart model | `FlowChart.Build(plan, graph, model, group)` (boxes by hop/column and time/row, `GroupAbove` = 220; per-box `Effects` from `Boundary.MemberDirect` or `NodeData.DirectEffects`), `FlowChart.Curve`, `ToMermaid`, `ToSvg` | `FlowPlan.Events` |
| Flow chart drawing and input | `FlowChartView` (`_Draw`, `DrawArrow`, `DrawBox`, `SetPlayhead`, `FollowFlow`, `Fit`, `ZoomAt`, `BoxAt`, `_GetTooltip`); `Main.UpdateFlowPlayhead` each frame while playing | `EffectBadges.WhiteIcon` for kind icons |
| Drawer (Follow, Fit, Group by class, Export, ✕) | `FlowDrawer` (`ShowChart`, `OnExport`, `Save`, `SavePng` via off-screen `SubViewport`, `Slug`); `Main.ShowFlowChart`, `RebuildFlowChart`, `SetFlowChartShown`; drawer resizer in `Hud.BuildFlowSection` | `ViewerSettings.ShowFlowChart`; exports to `~/.valence/exports` |

### Insights

| Report | Implemented by | Reads |
|---|---|---|
| Report model, BBCode and Markdown | `Report`, `ReportSection`, `ReportCell`, `ReportParagraph/List/Table` (`Report.cs`); `Report.ToBbcode`, `Report.ToMarkdown` | |
| Window, Show Only These, Copy / Save Markdown | `ReportDialog` (`ShowReport`, `SaveMarkdown`, `Slug`); `Main.ShowInsight` dispatches on `InsightKind` | `Report.Spotlight` |
| Blast Radius (three variants) | `Insights.BlastRadius`, `EntryPointsReaching`, `RecentlyChanged`; Main picks the start set in `ShowInsight` / `SelectionNodes` / `ComparedChanges` | `Incoming` edges, member edges, `lastChanged` |
| Unreached Code | `Insights.UnreachedCode` | `GraphModel.Reached`, `FanIn` |
| Cycles | `Insights.Cycles` | `BondData.Covalent`, class edges |
| Coverage Matrix | `Insights.Coverage` | `ReachByTrigger` |
| Abstractions | `Insights.Abstractions`, `Insights.IsAbstract` | `implements`/`inherits` edges, `abstract` attribute, member `calls` fan-out |
| System Metrics | `Insights.SystemMetrics` | bonds, `IsAbstract` |
| What Reaches Outside | `Insights.BoundaryReport`, `KindsOf` | `Boundary.DirectCounts`, `MemberReaches`, `Boundary.DirectEffects` |
| Outside Effects Over Time | `BoundaryHistory.Report`, `Changes`, `Flags`, `Kinds`, `NameOf`; `Main.TimelineForReport` (loaded timeline or the saved file) | `Timeline.Frames[*].Effects` |
| Reading Guide | `Insights.ReadingGuide` | everything above |

### History

| Feature | Implemented by | Reads / writes |
|---|---|---|
| Build timeline | `Main.BuildTimeline` → `AnalyzerLauncher.BuildTimeline` → `TimelineBuilder.Build` (`GitHistory.FirstParentCommits`, `Sample`, `GitHistory.ExportTree` = `git archive` + `TarFile.ExtractToDirectory` into `%TEMP%/valence-timeline-*`, `RepoAnalyzer.Analyze` with `UseGit = false`), saved by `ValenceJson.Write` to `TimelineBuilder.DefaultPath` | `~/.valence/timelines/<name>-<hash>.timeline.json` |
| Show / scrub / play / hide | `Main.LoadTimeline`, `ShowTimelineFrame` (sets `_timelinePresent`, `_timelineNew`, label, calls `UpdateFrameEffects`, `RefreshBoundaryBadges`, `ApplyVisibility`, `ShowBoundaryFrameInfo`), `ToggleTimelinePlay` (0.7 s per frame in `_Process`), `HideTimeline`; bar in `Hud.BuildTimelineBar` | `Timeline` |
| Boundary on the timeline | `Main.UpdateFrameEffects` (`_frameEffects`, `_frameGained`, `_frameLost`, `_frameChanges`), `Main.DirectEffectsOf`, `BoundaryColor` frame branch, `NodeColor` glow override, `BoundaryTooltip`, `BoundaryFrameLabel`, `InfoText.ForBoundaryFrame`, `EffectBadges.SetFlags` | `TimelineFrame.Effects`, `Timeline.HasEffects` |
| Take snapshot / compare / stop / only differences | `Main.TakeSnapshot` → `SnapshotStore.Save`; `Main.StartCompare` → `Comparison.Build` (added / changed by `loc`, member count, outgoing edge set / removed); `Main.StopCompare`; colors in `NodeColor` compare branch; `InfoText.ForComparison`; `Hud.SetSnapshots` fills View ▸ Compare With Snapshot | `~/.valence/snapshots/<repo>-<hash>/` |
| Snapshots dialog (list, rename, delete, take) | `SnapshotsDialog` (`SetEntries`, `Row`, `NameEditor`, two-click delete), `Main.OpenSnapshots`, `RefreshSnapshots`, `RenameSnapshot`, `DeleteSnapshot`; `SnapshotStore.Entries/List/LabelFor/Rename/Delete` (`names.json`, `OS.MoveToTrash`) | |

---

## 2. Data flow end to end

### Analysis: folder → graph file

```
folder ──► IProbe.Run(root, AnalysisContext) ──► ProbeOutput (per language)
              CSharpProbe → CSharpAnalyzer.Run            PythonProbe → PythonAnalyzer.Run
                                   │
                                   ▼
        RepoAnalyzer.Analyze: pick relevant probes (IsRelevant), run each in a Slice of the progress
        bar, GitHistory.Open + LoadChurn (unless UseGit = false), then
                                   │
                                   ▼
        GraphBuilder.Build: TryAdd modules/nodes (duplicate ids → warning diagnostic, first wins);
        edges and memberEdges merged per (source, target, kind) with summed weight and strongest
        confidence; dangling edges / member edges / entry points dropped; AddFanMetrics (fanIn/fanOut
        over non-contains edges); AddGitMetrics (commits, churn, lastChanged, authors, topAuthorShare,
        agentShare, topAuthor attribute, from the node's Files); AggregateModuleEdges
                                   │
                                   ▼
        Graph (schema valence.graph/1) ──► ValenceJson.Write ──► ~/.valence/graphs/<name>-<hash>.valence.json
```

Entry points: viewer `AnalyzerLauncher.Analyze` (also sets `DOTNET_HOST_PATH` for external probes,
discovers `dist/probes/`), CLI `src/Valence.Cli/Program.cs` (`valence analyze`), standalone C# probe
`probes/csharp/Program.cs`. `RepoAnalyzer.Combine` puts built-ins first and skips an external probe
whose name matches a built-in one. External probes: `ProbeRegistry.Discover` (`probe.json`),
`ExternalProbe.Run` → `ProbeRunner.Run` (separate process, `--root`, `--out`, timeout).

### Viewer: graph file → scene

```
GraphIO.Load (Storage.cs; rejects schemas not starting with "valence.graph/")
   │
   ▼
GraphModel.Build:  modules → MoleculeData (root module) / AtomData (leaf module, from NodeInfo.Module)
                   nodes → NodeData (MemberOwners, EntryPointsByMember)
                   ConnectEdges → Outgoing/Incoming, FanIn/FanOut (distinct, non-contains), BondData
                   ComputeLayers (DependencyLayers over bonds) → Layer, InCycle, MaxLayer
                   MaxChurn, MaxFanIn
                   RiskScores.Compute        → NodeData.Risk, GraphModel.TopRisks
                   Insights.ComputeReach     → ReachByTrigger, Reached
                   Insights.ComputeTrust     → LinkCount, InferredShare
                   Boundary.Compute          → DirectEffects, Effects, DirectCounts, trace map
   │
   ▼
Main.LoadGraph:    LayoutStore.Load pins → Layout.Run (PackNucleus: radius 0.16 + 0.07·log10(1+loc),
                   Fibonacci packing; SolveHorizontal: force layout on X/Z, Y = LayerHeight)
                   builds World: AtomView per atom, NucleonRenderer, BondRenderer, DetailLines,
                   FocusView, LabelLayer, EffectBadges, SimulationView, molecule Label3Ds
                   Hud.SetTriggers, VisibilityStore/FavoritesStore.Load, Hud.SetSnapshots,
                   Outline.SetModel, UpdateLegend, ApplyVisibility, SetUsageFilterRange, FrameAll,
                   ShowSummary, SetRepoInfo, SetReanalyzeEnabled, saves LastGraph
```

Per-frame state flow after load (see section 4): `ApplyVisibility` → `RefreshSelection` →
`ApplyLabels` + `ApplyColors` + `UpdateLines`.

### Timeline

```
Timeline ▸ Build ──► Main.BuildTimeline ──► AnalyzerLauncher.BuildTimeline ──► TimelineBuilder.Build
   GitHistory.FirstParentCommits (oldest first) → Sample(n) → per commit: GitHistory.ExportTree
   (git archive → tar → temp dir) → RepoAnalyzer.Analyze (UseGit=false) → TimelineFrame
   { Commit, Date, Author, Subject, Nodes: id → loc, Effects: id → ["kind api", …] }
   ──► Timeline { Schema "valence.timeline/1", Root, GeneratedAt, HasEffects = true, Frames }
   ──► ~/.valence/timelines/<name>-<hash>.timeline.json ──► Main.LoadTimeline
```

A frame that fails to export or analyze is kept empty (so the sample count stays stable);
`BoundaryHistory.Changes` treats an empty neighbour as "unknown", not "everything deleted".

### Snapshots and compare

`SnapshotStore.Save` writes the current `Graph` unchanged to
`~/.valence/snapshots/<repoName>-<hash8>/<yyyy-MM-dd_HH-mm-ss>[_<commit8>].valence.json`.
`Comparison.Build(snapshot, currentModel, label)` indexes the snapshot's nodes and outgoing edges by
id and classifies each current node: not in snapshot → `Added`; `loc` differs, member count differs,
or the set of `"target|kind"` outgoing edges differs → `Changed`; snapshot nodes missing now →
`Removed` (as `NodeInfo`, they aren't in the scene).

---

## 3. The data model

### Schema (`src/Valence.Schema/Model.cs`)

Serialized with `ValenceJson` (`src/Valence.Schema/ValenceJson.cs`; camelCase, nulls omitted).

| Type | Fields | Notes |
|---|---|---|
| `SchemaIds` | `ProbeManifest` = `valence.probe-manifest/1`, `ProbeOutput` = `valence.probe/1`, `Graph` = `valence.graph/1` | bump only on breaking changes (AGENTS.md rule 6) |
| `ProbeOutput` | `Schema`, `Probe` (`ToolInfo`), `Modules`, `Nodes`, `Edges`, `MemberEdges`, `EntryPoints`, `Diagnostics` | what a probe emits |
| `Graph` | `ProbeOutput`'s lists plus `Generator`, `Repo` (`RepoInfo`), `Probes`, `ModuleEdges` | what the viewer loads |
| `RepoInfo` | `Root`, `Name`, `Commit?`, `Branch?`, `GeneratedAt`, `ChurnWindowDays?` | `ChurnWindowDays != null` ⇔ `GraphModel.HasGit` |
| `ModuleInfo` | `Id`, `Name`, `Kind` (`ModuleKinds`: project/namespace/folder; Python also `"package"`), `Parent?`, `Language`, `Path?` | leaf = atom, root = molecule |
| `NodeInfo` | `Id`, `Name`, `FullName`, `Kind` (`NodeKinds`), `Module`, `Visibility`, `Language`, `File`, `Line`, `Files`, `Container?`, `Members`, `Metrics`, `Attributes`, `Effects?` | `Effects` = top-level / class-body effects |
| `MemberInfo` | `Id`, `Name`, `Kind` (`MemberKinds`), `Visibility`, `IsStatic`, `Signature?`, `File`, `Line`, `Attributes` (`override`, `abstract`, `signal`), `Effects?` | |
| `EdgeInfo` | `Source`, `Target`, `Kind` (`EdgeKinds`), `Weight`, `Confidence?` | null confidence = certain |
| `MemberEdgeInfo` | `Source`, `Target`, `Kind` (`MemberEdgeKinds`), `Weight`, `Order`, `Line`, `Confidence?` | `Order` = position of first occurrence among the source's edges |
| `EntryPointInfo` | `Member`, `Trigger` (`Triggers`), `Label`, `Source` | |
| `EffectInfo` | `Kind` (`EffectKinds`), `Api`, `Line`, `Weight`, `Confidence?` | merged per (kind, api) by `EffectCatalog.Add`, max 32 per member |
| `ModuleEdgeInfo` | `Source`, `Target`, `Weight`, `Kinds` | aggregated between leaf modules; the viewer recomputes bonds itself from `Edges` |
| `DiagnosticInfo` | `Level`, `Message`, `Source?` | |
| `Confidence` | `Certain`, `Likely`, `Guess`; `Rank`, `Strongest` | |
| `MetricKeys` | `loc`, `methods`, `fields`, `properties`, `events`, `fanIn`, `fanOut`, `commits`, `churn`, `lastChanged`, `authors`, `topAuthorShare`, `agentShare` | probes set the first five; core sets the rest |
| `AttributeKeys` | `abstract`, `topAuthor`, `externalImports`; C# also writes `externalBase`, `externalInterfaces` (string literals, not constants); Python writes `bases` | |

The human-readable contract is `docs/plugin-protocol.md`; keep both in step.

### Viewer runtime model (`valence/Scripts/GraphModel.cs`)

| Class | Field | Set by |
|---|---|---|
| `NodeData` | `Info` (`NodeInfo`), `Atom` | `GraphModel.Build` |
| | `Index` (MultiMesh instance) | `NucleonRenderer.Build` |
| | `PackedOffset`, `ExpandedOffset`, `LocalOffset`, `Radius` | `Layout.PackNucleus`, `Layout.Expand`, `Main.ApplyOpenAmount` |
| | `FanIn`, `FanOut`, `Outgoing`, `Incoming` | `GraphModel.ConnectEdges` |
| | `Risk` | `RiskScores.Compute` |
| | `LinkCount`, `InferredShare` | `Insights.ComputeTrust` |
| | `DirectEffects`, `Effects` | `Boundary.Compute` |
| | `UserOpacity` (hide/quiet only), `Opacity` (everything), `IsHidden` (`Opacity <= 0`) | `Main.ApplyVisibility`, `Main.RevealLabel` |
| | `WorldPosition`, `IsPublic`, `Metric(key)` | derived |
| `AtomData` | `Id`, `Name`, `Molecule`, `Nodes`, `Neighbors`, `Valence` | `GraphModel.Build`, `ConnectEdges` |
| | `Position`, `CoreRadius`, `Radius`, `DisplayRadius`, `Pinned` | `Layout`, `Main.MoveAtom/PinAtom`, `ApplyOpenAmount` |
| | `Layer`, `InCycle` | `GraphModel.ComputeLayers` |
| | `View` (`AtomView`), `UserOpacity`, `Opacity`, `IsHidden`, `ShortName` | |
| `MoleculeData` | `Id`, `Name`, `Atoms`, `Label` (`Label3D`), `Opacity` | |
| `BondData` | `A`, `B` (ordered by id), `WeightAB`, `WeightBA`, `Kinds`, `Mesh`; `Covalent`, `Weight`, `Other`, `WeightFrom` | `ConnectEdges` (skips `contains` and same-atom edges) |
| `GraphModel` | `Graph`, `Nodes`, `Atoms`, `AtomList`, `Molecules`, `Bonds`, `MemberOwners`, `EntryPointsByMember`, `MaxLayer`, `MaxChurn`, `MaxFanIn`, `TopRisks`, `ReachByTrigger`, `Reached`, `Boundary`, `HasGit`, `SnapshotTime` | `Build` |

`Boundary` (`Boundary.cs`) keeps per-member maps: `_direct` (own effects), `_reaches` (own or through
calls), `_next[(member, kind)]` (the next hop toward the effect, for `Trace`), `_members`;
`DirectCounts` per kind; `Any`. `Kinds` is the ordered table of `EffectKind(Flag, Key, Label, Icon,
Color, Tag)`; `AllKinds` = 255.

---

## 4. Viewer internals

### Node color (`Main.NodeColor`)

Returns a `Color` whose rgb is the lens color and whose **alpha is the glow** (the nucleon shader
reads alpha as emission). `ApplyColors` passes it through `Faded(color, node.Opacity)` so quiet items
blend toward `Palette.Background` and lose glow, then `NucleonRenderer.ApplyColors` writes every
instance color. Order of decisions:

1. Base color by `_colorMode` (table in section 1; `NoData` grey when a lens has nothing to say).
2. `glow = RecencyGlow(node, model)`: 1 when `lastChanged` is at `SnapshotTime`, fading to 0 over
   `RecencyWindowDays` = 14; 0 without git.
3. Boundary lens on a timeline frame (`_frameEffects != null`): glow is replaced by 1 for
   `_frameGained`, 0.7 for `_frameLost`, else 0.
4. **Simulation active**: anchor → white; flashing (`_flash`, 2 s, fades over the last 0.35 s) →
   lerp to `Palette.Gold`; heated (`_heat`) → glow from heat; untouched → darkened 75 %, glow × 0.1.
   Returns here.
5. **Comparing** (and not the anchor): added → `Palette.Added` α 0.8; changed → `Palette.Changed`
   α 0.5; else darkened 60 %, no glow. Returns here.
6. **Timeline** (not a Boundary frame): new in this frame → `Palette.Added`, glow 1.
7. **Selection**: anchor → white; not in `_lit` → darkened 70 %, glow × 0.15. Else, with a system
   open, classes of systems not bonded to it → darkened 65 %, glow × 0.2.

`ApplyColors` runs on every `RefreshSelection`, and every 0.05 s while a simulation is active
(`_colorCooldown` in `_Process`).

### Visibility (`Main.ApplyVisibility`)

For every molecule → atom → node, opacity is a product of independent factors; anything at 0 is
hidden (collapsed MultiMesh instance, no bond, no label, no badge, no pulse):

| Factor | Node | Atom |
|---|---|---|
| User hide/quiet | `OpacityOf(Most(atomState, node state))` → `UserOpacity` | `OpacityOf(Most(moleculeState, atomState))` → `UserOpacity` |
| Untouched during simulation | `UntouchedFactor(_heat.ContainsKey(node))` | `UntouchedFactor(_revealedAtoms.Contains(atom))` |
| Timeline frame | present in `_timelinePresent` | any node present |
| Usage filter | `UsageFactor(PassesUsageFilter(node))` (0 or quiet fade) | any node passes |
| Only Show Differences | `ShowsAsDifference(node)` | any node differs |
| Hide Unrelated | `related.Contains(node)` | any node related |
| Spotlight | `_spotlight.Contains(node)` | any node in spotlight |
| Boundary filter | `PassesBoundaryFilter(node)` | any node passes |

Then: a selected node the user hid is dropped, a hidden focused atom clears the selection;
`NucleonRenderer.UpdateAll`, `BondRenderer.UpdateAll`, `EffectBadges.Refresh`,
`FocusView.RefreshVisibility`, `SimulationView.RefreshTrace`, `Outline.RefreshStates`, then
`RefreshSelection`. Finally the status bar reports hidden/quiet counts (only when any). Any state
change that affects one of the factors must call `ApplyVisibility`, not just `ApplyColors`.

`RevealLabel` (called when a pulse lands) un-ghosts a node and its atom in place without a full
`ApplyVisibility`, so Untouched ▸ Hide can reveal the path as it is reached.

### Selection, labels, badges

- `_focusAtom` = opened system, `_selectedNode` = selected class, `_anchor` = selected node or (with
  nothing selected) the hovered node inside the opened system. `_lit` = anchor plus neighbours up to
  `_linkDepth` hops (hover: 1 hop) over `_enabledKinds`, skipping hidden nodes.
- `Back()` order: deselect class → close system → stop simulation → clear selection.
- `ApplyLabels`: during simulation only revealed nodes (via `LabelLayer.Reveal`) and revealed atoms are
  labeled; otherwise pinned = selection + neighbours (or just the hovered node), with `LabelMode`
  deciding the rest (`LabelLayer.Apply`). Atom names: `AtomView.SetLabelPinned`; port labels:
  `FocusView.SetPinnedAtoms`. `LabelProximity.Factor` fades unpinned labels by distance from the
  pointer (radius 220 px) every frame.
- `EffectBadges`: one `Sprite3D` per kind (max 3) under each node with direct effects, shown only
  while `_colorMode == Boundary` (`SetShown`); `SetFlags(flagsOf)` swaps them for a timeline frame's
  effects; `Refresh` applies visibility and opacity; `UpdatePositions` after moves/opening.
- `UpdateLines`: inside the opened system, draws class→class links (same atom, or direct to a lit
  node in another atom when it touches the selection) and class→port links, capped at
  `MaxFocusLines` = 900, alpha by lit state and end opacities.

### Input (`Main._UnhandledInput`)

Mouse buttons: left press records `_pressPosition` and (Shift + Select tool) the atom to drag; left
release with no drag → `Click(position)` (port → `FocusAtom(port.Other)`; node in the open atom →
`SelectNode`; atom → open, or deselect if it's the open one; else `ClearSelection`); double-click on
nothing → clear + `FrameAll`; wheel → `OrbitCamera.Zoom`. Motion: left-drag orbits/pans/zooms by
`_tool` or moves the dragged atom (`RayPlane` at its height); right/middle drag pans; otherwise
`UpdateHover`. Pan and magnify gestures map to orbit/pan and zoom. Keys in `HandleKey`: ⌘⇧O, ⌘O, ⌘R,
F11 / ⌃⌘F, ⌘F, F, D, U, G, Space, L, H, Esc. Menu shortcuts shown by `Hud.SetShortcut` are display
only (`SetItemShortcut(..., global: false)`); the keys are handled here. `ModalDialog._Input`
swallows keys while a dialog is open. UI panels call `Hud.PointerEnteredUi` → `Main.ClearHover`.

### Settings and per-repo state (`Storage.cs`)

| Store | Path | Content |
|---|---|---|
| `ViewerSettings` | `user://viewer.json` | `LastGraph`, `RecentFolders` (max 10), `ShowOutline/Methods/Inspector/Legend`, `QuietFade`, `Untouched`, `Labels`, `Brightness`, `OutlineWidth`, `RightWidth`, `ShowFlowChart`, `Fullscreen`, `BoundaryIndirect` |
| `LayoutStore` | `user://layouts/<sha1-16>.json` | atom id → [x, z] |
| `VisibilityStore` | `user://visibility/<sha1-16>.json` | key → `quiet` / `hidden` |
| `FavoritesStore` | `user://favorites/<sha1-16>.json` | sorted keys |
| `SnapshotStore` | `~/.valence/snapshots/<name>-<sha1-8>/` | graphs + `names.json` |
| `TimelineBuilder.DefaultPath` | `~/.valence/timelines/<name>-<sha1-8>.timeline.json` | |
| `RepoAnalyzer.DefaultOutputPath` | `~/.valence/graphs/<name>-<sha1-8>.valence.json` | |

Keys are `"molecule:" + id`, `"atom:" + id`, `"node:" + id` everywhere (Outline metadata, visibility,
favorites, inspector links). Every `Save` is called immediately after the change (no dirty flag).

### How Hud builds menus

- All UI is built in `Hud.Build` (menu bar, toolbar, viewport frame with legend/gizmo/tools, flow
  section, filter bar, timeline bar, right column, status bar, tooltip, dialogs). Styling comes from
  `ValenceTheme.Build` (set on the Hud root), `UiAssets.Box(Patch.…)` nine-patches and `UiKit`
  helpers (`IconButton`, `TextButton`, `IconMenu`, `Title`, `Header`, `Badge`, `SearchField`,
  `Pill`, `Card`, `Gutter`, `Strip`).
- Menu items have integer ids: constants at the top of `Hud` (`FileOpenSource` = 1 …
  `ViewFullscreen` = 30, `FilterClearSpotlight` = 31, `FilterBoundaryIndirect` = 32, `NavigateFind`
  = 40 …, `HelpControls` = 60, `SimDownstream` = 80 …, `TriggerBase` = 100 + index, `TimelineBuild10`
  = 110 …, `InspectorSummary` = 300 …, `ToolZoomIn` = 310, `InsightBase` = 400 + `(int)InsightKind`,
  `BoundaryFilterOff` = 500, `BoundaryFilterAny` = 501, `BoundaryFilterKind` = 502 + index into
  `Boundary.Kinds`, `RecentBase` = 1000 + index, `CompareBase` = 2000 + index, `CompareManage` =
  1998). Radio submenus (Color By, Link Depth, Quiet Fade, Labels, Brightness, Speed, Untouched)
  use the enum value or array index as id and their own `IdPressed` lambdas.
- Menus with ids in the shared ranges route through `Hud.OnMenu(int id)` (one `switch`), which
  raises a typed event (`SourceFolderChosen`, `ReanalyzeRequested`, `FlowRequested`,
  `TimelineBuildRequested(int)`, …). `Main.WireHud` subscribes to every event once. Submenus with
  their own handler: `OnCompare`, `OnTrigger`, `OnRecent`, `OnInspectorMenu`, the `_boundaryMenu`
  lambda, the view-corner popup lambda.
- Checked / disabled state is pushed back by Main through `Hud.Set*` methods (`SetColorMode`,
  `SetLinkDepth`, `SetSpeed`, `SetSimulationState`, `SetCompareActive`, `SetSpotlightActive`,
  `SetBoundaryFilter`, `SetBoundaryIndirect`, `SetFlowChart`, `SetUsageFilterVisible`, …) using
  `SetChecked` / `SetDisabled` / `IsChecked` helpers keyed by id. Items that appear in two menus
  (Only Show Differences, Filter by Usage, Show Everything) are synced in `OnMenu` /
  `SetUsageFilterVisible` / `SetCompareActive`.

---

## 5. Probe internals

### C# (`probes/csharp/`)

- **Discovery**: `RepoScanner.Files(root, ".cs")` (skips hidden, `bin`, `obj`, `node_modules`,
  `packages`, `dist`, `build`, `artifacts`, symlinks). `ProjectDiscovery.Discover` reads each
  `.csproj` as XML: `ProjectReference`, `ImplicitUsings`, `DefineConstants`, `Godot.NET.Sdk/<v>` →
  GodotSharp from the NuGet cache + `GODOT`, `TOOLS`; `PackageReference` → `lib/<tfm>/*.dll` from
  `~/.nuget/packages` (or `NUGET_PACKAGES`). `AssignFiles` gives each file to the deepest project
  folder; leftovers go to a synthetic `(loose files)` project (`cs:project:(loose)`).
- **Compilations** (`CSharpAnalyzer.BuildCompilations`): one `CSharpCompilation` per project, in
  reference order, with `PlatformReferences` (TPA `System*`, `mscorlib`, `netstandard`), package
  DLLs, and referenced projects' compilations; implicit usings are synthesized as an in-memory tree
  named `<implicit-usings>.cs` (not under root, so skipped later). No MSBuild, no restore.
- **Pass 1** `DeclareTypes`: for every type declaration → `NodeInfo` with `IdOf` = `"cs:" +`
  fully-qualified original definition (`IdFormat`), `KindOf`, `VisibilityOf`,
  `EnsureNamespaceModule` (module id `<projectModuleId>#<namespace>`, `(global)` for none),
  `abstract` attribute, `CountMembers`, `OwnLines` per declaration (partials add up; nested types
  subtract), `Files`. `DeclareMembers` (Members.cs) adds a `MemberInfo` per non-implicit member:
  `MemberIdOf` = `"cs:" + ToDisplayString(MemberIdFormat)` on the original definition (reduced
  extension methods, partial definition part), `Signature` via `SignatureFormat`, `override` /
  `abstract` attributes; `DetectEntryPoint` marks Godot callbacks (`GodotCallbacks` map, overrides
  only) and `static Main`; Godot `[Signal] delegate XEventHandler` nested types become synthetic
  event members `<typeId>.X` with `signal = true` (`_signals`).
- `BuildImplementationMap`: every override chain and interface implementation → `_implementations`
  (base member id → implementing member ids), used to fan calls out as `likely`.
- **Pass 2** `CollectEdges`: per type, `AddStructuralEdges` once (`inherits`, `implements`,
  `contains`; external base/interfaces become attributes), then `Walk` each member's syntax with the
  owning member id (`OwnerOf`): `VisitTypeLevel` emits type edges (`creates` for object creation;
  `has` for member-declared types, `references` otherwise; `calls` for methods; `uses` for
  fields/properties/events; confidence `Likely` when only a candidate symbol resolved).
  `VisitMemberLevel` emits member edges: invocations (`HandleInvocation`: `calls` + implementation
  fan-out, delegate invoke → `raises`, `Connect` / `EmitSignal*` wiring by name), object creation
  (`creates`), `+=` subscriptions (`HandleSubscription` → `triggers`, or an external-event entry
  point via `AddExternalEventEntry`), names (`HandleName`: `reads` / `writes` by `IsWrite`; method
  groups as `likely` calls). `AddMemberEdge` assigns `Order` per source at first occurrence.
- **Effects** (`CSharpAnalyzer.Effects.cs`): calls and `new` on non-repo methods go to
  `RecordCallEffect` → `EffectCatalog.DotNet(TypeKey(type), member or "new", ModeText(args))`;
  unresolved receivers/types use `DotNetByShortName` with `Likely`; `[DllImport]` /
  `[LibraryImport]` repo methods record `native` on the caller; `StreamReader/Writer` only count
  with a string path (`NeedsPathArgument`). Recorded on the owning member via `EffectCatalog.Add`.

### Python (`probes/python/`)

- **Runtime**: `TreeSitterRuntime.Load("Python")` (`src/Valence.Syntax`) finds
  `libtree-sitter-python` under `runtimes/<rid>/native` next to the bindings or the app, and resolves
  the core `tree-sitter` library through a `DllImportResolver`.
- **Files and naming** (`PythonAnalyzer.Run`, `PythonProject`): `RepoFiles.Find` with extra skips
  (`__pycache__`, `venv`, `site-packages`, `node_modules`), files over 5 MB skipped. `ModuleName`
  walks up unbroken `__init__.py` chains to get the import name; `RepoPath` is the dotted path from
  the root (for relative imports and fallbacks). `AtomFor(dir)` = one atom per folder
  (`py:package:<dir>`, kind `package` or `folder`), molecule = top-level folder (`py:project:<top>`).
  `FileNode` creates the file's own `module` nucleon (`py:<module>`) on demand (free functions,
  scripts, `__main__`), never for class-only files or packages.
- **Pass 1** `PythonExtractor.Extract` (one walk, trees freed): `Class` → `NodeInfo`
  (`py:<fullName>`, `UniqueId` adds `#n` on clashes; `bases` attribute; `abstract` for ABC /
  ABCMeta / Protocol / `@abstractmethod`; nested → `contains`), `Function` → `MemberInfo`
  (`MemberId` = `owner.id + "." + name`, `#line` on clashes; kind constructor / property / method /
  function; entry points from decorators (`EntryPoint`: route, task, command, receiver, startup
  hook) and engine callbacks (`EngineCallbacks`: arcade/pyglet windows, sprites, Pygame Zero)),
  `MainBlock` (`__main__` member + `startup` entry), `GameLoop` (a `while` pumping pygame events →
  `frame` entry member sharing the enclosing locals, plus an `Enter` use), Django management
  commands by path. It records `Imports` / `Wildcards` (`ImportBinding`, relative imports as repo
  paths), `Binding`s for parameters, locals, class fields (`FieldMember` creates field members),
  globals (type hint via `PrimaryType`, value via `RefOf`), and every `Use(kind, Ref, Scope, line,
  …)` in evaluation order (`Call`, `Mention`, `Inherit`, `Annotation`, `FieldType`, `Assigned`,
  `Write`, `Enter`), plus `Wiring` for `path()/re_path()/url()`, `router.register`, `.connect`.
  `Call` keeps builtins only when they have effects (`EffectBuiltins`) and captures open modes
  (`ModeOf`).
- **Pass 2** `PythonResolver.Run`: `Apply(use)` evaluates the chain (`Eval` → `LookupName` →
  `LookupInFile` → `ResolveImport` / `LookupInModule`, `Member` on `ModuleValue` / `ClassValue` /
  `InstanceValue` / `FieldValue` / `SuperValue`, `Call` turns a class into an instance and a hinted
  function into its return type; `Bases` ≈ MRO; cycles guarded by pre-seeding caches with `None`),
  then maps (use kind, value) to a type edge and a member edge (table in `Apply`), fans instance
  method calls out to subclass overrides (`Overrides`, `likely`), and wires framework entries
  (`Wire`, `HandlerEntries`). **Confidence**: `Resolved.Inferred` is set by bindings (`EvalBinding`),
  approximate module matches (`PythonProject.FindModule` by name → repo path → suffix) and wildcard
  imports; inferred → `Confidence.Likely`, else certain (null).
- **Effects**: `RecordEffect(use, lookup, resolved)`: when the chain resolves to nothing in the
  repo, `External(ref, scope, …)` qualifies it to a dotted path outside the repo (`ExternalName`
  through imports / `EffectBuiltins`, `ExternalField` through `self.x` assignments,
  `ExternalBindings` through locals/globals/type hints, appending `()` for calls; `inferred` when it
  passed through a binding) and `EffectCatalog.Python(api, mode)` (calls) or `PythonValue(api)`
  (reads, e.g. `os.environ`) classifies it. Django ORM chains (`IsQuerySetCall`) count as
  `database` (likely). Recorded on the enclosing function's member, else on the scope's owner node
  (class body / file top level). `MarkExternalImports` writes `externalImports`.

---

## 6. Extension recipes

Follow the style rules in AGENTS.md section 7 (file-scoped namespaces, `<summary>` on types, comments
that say why, `const` for tunables) and the pitfalls in section 8.

### Add a lens (`ColorMode`)

1. `valence/Scripts/Palette.cs`: add a value to `ColorMode` with a `<summary>` saying what "hot"
   means.
2. Compute the data at load: a new method in `Insights.cs` / `RiskScores.cs` / a new static class,
   called from `GraphModel.Build` (after `ConnectEdges`); store the result on `NodeData` (new
   property) or `GraphModel`.
3. `Main.NodeColor`: add a `case` to the lens `switch` (use `Palette.Heat` for scalars, `NoData`
   when the lens has nothing to say).
4. `Hud.ColorModes`: add `(ColorMode.X, "Label (what it means)")` so View ▸ Color By lists it (the
   enum value is the menu id). Optionally add it to `Hud.Lenses` for a toolbar tab (needs an icon,
   see below).
5. `Hud.SetLegend`: add a `case` with the legend text.
6. `Main.TooltipText`: add a line for the hover tooltip; `InfoText.AppendInsights` (or a new
   `Append*`) for the Inspector.
7. If it depends on something that may be absent (git, entry points, effects), fall back to
   `NoData` and say so in the legend, as Churn/Reach/Boundary do.
8. If the lens has side state (badges, a filter), mirror `Boundary`: toggle it in `Main.SetColorMode`.

### Add a filter

1. State in `Main` (a field, plus `ViewerSettings` if it should persist).
2. A predicate `Passes…(NodeData)` next to `PassesUsageFilter`.
3. `Main.ApplyVisibility`: multiply it into the node opacity and into the atom's "any node passes"
   term, in both places.
4. UI: an item in `Hud.BuildToolbar`'s `_filtersMenu` (new id constant, handled in `Hud.OnMenu`,
   raising a new event) or a bar like `BuildFilterBar`; a `Hud.Set…` to reflect state; subscribe in
   `Main.WireHud`; call `ApplyVisibility()` and set a status message.
5. Clear it in `Main.LoadGraph` (every filter is reset there) and make sure "Some classes vanished"
   in the user guide stays true.

### Add an Insights report

1. `Insights.cs`: add a value to `InsightKind` **at the end** (menu ids are `InsightBase + value`;
   reordering changes nothing functionally but keep it tidy).
2. Write `public static Report X(GraphModel model)` returning a `Report(title, summary)` with
   `Section(...).AddParagraph/AddList/AddTable`; use `ReportCell(text, node)` for clickable classes,
   `Color`/`Background` for emphasis; set `report.Spotlight` to the classes "Show Only These" should
   keep; handle the empty case with a "How to get data" section like `UnreachedCode`. Cap rows with
   `MaxRows`.
3. `Main.ShowInsight`: add the `case`.
4. `Hud.BuildMenuBar`: `insights.AddItem("Label", InsightBase + (int)InsightKind.X)` (or into the
   `blast` submenu).
5. Markdown export comes for free through `Report.ToMarkdown`.

### Add a menu item

1. `Hud`: a new `private const int` id in an unused range (check the ranges listed in section 4).
2. `Hud.BuildMenuBar` (or `BuildToolbar` / `BuildViewport`): `menu.AddItem("Label", Id)` /
   `AddCheckItem` / `AddRadioCheckItem`; `SetShortcut(menu, Id, Key.X, shift:, command:)` for a
   displayed shortcut.
3. `Hud.OnMenu`: a `case Id:` raising a new `public event Action…`.
4. `Main.WireHud`: subscribe. If the key should work globally, also add it to `Main.HandleKey` and to
   the Controls dialog (`Hud.BuildDialogs` → `AddHelpGroup`) and the user guide's key table.
5. If the item has state, add `Hud.Set…` using `SetChecked`/`SetDisabled`, called from Main whenever
   the state changes (including `LoadGraph`).

### Add an inspector section

1. `InfoText.cs`: a `private static void AppendX(StringBuilder sb, NodeData node, GraphModel model)`
   using BBCode; escape text with `Esc` (`[` → `[lb]`); link with `NodeLink` / `AtomLink` /
   `[url=node:<id>]`; mute with `[color={Muted}]`.
2. Call it from `ForNode` (or `ForAtom`) in the position that reads best.
3. For a new kind of link target, extend `Main.OnLink` (today: `node:` and `atom:`).

### Add effect catalog rules or a new effect kind

- **Rules only** (`src/Valence.Core/EffectCatalog.cs`): .NET → `Type("Ns.Type", ("Member", Kind),
  ("*", Kind))` or `Namespace("Ns", Kind)`; constructors are `"new"`; `FileOpen` resolves to read or
  write by the mode text; a `null` kind means "nothing outside" and must come before a broader
  `"*"`. Python → `Add(PythonRules, "pkg.path.**", Kind)`; value reads → `PythonValueRules`. First
  match wins, so order specific before broad.
- **New kind**: `Valence.Schema.EffectKinds` (constant + `All`), `docs/plugin-protocol.md` table,
  `Boundary.EffectFlags` (next bit) and `Boundary.AllKinds` (update the mask), a new `EffectKind`
  row in `Boundary.Kinds` **in priority order** (position decides the class color and the filter
  menu order; `Hud.SetBoundaryFilter` and the menu ids index this array), an icon on the UI sheet
  (below), `Hud.SetLegend` picks it up automatically, `BoundaryHistory` parses `"kind api"` strings
  by `Boundary.FlagOf(kind)` so old timelines simply lack it. `Insights.BoundaryReport` and
  `FlowChart` exports iterate `Boundary.In(...)`, no change needed.

### Add a metric or attribute

1. `Valence.Schema.MetricKeys` / `AttributeKeys`: a constant with a `<summary>`; document it in
   `docs/plugin-protocol.md` (Nodes section).
2. Set it: in a probe (`NodeInfo.Metrics[...]` in `DeclareTypes` / `PythonExtractor.Class`), or in
   the core (`GraphBuilder.AddFanMetrics` / `AddGitMetrics` for cross-language data).
3. Read it in the viewer with `node.Metric(MetricKeys.X)` / `node.Info.Attributes.TryGetValue`;
   show it in `InfoText` and, if a lens uses it, follow the lens recipe. Metrics are additive, so
   old graphs load with the value missing (0): guard with `model.HasGit`-style checks.

### Add a timeline-frame field

1. `src/Valence.Core/Timeline.cs`: a property on `TimelineFrame` (with a default so old files
   deserialize); fill it in `TimelineBuilder.Build` from the per-commit `Graph`.
2. If old timelines must be told apart, add a `Timeline` flag like `HasEffects` and set it in
   `Build`; check it in the viewer the way `Main.BoundaryFrameLabel` and `BoundaryHistory.Report`
   do, with a "rebuild the timeline" message.
3. Read it in `Main.ShowTimelineFrame` (and `UpdateFrameEffects` if it feeds colors), the timeline
   label, `InfoText.ForBoundaryFrame` or a sibling, and any report.

### Add an icon to the UI sheet

1. `tools/build_ui_sheets.py`: append the name to `ICON_NAMES` and draw it (the script's drawing
   helpers, same light-cyan stroke with darker outline).
2. `valence/Scripts/UiAssets.cs`: append the **same name at the same position** to `IconNames`
   (icons are located by index on a 128 px grid, 8 per row).
3. Rebuild the sheet (see the script's docstring; the maintainer runs it) so
   `valence/images/ui/icons.png` gains the cell; Godot regenerates `icons.png.import`.
4. Use it with `UiAssets.Icon("name")` / `UiKit.IconButton("name", …)`. A missing name logs
   `[valence] missing UI icon` and draws blank.

### Add a schema field

- Additive, optional fields only: give the property a default (or make it nullable with
  `JsonIgnoreCondition.WhenWritingNull` where the file size matters), never bump
  `SchemaIds.Graph`. Probes may omit it; `GraphBuilder` passes `NodeInfo`/`MemberInfo` through
  unchanged, so a probe-written field reaches the viewer as is.
- A breaking change (renaming, changing meaning, removing) bumps the suffix (`/1` → `/2`) in
  `SchemaIds`, the protocol doc and the README, and needs the maintainer's sign-off (AGENTS.md
  rule 6). `GraphIO.Load` only checks the `valence.graph/` prefix today; a version bump should add a
  real check there.
- Update `docs/plugin-protocol.md` in the same change.

---

## 7. Invariants and gotchas

- **Read-only, always.** Nothing under the analyzed root is created or modified: no MSBuild, no
  restore, no `__pycache__`, `GIT_OPTIONAL_LOCKS=0`, `git archive` to temp. Output goes to
  `~/.valence/` or `user://`. Any new file write must land there.
- **Ids are global and language-prefixed** (`cs:`, `py:`). Node ids must be stable across runs: the
  timeline, snapshots/compare, visibility, favorites and layout pins all key on them. Python
  `UniqueId` appends `#2`, `#3` on clashes, which can shift if an earlier duplicate disappears.
- **GraphBuilder drops** edges to unknown nodes, self-edges, and member edges / entry points whose
  members aren't declared on some node. A new probe must list every member it links to.
- **Additive schema** (section 6). Old graphs lack members, effects, reach data: every consumer
  checks (`FlowGraph.MemberLevel`, `Boundary.Any`, `ReachByTrigger.Count`, `HasGit`,
  `Timeline.HasEffects`) and tells the user to re-analyze or rebuild.
- **Things that must stay in sync**: `ICON_NAMES` (`tools/build_ui_sheets.py`) ↔ `UiAssets.IconNames`
  (same order); `Boundary.Kinds` order ↔ `EffectFlags` bits ↔ `BoundaryFilterKind + i` menu ids ↔
  `Boundary.AllKinds` mask; `InsightKind` values ↔ `InsightBase + value` menu ids; `ColorMode`
  values ↔ View ▸ Color By ids ↔ `Hud.Lenses` tabs ↔ `Hud.SetLegend` cases ↔ `Main.NodeColor`
  cases; `Hud.OnMenu` ids ↔ events ↔ `Main.WireHud` subscriptions; `GodotCallbacks` in the C# probe
  ↔ `Triggers`; `valence/help/HELP.md` ↔ every user-facing change (it ships in the app; keep to the
  Markdown subset `Markdown.cs` renders, and keep heading anchors that the Contents links use);
  `help/*` and `samples/*` ↔ `include_filter` in `valence/export_presets.cfg` (non-resource files
  only ship if listed); the README's "Viewer controls" and `valence/help/HELP.md` ↔ the Controls dialog and
  `Main.HandleKey`; `UiAssets.PatchRects` ↔ the panel sheet layout printed by the build script;
  `AnalyzerLauncher.BuiltInProbes` ↔ `src/Valence.Cli/Program.cs` probe list.
- **Names that clash**: `Insights.BoundaryReport` is the report; `Boundary` is the lens model class
  (a method named `Boundary` on `Insights` would break `new Boundary()` lookups, see AGENTS.md §8).
  `InsightKind.Boundary` and `InsightKind.BoundaryHistory` are enum members, not types. `Hud` has
  both `ViewUsageFilter` (menu id) and `Main._usageFilterActive` (state); `Report` (model) vs
  `ReportDialog` (window); `FlowChart` (model) vs `FlowChartView` (control) vs `FlowDrawer`
  (panel); `Timeline`/`TimelineFrame` live in `Valence.Core` (the viewer uses
  `Valence.Core.TimelineFrame` fully qualified in `InfoText`). Don't name Godot node members
  `Show`, `Hide`, `Name`, `VisibilityChanged`.
- **Godot generated files**: new scripts get a `*.cs.uid`, new images an `*.import`, when the
  maintainer opens the project; stage them by name in a follow-up commit (AGENTS.md §4).
- **Threading**: analysis and timeline builds run on a worker; everything that touches the scene or
  Hud goes through `Main._mainThread.Enqueue`. `AnalysisContext.Log/Report` callbacks are invoked
  on the worker.
- **Performance-sensitive paths**: `Main.ApplyColors` runs for every node every 0.05 s during a
  simulation (keep `NodeColor` allocation-free and O(1)); `Main.ApplyVisibility` is O(nodes) and
  rebuilds Outline states, bonds and badges, so don't call it per frame; `RefreshSelection` BFS +
  `UpdateLines` run on every hover change (`MaxFocusLines` = 900 cap); `SimulationView._Process`
  touches every in-flight pulse (`MaxPulses` = 3000) and rebuilds the trail at most every 0.1 s;
  `Layout.SolveHorizontal` is O(n²) per iteration with iterations scaled down for big graphs;
  `FlowChartView._Draw` redraws every arrow and box on each frame while playing (labels are
  cached in `_shortLabels`). `NucleonRenderer` is one MultiMesh on purpose: don't add per-class
  nodes.
- **Opacity semantics**: `UserOpacity` is the user's hide/quiet only; `Opacity` is the final
  product. Pulses may target a node that Untouched ▸ Hide is hiding (it appears on arrival) but never
  one the user hid (`UserOpacity <= 0`). `IsHidden` means `Opacity <= 0`, so a ghosted node is not
  hidden.
- **Node color alpha is glow**, not transparency. Quiet fading is done by `Faded` (lerp toward
  background), not by alpha.
- **Layout pins store X/Z only**; Y is always `Layout.LayerHeight(layer)`. `DependencyLayers` is
  shared by the scene (systems) and `Layout.Expand` (classes inside a system).
- **Menu shortcuts are cosmetic**; the actual bindings live in `Main.HandleKey`. Adding one in one
  place only gives a label without a key, or a key without a label.
- **Esc order** (`Main.Back`): a running or finished simulation stops first; then selection →
  system → clear. Dialogs swallow Esc while open.
- **Shift-drag only works in the Select tool** (`_dragAtom` is only picked when
  `_tool == ViewTool.Select`).
- **Only Show Differences and Filter by Usage are listed in two menus** (View and Filters ▾); keep
  both checked states in sync through `Hud.OnMenu` / `SetUsageFilterVisible` / `SetCompareActive`.
- **`ValenceJson.Options` is shared** by schema, settings and timeline files; changing it changes
  every file format at once.
- **Timeline frames hold only direct effects** (`"kind api"` strings per node): the lens on a frame
  can't show indirect reach, and `Main.BoundaryEffects` returns direct effects there regardless of
  Count Indirect Effects.
- **`SnapshotTime`** is `Repo.GeneratedAt`, not "now": recency glow and "recent changes" are
  measured relative to when the graph was made, so a stale graph still shows its own recency.
- **Risk ranks are percentiles within the repo**, so adding or removing files changes every score.

---

## 8. Known limitations and open work

From `docs/ROADMAP.md` and the code:

- **Languages**: only C# and Python. Next: PHP (phase 0 = moving language-neutral pieces of
  `probes/python/` into `src/Valence.Syntax/`, see AGENTS.md §9), then Java, Ruby, Objective-C,
  Swift, Go, JavaScript/TypeScript, HTML. Python framework rules are still regexes in
  `PythonExtractor` (data-driven "framework packs" are planned with the next language).
- **Godot scene files** (`.tscn`: attached scripts, signal connections) aren't read, so wiring done
  in the editor is invisible; the C# probe only sees `Connect` / `+=` in code.
- **Method-level selection** in 3D is not there; the Methods panel's ▶ is the way to start from one
  method, and the flow chart is the only method-level view.
- **Static analysis limits**: reflection, DI containers, serialization, string-based lookups and
  `dynamic` are invisible (the Dynamic effect kind only marks where it happens). Unresolved C#
  references (packages not in the NuGet cache) degrade to short-name matching for effects and
  `likely` edges. Python resolution is best effort (no flow typing, first binding wins, `MaxDepth`
  guards); monkey-patching and `getattr` are missed.
- **Effect catalog coverage** is a fixed list; wrappers outside the repo are missed. Only direct
  effects are stored per timeline frame.
- **Timeline** samples first-parent commits touching the folder; merges from side branches appear
  only where they land on the first-parent line. Deleted classes can't be drawn (no layout).
  Timelines are rebuilt from scratch each time (no incremental update).
- **Compare** only looks at `loc`, member count and outgoing edge set; a body change that keeps the
  line count and links is "unchanged".
- **Flow simulation** caps: 5,000 events, 18 hops, 3,000 pulses, 3,000 trail lines; charts above
  220 methods group by class automatically. Loops (game loop) replay the same plan each tick rather
  than simulating state.
- **Scale**: layout is O(n²) per iteration; label proximity is recomputed every frame for every
  unpinned label; reports cap at 300 rows; the Outline rebuilds its whole tree on each filter
  keystroke.
- **Platforms**: developed and tested on macOS (Apple Silicon); Windows and Linux native tree-sitter
  binaries ship but are untried (AGENTS.md §3). Fullscreen is exclusive, and the menu bar is drawn
  in-window for that reason.
- **Schema versioning**: `GraphIO.Load` accepts any `valence.graph/*`; there is no migration path
  yet, only "re-analyze".
- **No tests** exist in the repository; verification is the maintainer building and running it with
  a real repo and reading the `[valence]` log (AGENTS.md §4).
