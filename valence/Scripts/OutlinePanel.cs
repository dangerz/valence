using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Valence.Viewer;

/// <summary>
/// Searchable tree: ★ Favorites, then project → system → class, all alphabetical, with class counts.
/// Picking an item selects it in 3D, so finding something never means clicking around in the scene.
/// The eye on the right of each row cycles Shown → Quiet → Hidden; right-click a row for those
/// choices and to add or remove it from Favorites.
/// </summary>
public partial class OutlinePanel : PanelContainer
{
    private const int NameColumn = 0;
    private const int CountColumn = 1;
    private const int StateColumn = 2;
    private const int IconSize = 18;
    private const int MenuShow = 0;
    private const int MenuQuiet = 1;
    private const int MenuHide = 2;
    private const int MenuShowAll = 3;
    private const int MenuFavorite = 4;

    /// <summary>Main tree rows by key (what <see cref="Reveal"/> selects).</summary>
    private readonly Dictionary<string, TreeItem> _items = new(StringComparer.Ordinal);

    /// <summary>Rows in the Favorites section, by key.</summary>
    private readonly Dictionary<string, TreeItem> _favoriteItems = new(StringComparer.Ordinal);

    private LineEdit? _filter;
    private Tree? _tree;
    private PopupMenu? _context;
    private GraphModel? _model;
    private Func<string, Visibility> _stateOf = _ => Visibility.Shown;
    private ISet<string> _favorites = new HashSet<string>();
    private string? _contextKey;
    private bool _suppressSelection;

    /// <summary>"atom:&lt;id&gt;" or "node:&lt;id&gt;".</summary>
    public event Action<string>? ItemChosen;

    /// <summary>Visibility change for a "molecule:", "atom:" or "node:" key.</summary>
    public event Action<string, Visibility>? ItemVisibilityChanged;

    /// <summary>Show everything again.</summary>
    public event Action? ShowAllRequested;

    /// <summary>Key, and whether it should now be a favorite.</summary>
    public event Action<string, bool>? FavoriteToggled;

    public void Build()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        AddChild(column);

        var header = UiKit.Header("menu", UiKit.Title("Outline"));
        header.AddChild(UiKit.Spacer());
        header.AddChild(UiKit.IconButton("plus-square", "Expand all", () => SetAllCollapsed(false), 30f));
        header.AddChild(UiKit.IconButton("minus-square", "Collapse all", () => SetAllCollapsed(true), 30f));
        column.AddChild(header);

        column.AddChild(UiKit.SearchField("Filter classes and systems… (⌘F)", out var filter));
        _filter = filter;
        _filter.TextChanged += _ => Rebuild();
        _filter.TextSubmitted += _ => ChooseFirstMatch();

        _tree = new Tree
        {
            HideRoot = true,
            Columns = 3,
            SelectMode = Tree.SelectModeEnum.Single,
            AllowRmbSelect = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _tree.SetColumnExpand(NameColumn, true);
        _tree.SetColumnExpand(CountColumn, false);
        _tree.SetColumnCustomMinimumWidth(CountColumn, 48);
        _tree.SetColumnExpand(StateColumn, false);
        _tree.SetColumnCustomMinimumWidth(StateColumn, 30);
        _tree.ItemSelected += OnItemSelected;
        _tree.ItemMouseSelected += OnItemMouseSelected;
        column.AddChild(_tree);

        column.AddChild(new HSeparator());
        var legend = new HBoxContainer();
        legend.AddThemeConstantOverride("separation", 6);
        AddLegend(legend, "eye", "Shown");
        AddLegend(legend, "eye-quiet", "Quiet");
        AddLegend(legend, "eye-off", "Hidden");
        column.AddChild(legend);

        var hint = new Label
        {
            Text = "Click an eye to cycle · right-click for ★ favorites and more",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", Palette.MutedText);
        column.AddChild(hint);

        _context = new PopupMenu();
        _context.AddItem("★ Add to Favorites", MenuFavorite);
        _context.AddSeparator();
        _context.AddItem("Show", MenuShow);
        _context.AddItem("Quiet (faded)", MenuQuiet);
        _context.AddItem("Hide", MenuHide);
        _context.AddSeparator();
        _context.AddItem("Show Everything", MenuShowAll);
        _context.IdPressed += OnContextMenu;
        AddChild(_context);
    }

    private static void AddLegend(HBoxContainer row, string icon, string text)
    {
        row.AddChild(UiKit.IconRect(icon, 16f, new Color(1f, 1f, 1f, 0.8f)));
        var label = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", Palette.MutedText);
        row.AddChild(label);
        row.AddChild(new Control { CustomMinimumSize = new Vector2(8f, 0f) });
    }

    /// <summary>Expand or collapse every project and system (Favorites stay open).</summary>
    private void SetAllCollapsed(bool collapsed)
    {
        if (_tree?.GetRoot() is not { } root) return;
        foreach (var child in root.GetChildren())
        {
            if (child.GetMetadata(NameColumn).VariantType == Variant.Type.Nil) continue;   // the Favorites section
            child.SetCollapsedRecursive(collapsed);
        }
    }

    public void SetModel(GraphModel? model, Func<string, Visibility> stateOf, ISet<string> favorites)
    {
        _model = model;
        _stateOf = stateOf;
        _favorites = favorites;
        if (_filter != null) _filter.Text = "";
        Rebuild();
    }

    /// <summary>Favorites changed: rebuild, keeping the current filter.</summary>
    public void RefreshFavorites() => Rebuild();

    /// <summary>Re-reads every item's visibility after a change.</summary>
    public void RefreshStates()
    {
        foreach (var (key, item) in _items) ApplyState(item, key);
        foreach (var (key, item) in _favoriteItems) ApplyState(item, key);
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
        item.Select(NameColumn);
        _tree.ScrollToItem(item);
        _suppressSelection = false;
    }

    // ---------------------------------------------------------------- building

    private static readonly StringComparer ByName = StringComparer.OrdinalIgnoreCase;

    private void Rebuild()
    {
        if (_tree == null) return;
        _tree.Clear();
        _items.Clear();
        _favoriteItems.Clear();
        if (_model == null) return;

        var filter = _filter?.Text.Trim() ?? "";
        var filtering = filter.Length > 0;
        bool Matches(string text) => text.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var root = _tree.CreateItem();
        BuildFavorites(root, filtering ? new Func<string, bool>(Matches) : null);

        foreach (var molecule in _model.Molecules.OrderBy(m => m.Name, ByName))
        {
            TreeItem? moleculeItem = null;
            foreach (var atom in molecule.Atoms.OrderBy(a => a.Name, ByName))
            {
                var atomMatches = filtering && Matches(atom.Name);
                var nodes = filtering && !atomMatches
                    ? atom.Nodes.Where(n => Matches(n.Info.Name)).ToList()
                    : atom.Nodes;
                if (filtering && !atomMatches && nodes.Count == 0) continue;

                if (moleculeItem == null)
                {
                    moleculeItem = _tree.CreateItem(root);
                    Register(moleculeItem, "molecule:" + molecule.Id, molecule.Name, _items);
                    SetIcon(moleculeItem, "dot", Palette.MoleculeHue(molecule.Id));
                    SetCount(moleculeItem, molecule.Atoms.Sum(a => a.Nodes.Count));
                }

                var atomItem = _tree.CreateItem(moleculeItem);
                Register(atomItem, "atom:" + atom.Id, atom.Name, _items);
                SetIcon(atomItem, AtomIcon(atom.Name));
                SetCount(atomItem, atom.Nodes.Count);
                atomItem.SetTooltipText(NameColumn, $"{atom.Name}: {atom.Nodes.Count} classes, valence {atom.Valence}");
                atomItem.Collapsed = !filtering;

                foreach (var node in nodes.OrderBy(n => n.Info.Name, ByName))
                {
                    var nodeItem = _tree.CreateItem(atomItem);
                    Register(nodeItem, "node:" + node.Info.Id, node.Info.Name, _items);
                    SetIcon(nodeItem, "file");
                    nodeItem.SetTooltipText(NameColumn, $"{node.Info.FullName}\n{node.Info.Kind} · {node.Info.Visibility}");
                }
            }
        }
    }

    /// <summary>★ Favorites at the top: projects, systems and classes, alphabetically.</summary>
    private void BuildFavorites(TreeItem root, Func<string, bool>? matches)
    {
        if (_model == null || _favorites.Count == 0) return;
        var entries = _favorites
            .Select(key => (Key: key, Name: DisplayName(key), Kind: KindLabel(key)))
            .Where(e => e.Name != null && (matches == null || matches(e.Name)))
            .OrderBy(e => e.Name, ByName)
            .ToList();
        if (entries.Count == 0) return;

        var section = _tree!.CreateItem(root);
        section.SetText(NameColumn, "Favorites");
        section.SetCustomColor(NameColumn, Palette.Gold);
        SetIcon(section, "star");
        SetCount(section, entries.Count);
        section.SetSelectable(NameColumn, false);
        section.SetSelectable(CountColumn, false);
        section.SetSelectable(StateColumn, false);
        foreach (var (key, name, kind) in entries)
        {
            var item = _tree.CreateItem(section);
            Register(item, key, $"{name}  · {kind}", _favoriteItems);
            if (key.StartsWith("molecule:", StringComparison.Ordinal)) SetIcon(item, "dot", Palette.MoleculeHue(key["molecule:".Length..]));
            else SetIcon(item, key.StartsWith("atom:", StringComparison.Ordinal) ? AtomIcon(name!) : "file");
        }
    }

    private string? DisplayName(string key)
    {
        if (_model == null) return null;
        if (key.StartsWith("node:", StringComparison.Ordinal))
            return _model.Nodes.TryGetValue(key["node:".Length..], out var node) ? node.Info.Name : null;
        if (key.StartsWith("atom:", StringComparison.Ordinal))
            return _model.Atoms.TryGetValue(key["atom:".Length..], out var atom) ? atom.Name : null;
        if (key.StartsWith("molecule:", StringComparison.Ordinal))
            return _model.Molecules.FirstOrDefault(m => m.Id == key["molecule:".Length..])?.Name;
        return null;
    }

    private static string KindLabel(string key) =>
        key.StartsWith("node:", StringComparison.Ordinal) ? "class"
        : key.StartsWith("atom:", StringComparison.Ordinal) ? "system"
        : "project";

    private void Register(TreeItem item, string key, string text, Dictionary<string, TreeItem> index)
    {
        item.SetText(NameColumn, _favorites.Contains(key) && index == _items ? "★ " + text : text);
        item.SetMetadata(NameColumn, key);
        item.SetMetadata(CountColumn, key);
        item.SetMetadata(StateColumn, key);
        item.SetIconMaxWidth(StateColumn, IconSize);
        item.SetTooltipText(StateColumn, "Shown / Quiet / Hidden: click to cycle");
        index[key] = item;
        ApplyState(item, key);
    }

    private void ApplyState(TreeItem item, string key)
    {
        var state = _stateOf(key);
        item.SetIcon(StateColumn, UiAssets.Icon(state switch
        {
            Visibility.Quiet => "eye-quiet",
            Visibility.Hidden => "eye-off",
            _ => "eye",
        }));
        item.SetIconModulate(StateColumn, state == Visibility.Shown ? Colors.White : new Color(1f, 1f, 1f, 0.7f));

        var color = NameColor(key);
        if (state != Visibility.Shown) color = color.Darkened(state == Visibility.Hidden ? 0.6f : 0.35f);
        item.SetCustomColor(NameColumn, color);
    }

    private static void SetIcon(TreeItem item, string icon, Color? tint = null)
    {
        item.SetIcon(NameColumn, UiAssets.Icon(icon));
        item.SetIconMaxWidth(NameColumn, IconSize);
        if (tint is { } color) item.SetIconModulate(NameColumn, color);
    }

    /// <summary>A small count badge in its own column.</summary>
    private static void SetCount(TreeItem item, int count)
    {
        item.SetText(CountColumn, count.ToString("N0"));
        item.SetTextAlignment(CountColumn, HorizontalAlignment.Center);
        item.SetCustomBgColor(CountColumn, new Color(Palette.Accent, 0.14f));
        item.SetCustomColor(CountColumn, UiKit.Text);
    }

    /// <summary>"(global)" and other unnamed systems get a globe; namespaces and folders a folder.</summary>
    private static string AtomIcon(string name) => name.StartsWith('(') ? "globe" : "folder";

    private Color NameColor(string key)
    {
        if (_model == null) return Colors.White;
        if (key.StartsWith("molecule:", StringComparison.Ordinal)) return Palette.MoleculeHue(key["molecule:".Length..]);
        if (key.StartsWith("node:", StringComparison.Ordinal) && _model.Nodes.TryGetValue(key["node:".Length..], out var node))
        {
            return node.IsPublic ? Palette.Proton.Lightened(0.3f) : Palette.Neutron.Lightened(0.3f);
        }
        return new Color(0.88f, 0.9f, 0.95f);
    }

    private static Visibility Next(Visibility state) => state switch
    {
        Visibility.Shown => Visibility.Quiet,
        Visibility.Quiet => Visibility.Hidden,
        _ => Visibility.Shown,
    };

    // ---------------------------------------------------------------- input

    private void ChooseFirstMatch()
    {
        var first = _items
                        .Where(kv => kv.Key.StartsWith("node:", StringComparison.Ordinal))
                        .Select(kv => kv.Value)
                        .FirstOrDefault()
                    ?? _items.Values.FirstOrDefault();
        if (first == null) return;
        first.UncollapseTree();
        first.Select(NameColumn); // raises ItemSelected → ItemChosen
    }

    private void OnItemSelected()
    {
        if (_suppressSelection || _tree?.GetSelected() is not { } item) return;
        if (Input.IsMouseButtonPressed(MouseButton.Right)) return; // the context menu handles it
        var key = item.GetMetadata(NameColumn).AsString();
        if (string.IsNullOrEmpty(key)) return;

        if (_tree.GetSelectedColumn() == StateColumn)
        {
            // Visibility toggle; deselect so clicking it again toggles again.
            _tree.DeselectAll();
            ItemVisibilityChanged?.Invoke(key, Next(_stateOf(key)));
            return;
        }
        if (!key.StartsWith("molecule:", StringComparison.Ordinal)) ItemChosen?.Invoke(key);
    }

    private void OnItemMouseSelected(Vector2 position, long mouseButton)
    {
        if (mouseButton != (long)MouseButton.Right || _tree == null || _context == null) return;
        var item = _tree.GetItemAtPosition(position);
        if (item == null) return;
        _contextKey = item.GetMetadata(NameColumn).AsString();
        if (string.IsNullOrEmpty(_contextKey)) return;
        var favoriteIndex = _context.GetItemIndex(MenuFavorite);
        _context.SetItemText(favoriteIndex, _favorites.Contains(_contextKey) ? "☆ Remove from Favorites" : "★ Add to Favorites");
        _context.Position = (Vector2I)GetGlobalMousePosition();
        _context.Popup();
    }

    private void OnContextMenu(long id)
    {
        if (id == MenuShowAll)
        {
            ShowAllRequested?.Invoke();
            return;
        }
        if (string.IsNullOrEmpty(_contextKey)) return;
        if (id == MenuFavorite)
        {
            FavoriteToggled?.Invoke(_contextKey, !_favorites.Contains(_contextKey));
            return;
        }
        var state = id switch
        {
            MenuQuiet => Visibility.Quiet,
            MenuHide => Visibility.Hidden,
            _ => Visibility.Shown,
        };
        ItemVisibilityChanged?.Invoke(_contextKey, state);
    }
}
