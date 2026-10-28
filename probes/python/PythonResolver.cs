using Valence.Core;
using Valence.Schema;

namespace Valence.Probe.Python;

/// <summary>What a name chain evaluates to.</summary>
internal abstract record Value;

internal sealed record ModuleValue(PyModule Module) : Value;

internal sealed record ClassValue(PyClass Class) : Value;

/// <summary>A function or method; <see cref="Receiver"/> is the instance's class when called on one (dispatch).</summary>
internal sealed record FunctionValue(PyFunction Function, PyClass? Receiver = null) : Value;

/// <summary>An instance of a repo class (self, Foo(), a typed parameter or field).</summary>
internal sealed record InstanceValue(PyClass Class) : Value;

/// <summary>
/// <c>super()</c> inside a method of <see cref="Class"/>: its bases, bound to the instance. Members
/// found through it are the base's own (no dispatch to subclass overrides).
/// </summary>
internal sealed record SuperValue(PyClass Class) : Value;

/// <summary>Attribute <see cref="Name"/> of a repo class; <see cref="Type"/> is what it holds, when known.</summary>
internal sealed record FieldValue(PyClass Owner, Value? Type, string Name) : Value;

/// <summary>
/// Pass 2: evaluates every recorded use across the repo and turns it into bonds, plus member links
/// (calls, creates via __init__, field reads/writes) in evaluation order for simulation. Names resolve the
/// way Python does, statically: locals and parameters, then the file (classes, functions, imports,
/// globals, wildcard imports), following re-exports through __init__.py. Attribute chains go through
/// modules, classes (and their bases), and instances whose class is known from self, a constructor
/// call or a type hint. Anything that ends outside the repo (stdlib, packages) bonds to nothing.
/// Confidence: direct names and imports are certain; anything through an inferred type or an
/// approximate import match is "likely".
/// </summary>
internal sealed class PythonResolver
{
    private const int MaxDepth = 32;
    private const int MaxBases = 64;
    private const int MaxOverrides = 32;

    // Methods a class-based view or viewset serves requests through (Django, DRF), else its hooks.
    private static readonly string[] Handlers =
    {
        "get", "post", "put", "patch", "delete", "head", "options",
        "list", "create", "retrieve", "update", "partial_update", "destroy",
    };
    private static readonly string[] HandlerHooks =
    {
        "dispatch", "get_queryset", "get_context_data", "form_valid", "get_object", "get_serializer_class",
    };

    private readonly PythonProject _project;
    private readonly Dictionary<(PyFile, string), Resolved> _fileNames = new();
    private readonly Dictionary<(PyModule, string), Resolved> _moduleNames = new();
    private readonly Dictionary<Binding, Resolved> _bindings = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PyFunction, Resolved> _returns = new();
    private readonly Dictionary<PyClass, List<PyClass>> _mro = new();
    private Dictionary<PyClass, List<PyClass>>? _subclasses;

    public PythonResolver(PythonProject project)
    {
        _project = project;
    }

    /// <summary>Uses that became a bond between nucleons.</summary>
    public int Bonded { get; private set; }

    /// <summary>Uses that resolved inside their own class (self.helper()): no bond, maybe a member link.</summary>
    public int WithinClass { get; private set; }

    /// <summary>Uses that lead outside the repo or can't be resolved statically.</summary>
    public int Unresolved { get; private set; }

    /// <summary>Member links (calls, creates, reads, writes) for simulation.</summary>
    public int Linked { get; private set; }

    /// <summary>Uses in top-level code of files that have no node of their own (class-only files).</summary>
    public int WithoutOwner { get; private set; }

    private readonly record struct Resolved(Value? Value, bool Inferred)
    {
        public static readonly Resolved None = new(null, false);

        public Resolved Infer(bool inferred) => inferred ? this with { Inferred = true } : this;
    }

    public void Run(AnalysisContext context, string phase)
    {
        var files = _project.Files;
        for (var i = 0; i < files.Count; i++)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            context.Report(phase, i, files.Count, files[i].Path);
            foreach (var use in files[i].Uses) Apply(use);
        }
        foreach (var file in files)
        {
            foreach (var wiring in file.Wirings) Wire(wiring);
        }
        context.Report(phase, files.Count, files.Count);
    }

    /// <summary>
    /// Notes on every nucleon of a file which modules outside the repo that file imports
    /// (<see cref="AttributeKeys.ExternalImports"/>), so the inspector can show what code leans on.
    /// </summary>
    public void MarkExternalImports()
    {
        foreach (var file in _project.Files)
        {
            var external = file.Imports.Values.Concat(file.Wildcards)
                .Where(i => !i.IsRepoPath && ModuleOf(file, i).Module == null)
                .Select(i => i.Module.Split('.')[0])
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Take(15)
                .ToList();
            if (external.Count == 0) continue;
            var text = string.Join(", ", external);
            if (file.Node != null) file.Node.Attributes[AttributeKeys.ExternalImports] = text;
            foreach (var cls in file.Classes.Values.SelectMany(AllNested)) cls.Node.Attributes[AttributeKeys.ExternalImports] = text;
        }

        static IEnumerable<PyClass> AllNested(PyClass cls) => new[] { cls }.Concat(cls.Nested.Values.SelectMany(AllNested));
    }

    /// <summary>(resolved inside the repo, outside it) over every import statement.</summary>
    public (int Internal, int External) CountImports()
    {
        int inside = 0, outside = 0;
        foreach (var file in _project.Files)
        {
            foreach (var import in file.Imports.Values.Concat(file.Wildcards))
            {
                if (ModuleOf(file, import).Module != null) inside++;
                else outside++;
            }
        }
        return (inside, outside);
    }

    // ------------------------------------------------------------------ uses → bonds

    private void Apply(Use use)
    {
        if (use.Kind == UseKind.Enter)
        {
            if (use.Scope.Function?.Member is { } enclosing && use.Member != null)
            {
                _project.AddMemberEdge(enclosing, use.Member, MemberEdgeKinds.Calls, use.Line, null);
                Linked++;
            }
            return;
        }

        if ((use.Source ?? use.Scope.Owner) is not { } source)
        {
            WithoutOwner++;
            return;
        }

        // For a call, what matters is the callee: Foo() creates Foo, obj.run() calls run's class.
        var lookup = use.Kind == UseKind.Call ? use.Ref with { Call = false } : use.Ref;
        var resolved = Eval(lookup, use.Scope, 0);
        var confidence = resolved.Inferred ? Confidence.Likely : null;

        // A bond between nucleons, and (inside a function) a link between members for simulation.
        NodeInfo? bondTo = null;
        string? bondKind = null;
        MemberInfo? linkTo = null;
        string? linkKind = null;
        switch (use.Kind, resolved.Value)
        {
            case (UseKind.Call, ClassValue created):
                (bondTo, bondKind) = (created.Class.Node, EdgeKinds.Creates);
                (linkTo, linkKind) = (Constructor(created.Class), MemberEdgeKinds.Creates);
                break;
            case (UseKind.Call, FunctionValue called):
                (bondTo, bondKind) = (called.Function.Owner, EdgeKinds.Calls);
                (linkTo, linkKind) = (called.Function.Member, MemberEdgeKinds.Calls);
                break;
            case (UseKind.Call, FieldValue callable):
                (bondTo, bondKind) = (callable.Owner.Node, EdgeKinds.Calls);
                break;
            case (UseKind.Mention, ClassValue mentioned):
                (bondTo, bondKind) = (mentioned.Class.Node, EdgeKinds.References);
                break;
            case (UseKind.Mention, FunctionValue passed):
                (bondTo, bondKind) = (passed.Function.Owner, EdgeKinds.References);
                if (passed.Function.Member.Kind == MemberKinds.Property) (linkTo, linkKind) = (passed.Function.Member, MemberEdgeKinds.Reads);
                break;
            case (UseKind.Mention, FieldValue read):
                (bondTo, bondKind) = (read.Owner.Node, EdgeKinds.Uses);
                (linkTo, linkKind) = (FieldMember(read), MemberEdgeKinds.Reads);
                break;
            case (UseKind.Write, FieldValue written):
                (bondTo, bondKind) = (written.Owner.Node, EdgeKinds.Uses);
                (linkTo, linkKind) = (FieldMember(written), MemberEdgeKinds.Writes);
                break;
            case (UseKind.Write, FunctionValue setter) when setter.Function.Member.Kind == MemberKinds.Property:
                (bondTo, bondKind) = (setter.Function.Owner, EdgeKinds.Uses);
                (linkTo, linkKind) = (setter.Function.Member, MemberEdgeKinds.Writes);
                break;
            case (UseKind.Inherit, ClassValue baseClass):
                (bondTo, bondKind) = (baseClass.Class.Node, EdgeKinds.Inherits);
                break;
            case (UseKind.Annotation, ClassValue hinted):
                (bondTo, bondKind) = (hinted.Class.Node, EdgeKinds.References);
                break;
            case (UseKind.FieldType, ClassValue held):
                (bondTo, bondKind) = (held.Class.Node, EdgeKinds.Has);
                break;
            case (UseKind.Assigned, InstanceValue stored):
                (bondTo, bondKind) = (stored.Class.Node, EdgeKinds.Has);
                break;
        }

        if (bondTo == null || bondKind == null)
        {
            Unresolved++;
        }
        else if (ReferenceEquals(bondTo, source))
        {
            WithinClass++;      // self.helper(): no bond, but still a member link below
        }
        else
        {
            _project.AddEdge(source, bondTo, bondKind, confidence);
            Bonded++;
        }

        if (use.Scope.Function?.Member is not { } caller || linkTo == null || linkKind == null) return;
        if (!ReferenceEquals(caller, linkTo))
        {
            _project.AddMemberEdge(caller, linkTo, linkKind, use.Line, confidence);
            Linked++;
        }

        // obj.save() on an instance may run a subclass's override: link each one, as "likely".
        if (use.Kind == UseKind.Call && resolved.Value is FunctionValue { Receiver: { } receiver } dispatched)
        {
            foreach (var overriding in Overrides(receiver, dispatched.Function))
            {
                if (ReferenceEquals(caller, overriding.Member)) continue;
                _project.AddMemberEdge(caller, overriding.Member, MemberEdgeKinds.Calls, use.Line, Confidence.Likely);
                Linked++;
            }
        }
    }

    /// <summary>Methods of the same name in subclasses of the receiver's class (in the repo).</summary>
    private IEnumerable<PyFunction> Overrides(PyClass receiver, PyFunction method)
    {
        _subclasses ??= IndexSubclasses();
        if (!_subclasses.TryGetValue(receiver, out var subclasses)) yield break;
        var name = method.Member.Name;
        var count = 0;
        foreach (var subclass in subclasses)
        {
            if (!subclass.Methods.TryGetValue(name, out var overriding) || overriding == method) continue;
            yield return overriding;
            if (++count >= MaxOverrides) yield break;
        }
    }

    /// <summary>Every class → all its subclasses in the repo (transitively).</summary>
    private Dictionary<PyClass, List<PyClass>> IndexSubclasses()
    {
        var index = new Dictionary<PyClass, List<PyClass>>();
        foreach (var cls in _project.Files.SelectMany(f => f.Classes.Values).SelectMany(AllNested))
        {
            foreach (var ancestor in Bases(cls, 0))
            {
                if (ancestor == cls) continue;
                if (!index.TryGetValue(ancestor, out var list)) index[ancestor] = list = new List<PyClass>();
                list.Add(cls);
            }
        }
        return index;

        static IEnumerable<PyClass> AllNested(PyClass cls) =>
            new[] { cls }.Concat(cls.Nested.Values.SelectMany(AllNested));
    }

    // ------------------------------------------------------------------ framework wiring → entry points

    /// <summary>Entry points added from wiring (routes, viewsets, signal connections).</summary>
    public int Wired { get; private set; }

    private void Wire(Wiring wiring)
    {
        switch (wiring.Kind)
        {
            case WiringKind.Route when wiring.Target is { Call: true, Name: "as_view", Receiver: { } viewClass }:
                // path("x/", ArticleView.as_view()): the class's request handlers.
                if (Eval(viewClass, wiring.Scope, 0).Value is ClassValue cbv) HandlerEntries(cbv.Class, wiring.Label);
                break;
            case WiringKind.Route:
                switch (Eval(wiring.Target, wiring.Scope, 0).Value)
                {
                    case FunctionValue view:
                        Entry(view.Function, Triggers.Request, wiring.Label);
                        break;
                    case ClassValue viewClassValue:
                        HandlerEntries(viewClassValue.Class, wiring.Label);
                        break;
                }
                break;
            case WiringKind.ViewSet:
                if (Eval(wiring.Target, wiring.Scope, 0).Value is ClassValue viewSet) HandlerEntries(viewSet.Class, wiring.Label);
                break;
            case WiringKind.Connect:
                if (Eval(wiring.Target, wiring.Scope, 0).Value is FunctionValue handler) Entry(handler.Function, Triggers.Event, wiring.Label);
                break;
        }
    }

    /// <summary>A view class's HTTP/viewset handlers (its own or inherited from repo bases), else its hooks.</summary>
    private void HandlerEntries(PyClass cls, string label)
    {
        var order = Bases(cls, 0);
        var found = false;
        foreach (var names in new[] { Handlers, HandlerHooks })
        {
            foreach (var name in names)
            {
                var method = order.Select(k => k.Methods.GetValueOrDefault(name)).FirstOrDefault(m => m != null);
                if (method == null) continue;
                Entry(method, Triggers.Request, label, cls);
                found = true;
            }
            if (found) return;
        }
    }

    private void Entry(PyFunction function, string trigger, string label, PyClass? viewClass = null)
    {
        var owner = viewClass?.Node.Name ?? function.Class?.Node.Name ?? function.Owner.FullName;
        _project.Output.EntryPoints.Add(new EntryPointInfo
        {
            Member = function.Member.Id,
            Trigger = trigger,
            Label = $"{label} → {owner}.{function.Member.Name}",
            Source = "python",
        });
        Wired++;
    }

    /// <summary>The __init__ that Foo() runs: the class's own, else the nearest base's in the repo.</summary>
    private MemberInfo? Constructor(PyClass cls)
    {
        foreach (var k in Bases(cls, 0))
        {
            if (k.Methods.TryGetValue("__init__", out var init)) return init.Member;
        }
        return null;
    }

    private static MemberInfo? FieldMember(FieldValue field) => field.Owner.FieldMembers.GetValueOrDefault(field.Name);

    // ------------------------------------------------------------------ evaluation

    private Resolved Eval(Ref reference, Scope scope, int depth)
    {
        if (depth > MaxDepth) return Resolved.None;

        Resolved result;
        if (reference.Receiver == null)
        {
            // Python's zero-argument super() in a method (unless the file defines its own "super").
            if (reference is { Name: "super", Call: true }
                && scope.Function?.Class is { } owner
                && !scope.File.Functions.ContainsKey("super")
                && !scope.File.Imports.ContainsKey("super"))
            {
                return new Resolved(new SuperValue(owner), false);
            }
            result = LookupName(reference.Name, scope, depth + 1);
        }
        else
        {
            var receiver = Eval(reference.Receiver, scope, depth + 1);
            result = receiver.Value == null
                ? Resolved.None
                : Member(receiver.Value, reference.Name, depth + 1).Infer(receiver.Inferred);
        }
        return reference.Call && result.Value != null ? Call(result, depth + 1) : result;
    }

    /// <summary>Calling a class makes an instance; calling a function returns its annotated type.</summary>
    private Resolved Call(Resolved callee, int depth)
    {
        switch (callee.Value)
        {
            case ClassValue cls:
                return new Resolved(new InstanceValue(cls.Class), callee.Inferred);
            case FunctionValue fn when fn.Function.Returns != null:
                if (!_returns.TryGetValue(fn.Function, out var returned))
                {
                    _returns[fn.Function] = Resolved.None;
                    var type = Eval(fn.Function.Returns!, new Scope(fn.Function.File, fn.Function.Class, null), depth + 1);
                    returned = type.Value is ClassValue c ? new Resolved(new InstanceValue(c.Class), true) : Resolved.None;
                    _returns[fn.Function] = returned;
                }
                return returned;
            default:
                return Resolved.None;
        }
    }

    private Resolved LookupName(string name, Scope scope, int depth)
    {
        if (scope.Function is { } fn)
        {
            if (name == fn.SelfParam && fn.Class != null)
            {
                return new Resolved(fn.SelfIsClass ? new ClassValue(fn.Class) : new InstanceValue(fn.Class), false);
            }
            // A local shadows any global of the same name, even when its value is unknown.
            if (fn.Locals.TryGetValue(name, out var locals)) return FromBindings(locals, depth + 1);
        }
        else if (scope.Class is { } cls)
        {
            // Class body: earlier nested classes and attributes are in scope.
            if (cls.Nested.TryGetValue(name, out var nested)) return new Resolved(new ClassValue(nested), false);
            if (cls.Fields.TryGetValue(name, out var fields)) return FromBindings(fields, depth + 1);
        }
        return LookupInFile(scope.File, name, depth + 1);
    }

    private Resolved LookupInFile(PyFile file, string name, int depth)
    {
        if (depth > MaxDepth) return Resolved.None;
        var key = (file, name);
        if (_fileNames.TryGetValue(key, out var known)) return known;
        _fileNames[key] = Resolved.None;    // guards import cycles

        var result = Resolved.None;
        if (file.Classes.TryGetValue(name, out var cls))
        {
            result = new Resolved(new ClassValue(cls), false);
        }
        else if (file.Functions.TryGetValue(name, out var fn))
        {
            result = new Resolved(new FunctionValue(fn), false);
        }
        else if (file.Imports.TryGetValue(name, out var import))
        {
            result = ResolveImport(file, import, depth + 1);
        }
        else if (file.Globals.TryGetValue(name, out var globals))
        {
            result = FromBindings(globals, depth + 1);
        }
        else
        {
            foreach (var wildcard in file.Wildcards)
            {
                var (module, approximate) = ModuleOf(file, wildcard);
                if (module == null) continue;
                var found = LookupInModule(module, name, depth + 1);
                if (found.Value == null) continue;
                result = found.Infer(approximate);
                break;
            }
        }

        _fileNames[key] = result;
        return result;
    }

    private Resolved ResolveImport(PyFile file, ImportBinding import, int depth)
    {
        var (module, approximate) = ModuleOf(file, import);
        if (module == null) return Resolved.None;
        if (import.Name == null) return new Resolved(new ModuleValue(module), approximate);
        return LookupInModule(module, import.Name, depth + 1).Infer(approximate);
    }

    private (PyModule? Module, bool Approximate) ModuleOf(PyFile file, ImportBinding import) =>
        import.IsRepoPath ? (_project.ByRepoPath(import.Module), false) : _project.FindModule(import.Module, file);

    /// <summary>A name in a module: what its file defines or re-exports, else a submodule.</summary>
    private Resolved LookupInModule(PyModule module, string name, int depth)
    {
        if (depth > MaxDepth) return Resolved.None;
        var key = (module, name);
        if (_moduleNames.TryGetValue(key, out var known)) return known;
        _moduleNames[key] = Resolved.None;

        var result = module.File != null ? LookupInFile(module.File, name, depth + 1) : Resolved.None;
        if (result.Value == null)
        {
            var child = _project.ByRepoPath(module.RepoPath.Length == 0 ? name : module.RepoPath + "." + name);
            if (child != null) result = new Resolved(new ModuleValue(child), false);
        }

        _moduleNames[key] = result;
        return result;
    }

    private Resolved Member(Value value, string name, int depth)
    {
        if (depth > MaxDepth) return Resolved.None;
        return value switch
        {
            ModuleValue module => LookupInModule(module.Module, name, depth + 1),
            ClassValue cls => ClassMember(cls.Class, name, depth + 1, null),
            InstanceValue instance => ClassMember(instance.Class, name, depth + 1, instance.Class),
            FieldValue { Type: { } held } => Member(held, name, depth + 1),
            // Past an attribute of unknown type (User.objects.filter) we still only know it's User's.
            FieldValue unknown => new Resolved(unknown, false),
            SuperValue super => SuperMember(super.Class, name, depth + 1),
            _ => Resolved.None,
        };
    }

    /// <summary>
    /// A class attribute, searching its bases too. An attribute the repo never assigns (set dynamically
    /// or by a base outside the repo) still counts as an attribute of the class.
    /// </summary>
    /// <param name="receiver">The instance's class when accessed on an instance (method calls dispatch).</param>
    private Resolved ClassMember(PyClass cls, string name, int depth, PyClass? receiver)
    {
        foreach (var k in Bases(cls, depth + 1))
        {
            if (k.Nested.TryGetValue(name, out var nested)) return new Resolved(new ClassValue(nested), false);
            if (k.Methods.TryGetValue(name, out var method)) return new Resolved(new FunctionValue(method, receiver), false);
            if (k.Fields.TryGetValue(name, out var fields))
            {
                var held = FromBindings(fields, depth + 1);
                return new Resolved(new FieldValue(k, held.Value, name), held.Inferred);
            }
        }
        return new Resolved(new FieldValue(cls, null, name), false);
    }

    /// <summary>super().name: the nearest repo base that defines it, skipping the class itself.</summary>
    private Resolved SuperMember(PyClass cls, string name, int depth)
    {
        foreach (var k in Bases(cls, depth + 1).Skip(1))
        {
            if (k.Nested.TryGetValue(name, out var nested)) return new Resolved(new ClassValue(nested), false);
            if (k.Methods.TryGetValue(name, out var method)) return new Resolved(new FunctionValue(method), false);
            if (k.Fields.TryGetValue(name, out var fields))
            {
                var held = FromBindings(fields, depth + 1);
                return new Resolved(new FieldValue(k, held.Value, name), held.Inferred);
            }
        }
        return Resolved.None;   // defined by a base outside the repo (or nowhere)
    }

    /// <summary>The class followed by its repo bases, depth-first (close enough to Python's MRO).</summary>
    private List<PyClass> Bases(PyClass cls, int depth)
    {
        if (_mro.TryGetValue(cls, out var known)) return known;
        var order = new List<PyClass> { cls };
        _mro[cls] = order;      // partial while computing: a cycle sees just the class itself

        var scope = new Scope(cls.File, cls.Outer, null);
        foreach (var baseRef in cls.Bases)
        {
            if (Eval(baseRef, scope, depth + 1).Value is not ClassValue parent || parent.Class == cls) continue;
            foreach (var k in Bases(parent.Class, depth + 1).ToList())
            {
                if (order.Count < MaxBases && !order.Contains(k)) order.Add(k);
            }
        }
        return order;
    }

    /// <summary>A name's value from its bindings: a type hint first, else the first assignment that resolves.</summary>
    private Resolved FromBindings(List<Binding> bindings, int depth)
    {
        foreach (var binding in bindings)
        {
            if (binding.Type == null) continue;
            var typed = EvalBinding(binding, depth + 1);
            if (typed.Value != null) return typed;
        }
        foreach (var binding in bindings)
        {
            if (binding.Type != null) continue;
            var assigned = EvalBinding(binding, depth + 1);
            if (assigned.Value != null) return assigned;
        }
        return Resolved.None;
    }

    /// <summary>Inferred, so "likely": x: Foo → an instance of Foo; x = Foo() → the same; x = Foo → the class.</summary>
    private Resolved EvalBinding(Binding binding, int depth)
    {
        if (depth > MaxDepth) return Resolved.None;
        if (_bindings.TryGetValue(binding, out var known)) return known;
        _bindings[binding] = Resolved.None;     // x = x.next() must not loop

        var result = Resolved.None;
        if (binding.Type != null)
        {
            if (Eval(binding.Type, binding.Scope, depth + 1).Value is ClassValue cls)
            {
                result = new Resolved(new InstanceValue(cls.Class), true);
            }
        }
        else if (binding.Value != null)
        {
            var value = Eval(binding.Value, binding.Scope, depth + 1);
            if (value.Value != null) result = value.Infer(true);
        }

        _bindings[binding] = result;
        return result;
    }
}
