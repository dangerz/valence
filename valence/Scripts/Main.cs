using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Valence.Core;
using Valence.Schema;

namespace Valence.Viewer;

/// <summary>
/// Scene root. Loads a graph, builds the atoms/bonds/nucleons, and handles selection and input.
///
/// Hierarchy on screen: molecule (project) → atom (namespace / system) → nucleon (class).
/// Clicking an atom opens it: its classes spread out with labels, and ports on the rim lead
/// to the systems it's bonded with.
/// </summary>
public partial class Main : Node3D
{
    private const string DemoGraph = "res://samples/demo.valence.json";
    private const float RecencyWindowDays = 14f;
    private const int MaxFocusLines = 900;
    private const float OpenSpeed = 2.8f;

    private readonly HashSet<string> _enabledKinds = new(Palette.EdgeKindOrder);
    private readonly ConcurrentQueue<Action> _mainThread = new();
    private readonly HashSet<NodeData> _lit = new();

    private SceneAssets _assets = null!;
    private OrbitCamera _camera = null!;
    private Hud _hud = null!;
    private ViewerSettings _settings = null!;

    private Node3D? _world;
    private GraphModel? _model;
    private NucleonRenderer? _nucleons;
    private BondRenderer? _bonds;
    private DetailLines? _lines;
    private FocusView? _focusView;
    private LabelLayer? _labels;
    private SimulationView? _simulation;
    private FlowGraph? _flowGraph;
    private float _atomLabelRange = 70f;

    // Simulation state: heat per class (decays toward a floor so reached classes stay visible).
    private readonly Dictionary<NodeData, float> _heat = new();
    private readonly Dictionary<NodeData, int> _visits = new();
    private readonly HashSet<NodeData> _simStarts = new();
    private FlowPlan? _simPlan;
    private bool _simLoop;
    private bool _simFinished;
    private int _simTick;
    private bool _includeData;
    private float _colorCooldown;
    private readonly HashSet<AtomData> _revealedAtoms = new();

    // Gold flash: seconds left for each class a pulse just landed on.
    private const float FlashSeconds = 2f;
    private const float FlashFadeSeconds = 0.35f;
    private readonly Dictionary<NodeData, float> _flash = new();

    // Hide / quiet: per-item states (key "molecule:/atom:/node:" + id) and the quiet fade level.
    private Dictionary<string, Visibility> _visibility = new(StringComparer.Ordinal);
    private float _quietFade = 0.15f;
    private UntouchedMode _untouched = UntouchedMode.Show;
    private HashSet<string> _favorites = new(StringComparer.Ordinal);
    private Godot.Environment? _environment;
    private LabelMode _labelMode = LabelMode.NearMouse;

    // View ▸ Filter by Usage: show classes used by between min and max other classes.
    private bool _usageFilterActive;
    private int _usageMin;
    private int _usageMax = int.MaxValue;
    private bool _usageGhost;

    private bool PassesUsageFilter(NodeData node) =>
        !_usageFilterActive || (node.FanIn >= _usageMin && node.FanIn <= _usageMax);

    /// <summary>Opacity multiplier for classes outside the usage range: 0 (hide) or the quiet fade (ghost).</summary>
    private float UsageFactor(bool passes) => passes ? 1f : _usageGhost ? _quietFade : 0f;

    private void UpdateUsageFilterCount()
    {
        if (_model == null) return;
        var shown = _model.Nodes.Values.Count(PassesUsageFilter);
        _hud.SetUsageFilterCount(_usageFilterActive ? $"{shown:N0} of {_model.Nodes.Count:N0} classes" : "");
    }
    private float _brightness = 0.75f;

    // Snapshot comparison (View ▸ Compare With): added/changed classes vs a saved snapshot.
    // Only Show Differences hides everything else while comparing (the choice is kept between compares).
    private Comparison? _comparison;
    private string? _comparisonPath;
    private bool _onlyDifferences;

    // Git timeline: the frame being shown, which classes exist in it, and which are new since the previous frame.
    private Timeline? _timeline;
    private int _timelineIndex;
    private HashSet<string>? _timelinePresent;
    private readonly HashSet<string> _timelineNew = new(StringComparer.Ordinal);
    private bool _timelinePlaying;
    private float _timelineTimer;
    private const float TimelineStepSeconds = 0.7f;
    private Dictionary<string, Vector2> _pins = new();
    private ColorMode _colorMode = ColorMode.Visibility;
    private int _linkDepth = 1;

    // Selection: the focused (opened) system, and optionally a selected class inside it.
    private AtomData? _focusAtom;
    private NodeData? _selectedNode;

    /// <summary>The class whose links are lit: the selection, or the hovered class when nothing is selected.</summary>
    private NodeData? _anchor;

    // Hover.
    private NodeData? _hoverNode;
    private AtomData? _hoverAtom;
    private PortData? _hoverPort;

    // Opening animation.
    private AtomData? _opened;
    private float _openRadius;
    private float _openAmount;
    private float _openTarget;

    // Mouse.
    private bool _leftDown;
    private bool _dragged;

    // What a left-drag does (the tool buttons in the 3D view's corner).
    private ViewTool _tool = ViewTool.Select;

    // Hide Unrelated (toolbar, shown while something is selected): hide everything the selection
    // doesn't link to. Kept between selections; it does nothing without one.
    private bool _hideUnrelated;

    // The flow chart of the latest simulation (kept after it stops, for exporting), and the user's
    // Group by class choice (null = decide by size).
    private FlowPlan? _chartPlan;
    private FlowGraph? _chartGraph;
    private bool? _chartGrouping;
    private Vector2 _pressPosition;
    private AtomData? _dragAtom;
    private Vector3 _dragOffset;

    private CancellationTokenSource? _analysis;

    public override void _Ready()
    {
        _settings = ViewerSettings.Load();
        _assets = new SceneAssets();
        BuildEnvironment();

        _camera = new OrbitCamera();
        AddChild(_camera);
        _camera.MakeCurrent();

        _hud = new Hud();
        _hud.Build(_settings.ShowOutline, _settings.ShowMethods, _settings.ShowInspector, _settings.ShowLegend,
                   _settings.OutlineWidth, _settings.RightWidth);
        var canvas = new CanvasLayer();
        AddChild(canvas);
        canvas.AddChild(_hud);
        _hud.Gizmo.Camera = _camera;
        WireHud();
        ApplyFullscreen(_settings.Fullscreen, save: false);
        _hud.SetFlowChart(_settings.ShowFlowChart, false);
        _hud.SetRecent(_settings.RecentFolders);
        _quietFade = Mathf.Clamp(_settings.QuietFade, 0.02f, 0.9f);
        _hud.SetQuietFade(_quietFade);
        _untouched = _settings.Untouched;
        _hud.SetUntouchedMode(_untouched);
        ApplyBrightness(Mathf.Clamp(_settings.Brightness, 0.1f, 2f));
        _hud.SetBrightness(_brightness);
        _labelMode = _settings.Labels;
        _hud.SetLabelMode(_labelMode);

        var startup = StartupGraph();
        GD.Print($"[valence] viewer ready; loading {startup}");
        LoadGraph(startup);
    }

    public override void _Process(double delta)
    {
        while (_mainThread.TryDequeue(out var action)) action();

        if (_opened != null && !Mathf.IsEqualApprox(_openAmount, _openTarget))
        {
            _openAmount = Mathf.MoveToward(_openAmount, _openTarget, (float)delta * OpenSpeed);
            ApplyOpenAmount();
            if (_openAmount <= 0f && _openTarget <= 0f) FinishClosing();
        }

        if (_timelinePlaying && _timeline != null)
        {
            _timelineTimer += (float)delta;
            if (_timelineTimer >= TimelineStepSeconds)
            {
                _timelineTimer = 0f;
                if (_timelineIndex >= _timeline.Frames.Count - 1) ToggleTimelinePlay();
                else ShowTimelineFrame(_timelineIndex + 1, pause: false);
            }
        }

        if (_simulation is { Active: true })
        {
            UpdateFlowPlayhead();
            if (_simulation.Playing)
            {
                foreach (var node in _heat.Keys.ToList()) _heat[node] = Mathf.Max(0.12f, _heat[node] - (float)delta * 0.6f);
                foreach (var node in _flash.Keys.ToList())
                {
                    var left = _flash[node] - (float)delta;
                    if (left <= 0f) _flash.Remove(node);
                    else _flash[node] = left;
                }
            }
            _colorCooldown -= (float)delta;
            if (_colorCooldown <= 0f)
            {
                ApplyColors();
                _colorCooldown = 0.05f;
            }
        }
    }

    private void WireHud()
    {
        _hud.SourceFolderChosen += AnalyzeSourceFolder;
        _hud.GraphFileChosen += LoadGraph;
        _hud.RecentFolderChosen += OpenRecent;
        _hud.ReanalyzeRequested += Reanalyze;
        _hud.QuitRequested += () => GetTree().Quit();
        _hud.ToolChanged += tool => _tool = tool;
        _hud.FlowChartToggled += SetFlowChartShown;
        _hud.Flow.CloseRequested += () => SetFlowChartShown(false);
        _hud.Flow.GroupingChanged += group =>
        {
            _chartGrouping = group;
            RebuildFlowChart();
        };
        _hud.Flow.Status += message => _hud.SetStatus(message);
        _hud.Flow.View.BoxChosen += box =>
        {
            if (box.Owner != null) SelectNode(box.Owner);
        };
        _hud.HideUnrelatedChanged += on =>
        {
            _hideUnrelated = on;
            ApplyVisibility();
            _hud.SetStatus(on ? "Hiding everything unrelated to the selection" : "Showing everything again");
        };
        _hud.FullscreenToggled += () => ApplyFullscreen(!IsFullscreen(), save: true);
        _hud.ZoomStepRequested += factor => _camera.Zoom(factor);
        _hud.ColorModeChanged += mode =>
        {
            _colorMode = mode;
            _hud.SetColorMode(mode);
            UpdateLegend();
            ApplyColors();
        };
        _hud.LinkDepthChanged += depth =>
        {
            _linkDepth = depth;
            SelectionChanged();
        };
        _hud.FrameAllRequested += () => FrameAll(false);
        _hud.ResetLayoutRequested += ResetLayout;
        _hud.BackRequested += Back;
        _hud.ClearSelectionRequested += ClearSelection;
        _hud.EdgeKindToggled += (kind, on) =>
        {
            if (on) _enabledKinds.Add(kind);
            else _enabledKinds.Remove(kind);
            SelectionChanged();
        };
        _hud.LinkClicked += OnLink;
        _hud.PanelsChanged += (outline, methods, inspector, legend) =>
        {
            _settings.ShowOutline = outline;
            _settings.ShowMethods = methods;
            _settings.ShowInspector = inspector;
            _settings.ShowLegend = legend;
            _settings.Save();
        };
        _hud.Methods.PlayRequested += StartFromMember;
        _hud.Methods.ClassChosen += OnLink;
        _hud.Outline.FavoriteToggled += ToggleFavorite;
        _hud.Outline.ItemVisibilityChanged += SetVisibility;
        _hud.Outline.ShowAllRequested += ShowEverything;
        _hud.ShowAllRequested += ShowEverything;
        _hud.QuietFadeChanged += fade =>
        {
            _quietFade = fade;
            _settings.QuietFade = fade;
            _settings.Save();
            ApplyVisibility();
        };
        _hud.PointerEnteredUi += ClearHover;
        _hud.Progress.CancelRequested += () => _analysis?.Cancel();

        _hud.FlowRequested += StartFlow;
        _hud.GameLoopRequested += () => StartTrigger(new[] { Triggers.Frame, Triggers.Physics, Triggers.Draw }, loop: true, "Game loop");
        _hud.TriggerRequested += trigger => StartTrigger(new[] { trigger }, loop: false, $"Trigger: {trigger}");
        _hud.PauseToggleRequested += TogglePause;
        _hud.StopSimulationRequested += StopSimulation;
        _hud.SpeedChanged += speed =>
        {
            if (_simulation != null) _simulation.Speed = speed;
        };
        _hud.IncludeDataChanged += on => _includeData = on;
        _hud.UntouchedModeChanged += SetUntouchedMode;
        _hud.BrightnessChanged += brightness =>
        {
            ApplyBrightness(brightness);
            _settings.Brightness = brightness;
            _settings.Save();
        };
        _hud.LabelModeChanged += SetLabelMode;
        _hud.UsageFilterChanged += (active, min, max, ghost) =>
        {
            _usageFilterActive = active;
            _usageMin = min;
            _usageMax = max;
            _usageGhost = ghost;
            ApplyVisibility();
            UpdateUsageFilterCount();
        };
        _hud.SnapshotRequested += TakeSnapshot;
        _hud.CompareRequested += StartCompare;
        _hud.StopCompareRequested += StopCompare;
        _hud.ManageSnapshotsRequested += OpenSnapshots;
        _hud.OnlyDifferencesChanged += on =>
        {
            _onlyDifferences = on;
            ApplyVisibility();
            ReportComparison();
        };
        _hud.Snapshots.CompareRequested += path =>
        {
            _hud.Snapshots.Close();
            StartCompare(path);
        };
        _hud.Snapshots.RenameRequested += RenameSnapshot;
        _hud.Snapshots.DeleteRequested += DeleteSnapshot;
        _hud.Snapshots.TakeRequested += TakeSnapshot;
        _hud.TimelineBuildRequested += BuildTimeline;
        _hud.TimelineShowRequested += LoadTimeline;
        _hud.TimelineHideRequested += HideTimeline;
        _hud.TimelineFrameChanged += index => ShowTimelineFrame(index, pause: true);
        _hud.TimelinePlayToggled += ToggleTimelinePlay;
        _hud.PanelWidthsChanged += (outline, right) =>
        {
            _settings.OutlineWidth = outline;
            _settings.RightWidth = right;
            _settings.Save();
        };
    }

    // ---------------------------------------------------------------- loading

    private string StartupGraph()
    {
        var args = OS.GetCmdlineUserArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--graph") return args[i + 1];
        }
        var last = _settings.LastGraph;
        return !string.IsNullOrEmpty(last) && File.Exists(last) ? last : DemoGraph;
    }

    private void LoadGraph(string path)
    {
        Graph graph;
        try
        {
            graph = GraphIO.Load(path);
        }
        catch (Exception e)
        {
            _hud.SetStatus($"Could not open {path}: {e.Message}");
            GD.PushError($"[valence] could not open {path}: {e}");
            return;
        }

        StopSimulation();
        ClearSelection();
        _comparison = null;
        _comparisonPath = null;
        _chartPlan = null;
        _chartGraph = null;
        _hud.Flow.ShowChart(null);
        _hud.SetFlowChart(_settings.ShowFlowChart, false);
        _timeline = null;
        _timelinePresent = null;
        _timelinePlaying = false;
        _hud.HideTimeline();
        _hud.SetCompareActive(false);
        _opened = null;
        _openAmount = _openTarget = 0f;
        _flowGraph = null;
        _world?.QueueFree();
        _world = new Node3D { Name = "World" };
        AddChild(_world);

        _model = GraphModel.Build(graph);
        _pins = LayoutStore.Load(graph.Repo.Root);
        Layout.Run(_model, _pins);

        // Big graphs: system names only appear once you're closer.
        _atomLabelRange = _model.AtomList.Count > 40 ? 35f : 70f;
        foreach (var atom in _model.AtomList)
        {
            var view = new AtomView();
            view.Setup(atom, _assets);
            view.SetLabelRange(_atomLabelRange);
            _world.AddChild(view);
        }

        _nucleons = new NucleonRenderer();
        _world.AddChild(_nucleons);
        _nucleons.Build(_model, _assets);

        _bonds = new BondRenderer();
        _world.AddChild(_bonds);
        _bonds.Build(_model, _assets);

        _lines = new DetailLines();
        _lines.Init(_assets);
        _world.AddChild(_lines);

        _focusView = new FocusView();
        _world.AddChild(_focusView);

        _labels = new LabelLayer();
        _world.AddChild(_labels);

        _simulation = new SimulationView();
        _simulation.Init(_assets);
        _simulation.NodeReached += OnNodeReached;
        _simulation.Finished += OnSimulationFinished;
        _world.AddChild(_simulation);

        var triggers = graph.EntryPoints
            .GroupBy(e => e.Trigger)
            .Select(g => (g.Key, g.Select(e => e.Member).Distinct().Count()))
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .ToList();
        var hasGameLoop = graph.EntryPoints.Any(e => e.Trigger is Triggers.Frame or Triggers.Physics or Triggers.Draw);
        _hud.SetTriggers(triggers, hasGameLoop);

        foreach (var molecule in _model.Molecules)
        {
            molecule.Label = new Label3D
            {
                Text = molecule.Name,
                FontSize = 96,
                PixelSize = 0.03f,
                OutlineSize = 18,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                Modulate = Palette.MoleculeHue(molecule.Id),
                VisibilityRangeBegin = 45f,
                VisibilityRangeBeginMargin = 10f,
                VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
            };
            _world.AddChild(molecule.Label);
            PlaceMoleculeLabel(molecule);
        }

        _visibility = VisibilityStore.Load(graph.Repo.Root);
        _favorites = FavoritesStore.Load(graph.Repo.Root);
        _hud.SetSnapshots(SnapshotStore.List(graph.Repo.Root));
        _hud.Outline.SetModel(_model, StateOf, _favorites);
        UpdateLegend();
        ApplyVisibility();
        _hud.SetUsageFilterRange(_model.MaxFanIn); // resets the range; re-applies visibility if the filter is on
        FrameAll(true);
        ShowSummary();

        var repo = graph.Repo;
        _hud.SetRepoInfo(repo.Name, repo.Root, repo.Branch, repo.Commit);
        _hud.SetReanalyzeEnabled(Directory.Exists(repo.Root));
        _hud.SetStatus($"{_model.Nodes.Count} classes in {_model.AtomList.Count} systems, " +
                       $"{_model.Molecules.Count} projects, {_model.Bonds.Count} bonds · {path}");
        DisplayServer.WindowSetTitle($"Valence — {repo.Name}");
        GD.Print($"[valence] loaded {path}: {_model.Nodes.Count} classes, {_model.AtomList.Count} systems, " +
                 $"{_model.Bonds.Count} bonds, {graph.EntryPoints.Count} entry points, max layer {_model.MaxLayer}");

        if (!path.StartsWith("res://", StringComparison.Ordinal))
        {
            _settings.LastGraph = path;
            _settings.Save();
        }
    }

    private void OpenRecent(string folder)
    {
        // Reopen the saved graph instantly if there is one; File ▸ Re-analyze refreshes it.
        var cached = RepoAnalyzer.DefaultOutputPath(folder);
        if (File.Exists(cached)) LoadGraph(cached);
        else AnalyzeSourceFolder(folder);
    }

    private void Reanalyze()
    {
        var root = _model?.Graph.Repo.Root;
        if (!string.IsNullOrEmpty(root) && Directory.Exists(root)) AnalyzeSourceFolder(root);
        else _hud.SetStatus("This graph has no source folder on this machine to re-analyze.");
    }

    private void AnalyzeSourceFolder(string directory)
    {
        if (_analysis != null)
        {
            _hud.SetStatus("An analysis is already running…");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _analysis = cancellation;
        _hud.Progress.Begin($"Analyzing {Path.GetFileName(Path.TrimEndingDirectorySeparator(directory))} (read-only)");
        GD.Print($"[valence] analyzing {directory}");

        var context = new AnalysisContext(
            line => _mainThread.Enqueue(() =>
            {
                _hud.Progress.AppendLog(line);
                GD.Print($"[valence] {line}");
            }),
            progress => _mainThread.Enqueue(() => _hud.Progress.SetProgress(progress)),
            cancellation.Token);

        Task.Run(() => AnalyzerLauncher.Analyze(directory, context), cancellation.Token)
            .ContinueWith(task =>
            {
                var path = task.IsCompletedSuccessfully ? task.Result : null;
                var error = task.Exception?.GetBaseException();
                var cancelled = task.IsCanceled || error is OperationCanceledException;
                _mainThread.Enqueue(() =>
                {
                    _analysis = null;
                    cancellation.Dispose();
                    if (path != null)
                    {
                        _hud.Progress.Finish();
                        _settings.AddRecentFolder(Path.TrimEndingDirectorySeparator(directory));
                        _settings.Save();
                        _hud.SetRecent(_settings.RecentFolders);
                        LoadGraph(path);
                    }
                    else if (cancelled)
                    {
                        _hud.Progress.Finish();
                        _hud.SetStatus("Analysis cancelled.");
                    }
                    else
                    {
                        _hud.Progress.Fail(error?.Message ?? "unknown error");
                        GD.PushError($"[valence] analysis of {directory} failed: {error}");
                    }
                });
            });
    }

    private void BuildEnvironment()
    {
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = Palette.Background,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.55f, 0.6f, 0.75f),
            AmbientLightEnergy = 0.6f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            GlowEnabled = true,
            GlowIntensity = 0.9f,
            GlowBloom = 0.08f,
        };
        _environment = environment;
        AddChild(new WorldEnvironment { Environment = environment });
        AddChild(new DirectionalLight3D
        {
            LightEnergy = 0.9f,
            RotationDegrees = new Vector3(-50f, 35f, 0f),
        });
    }

    /// <summary>View ▸ Brightness: scales atom clouds, bonds, electrons, nucleon emission and bloom together.</summary>
    private void ApplyBrightness(float brightness)
    {
        _brightness = brightness;
        if (_environment != null)
        {
            _environment.GlowIntensity = 0.9f * brightness;
            _environment.GlowBloom = 0.08f * brightness;
        }
        _assets.CloudMaterial.SetShaderParameter("brightness", brightness);
        _assets.BondMaterial.SetShaderParameter("brightness", brightness);
        _assets.NucleonMaterial.SetShaderParameter("glow_strength", 2.6f * brightness);
        _assets.NucleonMaterial.SetShaderParameter("rim_strength", 0.35f * brightness);
        _assets.ElectronMaterial.EmissionEnergyMultiplier = 3f * brightness;
    }

    // ---------------------------------------------------------------- selection

    private void FocusAtom(AtomData atom, bool fly = true)
    {
        if (_opened != atom) BeginOpening(atom);
        else _openTarget = 1f; // it may be mid-close
        _focusAtom = atom;
        _selectedNode = null;
        SelectionChanged();
        if (_model != null) _hud.ShowInfo(InfoText.ForAtom(atom, _model));
        _hud.Outline.Reveal("atom:" + atom.Id);
        _hud.SetSelection(atom.Name);
        if (_model != null) _hud.Methods.ShowEntryPoints(_model, CanPlay);
        if (fly) _camera.FlyTo(atom.Position, _openRadius * 2.4f + 4f);
    }

    private void SelectNode(NodeData node, bool fly = true)
    {
        if (_focusAtom != node.Atom) FocusAtom(node.Atom, fly: false);
        _selectedNode = node;
        SelectionChanged();
        if (_model != null)
        {
            _hud.ShowInfo(InfoText.ForNode(node, _model));
            _hud.Methods.ShowMembers(node, _model, CanPlay);
        }
        _hud.Outline.Reveal("node:" + node.Info.Id);
        _hud.SetSelection($"{node.Atom.Name} ▸ {node.Info.Name}");
        // Aim at where the class will be once the atom is fully open.
        if (fly) _camera.FlyTo(node.Atom.Position + node.ExpandedOffset, Mathf.Max(7f, _openRadius * 1.3f));
    }

    private void ClearSelection()
    {
        _focusAtom = null;
        _selectedNode = null;
        if (_opened != null) _openTarget = 0f;
        SelectionChanged();
        ShowSummary();
        _hud.Outline.Reveal("");
        _hud.SetSelection("");
    }

    /// <summary>
    /// After the selection (or link depth / link kinds) changes: lights and labels follow it, and with
    /// Hide Unrelated on, so does what's drawn at all.
    /// </summary>
    private void SelectionChanged()
    {
        _hud.SetHasSelection(_focusAtom != null || _selectedNode != null);
        if (_hideUnrelated) ApplyVisibility();  // ends by refreshing the selection
        else RefreshSelection();
    }

    /// <summary>
    /// Hide Unrelated: the selected class and everything within Link Depth hops of it (over the
    /// enabled link kinds), or with only a system open, its classes and those of the systems it bonds
    /// with. Null when it's off or nothing is selected: hide nothing.
    /// </summary>
    private HashSet<NodeData>? RelatedToSelection()
    {
        if (!_hideUnrelated || _model == null) return null;
        if (_selectedNode != null)
        {
            var related = new HashSet<NodeData> { _selectedNode };
            var frontier = new List<NodeData> { _selectedNode };
            for (var hop = 0; hop < _linkDepth && frontier.Count > 0; hop++)
            {
                var next = new List<NodeData>();
                foreach (var node in frontier)
                {
                    // Walk past classes Hide Unrelated itself hid (so the set can grow again when the
                    // link depth or selection changes); only skip what the user hid.
                    foreach (var neighbor in _model.Neighbors(node, _enabledKinds, includeHidden: true))
                    {
                        if (neighbor.UserOpacity > 0f && related.Add(neighbor)) next.Add(neighbor);
                    }
                }
                frontier = next;
            }
            return related;
        }
        if (_focusAtom != null)
        {
            return new HashSet<NodeData>(_focusAtom.Nodes.Concat(_focusAtom.Neighbors.SelectMany(a => a.Nodes)));
        }
        return null;
    }

    private void Back()
    {
        if (_selectedNode != null) FocusAtom(_selectedNode.Atom, fly: false);
        else if (_focusAtom != null) ClearSelection();
        else if (_simulation is { Active: true }) StopSimulation();
        else ClearSelection();
    }

    // ---------------------------------------------------------------- simulation

    /// <summary>Flow from (downstream) or into (upstream) the selected class.</summary>
    private void StartFlow(FlowDirection direction)
    {
        if (_model == null || _simulation == null) return;
        if (_selectedNode is not { } node)
        {
            _hud.SetStatus("Select a class first (click one, or find it in the Outline), then run the flow.");
            return;
        }
        var graph = _flowGraph ??= FlowGraph.For(_model);
        var starts = graph.StartPointsFor(node);
        if (starts.Count == 0)
        {
            _hud.SetStatus($"{node.Info.Name} has no recorded calls or data links.");
            return;
        }
        var title = direction == FlowDirection.Downstream
            ? $"Flow downstream from {node.Info.Name}"
            : $"Flow upstream into {node.Info.Name}";
        var plan = FlowSimulator.Build(graph, starts, direction, _includeData, title);
        BeginSimulation(graph, plan, new[] { node }, loop: false);
    }

    /// <summary>Plays every entry point with one of these triggers (the game loop is frame+physics+draw).</summary>
    private void StartTrigger(string[] triggers, bool loop, string title)
    {
        if (_model == null || _simulation == null) return;
        var graph = _flowGraph ??= FlowGraph.For(_model);
        if (!graph.MemberLevel)
        {
            _hud.SetStatus("This graph has no method-level data. Re-analyze the source folder (File ▸ Re-analyze).");
            return;
        }
        var starts = _model.Graph.EntryPoints
            .Where(e => triggers.Contains(e.Trigger))
            .Select(e => e.Member)
            .Distinct()
            .ToList();
        if (starts.Count == 0)
        {
            _hud.SetStatus($"No entry points for {string.Join("/", triggers)} in this graph.");
            return;
        }
        var plan = FlowSimulator.Build(graph, starts, FlowDirection.Downstream, _includeData, title);
        var startNodes = starts.Select(graph.OwnerOf).Where(n => n != null).Cast<NodeData>().Distinct().ToList();
        BeginSimulation(graph, plan, startNodes, loop);
    }

    private void BeginSimulation(FlowGraph graph, FlowPlan plan, IEnumerable<NodeData> startNodes, bool loop)
    {
        if (_simulation == null || _model == null) return;
        if (plan.Events.Count == 0)
        {
            _hud.SetStatus($"{plan.Title}: nothing to play (no outgoing calls).");
            return;
        }
        _heat.Clear();
        _flash.Clear();
        _visits.Clear();
        _simStarts.Clear();
        _simStarts.UnionWith(startNodes);
        foreach (var node in _simStarts) _heat[node] = 1f;

        // Every label hides; the start classes count as already reached.
        _labels?.SetSimulation(true);
        _revealedAtoms.Clear();
        foreach (var node in _simStarts) RevealLabel(node);

        _simPlan = plan;
        _simLoop = loop;
        _simTick = 1;
        _simFinished = false;
        _simulation.Play(graph, plan, keepTrace: false);
        ShowFlowChart(graph, plan);
        ApplyVisibility(); // ghosts/hides untouched classes per Simulate ▸ Untouched; also labels + colors
        _hud.SetSimulationState(true, true);
        _hud.ShowInfo(InfoText.ForSimulation(plan, _simTick, loop, _visits, _model));
        _hud.SetStatus($"{plan.Title}: {plan.Events.Count} pulses, {plan.Reached} points reached" +
                       (plan.Truncated ? " (truncated)" : "") + " · Space pauses · H untouched · Esc stops");
    }

    private void OnNodeReached(NodeData node, float amount)
    {
        _heat[node] = Mathf.Min(1f, _heat.GetValueOrDefault(node) + amount);
        _visits[node] = _visits.GetValueOrDefault(node) + 1;
        _flash[node] = FlashSeconds; // gold for two seconds, then back to normal
        RevealLabel(node);
    }

    /// <summary>The flow reached a class: un-ghost/un-hide it, label it, and name its system.</summary>
    private void RevealLabel(NodeData node)
    {
        if (node.UserOpacity <= 0f) return; // the user hid it
        var atom = node.Atom;
        var newAtom = _revealedAtoms.Add(atom);
        if (node.Opacity < node.UserOpacity || (newAtom && atom.Opacity < atom.UserOpacity))
        {
            node.Opacity = node.UserOpacity;
            atom.Opacity = atom.UserOpacity;
            atom.View?.SetOpacity(atom.Opacity);
            _nucleons?.UpdateAtom(atom);
            if (newAtom)
            {
                _bonds?.UpdateAtom(atom);
                _bonds?.SetFocus(_focusAtom);
                _focusView?.RefreshVisibility();
            }
        }
        _labels?.Reveal(node);
        if (!newAtom) return;
        atom.View?.SetLabelPinned(true, true);
        _focusView?.SetPinnedAtoms(_revealedAtoms, true);
    }

    /// <summary>Play button in the Methods panel: simulate downstream from exactly this member.</summary>
    private void StartFromMember(string memberId)
    {
        if (_model == null || _simulation == null) return;
        var graph = _flowGraph ??= FlowGraph.For(_model);
        var owner = graph.OwnerOf(memberId);
        if (!graph.MemberLevel || owner == null)
        {
            _hud.SetStatus("This graph has no method-level data. Re-analyze the source folder (File ▸ Re-analyze).");
            return;
        }
        var name = owner.Info.Members.FirstOrDefault(m => m.Id == memberId)?.Name ?? memberId;
        var plan = FlowSimulator.Build(graph, new[] { memberId }, FlowDirection.Downstream, _includeData,
            $"Flow from {owner.Info.Name}.{name}");
        BeginSimulation(graph, plan, new[] { owner }, loop: false);
    }

    private void OnSimulationFinished()
    {
        if (_simulation == null || _simPlan == null || _model == null) return;
        if (_simLoop && _flowGraph != null)
        {
            _simTick++;
            _simulation.Play(_flowGraph, _simPlan, keepTrace: true);
            if (_selectedNode == null && _focusAtom == null) _hud.ShowInfo(InfoText.ForSimulation(_simPlan, _simTick, true, _visits, _model));
            return;
        }
        _simFinished = true;
        _hud.SetSimulationState(true, false);
        _hud.ShowInfo(InfoText.ForSimulation(_simPlan, _simTick, false, _visits, _model));
        _hud.SetStatus($"{_simPlan.Title}: finished. {_visits.Count} classes reached · Simulate ▸ Stop (or Esc) clears it.");
    }

    private void TogglePause()
    {
        if (_simulation is not { Active: true }) return;
        if (_simulation.Playing)
        {
            _simulation.Pause();
        }
        else if (_simFinished && _flowGraph != null && _simPlan != null)
        {
            // A single run that already finished: play it again.
            _simFinished = false;
            _simTick++;
            _simulation.Play(_flowGraph, _simPlan, keepTrace: true);
        }
        else
        {
            _simulation.Resume();
        }
        _hud.SetSimulationState(true, _simulation.Playing);
    }

    private void StopSimulation()
    {
        _simulation?.Stop();
        _heat.Clear();
        _flash.Clear();
        _visits.Clear();
        _simStarts.Clear();
        _revealedAtoms.Clear();
        _labels?.SetSimulation(false);
        _simPlan = null;
        _simLoop = false;
        _simFinished = false;
        _hud.SetSimulationState(false, false);
        _hud.Flow.SetPlayhead(null, 1); // the chart stays (for export), drawn as fully reached
        ApplyVisibility(); // brings back anything "untouched" was hiding; also labels + colors
    }

    // ---------------------------------------------------------------- flow chart

    private void ShowFlowChart(FlowGraph graph, FlowPlan plan)
    {
        _chartGraph = graph;
        _chartPlan = plan;
        RebuildFlowChart();
        _hud.SetFlowChart(_settings.ShowFlowChart, _settings.ShowFlowChart);
    }

    private void RebuildFlowChart()
    {
        if (_model == null || _chartGraph == null || _chartPlan == null) return;
        _hud.Flow.ShowChart(FlowChart.Build(_chartPlan, _chartGraph, _model, _chartGrouping));
        UpdateFlowPlayhead();
    }

    /// <summary>The chart lights up in step with the 3D simulation (all lit once it's finished).</summary>
    private void UpdateFlowPlayhead()
    {
        if (!_hud.FlowChartVisible) return;
        var playing = _simulation is { Active: true } && !_simFinished;
        _hud.Flow.SetPlayhead(playing ? _simulation!.Time : null, _simTick);
    }

    /// <summary>Simulate ▸ Flow Chart / the drawer's ✕. Remembered between runs.</summary>
    private void SetFlowChartShown(bool on)
    {
        _settings.ShowFlowChart = on;
        _settings.Save();
        var hasChart = _hud.Flow.View.Chart != null;
        _hud.SetFlowChart(on, on && hasChart);
        if (on && !hasChart) _hud.SetStatus("Start a simulation (Simulate menu, or ▶ on a method) to see its flow chart.");
        if (on) UpdateFlowPlayhead();
    }

    private void ShowSummary()
    {
        if (_model == null) return;
        _hud.ShowRepoSummary(_model);
        _hud.Methods.ShowEntryPoints(_model, CanPlay);
    }

    /// <summary>Whether the method list's ▶ has anything to play for this member.</summary>
    private bool CanPlay(string memberId)
    {
        if (_model == null) return false;
        var graph = _flowGraph ??= FlowGraph.For(_model);
        return graph.MemberLevel && graph.Out(memberId).Count > 0;
    }

    // ---------------------------------------------------------------- hide / quiet

    private Visibility StateOf(string key) => _visibility.GetValueOrDefault(key, Visibility.Shown);

    private void SetVisibility(string key, Visibility state)
    {
        if (_model == null) return;
        if (state == Visibility.Shown) _visibility.Remove(key);
        else _visibility[key] = state;
        VisibilityStore.Save(_model.Graph.Repo.Root, _visibility);
        ApplyVisibility();
    }

    // ---------------------------------------------------------------- snapshots & compare

    private void TakeSnapshot()
    {
        if (_model == null) return;
        var path = SnapshotStore.Save(_model.Graph);
        RefreshSnapshots();
        _hud.SetStatus($"Snapshot saved ({SnapshotStore.LabelFor(path)}). Later: File ▸ Re-analyze, then View ▸ Compare With Snapshot.");
    }

    /// <summary>File ▸ Snapshots…: the list with Compare, Rename and Delete.</summary>
    private void OpenSnapshots()
    {
        if (_model == null) return;
        RefreshSnapshots();
        _hud.Snapshots.SetEntries(_model.Graph.Repo.Name, SnapshotStore.Entries(_model.Graph.Repo.Root), _comparisonPath);
        _hud.Snapshots.Open();
    }

    /// <summary>Re-reads the snapshot folder into the Compare menu and, if it's open, the Snapshots dialog.</summary>
    private void RefreshSnapshots()
    {
        if (_model == null) return;
        var root = _model.Graph.Repo.Root;
        _hud.SetSnapshots(SnapshotStore.List(root));
        if (_hud.Snapshots.Visible) _hud.Snapshots.SetEntries(_model.Graph.Repo.Name, SnapshotStore.Entries(root), _comparisonPath);
    }

    private void RenameSnapshot(string path, string name)
    {
        SnapshotStore.Rename(path, name);
        RefreshSnapshots();
        _hud.SetStatus(name.Length > 0 ? $"Snapshot renamed to \"{name}\"" : "Snapshot name cleared (shown by date)");
    }

    private void DeleteSnapshot(string path)
    {
        var label = SnapshotStore.LabelFor(path);
        if (path == _comparisonPath) StopCompare();
        var error = SnapshotStore.Delete(path);
        RefreshSnapshots();
        _hud.SetStatus(error == null ? $"Snapshot {label} moved to the trash" : $"Could not delete snapshot: {error}");
    }

    private void StartCompare(string path)
    {
        if (_model == null) return;
        Graph snapshot;
        try
        {
            snapshot = GraphIO.Load(path);
        }
        catch (Exception e)
        {
            _hud.SetStatus($"Could not open snapshot: {e.Message}");
            return;
        }
        _comparison = Comparison.Build(snapshot, _model, SnapshotStore.LabelFor(path));
        _comparisonPath = path;
        _hud.SetCompareActive(true);
        _hud.ShowInfo(InfoText.ForComparison(_comparison, _model));
        ApplyVisibility();      // colors, and Only Show Differences if it's on
        ReportComparison();
    }

    private void StopCompare()
    {
        _comparison = null;
        _comparisonPath = null;
        _hud.SetCompareActive(false);
        ShowSummary();
        ApplyVisibility();
    }

    private void ReportComparison()
    {
        if (_comparison == null) return;
        var c = _comparison;
        _hud.SetStatus(_onlyDifferences
            ? $"Only differences since {c.Label}: {c.Added.Count} added, {c.Changed.Count} changed shown; " +
              $"{c.Removed.Count} removed (listed in the inspector)"
            : $"Comparing with {c.Label}: {c.Added.Count} added, {c.Changed.Count} changed, {c.Removed.Count} removed");
    }

    /// <summary>While Only Show Differences is on during a compare: whether a class was added or changed.</summary>
    private bool ShowsAsDifference(NodeData node) =>
        _comparison == null || !_onlyDifferences
        || _comparison.Added.Contains(node.Info.Id) || _comparison.Changed.Contains(node.Info.Id);

    // ---------------------------------------------------------------- git timeline

    private void BuildTimeline(int samples)
    {
        var root = _model?.Graph.Repo.Root;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            _hud.SetStatus("Open a source folder first: the timeline is built from its git history.");
            return;
        }
        if (_analysis != null)
        {
            _hud.SetStatus("An analysis is already running…");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _analysis = cancellation;
        _hud.Progress.Begin($"Building timeline: {samples} commits of {Path.GetFileName(root)} (read-only)");
        var context = new AnalysisContext(
            line => _mainThread.Enqueue(() =>
            {
                _hud.Progress.AppendLog(line);
                GD.Print($"[valence] {line}");
            }),
            progress => _mainThread.Enqueue(() => _hud.Progress.SetProgress(progress)),
            cancellation.Token);

        Task.Run(() => AnalyzerLauncher.BuildTimeline(root, samples, context), cancellation.Token)
            .ContinueWith(task =>
            {
                var path = task.IsCompletedSuccessfully ? task.Result : null;
                var error = task.Exception?.GetBaseException();
                var cancelled = task.IsCanceled || error is OperationCanceledException;
                _mainThread.Enqueue(() =>
                {
                    _analysis = null;
                    cancellation.Dispose();
                    if (path != null)
                    {
                        _hud.Progress.Finish();
                        LoadTimeline();
                    }
                    else if (cancelled)
                    {
                        _hud.Progress.Finish();
                        _hud.SetStatus("Timeline cancelled.");
                    }
                    else
                    {
                        _hud.Progress.Fail(error?.Message ?? "unknown error");
                        GD.PushError($"[valence] timeline for {root} failed: {error}");
                    }
                });
            });
    }

    private void LoadTimeline()
    {
        var root = _model?.Graph.Repo.Root;
        if (string.IsNullOrEmpty(root)) return;
        var path = TimelineBuilder.DefaultPath(root);
        if (!File.Exists(path))
        {
            _hud.SetStatus("No timeline yet for this repo: Timeline ▸ Build Timeline.");
            return;
        }
        try
        {
            _timeline = ValenceJson.Read<Timeline>(path);
        }
        catch (Exception e)
        {
            _hud.SetStatus($"Could not read the timeline: {e.Message}");
            return;
        }
        if (_timeline.Frames.Count == 0)
        {
            _timeline = null;
            _hud.SetStatus("The timeline has no commits.");
            return;
        }
        _hud.ShowTimeline(_timeline.Frames.Count);
        ShowTimelineFrame(_timeline.Frames.Count - 1, pause: true);
    }

    private void ShowTimelineFrame(int index, bool pause)
    {
        if (_timeline == null || _model == null) return;
        if (pause && _timelinePlaying) ToggleTimelinePlay();
        index = Math.Clamp(index, 0, _timeline.Frames.Count - 1);
        _timelineIndex = index;
        var frame = _timeline.Frames[index];
        _timelinePresent = new HashSet<string>(frame.Nodes.Keys, StringComparer.Ordinal);

        _timelineNew.Clear();
        var previous = index > 0 ? _timeline.Frames[index - 1].Nodes : null;
        foreach (var id in frame.Nodes.Keys)
        {
            if (previous == null || !previous.ContainsKey(id)) _timelineNew.Add(id);
        }
        var removed = previous?.Keys.Count(id => !frame.Nodes.ContainsKey(id)) ?? 0;
        var missing = frame.Nodes.Keys.Count(id => !_model.Nodes.ContainsKey(id));

        ApplyVisibility();
        var commit = frame.Commit.Length >= 7 ? frame.Commit[..7] : frame.Commit;
        var label = $"{index + 1}/{_timeline.Frames.Count} · {frame.Date.ToLocalTime():yyyy-MM-dd} · {commit} · {frame.Subject} · " +
                    $"{frame.Nodes.Count} classes (+{(previous == null ? frame.Nodes.Count : _timelineNew.Count)} −{removed})" +
                    (missing > 0 ? $" · {missing} since deleted (not drawn)" : "");
        _hud.SetTimelineFrame(index, label);
    }

    private void ToggleTimelinePlay()
    {
        if (_timeline == null) return;
        _timelinePlaying = !_timelinePlaying;
        _timelineTimer = 0f;
        // Playing from the end starts over from the first commit.
        if (_timelinePlaying && _timelineIndex >= _timeline.Frames.Count - 1) ShowTimelineFrame(0, pause: false);
        _hud.SetTimelinePlaying(_timelinePlaying);
    }

    private void HideTimeline()
    {
        _timeline = null;
        _timelinePresent = null;
        _timelineNew.Clear();
        _timelinePlaying = false;
        _hud.SetTimelinePlaying(false);
        _hud.HideTimeline();
        ApplyVisibility();
    }

    /// <summary>Outline right-click ▸ Add/Remove from Favorites; saved per repo.</summary>
    private void ToggleFavorite(string key, bool favorite)
    {
        if (_model == null) return;
        if (favorite) _favorites.Add(key);
        else _favorites.Remove(key);
        FavoritesStore.Save(_model.Graph.Repo.Root, _favorites);
        _hud.Outline.RefreshFavorites();
        _hud.SetStatus(favorite ? "Added to Favorites (top of the Outline)." : "Removed from Favorites.");
    }

    private void ShowEverything()
    {
        if (_model == null) return;
        _visibility.Clear();
        VisibilityStore.Save(_model.Graph.Repo.Root, _visibility);
        ApplyVisibility();
    }

    /// <summary>
    /// Effective visibility is the most restrictive of the item and its parents (project → system →
    /// class). Hidden draws nothing (nor its bonds, links, ports, labels or pulses); quiet draws faded.
    /// </summary>
    /// <summary>During a simulation, what happens to classes/systems the flow hasn't reached yet.</summary>
    private float UntouchedFactor(bool touched)
    {
        if (touched || _untouched == UntouchedMode.Show || _simulation is not { Active: true }) return 1f;
        return _untouched == UntouchedMode.Hide ? 0f : _quietFade;
    }

    private void SetUntouchedMode(UntouchedMode mode)
    {
        _untouched = mode;
        _hud.SetUntouchedMode(mode);
        _settings.Untouched = mode;
        _settings.Save();
        ApplyVisibility();
        if (_simulation is { Active: true }) _hud.SetStatus($"Untouched classes: {mode.ToString().ToLowerInvariant()}");
    }

    private void ApplyVisibility()
    {
        if (_model == null) return;
        float OpacityOf(Visibility state) => state switch
        {
            Visibility.Hidden => 0f,
            Visibility.Quiet => _quietFade,
            _ => 1f,
        };
        static Visibility Most(Visibility a, Visibility b) => (Visibility)Math.Max((int)a, (int)b);

        var related = RelatedToSelection();
        foreach (var molecule in _model.Molecules)
        {
            var moleculeState = StateOf("molecule:" + molecule.Id);
            molecule.Opacity = OpacityOf(moleculeState);
            if (molecule.Label != null)
            {
                molecule.Label.Visible = molecule.Opacity > 0f;
                var tint = Palette.MoleculeHue(molecule.Id);
                molecule.Label.Modulate = new Color(tint.R, tint.G, tint.B, molecule.Opacity);
            }
            foreach (var atom in molecule.Atoms)
            {
                var atomState = Most(moleculeState, StateOf("atom:" + atom.Id));
                // Timeline: only classes that existed at the shown commit (and their systems) are drawn.
                var present = _timelinePresent;
                var atomExists = present == null || atom.Nodes.Any(n => present.Contains(n.Info.Id));
                // Usage filter: a system with no classes in the range is hidden/ghosted as a whole.
                var atomPassesUsage = atom.Nodes.Any(PassesUsageFilter);
                // Only Show Differences: a system with no added or changed classes goes away too.
                var atomDiffers = atom.Nodes.Any(ShowsAsDifference);
                // Hide Unrelated: a system with no related classes goes away too.
                var atomRelated = related == null || atom.Nodes.Any(related.Contains);
                atom.UserOpacity = OpacityOf(atomState);
                atom.Opacity = atom.UserOpacity * UntouchedFactor(_revealedAtoms.Contains(atom)) * (atomExists ? 1f : 0f)
                               * UsageFactor(atomPassesUsage) * (atomDiffers ? 1f : 0f) * (atomRelated ? 1f : 0f);
                atom.View?.SetOpacity(atom.Opacity);
                foreach (var node in atom.Nodes)
                {
                    var exists = present == null || present.Contains(node.Info.Id);
                    node.UserOpacity = OpacityOf(Most(atomState, StateOf("node:" + node.Info.Id)));
                    node.Opacity = node.UserOpacity * UntouchedFactor(_heat.ContainsKey(node)) * (exists ? 1f : 0f)
                                   * UsageFactor(PassesUsageFilter(node)) * (ShowsAsDifference(node) ? 1f : 0f)
                                   * (related == null || related.Contains(node) ? 1f : 0f);
                }
            }
        }

        // Selections the user hid go away (not ones that are only untouched by a simulation).
        if (_selectedNode is { UserOpacity: <= 0f }) _selectedNode = null;
        if (_focusAtom is { UserOpacity: <= 0f }) ClearSelection();

        _nucleons?.UpdateAll();
        _bonds?.UpdateAll();
        _focusView?.RefreshVisibility();
        _simulation?.RefreshTrace();
        _hud.Outline.RefreshStates();
        RefreshSelection();

        var hidden = _visibility.Count(kv => kv.Value == Visibility.Hidden);
        var quiet = _visibility.Count(kv => kv.Value == Visibility.Quiet);
        if (hidden + quiet > 0) _hud.SetStatus($"{hidden} hidden, {quiet} quiet · View ▸ Show Everything to reset");
    }

    private void OnLink(string meta)
    {
        if (_model == null) return;
        if (meta.StartsWith("node:", StringComparison.Ordinal) && _model.Nodes.TryGetValue(meta[5..], out var node))
        {
            SelectNode(node);
        }
        else if (meta.StartsWith("atom:", StringComparison.Ordinal) && _model.Atoms.TryGetValue(meta[5..], out var atom))
        {
            FocusAtom(atom);
        }
    }

    /// <summary>Recomputes which classes are lit, then refreshes colors, labels, bonds and lines.</summary>
    private void RefreshSelection()
    {
        if (_model == null) return;

        _anchor = _selectedNode ?? (_hoverNode != null && _hoverNode.Atom == _focusAtom ? _hoverNode : null);
        _lit.Clear();
        if (_anchor != null)
        {
            var depth = _selectedNode != null ? _linkDepth : 1;
            var frontier = new List<NodeData> { _anchor };
            _lit.Add(_anchor);
            for (var hop = 0; hop < depth && frontier.Count > 0; hop++)
            {
                var next = new List<NodeData>();
                foreach (var node in frontier)
                {
                    foreach (var neighbor in _model.Neighbors(node, _enabledKinds))
                    {
                        if (_lit.Add(neighbor)) next.Add(neighbor);
                    }
                }
                frontier = next;
            }
        }

        foreach (var atom in _model.AtomList)
        {
            // The opened system's connections glow yellow; everything else dims.
            var connected = _focusAtom != null && _focusAtom.Neighbors.Contains(atom);
            atom.View?.SetState(atom == _focusAtom, _focusAtom != null && atom != _focusAtom && !connected, connected);
        }
        _bonds?.SetFocus(_focusAtom);
        ApplyLabels();
        ApplyColors();
        UpdateLines();
    }

    /// <summary>
    /// Labels: with a selection (clicked or found via search), only the selection and everything it
    /// connects to are labeled, wherever they are. Without one, labels appear as you zoom in; the
    /// hovered class and its neighbours are labeled too. Simulation start points stay labeled.
    /// </summary>
    private void ApplyLabels()
    {
        if (_model == null || _labels == null) return;
        var simulating = _simulation is { Active: true };
        foreach (var molecule in _model.Molecules)
        {
            if (molecule.Label != null) molecule.Label.Visible = !simulating && molecule.Opacity > 0f;
        }

        if (simulating)
        {
            // Simulation: no labels except where the flow has been (revealed in OnNodeReached).
            _labels.SetPinned(_anchor != null ? new[] { _anchor } : Array.Empty<NodeData>(), _anchor, true);
            foreach (var atom in _model.AtomList) atom.View?.SetLabelPinned(_revealedAtoms.Contains(atom), true);
            _focusView?.SetPinnedAtoms(_revealedAtoms, true);
            return;
        }

        // Pinned (always-visible) labels are kept to a minimum: the selected class and the classes it
        // connects to directly, or just the hovered class. Everything else only shows near the mouse.
        // On Click: only what was clicked (the selected class, the opened system) is labeled.
        var onClick = _labelMode == LabelMode.OnClick;
        var selectionActive = _selectedNode != null;
        var pinned = new HashSet<NodeData>();
        if (_selectedNode != null)
        {
            pinned.Add(_selectedNode);
            if (!onClick) pinned.UnionWith(_model.Neighbors(_selectedNode, _enabledKinds));
        }
        else if (_hoverNode != null && !onClick)
        {
            pinned.Add(_hoverNode);
        }
        _labels.SetMode(_labelMode);
        _labels.SetPinned(pinned, _anchor, selectionActive);

        var pinnedAtoms = new HashSet<AtomData>();
        if (_selectedNode != null) pinnedAtoms.UnionWith(pinned.Select(n => n.Atom));
        if (_focusAtom != null) pinnedAtoms.Add(_focusAtom);
        foreach (var atom in _model.AtomList) atom.View?.SetLabelPinned(pinnedAtoms.Contains(atom), selectionActive, _labelMode);
        _focusView?.SetPinnedAtoms(pinnedAtoms, selectionActive, _labelMode);
        foreach (var molecule in _model.Molecules)
        {
            if (molecule.Label != null) molecule.Label.Visible = !onClick && molecule.Opacity > 0f;
        }
    }

    private void SetLabelMode(LabelMode mode)
    {
        _labelMode = mode;
        _hud.SetLabelMode(mode);
        _settings.Labels = mode;
        _settings.Save();
        ApplyLabels();
        var name = mode switch
        {
            LabelMode.All => "all",
            LabelMode.OnClick => "on click only",
            _ => "near the mouse",
        };
        _hud.SetStatus(_simulation is { Active: true }
            ? $"Labels: {name} (after the simulation; while it runs, only reached classes are labeled)"
            : $"Labels: {name}");
    }

    // ---------------------------------------------------------------- opening an atom

    private void BeginOpening(AtomData atom)
    {
        if (_model == null || _focusView == null) return;
        if (_opened != null && _opened != atom) CloseImmediately(_opened);
        _opened = atom;
        _openRadius = Layout.Expand(atom, _model);
        _openAmount = 0f;
        _openTarget = 1f;
        // Labels appear once the camera is within this distance, so zooming in reveals more of them.
        var zoomRange = Mathf.Max(10f, _openRadius * 1.6f);
        _focusView.Open(atom, _model, _assets, _openRadius, zoomRange);
        _labels?.SetOpened(atom.Nodes, zoomRange);
        _labels?.SetOpenFade(0f);
    }

    private void ApplyOpenAmount()
    {
        if (_opened == null) return;
        var t = Mathf.SmoothStep(0f, 1f, _openAmount);
        foreach (var node in _opened.Nodes) node.LocalOffset = node.PackedOffset.Lerp(node.ExpandedOffset, t);
        _opened.DisplayRadius = Mathf.Lerp(_opened.Radius, _openRadius, t);
        _opened.View?.SetExpansion(t, _openRadius);
        _nucleons?.UpdateAtom(_opened);
        _bonds?.UpdateAtom(_opened);
        _focusView?.UpdateLayout(t);
        _labels?.SetOpenFade(Mathf.Clamp((t - 0.5f) * 2f, 0f, 1f));
        _labels?.UpdatePositions();
        _simulation?.RefreshTrace();
        UpdateLines();
    }

    private void FinishClosing()
    {
        if (_opened == null) return;
        CloseImmediately(_opened);
        _opened = null;
        UpdateLines();
    }

    private void CloseImmediately(AtomData atom)
    {
        foreach (var node in atom.Nodes) node.LocalOffset = node.PackedOffset;
        atom.DisplayRadius = atom.Radius;
        atom.View?.SetExpansion(0f, atom.Radius);
        _nucleons?.UpdateAtom(atom);
        _bonds?.UpdateAtom(atom);
        _focusView?.Close();
        _labels?.ClearOpened();
        _labels?.SetOpenFade(1f);
        _labels?.UpdatePositions();
        _simulation?.RefreshTrace();
    }

    // ---------------------------------------------------------------- colors & lines

    private void UpdateLegend() => _hud.SetLegend(_colorMode, _model?.HasGit ?? false, _model?.Graph.Repo.ChurnWindowDays);

    private void ApplyColors() => _nucleons?.ApplyColors(node => Faded(NodeColor(node), node.Opacity));

    /// <summary>Quiet items blend toward the background and lose their glow.</summary>
    private static Color Faded(Color color, float opacity)
    {
        if (opacity >= 1f) return color;
        var rgb = Palette.Background.Lerp(new Color(color.R, color.G, color.B), opacity);
        return new Color(rgb.R, rgb.G, rgb.B, color.A * opacity);
    }

    private Color NodeColor(NodeData node)
    {
        var model = _model!;
        var color = _colorMode switch
        {
            ColorMode.Kind => Palette.NodeKind(node.Info.Kind),
            ColorMode.Churn => Palette.Heat(model.MaxChurn > 0
                ? MathF.Log(1f + (float)node.Metric(MetricKeys.Churn)) / MathF.Log(1f + (float)model.MaxChurn)
                : 0f),
            ColorMode.Coupling => Palette.Heat(model.MaxFanIn > 0 ? MathF.Sqrt((float)node.FanIn / model.MaxFanIn) : 0f),
            ColorMode.Risk => Palette.Heat(node.Risk.Score),
            _ => node.IsPublic ? Palette.Proton : Palette.Neutron,
        };
        var glow = RecencyGlow(node, model);

        if (_simulation is { Active: true })
        {
            // Simulation: a class turns gold for two seconds when the flow pings it, then returns to
            // its normal color; classes not reached yet are dimmed.
            if (node == _anchor) return new Color(Palette.Selected.R, Palette.Selected.G, Palette.Selected.B, 1f);
            if (_flash.TryGetValue(node, out var left))
            {
                var gold = left > FlashFadeSeconds ? 1f : left / FlashFadeSeconds;
                color = color.Lerp(Palette.Gold, gold);
                glow = Mathf.Max(glow * 0.3f, gold);
            }
            else if (_heat.TryGetValue(node, out var heat))
            {
                glow = Mathf.Max(glow * 0.3f, heat * 0.4f);
            }
            else
            {
                color = color.Darkened(0.75f);
                glow *= 0.1f;
            }
            return new Color(color.R, color.G, color.B, glow);
        }

        // Compare: added green, changed amber, unchanged dimmed.
        if (_comparison != null && node != _anchor)
        {
            var id = node.Info.Id;
            if (_comparison.Added.Contains(id)) return new Color(Palette.Added.R, Palette.Added.G, Palette.Added.B, 0.8f);
            if (_comparison.Changed.Contains(id)) return new Color(Palette.Changed.R, Palette.Changed.G, Palette.Changed.B, 0.5f);
            color = color.Darkened(0.6f);
            return new Color(color.R, color.G, color.B, 0f);
        }

        // Timeline: classes new in the shown commit glow green.
        if (_timelinePresent != null && _timelineNew.Contains(node.Info.Id) && node != _anchor)
        {
            return new Color(Palette.Added.R, Palette.Added.G, Palette.Added.B, 1f);
        }

        if (_anchor != null)
        {
            if (node == _anchor) return new Color(Palette.Selected.R, Palette.Selected.G, Palette.Selected.B, 1f);
            if (!_lit.Contains(node))
            {
                color = color.Darkened(0.7f);
                glow *= 0.15f;
            }
        }
        else if (_focusAtom != null && node.Atom != _focusAtom && !_focusAtom.Neighbors.Contains(node.Atom))
        {
            color = color.Darkened(0.65f);
            glow *= 0.2f;
        }
        return new Color(color.R, color.G, color.B, glow);
    }

    /// <summary>1 when changed right before the snapshot, fading to 0 over two weeks.</summary>
    private static float RecencyGlow(NodeData node, GraphModel model)
    {
        if (!model.HasGit) return 0f;
        var last = node.Metric(MetricKeys.LastChanged);
        if (last <= 0) return 0f;
        var ageDays = (model.SnapshotTime - last) / 86400.0;
        return Mathf.Clamp(1f - (float)ageDays / RecencyWindowDays, 0f, 1f);
    }

    /// <summary>
    /// Inside an opened atom: links between its classes, plus links to other systems drawn to the
    /// matching rim port. With a lit anchor, only links touching the lit set are drawn.
    /// </summary>
    private void UpdateLines()
    {
        if (_lines == null || _model == null) return;
        var lines = new List<(Vector3 From, Vector3 To, Color Color)>();
        var atom = _opened;
        if (atom == null || atom != _focusAtom || _focusView == null)
        {
            _lines.SetLines(lines);
            return;
        }

        // Links from the selected class go straight to connected classes in other systems (which are
        // labeled), instead of stopping at the port.
        bool DrawDirect(NodeData a, NodeData b) =>
            _selectedNode != null && (a == _selectedNode || b == _selectedNode) && _lit.Contains(a) && _lit.Contains(b);

        bool Hot(NodeData a, NodeData b) =>
            _anchor == null || a == _anchor || b == _anchor || (_selectedNode != null && _lit.Contains(a) && _lit.Contains(b));

        // Hidden ends draw nothing; quiet ends fade the line.
        void Add(Vector3 from, Vector3 to, string kind, bool hot, NodeData a, NodeData b)
        {
            if (lines.Count >= MaxFocusLines || a.IsHidden || b.IsHidden) return;
            var color = Palette.EdgeKind(kind);
            color.A = (_anchor == null ? 0.32f : hot ? 0.95f : 0f) * Mathf.Min(a.Opacity, b.Opacity);
            if (color.A > 0.005f) lines.Add((from, to, color));
        }

        foreach (var node in atom.Nodes)
        {
            if (node.IsHidden) continue;
            foreach (var edge in node.Outgoing)
            {
                if (!_enabledKinds.Contains(edge.Kind) || !_model.Nodes.TryGetValue(edge.Target, out var target)) continue;
                if (target.Atom == atom || DrawDirect(node, target))
                {
                    Add(node.WorldPosition, target.WorldPosition, edge.Kind, Hot(node, target), node, target);
                }
                else if (_focusView.PortPosition(target.Atom) is { } port)
                {
                    Add(node.WorldPosition, port, edge.Kind, Hot(node, target), node, target);
                }
            }
            foreach (var edge in node.Incoming)
            {
                if (!_enabledKinds.Contains(edge.Kind) || !_model.Nodes.TryGetValue(edge.Source, out var source)) continue;
                if (source.Atom == atom) continue; // drawn once, from the outgoing side
                if (DrawDirect(source, node))
                {
                    Add(source.WorldPosition, node.WorldPosition, edge.Kind, true, source, node);
                }
                else if (_focusView.PortPosition(source.Atom) is { } port)
                {
                    Add(port, node.WorldPosition, edge.Kind, Hot(source, node), source, node);
                }
            }
        }
        _lines.SetLines(lines);
    }

    // ---------------------------------------------------------------- layout tweaks

    private void MoveAtom(AtomData atom, Vector3 position)
    {
        atom.Position = position;
        if (atom.View != null) atom.View.Position = position;
        _nucleons?.UpdateAtom(atom);
        _bonds?.UpdateAtom(atom);
        PlaceMoleculeLabel(atom.Molecule);
        _labels?.UpdatePositions();
        _simulation?.RefreshTrace();
        if (_opened != null)
        {
            _focusView?.UpdatePorts();
            _focusView?.UpdateLayout(Mathf.SmoothStep(0f, 1f, _openAmount));
            UpdateLines();
        }
    }

    private void PinAtom(AtomData atom)
    {
        if (_model == null) return;
        atom.Pinned = true;
        _pins[atom.Id] = new Vector2(atom.Position.X, atom.Position.Z);
        LayoutStore.Save(_model.Graph.Repo.Root, _pins);
        _hud.SetStatus($"Moved {atom.Name}; layout saved. View ▸ Reset Layout undoes all moves.");
    }

    private void ResetLayout()
    {
        if (_model == null) return;
        if (_opened != null) CloseImmediately(_opened);
        _opened = null;
        _openAmount = _openTarget = 0f;
        _pins.Clear();
        LayoutStore.Save(_model.Graph.Repo.Root, _pins);
        Layout.Run(_model, _pins);
        foreach (var atom in _model.AtomList)
        {
            if (atom.View != null) atom.View.Position = atom.Position;
        }
        _nucleons?.UpdateAll();
        _bonds?.UpdateAll();
        foreach (var molecule in _model.Molecules) PlaceMoleculeLabel(molecule);
        if (_focusAtom != null) FocusAtom(_focusAtom, fly: false);
        _hud.SetStatus("Layout reset.");
    }

    private static void PlaceMoleculeLabel(MoleculeData molecule)
    {
        if (molecule.Label == null || molecule.Atoms.Count == 0) return;
        var center = Vector3.Zero;
        var top = float.MinValue;
        foreach (var atom in molecule.Atoms)
        {
            center += atom.Position;
            top = Mathf.Max(top, atom.Position.Y + atom.Radius);
        }
        center /= molecule.Atoms.Count;
        molecule.Label.Position = new Vector3(center.X, top + 3f, center.Z);
    }

    private void FrameAll(bool immediate)
    {
        if (_model == null || _model.AtomList.Count == 0)
        {
            _camera.FlyTo(Vector3.Zero, 30f, immediate);
            return;
        }
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var atom in _model.AtomList)
        {
            var p = atom.Position;
            var r = atom.Radius;
            min = new Vector3(Mathf.Min(min.X, p.X - r), Mathf.Min(min.Y, p.Y - r), Mathf.Min(min.Z, p.Z - r));
            max = new Vector3(Mathf.Max(max.X, p.X + r), Mathf.Max(max.Y, p.Y + r), Mathf.Max(max.Z, p.Z + r));
        }
        var size = (max - min).Length();
        var center = (min + max) * 0.5f;
        if (!float.IsFinite(size) || !center.IsFinite())
        {
            GD.PushError($"[valence] layout produced invalid positions (bounds {min} .. {max})");
            return;
        }
        _camera.FlyTo(center, size * 0.85f + 6f, immediate);
        if (immediate) GD.Print($"[valence] framing center {center}, size {size:F1}, camera at {_camera.GlobalPosition}");
    }

    // ---------------------------------------------------------------- window

    private static bool IsFullscreen() =>
        DisplayServer.WindowGetMode() is DisplayServer.WindowMode.ExclusiveFullscreen or DisplayServer.WindowMode.Fullscreen;

    /// <summary>View ▸ Full Screen: exclusive full screen, or a normal window filling the screen.</summary>
    private void ApplyFullscreen(bool fullscreen, bool save)
    {
        if (IsFullscreen() != fullscreen)
        {
            DisplayServer.WindowSetMode(fullscreen ? DisplayServer.WindowMode.ExclusiveFullscreen : DisplayServer.WindowMode.Maximized);
        }
        _hud.SetFullscreen(fullscreen);
        if (!save) return;
        _settings.Fullscreen = fullscreen;
        _settings.Save();
    }

    // ---------------------------------------------------------------- input

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton button:
                HandleMouseButton(button);
                break;
            case InputEventMouseMotion motion:
                HandleMouseMotion(motion);
                break;
            case InputEventPanGesture pan:
                if (pan.ShiftPressed) _camera.Pan(pan.Delta * 12f);
                else _camera.Orbit(pan.Delta * 10f);
                break;
            case InputEventMagnifyGesture magnify when magnify.Factor > 0f:
                _camera.Zoom(1f / magnify.Factor);
                break;
            case InputEventKey { Pressed: true, Echo: false } key:
                HandleKey(key);
                break;
        }
    }

    private void HandleMouseButton(InputEventMouseButton button)
    {
        var factor = button.Factor > 0f ? button.Factor : 1f;
        switch (button.ButtonIndex)
        {
            case MouseButton.Left when button.Pressed && button.DoubleClick:
                _leftDown = false;
                if (PickAtom(button.Position) == null)
                {
                    ClearSelection();
                    FrameAll(false);
                }
                break;
            case MouseButton.Left when button.Pressed:
                _leftDown = true;
                _dragged = false;
                _pressPosition = button.Position;
                _dragAtom = button.ShiftPressed && _tool == ViewTool.Select ? PickAtom(button.Position) : null;
                if (_dragAtom != null && RayPlane(button.Position, _dragAtom.Position.Y) is { } hit)
                {
                    _dragOffset = _dragAtom.Position - hit;
                }
                break;
            case MouseButton.Left:
                if (!_leftDown) break;
                _leftDown = false;
                if (_dragAtom != null && _dragged) PinAtom(_dragAtom);
                else if (!_dragged && _tool == ViewTool.Select) Click(button.Position);
                _dragAtom = null;
                break;
            case MouseButton.WheelUp when button.Pressed:
                _camera.Zoom(1f / (1f + 0.12f * factor));
                break;
            case MouseButton.WheelDown when button.Pressed:
                _camera.Zoom(1f + 0.12f * factor);
                break;
        }
    }

    private void HandleMouseMotion(InputEventMouseMotion motion)
    {
        if (_leftDown && motion.ButtonMask.HasFlag(MouseButtonMask.Left))
        {
            if (!_dragged && (motion.Position - _pressPosition).Length() > 4f) _dragged = true;
            if (!_dragged) return;
            ClearHover();
            if (_dragAtom != null)
            {
                if (RayPlane(motion.Position, _dragAtom.Position.Y) is { } hit) MoveAtom(_dragAtom, hit + _dragOffset);
            }
            else
            {
                switch (_tool)
                {
                    case ViewTool.Pan:
                        _camera.Pan(motion.Relative);
                        break;
                    case ViewTool.Zoom:
                        _camera.Zoom(1f + motion.Relative.Y * 0.006f);
                        break;
                    default:
                        _camera.Orbit(motion.Relative);
                        break;
                }
            }
        }
        else if (motion.ButtonMask.HasFlag(MouseButtonMask.Right) || motion.ButtonMask.HasFlag(MouseButtonMask.Middle))
        {
            ClearHover();
            _camera.Pan(motion.Relative);
        }
        else
        {
            UpdateHover(motion.Position);
        }
    }

    private void HandleKey(InputEventKey key)
    {
        var command = key.IsCommandOrControlPressed();
        switch (key.Keycode)
        {
            case Key.O when command && key.ShiftPressed:
                _hud.PopupOpenGraph();
                break;
            case Key.O when command:
                _hud.PopupOpenSource();
                break;
            case Key.R when command:
                Reanalyze();
                break;
            case Key.F11:
            case Key.F when key.CtrlPressed && key.MetaPressed:
                ApplyFullscreen(!IsFullscreen(), save: true);
                break;
            case Key.F when command:
                _hud.FocusSearch();
                break;
            case Key.F:
                FrameAll(false);
                break;
            case Key.D when !command:
                StartFlow(FlowDirection.Downstream);
                break;
            case Key.U when !command:
                StartFlow(FlowDirection.Upstream);
                break;
            case Key.G when !command:
                StartTrigger(new[] { Triggers.Frame, Triggers.Physics, Triggers.Draw }, loop: true, "Game loop");
                break;
            case Key.Space:
                TogglePause();
                break;
            case Key.L when !command:
                SetLabelMode(_labelMode switch
                {
                    LabelMode.All => LabelMode.NearMouse,
                    LabelMode.NearMouse => LabelMode.OnClick,
                    _ => LabelMode.All,
                });
                break;
            case Key.H when !command:
                SetUntouchedMode(_untouched switch
                {
                    UntouchedMode.Show => UntouchedMode.Ghost,
                    UntouchedMode.Ghost => UntouchedMode.Hide,
                    _ => UntouchedMode.Show,
                });
                break;
            case Key.Escape:
                Back();
                break;
        }
    }

    private void Click(Vector2 screen)
    {
        if (_model == null) return;
        if (PickPort(screen) is { } port)
        {
            FocusAtom(port.Other);
            return;
        }
        if (_focusAtom != null && PickNode(screen, _focusAtom) is { } node)
        {
            SelectNode(node);
            return;
        }
        if (PickAtom(screen) is { } atom)
        {
            if (atom != _focusAtom) FocusAtom(atom);
            else if (_selectedNode != null) FocusAtom(atom, fly: false); // empty space inside the open atom: deselect
            return;
        }
        ClearSelection();
    }

    // ---------------------------------------------------------------- hover

    private void UpdateHover(Vector2 screen)
    {
        if (_model == null) return;
        var port = PickPort(screen);
        var node = port == null && _focusAtom != null ? PickNode(screen, _focusAtom) : null;
        var atom = port == null && node == null ? PickAtom(screen) : null;
        if (atom == _focusAtom) atom = null;

        var changed = node != _hoverNode || atom != _hoverAtom || port != _hoverPort;
        _hoverNode = node;
        _hoverAtom = atom;
        _hoverPort = port;

        _hud.ShowTooltip(TooltipText(), screen);
        if (changed && _selectedNode == null) RefreshSelection();
    }

    private void ClearHover()
    {
        _hud.ShowTooltip(null, Vector2.Zero);
        if (_hoverNode == null && _hoverAtom == null && _hoverPort == null) return;
        _hoverNode = null;
        _hoverAtom = null;
        _hoverPort = null;
        if (_selectedNode == null) RefreshSelection();
    }

    private string? TooltipText()
    {
        if (_hoverPort != null) return $"{_hoverPort.Text}\nClick to go to {_hoverPort.Other.Name}";
        if (_hoverNode != null)
        {
            var info = _hoverNode.Info;
            var risk = _colorMode == ColorMode.Risk ? $"\nrisk {_hoverNode.Risk.Score:0.00}" : "";
            return $"{info.Name}  ·  {info.Kind}, {info.Visibility}\n" +
                   $"used by {_hoverNode.FanIn} · depends on {_hoverNode.FanOut} · {info.Members.Count} members{risk}";
        }
        if (_hoverAtom != null)
        {
            return $"{_hoverAtom.Name}\n{_hoverAtom.Nodes.Count} classes · valence {_hoverAtom.Valence} · click to open";
        }
        return null;
    }

    // ---------------------------------------------------------------- picking

    private AtomData? PickAtom(Vector2 screen)
    {
        if (_model == null) return null;
        var origin = _camera.ProjectRayOrigin(screen);
        var direction = _camera.ProjectRayNormal(screen);
        AtomData? best = null;
        var bestDistance = float.MaxValue;
        foreach (var atom in _model.AtomList)
        {
            if (atom.IsHidden) continue;
            var radius = atom == _opened ? atom.DisplayRadius * 0.9f : atom.Radius * 0.8f;
            if (RaySphere(origin, direction, atom.Position, radius) is { } t && t < bestDistance)
            {
                best = atom;
                bestDistance = t;
            }
        }
        return best;
    }

    private NodeData? PickNode(Vector2 screen, AtomData atom)
    {
        var origin = _camera.ProjectRayOrigin(screen);
        var direction = _camera.ProjectRayNormal(screen);
        NodeData? best = null;
        var bestDistance = float.MaxValue;
        foreach (var node in atom.Nodes)
        {
            if (node.IsHidden) continue;
            if (RaySphere(origin, direction, node.WorldPosition, node.Radius * 1.4f) is { } t && t < bestDistance)
            {
                best = node;
                bestDistance = t;
            }
        }
        return best;
    }

    private PortData? PickPort(Vector2 screen)
    {
        if (_focusView == null || _opened == null || _openAmount < 0.5f) return null;
        var origin = _camera.ProjectRayOrigin(screen);
        var direction = _camera.ProjectRayNormal(screen);
        PortData? best = null;
        var bestDistance = float.MaxValue;
        foreach (var port in _focusView.Ports)
        {
            if (port.Other.IsHidden) continue;
            if (RaySphere(origin, direction, port.Position, 0.45f) is { } t && t < bestDistance)
            {
                best = port;
                bestDistance = t;
            }
        }
        return best;
    }

    private Vector3? RayPlane(Vector2 screen, float height)
    {
        var origin = _camera.ProjectRayOrigin(screen);
        var direction = _camera.ProjectRayNormal(screen);
        if (Mathf.Abs(direction.Y) < 1e-4f) return null;
        var t = (height - origin.Y) / direction.Y;
        return t < 0f ? null : origin + direction * t;
    }

    private static float? RaySphere(Vector3 origin, Vector3 direction, Vector3 center, float radius)
    {
        var toCenter = center - origin;
        var along = toCenter.Dot(direction);
        if (along < 0f) return null;
        var distanceSquared = toCenter.LengthSquared() - along * along;
        return distanceSquared <= radius * radius ? along : null;
    }
}
