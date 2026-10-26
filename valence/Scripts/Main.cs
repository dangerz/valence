using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Valence.Schema;

namespace Valence.Viewer;

/// <summary>
/// Scene root. Loads a graph, builds the atoms/bonds/nucleons, and handles selection and input.
///
/// Hierarchy on screen: molecule (project) → atom (namespace / system) → nucleon (class).
/// </summary>
public partial class Main : Node3D
{
    private const string DemoGraph = "res://samples/demo.valence.json";
    private const float RecencyWindowDays = 14f;
    private const int MaxAtomLines = 600;

    private readonly HashSet<string> _enabledKinds = new(Palette.EdgeKindOrder);
    private readonly ConcurrentQueue<Action> _mainThread = new();
    private readonly HashSet<NodeData> _selectedNeighbors = new();

    private SceneAssets _assets = null!;
    private OrbitCamera _camera = null!;
    private Hud _hud = null!;

    private Node3D? _world;
    private GraphModel? _model;
    private NucleonRenderer? _nucleons;
    private BondRenderer? _bonds;
    private DetailLines? _lines;
    private Dictionary<string, Vector2> _pins = new();
    private ColorMode _colorMode = ColorMode.Visibility;

    private AtomData? _focusAtom;
    private NodeData? _selectedNode;

    private bool _leftDown;
    private bool _dragged;
    private Vector2 _pressPosition;
    private AtomData? _dragAtom;
    private Vector3 _dragOffset;

    private string _lastSearch = "";
    private int _searchIndex;
    private bool _analyzing;

    public override void _Ready()
    {
        _assets = new SceneAssets();
        BuildEnvironment();

        _camera = new OrbitCamera();
        AddChild(_camera);
        _camera.MakeCurrent();

        _hud = new Hud();
        _hud.Build();
        var canvas = new CanvasLayer();
        AddChild(canvas);
        canvas.AddChild(_hud);

        _hud.GraphFileChosen += LoadGraph;
        _hud.AnalyzeFolderChosen += Analyze;
        _hud.SearchSubmitted += Search;
        _hud.ColorModeChanged += mode =>
        {
            _colorMode = mode;
            UpdateLegend();
            ApplyColors();
        };
        _hud.FrameAllRequested += () => FrameAll(false);
        _hud.ResetLayoutRequested += ResetLayout;
        _hud.EdgeKindToggled += (kind, on) =>
        {
            if (on) _enabledKinds.Add(kind);
            else _enabledKinds.Remove(kind);
            UpdateLines();
        };
        _hud.LinkClicked += OnLink;

        var startup = StartupGraph();
        GD.Print($"[valence] viewer ready; loading {startup}");
        LoadGraph(startup);
    }

    public override void _Process(double delta)
    {
        while (_mainThread.TryDequeue(out var action)) action();
    }

    // ---------------------------------------------------------------- loading

    private static string StartupGraph()
    {
        var args = OS.GetCmdlineUserArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--graph") return args[i + 1];
        }
        var last = ViewerSettings.LastGraph;
        return !string.IsNullOrEmpty(last) && System.IO.File.Exists(last) ? last : DemoGraph;
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

        foreach (var molecule in _model.Molecules)
        {
            var tint = Palette.MoleculeHue(molecule.Id);
            molecule.Label = new Label3D
            {
                Text = molecule.Name,
                FontSize = 96,
                PixelSize = 0.03f,
                OutlineSize = 18,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                Modulate = tint,
                VisibilityRangeBegin = 45f,
                VisibilityRangeBeginMargin = 10f,
                VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
            };
            _world.AddChild(molecule.Label);
            PlaceMoleculeLabel(molecule);
        }

        UpdateLegend();
        ApplyColors();
        FrameAll(true);

        var repo = graph.Repo;
        var commit = string.IsNullOrEmpty(repo.Commit) ? "" : $" @ {repo.Commit[..Math.Min(8, repo.Commit.Length)]}";
        var branch = string.IsNullOrEmpty(repo.Branch) ? "" : $" ({repo.Branch})";
        _hud.SetRepo($"{repo.Name}{branch}{commit}  ·  {repo.Root}");
        _hud.SetStatus($"{_model.Nodes.Count} classes in {_model.AtomList.Count} systems, " +
                       $"{_model.Molecules.Count} projects, {_model.Bonds.Count} bonds · {path}");
        DisplayServer.WindowSetTitle($"Valence — {repo.Name}");
        GD.Print($"[valence] loaded {path}: {_model.Nodes.Count} classes, {_model.AtomList.Count} systems, " +
                 $"{_model.Bonds.Count} bonds, max layer {_model.MaxLayer}; world children {_world.GetChildCount()}");
        if (!path.StartsWith("res://", StringComparison.Ordinal)) ViewerSettings.LastGraph = path;
    }

    private void Analyze(string directory)
    {
        if (_analyzing)
        {
            _hud.SetStatus("An analysis is already running…");
            return;
        }
        var cli = AnalyzerLauncher.FindCli();
        if (cli == null)
        {
            _hud.SetStatus("Valence CLI not found. Run ./build.sh in the repo (creates dist/valence.dll) or set VALENCE_CLI.");
            return;
        }

        _analyzing = true;
        _hud.SetStatus($"Analyzing {directory} (read-only)…");
        Task.Run(() => AnalyzerLauncher.Run(cli, directory, line => _mainThread.Enqueue(() => _hud.SetStatus(line))))
            .ContinueWith(task =>
            {
                var path = task.IsCompletedSuccessfully ? task.Result : null;
                var error = task.Exception?.GetBaseException().Message;
                _mainThread.Enqueue(() =>
                {
                    _analyzing = false;
                    if (path != null) LoadGraph(path);
                    else _hud.SetStatus($"Analysis failed{(error != null ? ": " + error : "")}. See the log above.");
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
        _focusAtom = atom;
        _selectedNode = null;
        _selectedNeighbors.Clear();
        RefreshSelection();
        if (_model != null) _hud.ShowInfo(InfoText.ForAtom(atom, _model));
        if (fly) _camera.FlyTo(atom.Position, atom.Radius * 3.2f + 6f);
    }

    private void SelectNode(NodeData node, bool fly = true)
    {
        _focusAtom = node.Atom;
        _selectedNode = node;
        _selectedNeighbors.Clear();
        if (_model != null)
        {
            foreach (var edge in node.Outgoing)
            {
                if (_model.Nodes.TryGetValue(edge.Target, out var target)) _selectedNeighbors.Add(target);
            }
            foreach (var edge in node.Incoming)
            {
                if (_model.Nodes.TryGetValue(edge.Source, out var source)) _selectedNeighbors.Add(source);
            }
            _hud.ShowInfo(InfoText.ForNode(node, _model));
        }
        RefreshSelection();
        if (fly) _camera.FlyTo(node.WorldPosition, node.Atom.Radius * 2.2f + 3f);
    }

    private void ClearSelection()
    {
        _focusAtom = null;
        _selectedNode = null;
        _selectedNeighbors.Clear();
        _hud.HideInfo();
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (_model == null) return;
        foreach (var atom in _model.AtomList)
        {
            atom.View?.SetState(atom == _focusAtom, _focusAtom != null && atom != _focusAtom);
        }
        _bonds?.SetFocus(_focusAtom);
        ApplyColors();
        UpdateLines();
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

    private void Search(string text)
    {
        if (_model == null || string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        if (text != _lastSearch)
        {
            _lastSearch = text;
            _searchIndex = 0;
        }

        var matches = _model.Nodes.Values
            .Where(n => n.Info.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || n.Info.FullName.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Info.Name.Equals(text, StringComparison.OrdinalIgnoreCase) ? 0
                : n.Info.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(n => n.Info.Name.Length)
            .ThenBy(n => n.Info.FullName, StringComparer.Ordinal)
            .Cast<object>()
            .Concat(_model.AtomList.Where(a => a.Name.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matches.Count == 0)
        {
            _hud.SetStatus($"No class or system matches \"{text}\"");
            return;
        }

        var index = _searchIndex % matches.Count;
        _searchIndex++;
        switch (matches[index])
        {
            case NodeData node:
                SelectNode(node);
                break;
            case AtomData atom:
                FocusAtom(atom);
                break;
        }
        _hud.SetStatus($"Match {index + 1} of {matches.Count} for \"{text}\" — press Enter for the next");
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

        if (_selectedNode != null)
        {
            if (node == _selectedNode) return new Color(Palette.Selected.R, Palette.Selected.G, Palette.Selected.B, 1f);
            if (!_selectedNeighbors.Contains(node))
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

    private void UpdateLines()
    {
        if (_lines == null || _model == null) return;
        var lines = new List<(Vector3 From, Vector3 To, Color Color)>();

        if (_selectedNode is { } selected)
        {
            foreach (var edge in selected.Outgoing.Where(e => _enabledKinds.Contains(e.Kind)))
            {
                if (_model.Nodes.TryGetValue(edge.Target, out var target))
                {
                    lines.Add((selected.WorldPosition, target.WorldPosition, Palette.EdgeKind(edge.Kind)));
                }
            }
            foreach (var edge in selected.Incoming.Where(e => _enabledKinds.Contains(e.Kind)))
            {
                if (_model.Nodes.TryGetValue(edge.Source, out var source))
                {
                    var color = Palette.EdgeKind(edge.Kind);
                    lines.Add((source.WorldPosition, selected.WorldPosition, new Color(color.R, color.G, color.B, 0.6f)));
                }
            }
        }
        else if (_focusAtom is { } atom)
        {
            // Class-level links leaving or entering the focused system.
            foreach (var node in atom.Nodes)
            {
                foreach (var edge in node.Outgoing.Where(e => _enabledKinds.Contains(e.Kind)))
                {
                    if (lines.Count >= MaxAtomLines) break;
                    if (_model.Nodes.TryGetValue(edge.Target, out var target) && target.Atom != atom)
                    {
                        lines.Add((node.WorldPosition, target.WorldPosition, Palette.EdgeKind(edge.Kind)));
                    }
                }
                foreach (var edge in node.Incoming.Where(e => _enabledKinds.Contains(e.Kind)))
                {
                    if (lines.Count >= MaxAtomLines) break;
                    if (_model.Nodes.TryGetValue(edge.Source, out var source) && source.Atom != atom)
                    {
                        var color = Palette.EdgeKind(edge.Kind);
                        lines.Add((source.WorldPosition, node.WorldPosition, new Color(color.R, color.G, color.B, 0.45f)));
                    }
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
        if (_focusAtom != null) UpdateLines();
    }

    private void PinAtom(AtomData atom)
    {
        if (_model == null) return;
        atom.Pinned = true;
        _pins[atom.Id] = new Vector2(atom.Position.X, atom.Position.Z);
        LayoutStore.Save(_model.Graph.Repo.Root, _pins);
        _hud.SetStatus($"Moved {atom.Name}; layout saved. \"Reset layout\" undoes all moves.");
    }

    private void ResetLayout()
    {
        if (_model == null) return;
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
        UpdateLines();
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
            _camera.Pan(motion.Relative);
        }
    }

    private void HandleKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.F:
                FrameAll(false);
                break;
            case Key.Escape when _selectedNode != null:
                FocusAtom(_selectedNode.Atom);
                break;
            case Key.Escape:
                ClearSelection();
                break;
        }
    }

    private void Click(Vector2 screen)
    {
        if (_model == null) return;
        if (_focusAtom != null && PickNode(screen, _focusAtom) is { } node)
        {
            SelectNode(node);
            return;
        }
        if (PickAtom(screen) is { } atom)
        {
            FocusAtom(atom);
            return;
        }
        ClearSelection();
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
            if (RaySphere(origin, direction, atom.Position, atom.Radius * 0.8f) is { } t && t < bestDistance)
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
            if (RaySphere(origin, direction, node.WorldPosition, node.Radius * 1.3f) is { } t && t < bestDistance)
            {
                best = node;
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
