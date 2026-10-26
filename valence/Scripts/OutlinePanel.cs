using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// Searchable tree: project → system → class. Picking an item selects it in 3D, so finding
/// something never means clicking around in the scene.
/// </summary>
public partial class OutlinePanel : PanelContainer
{
    private readonly Dictionary<string, TreeItem> _items = new(StringComparer.Ordinal);
    private LineEdit? _filter;
    private Tree? _tree;
    private GraphModel? _model;
    private bool _suppressSelection;

    /// <summary>"atom:&lt;id&gt;" or "node:&lt;id&gt;".</summary>
    public event Action<string>? ItemChosen;

    public void Build()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        AddChild(column);

        var title = new Label { Text = "Outline" };
        title.AddThemeColorOverride("font_color", Palette.Accent);
        column.AddChild(title);

        _filter = new LineEdit { PlaceholderText = "Filter classes and systems… (⌘F)", ClearButtonEnabled = true };
        _filter.TextChanged += _ => Rebuild();
        _filter.TextSubmitted += _ => ChooseFirstMatch();
        column.AddChild(_filter);

        _tree = new Tree
        {
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Single,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _tree.ItemSelected += OnItemSelected;
        column.AddChild(_tree);
    }

    public void SetModel(GraphModel? model)
    {
        _model = model;
        if (_filter != null) _filter.Text = "";
        Rebuild();
    }

    public void FocusFilter()
    {
        if (_filter == null) return;
        _filter.GrabFocus();
        _filter.SelectAll();
    }

    /// <summary>Expands to, selects and scrolls to an item without re-raising <see cref="ItemChosen"/>.</summary>
    public void Reveal(string meta)
    {
        if (_tree == null) return;
        if (!_items.TryGetValue(meta, out var item))
        {
            _tree.DeselectAll();
            return;
        }
        _suppressSelection = true;
        item.UncollapseTree();
        item.Select(0);
        _tree.ScrollToItem(item);
        _suppressSelection = false;
    }

    private void Rebuild()
    {
        if (_tree == null) return;
        _tree.Clear();
        _items.Clear();
        if (_model == null) return;

        var filter = _filter?.Text.Trim() ?? "";
        var filtering = filter.Length > 0;
        bool Matches(string text) => text.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var root = _tree.CreateItem();
        foreach (var molecule in _model.Molecules)
        {
            TreeItem? moleculeItem = null;
            foreach (var atom in molecule.Atoms)
            {
                var atomMatches = filtering && Matches(atom.Name);
                var nodes = filtering && !atomMatches
                    ? atom.Nodes.Where(n => Matches(n.Info.Name)).ToList()
                    : atom.Nodes;
                if (filtering && !atomMatches && nodes.Count == 0) continue;

                if (moleculeItem == null)
                {
                    moleculeItem = _tree.CreateItem(root);
                    moleculeItem.SetText(0, molecule.Name);
                    moleculeItem.SetCustomColor(0, Palette.MoleculeHue(molecule.Id));
                    moleculeItem.SetSelectable(0, false);
                }

                var atomItem = _tree.CreateItem(moleculeItem);
                atomItem.SetText(0, $"{atom.Name}  ({atom.Nodes.Count})");
                atomItem.SetMetadata(0, "atom:" + atom.Id);
                atomItem.SetTooltipText(0, $"{atom.Name}: {atom.Nodes.Count} classes, valence {atom.Valence}");
                atomItem.Collapsed = !filtering;
                _items["atom:" + atom.Id] = atomItem;

                foreach (var node in nodes.OrderBy(n => n.Info.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var nodeItem = _tree.CreateItem(atomItem);
                    nodeItem.SetText(0, node.Info.Name);
                    nodeItem.SetMetadata(0, "node:" + node.Info.Id);
                    nodeItem.SetTooltipText(0, $"{node.Info.FullName}\n{node.Info.Kind} · {node.Info.Visibility}");
                    nodeItem.SetCustomColor(0, node.IsPublic ? Palette.Proton.Lightened(0.3f) : Palette.Neutron.Lightened(0.3f));
                    _items["node:" + node.Info.Id] = nodeItem;
                }
            }
        }
    }

    private void ChooseFirstMatch()
    {
        var first = _items.Values.FirstOrDefault(i => i.GetMetadata(0).AsString().StartsWith("node:", StringComparison.Ordinal))
                    ?? _items.Values.FirstOrDefault();
        if (first == null) return;
        first.UncollapseTree();
        first.Select(0); // raises ItemSelected → ItemChosen
    }

    private void OnItemSelected()
    {
        if (_suppressSelection || _tree?.GetSelected() is not { } item) return;
        var meta = item.GetMetadata(0).AsString();
        if (!string.IsNullOrEmpty(meta)) ItemChosen?.Invoke(meta);
    }
}
