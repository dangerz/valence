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
        _hud.Build(_settings.ShowOutline, _settings.ShowInspector, _settings.ShowLegend);
        var canvas = new CanvasLayer();
        AddChild(canvas);
        canvas.AddChild(_hud);
        WireHud();
        _hud.SetRecent(_settings.RecentFolders);

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
    }

    private void WireHud()
    {
        _hud.SourceFolderChosen += AnalyzeSourceFolder;
        _hud.GraphFileChosen += LoadGraph;
        _hud.RecentFolderChosen += OpenRecent;
        _hud.ReanalyzeRequested += Reanalyze;
        _hud.QuitRequested += () => GetTree().Quit();
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
            RefreshSelection();
        };
        _hud.FrameAllRequested += () => FrameAll(false);
        _hud.ResetLayoutRequested += ResetLayout;
        _hud.BackRequested += Back;
        _hud.ClearSelectionRequested += ClearSelection;
        _hud.EdgeKindToggled += (kind, on) =>
        {
            if (on) _enabledKinds.Add(kind);
            else _enabledKinds.Remove(kind);
            RefreshSelection();
        };
        _hud.LinkClicked += OnLink;
        _hud.PanelsChanged += (outline, inspector, legend) =>
        {
            _settings.ShowOutline = outline;
            _settings.ShowInspector = inspector;
            _settings.ShowLegend = legend;
            _settings.Save();
        };
        _hud.PointerEnteredUi += ClearHover;
        _hud.Progress.CancelRequested += () => _analysis?.Cancel();
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

        ClearSelection();
        _opened = null;
        _openAmount = _openTarget = 0f;
        _world?.QueueFree();
        _world = new Node3D { Name = "World" };
        AddChild(_world);

        _model = GraphModel.Build(graph);
        _pins = LayoutStore.Load(graph.Repo.Root);
        Layout.Run(_model, _pins);

        foreach (var atom in _model.AtomList)
        {
            var view = new AtomView();
            view.Setup(atom, _assets);
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

        _hud.Outline.SetModel(_model);
        UpdateLegend();
        ApplyColors();
        FrameAll(true);
        ShowSummary();

        var repo = graph.Repo;
        var commit = string.IsNullOrEmpty(repo.Commit) ? "" : $" @ {repo.Commit[..Math.Min(8, repo.Commit.Length)]}";
        var branch = string.IsNullOrEmpty(repo.Branch) ? "" : $" ({repo.Branch})";
        _hud.SetRepo($"{repo.Name}{branch}{commit}  ·  {repo.Root}");
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
        AddChild(new WorldEnvironment { Environment = environment });
        AddChild(new DirectionalLight3D
        {
            LightEnergy = 0.9f,
            RotationDegrees = new Vector3(-50f, 35f, 0f),
        });
    }

    // ---------------------------------------------------------------- selection

    private void FocusAtom(AtomData atom, bool fly = true)
    {
        if (_opened != atom) BeginOpening(atom);
        else _openTarget = 1f; // it may be mid-close
        _focusAtom = atom;
        _selectedNode = null;
        RefreshSelection();
        if (_model != null) _hud.ShowInfo(InfoText.ForAtom(atom, _model));
        _hud.Outline.Reveal("atom:" + atom.Id);
        _hud.SetSelection(atom.Name);
        if (fly) _camera.FlyTo(atom.Position, _openRadius * 2.4f + 4f);
    }

    private void SelectNode(NodeData node, bool fly = true)
    {
        if (_focusAtom != node.Atom) FocusAtom(node.Atom, fly: false);
        _selectedNode = node;
        RefreshSelection();
        if (_model != null) _hud.ShowInfo(InfoText.ForNode(node, _model));
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
        RefreshSelection();
        ShowSummary();
        _hud.Outline.Reveal("");
        _hud.SetSelection("");
    }

    private void Back()
    {
        if (_selectedNode != null) FocusAtom(_selectedNode.Atom, fly: false);
        else ClearSelection();
    }

    private void ShowSummary()
    {
        if (_model != null) _hud.ShowInfo(InfoText.ForSummary(_model));
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
            atom.View?.SetState(atom == _focusAtom, _focusAtom != null && atom != _focusAtom);
        }
        _bonds?.SetFocus(_focusAtom);
        _focusView?.SetHighlight(_lit, _anchor, _hoverNode);
        ApplyColors();
        UpdateLines();
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
        _focusView.Open(atom, _model, _assets, _openRadius);
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
    }

    // ---------------------------------------------------------------- colors & lines

    private void UpdateLegend() => _hud.SetLegend(_colorMode, _model?.HasGit ?? false, _model?.Graph.Repo.ChurnWindowDays);

    private void ApplyColors() => _nucleons?.ApplyColors(NodeColor);

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
            _ => node.IsPublic ? Palette.Proton : Palette.Neutron,
        };
        var glow = RecencyGlow(node, model);

        if (_anchor != null)
        {
            if (node == _anchor) return new Color(Palette.Selected.R, Palette.Selected.G, Palette.Selected.B, 1f);
            if (!_lit.Contains(node))
            {
                color = color.Darkened(0.7f);
                glow *= 0.15f;
            }
        }
        else if (_focusAtom != null && node.Atom != _focusAtom)
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

        bool Hot(NodeData a, NodeData b) =>
            _anchor == null || a == _anchor || b == _anchor || (_selectedNode != null && _lit.Contains(a) && _lit.Contains(b));

        void Add(Vector3 from, Vector3 to, string kind, bool hot)
        {
            if (lines.Count >= MaxFocusLines) return;
            var color = Palette.EdgeKind(kind);
            color.A = _anchor == null ? 0.32f : hot ? 0.95f : 0f;
            if (color.A > 0f) lines.Add((from, to, color));
        }

        foreach (var node in atom.Nodes)
        {
            foreach (var edge in node.Outgoing)
            {
                if (!_enabledKinds.Contains(edge.Kind) || !_model.Nodes.TryGetValue(edge.Target, out var target)) continue;
                if (target.Atom == atom)
                {
                    Add(node.WorldPosition, target.WorldPosition, edge.Kind, Hot(node, target));
                }
                else if (_focusView.PortPosition(target.Atom) is { } port)
                {
                    Add(node.WorldPosition, port, edge.Kind, Hot(node, target));
                }
            }
            foreach (var edge in node.Incoming)
            {
                if (!_enabledKinds.Contains(edge.Kind) || !_model.Nodes.TryGetValue(edge.Source, out var source)) continue;
                if (source.Atom == atom) continue; // drawn once, from the outgoing side
                if (_focusView.PortPosition(source.Atom) is { } port)
                {
                    Add(port, node.WorldPosition, edge.Kind, Hot(source, node));
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
                _dragAtom = button.ShiftPressed ? PickAtom(button.Position) : null;
                if (_dragAtom != null && RayPlane(button.Position, _dragAtom.Position.Y) is { } hit)
                {
                    _dragOffset = _dragAtom.Position - hit;
                }
                break;
            case MouseButton.Left:
                if (!_leftDown) break;
                _leftDown = false;
                if (_dragAtom != null && _dragged) PinAtom(_dragAtom);
                else if (!_dragged) Click(button.Position);
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
                _camera.Orbit(motion.Relative);
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
            case Key.F when command:
                _hud.FocusSearch();
                break;
            case Key.F:
                FrameAll(false);
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
        if (!changed) return;
        if (_selectedNode == null) RefreshSelection();
        else _focusView?.SetHighlight(_lit, _anchor, _hoverNode);
    }

    private void ClearHover()
    {
        _hud.ShowTooltip(null, Vector2.Zero);
        if (_hoverNode == null && _hoverAtom == null && _hoverPort == null) return;
        _hoverNode = null;
        _hoverAtom = null;
        _hoverPort = null;
        if (_selectedNode == null) RefreshSelection();
        else _focusView?.SetHighlight(_lit, _anchor, null);
    }

    private string? TooltipText()
    {
        if (_hoverPort != null) return $"{_hoverPort.Text}\nClick to go to {_hoverPort.Other.Name}";
        if (_hoverNode != null)
        {
            var info = _hoverNode.Info;
            return $"{info.Name}  ·  {info.Kind}, {info.Visibility}\n" +
                   $"used by {_hoverNode.FanIn} · depends on {_hoverNode.FanOut} · {info.Members.Count} members";
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
