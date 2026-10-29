using System.Text.RegularExpressions;
using TreeSitter;
using Valence.Schema;

namespace Valence.Probe.Python;

internal sealed class ExtractionStats
{
    public int Classes;
    public int Functions;
    public int Methods;
    public int Imports;
    public int Uses;
    public int LocalClasses;
    public int Fields;
    public int EntryPoints;
}

/// <summary>
/// Pass 1: one walk over a file's syntax tree. Creates the nucleons (classes, and the file's own node
/// for free functions) with their members (methods, functions, fields), and records imports, bindings
/// (assignments, parameters, type hints), entry points and every name use with its scope, in the order
/// Python evaluates them. Resolution happens later, across all files.
/// </summary>
internal sealed class PythonExtractor
{
    // Deeply nested expressions (long concatenations) beyond this are skipped rather than overflowing the stack.
    private const int MaxDepth = 400;
    private const int MaxBindingsPerName = 8;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex DottedName = new(@"^[A-Za-z_][\w]*(\.[A-Za-z_][\w]*)*$", RegexOptions.Compiled);
    private static readonly Regex NameToken = new(@"[A-Za-z_][\w]*(\.[A-Za-z_][\w]*)*", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s", RegexOptions.Compiled);
    private static readonly Regex AbstractBase = new(@"\b(?:ABC|ABCMeta|Protocol)\b", RegexOptions.Compiled);

    // Entry-point decorators (until data-driven framework packs): web routes, task queues, CLIs.
    private static readonly Regex RouteDecorator = new(
        @"^[\w.]+\.(?<verb>route|get|post|put|patch|delete|head|options|websocket|api_route)\((?<args>.*)\)$",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex TaskDecorator = new(@"^(?:[\w.]+\.)?(?:task|shared_task)(?:\(.*\))?$",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex CommandDecorator = new(@"^[\w.]+\.(?:command|group)(?:\(.*\))?$",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex FirstString = new(@"^\s*[rbuf]*[""'](?<text>[^""']*)[""']", RegexOptions.Compiled);
    private static readonly Regex ReceiverDecorator = new(@"^(?:[\w.]+\.)?receiver\(.*\)$",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex StartupHookDecorator = new(@"^[\w.]+\.on_event\(\s*[""']startup[""']",
        RegexOptions.Compiled);

    // Games: a loop that pumps events or flips the display runs once per frame.
    private static readonly Regex GameLoopCall = new(
        @"\b(?:pygame|pg)\.(?:event\.(?:get|poll|wait)|display\.(?:flip|update))\s*\(|\bclock\.tick\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex WindowBase = new(@"\b(?:arcade\.(?:Window|View)|pyglet\.window\.Window)\b", RegexOptions.Compiled);
    private static readonly Regex SpriteBase = new(@"\bSprite\b", RegexOptions.Compiled);

    /// <summary>Builtins never resolve to repo code; skipping them keeps the use list small.</summary>
    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "print", "len", "range", "str", "int", "float", "bool", "list", "dict", "set", "tuple", "frozenset",
        "bytes", "bytearray", "object", "type", "isinstance", "issubclass", "getattr", "setattr",
        "hasattr", "delattr", "callable", "iter", "next", "enumerate", "zip", "map", "filter", "sorted",
        "reversed", "min", "max", "sum", "any", "all", "abs", "round", "divmod", "pow", "repr", "format", "id",
        "hash", "vars", "dir", "open", "input", "property", "staticmethod", "classmethod", "slice", "chr",
        "ord", "bin", "hex", "oct", "complex", "memoryview", "locals", "globals", "eval", "exec", "compile",
        "exit", "quit", "Exception", "BaseException", "ValueError", "TypeError", "KeyError", "IndexError",
        "AttributeError", "RuntimeError", "NotImplementedError", "StopIteration", "ImportError", "OSError",
        "IOError", "NotImplemented", "Ellipsis", "True", "False", "None", "__name__", "__file__", "__doc__",
    };

    /// <summary>Builtins that reach outside the program: kept as uses so pass 2 can record their effects.</summary>
    internal static readonly HashSet<string> EffectBuiltins = new(StringComparer.Ordinal) { "open", "eval", "exec", "compile", "__import__" };

    /// <summary>An open mode string: "r", "wb", "a+", "rt"…</summary>
    private static readonly Regex OpenMode = new(@"^[rwxabtU+]{1,4}$", RegexOptions.Compiled);

    private readonly PythonProject _project;
    private readonly PyFile _file;
    private readonly ExtractionStats _stats;

    public PythonExtractor(PythonProject project, PyFile file, ExtractionStats stats)
    {
        _project = project;
        _file = file;
        _stats = stats;
    }

    public void Extract(Node root)
    {
        WalkChildren(root, new Scope(_file, null, null), 0);

        // Django management command: python manage.py <file name> runs Command.handle.
        if (("/" + _file.Path).Contains("/management/commands/", StringComparison.Ordinal)
            && _file.Classes.TryGetValue("Command", out var command)
            && command.Methods.TryGetValue("handle", out var handle))
        {
            AddEntryPoint(handle.Member, Triggers.Startup, $"manage.py {Path.GetFileNameWithoutExtension(_file.Path)}");
        }

        // Scripts (no classes, no functions) still show up; empty or class-only __init__.py files don't.
        if (_file.Node == null && _file.Classes.Count == 0 && !_file.IsPackage) _project.FileNode(_file);
    }

    // ------------------------------------------------------------------ statements

    private void Walk(Node node, Scope scope, int depth)
    {
        if (depth > MaxDepth) return;
        switch (node.Type)
        {
            case "class_definition":
                Class(node, scope, depth);
                break;
            case "function_definition":
                Function(node, scope, depth);
                break;
            case "decorated_definition":
                foreach (var child in node.NamedChildren)
                {
                    if (child.Type == "decorator") WalkChildren(child, scope, depth + 1);
                }
                if (node.GetChildForField("definition") is { } definition) Walk(definition, scope, depth + 1);
                break;
            case "import_statement":
                Import(node);
                break;
            case "import_from_statement":
                ImportFrom(node);
                break;
            case "assignment":
                Assignment(node, scope, depth);
                break;
            case "augmented_assignment":
                AugmentedAssignment(node, scope, depth);
                break;
            case "if_statement" when scope.Function == null && scope.Class == null && IsMainGuard(node):
                MainBlock(node, scope, depth);
                break;
            case "while_statement" when IsGameLoop(node):
                GameLoop(node, scope, depth);
                break;
            case "call":
                Call(node, scope, depth);
                break;
            case "identifier":
            case "attribute":
                Mention(node, scope, depth);
                break;
            case "keyword_argument":
                if (node.GetChildForField("value") is { } argument) Walk(argument, scope, depth + 1);
                break;
            case "lambda":
                if (node.GetChildForField("body") is { } lambdaBody) Walk(lambdaBody, scope, depth + 1);
                break;
            case "type":
                TypeUses(node, UseKind.Annotation, scope);
                break;
            case "string":
                // Docstrings and plain strings aren't code; f-string expressions are.
                foreach (var child in node.NamedChildren)
                {
                    if (child.Type == "interpolation") WalkChildren(child, scope, depth + 1);
                }
                break;
            case "future_import_statement":
            case "global_statement":
            case "nonlocal_statement":
            case "comment":
                break;
            default:
                WalkChildren(node, scope, depth);
                break;
        }
    }

    private void WalkChildren(Node node, Scope scope, int depth)
    {
        foreach (var child in node.NamedChildren) Walk(child, scope, depth + 1);
    }

    private void Class(Node node, Scope scope, int depth)
    {
        var name = node.GetChildForField("name")?.Text;
        var superclasses = node.GetChildForField("superclasses");
        var body = node.GetChildForField("body");

        if (string.IsNullOrEmpty(name) || scope.Function != null)
        {
            // A class inside a function isn't a nucleon; its code counts toward the function.
            if (scope.Function != null) _stats.LocalClasses++;
            if (superclasses != null) Walk(superclasses, scope, depth + 1);
            if (body != null) Walk(body, scope, depth + 1);
            return;
        }

        var outer = scope.Class;
        var fullName = (outer?.Node.FullName ?? _file.Module) + "." + name;
        var info = new NodeInfo
        {
            Id = _project.UniqueId("py:" + fullName),
            Name = name,
            FullName = fullName,
            Kind = NodeKinds.Class,
            Module = _file.Atom,
            Visibility = IsPrivate(name) ? "private" : "public",
            Language = PythonProject.Lang,
            File = _file.Path,
            Line = node.StartPosition.Row + 1,
            Files = { _file.Path },
            Container = outer?.Node.Id,
            Metrics = { [MetricKeys.Loc] = node.EndPosition.Row - node.StartPosition.Row + 1, [MetricKeys.Methods] = 0 },
        };
        if (superclasses != null)
        {
            info.Attributes["bases"] = Collapse(superclasses.Text.Trim('(', ')'));
            // abc.ABC, metaclass=ABCMeta and typing.Protocol classes are abstract.
            if (AbstractBase.IsMatch(info.Attributes["bases"])) info.Attributes[AttributeKeys.Abstract] = "true";
        }
        _project.Output.Nodes.Add(info);
        _stats.Classes++;

        var cls = new PyClass(info, _file, outer);
        if (outer != null)
        {
            outer.Nested[name] = cls;
            _project.AddEdge(outer.Node, info, EdgeKinds.Contains, null);
        }
        else
        {
            _file.Classes[name] = cls;
        }

        // Bases are evaluated where the class statement is, but the bond starts at the new class.
        if (superclasses != null)
        {
            foreach (var argument in superclasses.NamedChildren)
            {
                var baseRef = argument.Type == "subscript" ? RefOf(argument.GetChildForField("value")) : RefOf(argument);
                if (baseRef != null && !IsBuiltin(baseRef))
                {
                    cls.Bases.Add(baseRef);
                    AddUse(UseKind.Inherit, baseRef, scope, argument, info);
                }
                else if (baseRef == null)
                {
                    Walk(argument, scope, depth + 1);    // metaclass=…, Generic[T] arguments, calls
                }
            }
        }
        if (body != null) Walk(body, new Scope(_file, cls, null), depth + 1);
    }

    private void Function(Node node, Scope scope, int depth)
    {
        var name = node.GetChildForField("name")?.Text;
        var parameters = node.GetChildForField("parameters");
        var returns = node.GetChildForField("return_type");
        var body = node.GetChildForField("body");

        if (string.IsNullOrEmpty(name) || scope.Function != null)
        {
            // A nested function isn't a member; its parameters and code fold into the enclosing function.
            if (parameters != null) Parameters(parameters, scope, scope.Function, isMethod: false, depth);
            if (returns != null) TypeUses(returns, UseKind.Annotation, scope);
            if (body != null) Walk(body, scope, depth + 1);
            return;
        }

        var cls = scope.Class;
        var owner = cls?.Node ?? _project.FileNode(_file);
        var decorators = Decorators(node);
        var isStatic = decorators.Contains("staticmethod");
        if (cls != null && decorators.Any(d => d is "abstractmethod" or "abc.abstractmethod"))
        {
            cls.Node.Attributes[AttributeKeys.Abstract] = "true";
        }
        var member = Member(node, name, owner, decorators, isMethod: cls != null);
        owner.Members.Add(member);
        owner.Metrics[MetricKeys.Methods] = owner.Metrics.GetValueOrDefault(MetricKeys.Methods) + 1;
        foreach (var decorator in decorators)
        {
            if (EntryPoint(decorator, name) is { } entry) AddEntryPoint(member, entry.Trigger, entry.Label);
        }
        foreach (var (trigger, label) in EngineCallbacks(cls, name)) AddEntryPoint(member, trigger, label);

        var fn = new PyFunction(member, owner, cls, _file)
        {
            SelfIsClass = decorators.Contains("classmethod") || name is "__new__" or "__init_subclass__",
        };
        if (cls != null)
        {
            cls.Methods[name] = fn;
            _stats.Methods++;
        }
        else
        {
            _file.Functions[name] = fn;
            _stats.Functions++;
        }

        var inner = new Scope(_file, cls, fn);
        if (parameters != null) Parameters(parameters, inner, fn, isMethod: cls != null && !isStatic, depth);
        if (returns != null)
        {
            fn.Returns = PrimaryType(returns);
            TypeUses(returns, UseKind.Annotation, inner);
        }
        if (body != null) Walk(body, inner, depth + 1);
    }

    /// <summary>Binds parameters as locals (with their type hints); the first one of a method is self/cls.</summary>
    private void Parameters(Node node, Scope scope, PyFunction? fn, bool isMethod, int depth)
    {
        var first = true;
        foreach (var parameter in node.NamedChildren)
        {
            string? name = null;
            Node? type = null;
            Node? value = null;
            switch (parameter.Type)
            {
                case "identifier":
                    name = parameter.Text;
                    break;
                case "typed_parameter":
                    name = parameter.NamedChildren.FirstOrDefault(c => c.Type == "identifier")?.Text;
                    type = parameter.GetChildForField("type");
                    break;
                case "default_parameter":
                case "typed_default_parameter":
                    name = parameter.GetChildForField("name")?.Text;
                    type = parameter.GetChildForField("type");
                    value = parameter.GetChildForField("value");
                    break;
            }

            var isSelf = first && isMethod && name != null;
            first = false;
            if (isSelf && fn != null) fn.SelfParam = name;
            if (value != null) Walk(value, scope, depth + 1);
            if (type != null) TypeUses(type, UseKind.Annotation, scope);
            if (name != null && fn != null && !isSelf)
            {
                Bind(fn.Locals, name, new Binding(null, type != null ? PrimaryType(type) : null, scope));
            }
        }
    }

    private void Assignment(Node node, Scope scope, int depth)
    {
        var left = node.GetChildForField("left");
        var right = node.GetChildForField("right");
        var type = node.GetChildForField("type");
        if (left == null)
        {
            WalkChildren(node, scope, depth);
            return;
        }

        var target = TargetOf(left, scope);
        if (target is { IsField: true } field && scope.Class != null) FieldMember(scope.Class, field.Name, left, type);
        if (type != null)
        {
            TypeUses(type, target is { IsField: true } ? UseKind.FieldType : UseKind.Annotation, scope);
            if (target is { } typed) Bind(typed.Map, typed.Name, new Binding(null, PrimaryType(type), scope));
        }
        if (right != null)
        {
            if (RefOf(right) is { } value)
            {
                if (target is { } assigned) Bind(assigned.Map, assigned.Name, new Binding(value, null, scope));
                if (target is { IsField: true } && value.Call) AddUse(UseKind.Assigned, value, scope, right);
            }
            Walk(right, scope, depth + 1);
        }

        // The value is computed first, then stored: self.x = …, Foo.count = … are writes.
        if (left.Type == "attribute") Write(left, scope, depth);
        else if (left.Type == "subscript") Walk(left, scope, depth + 1);
    }

    /// <summary>x += …, self.count += 1: the target is read, then written.</summary>
    private void AugmentedAssignment(Node node, Scope scope, int depth)
    {
        if (node.GetChildForField("right") is { } right) Walk(right, scope, depth + 1);
        if (node.GetChildForField("left") is not { } left) return;
        if (left.Type == "attribute")
        {
            Mention(left, scope, depth);
            if (RefOf(left) is { } written && !IsBuiltin(written)) AddUse(UseKind.Write, written, scope, left);
        }
        else
        {
            Walk(left, scope, depth + 1);
        }
    }

    private void Write(Node attribute, Scope scope, int depth)
    {
        if (attribute.GetChildForField("object") is { } obj) WalkChain(obj, scope, depth + 1);
        if (RefOf(attribute) is { } written && !IsBuiltin(written)) AddUse(UseKind.Write, written, scope, attribute);
    }

    /// <summary>A class attribute or self.x becomes a field member, the first time it's assigned.</summary>
    private void FieldMember(PyClass cls, string name, Node at, Node? type)
    {
        if (cls.FieldMembers.ContainsKey(name) || cls.Methods.ContainsKey(name)) return;
        var member = new MemberInfo
        {
            Id = MemberId(cls.Node, name, Line(at)),
            Name = name,
            Kind = MemberKinds.Field,
            Visibility = IsPrivate(name) ? "private" : "public",
            Signature = type != null ? Collapse($"{name}: {type.Text}") : name,
            File = _file.Path,
            Line = Line(at),
        };
        cls.Node.Members.Add(member);
        cls.FieldMembers[name] = member;
        cls.Node.Metrics[MetricKeys.Fields] = cls.Node.Metrics.GetValueOrDefault(MetricKeys.Fields) + 1;
        _stats.Fields++;
    }

    // ------------------------------------------------------------------ entry points

    private static bool IsMainGuard(Node node)
    {
        if (node.GetChildForField("condition") is not { } condition) return false;
        var text = Spaces.Replace(condition.Text, "").Replace('\'', '"');
        return text is "__name__==\"__main__\"" or "\"__main__\"==__name__";
    }

    /// <summary>
    /// <c>if __name__ == "__main__":</c> is the file's startup entry point: its body becomes a function
    /// member (__main__) on the file's node, so what it calls plays in order.
    /// </summary>
    private void MainBlock(Node node, Scope scope, int depth)
    {
        var owner = _project.FileNode(_file);
        var member = new MemberInfo
        {
            Id = MemberId(owner, "__main__", Line(node)),
            Name = "__main__",
            Kind = MemberKinds.Function,
            Visibility = "public",
            Signature = "if __name__ == \"__main__\":",
            File = _file.Path,
            Line = Line(node),
        };
        owner.Members.Add(member);
        AddEntryPoint(member, Triggers.Startup, $"{_file.Path} (__main__)");

        var main = new PyFunction(member, owner, null, _file);
        if (node.GetChildForField("consequence") is { } body) Walk(body, new Scope(_file, null, main), depth + 1);
        foreach (var (field, child) in node.Fields)
        {
            if (field == "alternative") Walk(child, scope, depth + 1);
        }
    }

    /// <summary>Routes (Flask, FastAPI, Quart…), tasks (Celery) and CLI commands (click, Typer) by decorator.</summary>
    private static (string Trigger, string Label)? EntryPoint(string decorator, string name)
    {
        var route = RouteDecorator.Match(decorator);
        if (route.Success)
        {
            var verb = route.Groups["verb"].Value;
            var path = FirstString.Match(route.Groups["args"].Value);
            var method = verb is "route" or "api_route" or "websocket" ? verb : verb.ToUpperInvariant();
            return (Triggers.Request, $"{method} {(path.Success ? path.Groups["text"].Value : "")} → {name}".Replace("  ", " "));
        }
        if (TaskDecorator.IsMatch(decorator)) return (Triggers.Job, $"task {name}");
        if (CommandDecorator.IsMatch(decorator)) return (Triggers.Startup, $"command {name}");
        if (ReceiverDecorator.IsMatch(decorator)) return (Triggers.Event, $"{Shorten(decorator)} → {name}");
        if (StartupHookDecorator.IsMatch(decorator)) return (Triggers.Startup, $"startup hook {name}");
        return null;
    }

    /// <summary>Methods a game engine calls by name: arcade/pyglet windows, sprites, Pygame Zero scripts.</summary>
    private IEnumerable<(string Trigger, string Label)> EngineCallbacks(PyClass? cls, string name)
    {
        if (cls != null)
        {
            if (!cls.Node.Attributes.TryGetValue("bases", out var bases)) yield break;
            if (WindowBase.IsMatch(bases))
            {
                var trigger = name switch
                {
                    "on_draw" => Triggers.Draw,
                    "on_update" or "update" => Triggers.Frame,
                    "on_key_press" or "on_key_release" or "on_mouse_press" or "on_mouse_release" or "on_mouse_motion"
                        or "on_mouse_drag" or "on_mouse_scroll" or "on_text" => Triggers.Input,
                    _ => null,
                };
                if (trigger != null) yield return (trigger, $"{cls.Node.Name}.{name}");
            }
            else if (name == "update" && SpriteBase.IsMatch(bases))
            {
                yield return (Triggers.Frame, $"{cls.Node.Name}.update (sprite group)");
            }
        }
        else if (_file.Imports.ContainsKey("pgzrun") || _file.Imports.ContainsKey("pgzero"))
        {
            var trigger = name switch
            {
                "draw" => Triggers.Draw,
                "update" => Triggers.Frame,
                "on_key_down" or "on_key_up" or "on_mouse_down" or "on_mouse_up" or "on_mouse_move" => Triggers.Input,
                _ => null,
            };
            if (trigger != null) yield return (trigger, $"{name} (Pygame Zero)");
        }
    }

    private static bool IsGameLoop(Node node) =>
        node.GetChildForField("body") is { } body && GameLoopCall.IsMatch(body.Text);

    /// <summary>
    /// A game's main loop: its body becomes a member that plays once per frame (a frame entry point),
    /// entered from the enclosing function at the loop's position. It shares the enclosing locals.
    /// </summary>
    private void GameLoop(Node node, Scope scope, int depth)
    {
        var owner = scope.Function?.Owner ?? _project.FileNode(_file);
        var where = scope.Function?.Member.Name ?? Path.GetFileName(_file.Path);
        var condition = node.GetChildForField("condition");
        var member = new MemberInfo
        {
            Id = MemberId(owner, where + " loop", Line(node)),
            Name = $"{where} loop",
            Kind = MemberKinds.Function,
            Visibility = "public",
            Signature = Collapse($"while {condition?.Text}:"),
            File = _file.Path,
            Line = Line(node),
        };
        owner.Members.Add(member);
        AddEntryPoint(member, Triggers.Frame, $"game loop in {where}");

        var loop = new PyFunction(member, owner, scope.Class, _file, scope.Function?.Locals ?? _file.Globals)
        {
            SelfParam = scope.Function?.SelfParam,
            SelfIsClass = scope.Function?.SelfIsClass ?? false,
        };
        if (scope.Function != null) AddUse(UseKind.Enter, new Ref(null, member.Name, false), scope, node, member: member);
        if (condition != null) Walk(condition, scope, depth + 1);
        if (node.GetChildForField("body") is { } body) Walk(body, new Scope(_file, scope.Class, loop), depth + 1);
        foreach (var (field, child) in node.Fields)
        {
            if (field == "alternative") Walk(child, scope, depth + 1);
        }
    }

    /// <summary>
    /// Framework wiring by call shape: Django url patterns, DRF routers, signal.connect(handler).
    /// Resolved into entry points in pass 2.
    /// </summary>
    private void RecordWiring(Ref callee, Node arguments, Scope scope)
    {
        var positional = arguments.NamedChildren.Where(a => a.Type is not ("keyword_argument" or "comment")).ToList();
        switch (callee.Name)
        {
            case "path" or "re_path" or "url"
                when callee.Receiver == null && positional.Count >= 2 && positional[0].Type == "string"
                     && RefOf(positional[1]) is { } view:
                _file.Wirings.Add(new Wiring(WiringKind.Route, view, scope, $"{callee.Name} '{StringContent(positional[0])}'"));
                break;
            case "register"
                when callee.Receiver != null && positional.Count >= 2 && positional[0].Type == "string"
                     && RefOf(positional[1]) is { } viewSet:
                _file.Wirings.Add(new Wiring(WiringKind.ViewSet, viewSet, scope, $"router '{StringContent(positional[0])}'"));
                break;
            case "connect"
                when callee.Receiver != null && positional.Count >= 1 && RefOf(positional[0]) is { } handler:
                _file.Wirings.Add(new Wiring(WiringKind.Connect, handler, scope, $"{callee.Receiver}.connect"));
                break;
        }
    }

    private static string Shorten(string text)
    {
        var collapsed = Collapse(text);
        return collapsed.Length > 80 ? collapsed[..77] + "…" : collapsed;
    }

    private void AddEntryPoint(MemberInfo member, string trigger, string label)
    {
        _project.Output.EntryPoints.Add(new EntryPointInfo { Member = member.Id, Trigger = trigger, Label = label, Source = "python" });
        _stats.EntryPoints++;
    }

    private readonly record struct Target(Dictionary<string, List<Binding>> Map, string Name, bool IsField);

    /// <summary>Where an assignment's name lands: a local, a class field (incl. self.x), or a global.</summary>
    private Target? TargetOf(Node left, Scope scope)
    {
        if (left.Type == "identifier")
        {
            var name = left.Text;
            if (scope.Function != null) return new Target(scope.Function.Locals, name, false);
            if (scope.Class != null) return new Target(scope.Class.Fields, name, true);
            return new Target(_file.Globals, name, false);
        }
        if (left.Type == "attribute"
            && scope.Class != null
            && scope.Function?.SelfParam is { } self
            && left.GetChildForField("object") is { Type: "identifier" } obj
            && obj.Text == self
            && left.GetChildForField("attribute")?.Text is { } attribute)
        {
            return new Target(scope.Class.Fields, attribute, true);
        }
        return null;
    }

    private static void Bind(Dictionary<string, List<Binding>> map, string name, Binding binding)
    {
        if (!map.TryGetValue(name, out var list)) map[name] = list = new List<Binding>();
        if (list.Count < MaxBindingsPerName) list.Add(binding);
    }

    // ------------------------------------------------------------------ imports

    private void Import(Node node)
    {
        foreach (var child in node.NamedChildren)
        {
            if (child.Type == "dotted_name")
            {
                // import a.b.c binds "a"; a.b.c.X then walks the packages.
                var head = child.Text.Split('.')[0];
                _file.Imports[head] = new ImportBinding(head, null, false, Line(child));
                _stats.Imports++;
            }
            else if (child.Type == "aliased_import"
                     && child.GetChildForField("name") is { } module
                     && child.GetChildForField("alias") is { } alias)
            {
                _file.Imports[alias.Text] = new ImportBinding(module.Text, null, false, Line(child));
                _stats.Imports++;
            }
        }
    }

    private void ImportFrom(Node node)
    {
        string? module = null;
        var isRepoPath = false;
        foreach (var (field, child) in node.Fields)
        {
            if (field != "module_name") continue;
            isRepoPath = child.Type == "relative_import";
            module = isRepoPath ? RelativeModule(child.Text) : child.Text;
        }
        if (module == null) return;

        var line = Line(node);
        foreach (var (field, child) in node.Fields)
        {
            if (child.Type == "wildcard_import")
            {
                _file.Wildcards.Add(new ImportBinding(module, null, isRepoPath, line));
                _stats.Imports++;
            }
            else if (field == "name" && child.Type == "dotted_name")
            {
                _file.Imports[child.Text] = new ImportBinding(module, child.Text, isRepoPath, line);
                _stats.Imports++;
            }
            else if (field == "name"
                     && child.Type == "aliased_import"
                     && child.GetChildForField("name") is { } name
                     && child.GetChildForField("alias") is { } alias)
            {
                _file.Imports[alias.Text] = new ImportBinding(module, name.Text, isRepoPath, line);
                _stats.Imports++;
            }
        }
    }

    /// <summary>"..models" from a/b/c.py → a.models (as a repo path).</summary>
    private string RelativeModule(string text)
    {
        var dots = text.TakeWhile(c => c == '.').Count();
        var rest = text[dots..].Trim();
        var parts = _file.PackageRepoPath.Length == 0 ? new List<string>() : _file.PackageRepoPath.Split('.').ToList();
        parts.RemoveRange(Math.Max(0, parts.Count - (dots - 1)), Math.Min(parts.Count, dots - 1));
        if (rest.Length > 0) parts.AddRange(rest.Split('.'));
        return string.Join('.', parts);
    }

    // ------------------------------------------------------------------ expressions

    /// <summary>The receiver chain and arguments run before the call itself, so their uses come first.</summary>
    private void Call(Node node, Scope scope, int depth)
    {
        if (node.GetChildForField("function") is { } function) WalkChain(function, scope, depth + 1);
        var arguments = node.GetChildForField("arguments");
        if (arguments != null) Walk(arguments, scope, depth + 1);
        if (RefOf(node) is not { } callee) return;
        var argumentList = arguments is { Type: "argument_list" } ? arguments : null;
        // open(), eval() and friends are builtins, but they reach outside the program (the Boundary lens).
        if (!IsBuiltin(callee) || callee is { Receiver: null } && EffectBuiltins.Contains(callee.Name))
        {
            AddUse(UseKind.Call, callee, scope, node, mode: argumentList != null ? ModeOf(callee, argumentList) : null);
        }
        if (argumentList != null) RecordWiring(callee, argumentList, scope);
    }

    /// <summary>The mode of open(path, "w"), io.open(path, mode="a"), ZipFile(path, "w"), path.open("w").</summary>
    private static string? ModeOf(Ref callee, Node arguments)
    {
        if (callee.Name is not ("open" or "ZipFile" or "File")) return null;
        var positional = new List<Node>();
        foreach (var argument in arguments.NamedChildren)
        {
            if (argument.Type == "keyword_argument")
            {
                if (argument.GetChildForField("name")?.Text == "mode" && argument.GetChildForField("value") is { Type: "string" } value)
                {
                    return Mode(value);
                }
            }
            else if (argument.Type != "comment" && positional.Count < 2)
            {
                positional.Add(argument);
            }
        }
        // The mode comes after the path, except on a path object: Path(p).open("w").
        var index = callee.Receiver != null && callee.Name == "open" && positional.Count == 1 ? 0 : 1;
        return positional.Count > index && positional[index].Type == "string" ? Mode(positional[index]) : null;

        static string? Mode(Node literal) => StringContent(literal) is var text && OpenMode.IsMatch(text) ? text : null;
    }

    /// <summary>A name or attribute chain read as a value (not the callee of a call).</summary>
    private void Mention(Node node, Scope scope, int depth)
    {
        if (node.Type == "attribute" && node.GetChildForField("object") is { } obj) WalkChain(obj, scope, depth + 1);
        if (RefOf(node) is { } reference && !IsBuiltin(reference) && !IsLocal(reference, scope))
        {
            AddUse(UseKind.Mention, reference, scope, node);
        }
    }

    /// <summary>The parts of a chain that are code of their own: inner calls, subscripts, literals.</summary>
    private void WalkChain(Node node, Scope scope, int depth)
    {
        if (depth > MaxDepth) return;
        switch (node.Type)
        {
            case "identifier":
                return;
            case "attribute":
                if (node.GetChildForField("object") is { } obj) WalkChain(obj, scope, depth + 1);
                return;
            case "call":
                Call(node, scope, depth + 1);
                return;
            case "parenthesized_expression":
                foreach (var child in node.NamedChildren) WalkChain(child, scope, depth + 1);
                return;
            default:
                Walk(node, scope, depth + 1);
                return;
        }
    }

    /// <summary>The chain an expression names, or null when it isn't one (subscripts, literals, operators).</summary>
    private static Ref? RefOf(Node? node)
    {
        if (node == null) return null;
        switch (node.Type)
        {
            case "identifier":
                return new Ref(null, node.Text, false);
            case "attribute":
                var receiver = RefOf(node.GetChildForField("object"));
                var attribute = node.GetChildForField("attribute")?.Text;
                return receiver != null && attribute != null ? new Ref(receiver, attribute, false) : null;
            case "call":
                var function = RefOf(node.GetChildForField("function"));
                return function is { Call: false } ? function with { Call = true } : null;
            case "parenthesized_expression":
                return node.NamedChildren.Count == 1 ? RefOf(node.NamedChildren[0]) : null;
            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ type hints

    private void TypeUses(Node node, UseKind kind, Scope scope)
    {
        foreach (var chain in TypeChains(node))
        {
            if (!IsBuiltin(chain)) AddUse(kind, chain, scope, node);
        }
    }

    /// <summary>Every class name in a type hint: Dict[str, Foo] → Dict, Foo; "pkg.Foo" strings too.</summary>
    private static IEnumerable<Ref> TypeChains(Node node)
    {
        switch (node.Type)
        {
            case "identifier":
            case "attribute":
                if (RefOf(node) is { } chain) yield return chain;
                break;
            case "member_type":
                if (MemberTypeRef(node) is { } member) yield return member;
                break;
            case "string":
                foreach (Match match in NameToken.Matches(StringContent(node))) yield return Dotted(match.Value);
                break;
            default:
                foreach (var child in node.NamedChildren)
                {
                    foreach (var inner in TypeChains(child)) yield return inner;
                }
                break;
        }
    }

    /// <summary>
    /// The one class a hint makes a value an instance of: Foo, pkg.Foo, "Foo", Optional[Foo], Foo | None.
    /// Containers (list[Foo], dict[…]) name no single instance type.
    /// </summary>
    private static Ref? PrimaryType(Node? node)
    {
        if (node == null) return null;
        switch (node.Type)
        {
            case "type":
            case "parenthesized_expression":
                return node.NamedChildren.Count == 1 ? PrimaryType(node.NamedChildren[0]) : null;
            case "identifier":
            case "attribute":
                return RefOf(node);
            case "member_type":
                return MemberTypeRef(node);
            case "string":
                var text = StringContent(node).Trim();
                return DottedName.IsMatch(text) ? Dotted(text) : null;
            case "generic_type":
            case "subscript":
            {
                var children = node.NamedChildren;
                if (children.Count < 2) return null;
                var head = children[0].Text;
                var arguments = children[1].Type == "type_parameter"
                    ? children[1].NamedChildren.ToList()
                    : children.Skip(1).ToList();
                return head switch
                {
                    "Optional" or "typing.Optional" or "Annotated" or "typing.Annotated" or "ClassVar"
                        or "typing.ClassVar" or "Final" or "typing.Final" => PrimaryType(arguments.FirstOrDefault()),
                    "Union" or "typing.Union" => OnlyNonNone(arguments),
                    _ => null,
                };
            }
            case "union_type":
            case "binary_operator":
                return OnlyNonNone(node.NamedChildren.ToList());
            default:
                return null;
        }
    }

    private static Ref? OnlyNonNone(List<Node> options)
    {
        var real = options.Where(o => o.Type != "none" && o.Text != "None").ToList();
        return real.Count == 1 ? PrimaryType(real[0]) : null;
    }

    /// <summary>pkg.Foo in type position (member_type: type '.' identifier).</summary>
    private static Ref? MemberTypeRef(Node node)
    {
        var parts = node.NamedChildren;
        if (parts.Count < 2 || parts[^1].Type != "identifier") return null;
        var left = TypeChains(parts[0]).FirstOrDefault();
        return left != null ? new Ref(left, parts[^1].Text, false) : null;
    }

    private static string StringContent(Node node)
    {
        var content = string.Concat(node.NamedChildren.Where(c => c.Type == "string_content").Select(c => c.Text));
        return content.Length > 0 ? content : node.Text.Trim('"', '\'');
    }

    private static Ref Dotted(string dotted)
    {
        Ref? chain = null;
        foreach (var part in dotted.Split('.')) chain = new Ref(chain, part, false);
        return chain!;
    }

    // ------------------------------------------------------------------ members

    private static List<string> Decorators(Node function) =>
        function.Parent is { Type: "decorated_definition" } decorated
            ? decorated.NamedChildren.Where(c => c.Type == "decorator").Select(c => c.Text.TrimStart('@').Trim()).ToList()
            : new List<string>();

    private MemberInfo Member(Node function, string name, NodeInfo owner, List<string> decorators, bool isMethod)
    {
        var kind = !isMethod ? MemberKinds.Function
            : name == "__init__" ? MemberKinds.Constructor
            : decorators.Any(d => d == "property" || d.EndsWith(".setter") || d.EndsWith(".getter")) ? MemberKinds.Property
            : MemberKinds.Method;

        var parameters = function.GetChildForField("parameters")?.Text ?? "()";
        var isAsync = function.Children.Any(c => c.Type == "async");
        var signature = Collapse($"{(isAsync ? "async " : "")}def {name}{parameters}");
        if (signature.Length > 160) signature = signature[..157] + "…";

        return new MemberInfo
        {
            Id = MemberId(owner, name, function.StartPosition.Row + 1),
            Name = name,
            Kind = kind,
            Visibility = IsPrivate(name) ? "private" : "public",
            IsStatic = decorators.Contains("staticmethod") || decorators.Contains("classmethod"),
            Signature = signature,
            File = _file.Path,
            Line = function.StartPosition.Row + 1,
        };
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>owner.name, or owner.name#line when the name is taken (property setters, redefinitions).</summary>
    private static string MemberId(NodeInfo owner, string name, int line)
    {
        var id = owner.Id + "." + name;
        return owner.Members.Any(m => m.Id == id) ? id + "#" + line : id;
    }

    private void AddUse(UseKind kind, Ref reference, Scope scope, Node at, NodeInfo? source = null, MemberInfo? member = null,
        string? mode = null)
    {
        _file.Uses.Add(new Use(kind, reference, scope, Line(at), source, member, mode));
        _stats.Uses++;
    }

    /// <summary>A builtin name, unless this file defines or imports something by that name.</summary>
    private bool IsBuiltin(Ref reference)
    {
        var head = reference;
        while (head.Receiver != null) head = head.Receiver;
        var name = head.Name;
        return Builtins.Contains(name)
               && !_file.Imports.ContainsKey(name)
               && !_file.Classes.ContainsKey(name)
               && !_file.Functions.ContainsKey(name)
               && !_file.Globals.ContainsKey(name);
    }

    /// <summary>A bare parameter or local variable: reading it alone bonds to nothing.</summary>
    private static bool IsLocal(Ref reference, Scope scope) =>
        reference.Receiver == null
        && scope.Function is { } fn
        && (reference.Name == fn.SelfParam || fn.Locals.ContainsKey(reference.Name));

    /// <summary>_name is private by convention; __dunder__ names are public protocol.</summary>
    private static bool IsPrivate(string name) =>
        name.StartsWith('_') && !(name.StartsWith("__") && name.EndsWith("__"));

    private static string Collapse(string text) => Whitespace.Replace(text, " ").Trim();

    private static int Line(Node node) => node.StartPosition.Row + 1;
}
