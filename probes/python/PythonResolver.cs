using Valence.Core;
using Valence.Schema;

namespace Valence.Probe.Python;

/// <summary>What a name chain evaluates to.</summary>
internal abstract record Value;

internal sealed record ModuleValue(PyModule Module) : Value;

internal sealed record ClassValue(PyClass Class) : Value;

internal sealed record FunctionValue(PyFunction Function) : Value;

/// <summary>An instance of a repo class (self, Foo(), a typed parameter or field).</summary>
internal sealed record InstanceValue(PyClass Class) : Value;

/// <summary>An attribute of a repo class; <see cref="Type"/> is what it holds, when known.</summary>
internal sealed record FieldValue(PyClass Owner, Value? Type) : Value;

/// <summary>
/// Pass 2: evaluates every recorded use across the repo and turns it into bonds. Names resolve the
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

    private readonly PythonProject _project;
    private readonly Dictionary<(PyFile, string), Resolved> _fileNames = new();
    private readonly Dictionary<(PyModule, string), Resolved> _moduleNames = new();
    private readonly Dictionary<Binding, Resolved> _bindings = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PyFunction, Resolved> _returns = new();
    private readonly Dictionary<PyClass, List<PyClass>> _mro = new();

    public PythonResolver(PythonProject project)
    {
        _project = project;
    }

    public int Bonded { get; private set; }

    public int Unresolved { get; private set; }

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
        context.Report(phase, files.Count, files.Count);
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
        if ((use.Source ?? use.Scope.Owner) is not { } source)
        {
            WithoutOwner++;
            return;
        }

        // For a call, what matters is the callee: Foo() creates Foo, obj.run() calls run's class.
        var lookup = use.Kind == UseKind.Call ? use.Ref with { Call = false } : use.Ref;
        var resolved = Eval(lookup, use.Scope, 0);
        var confidence = resolved.Inferred ? Confidence.Likely : null;
        var before = Bonded;

        switch (use.Kind, resolved.Value)
        {
            case (UseKind.Call, ClassValue created):
                Bond(created.Class.Node, EdgeKinds.Creates);
                break;
            case (UseKind.Call, FunctionValue called):
                Bond(called.Function.Owner, EdgeKinds.Calls);
                break;
            case (UseKind.Call, FieldValue callable):
                Bond(callable.Owner.Node, EdgeKinds.Calls);
                break;
            case (UseKind.Mention, ClassValue mentioned):
                Bond(mentioned.Class.Node, EdgeKinds.References);
                break;
            case (UseKind.Mention, FunctionValue passed):
                Bond(passed.Function.Owner, EdgeKinds.References);
                break;
            case (UseKind.Mention, FieldValue field):
                Bond(field.Owner.Node, EdgeKinds.Uses);
                break;
            case (UseKind.Inherit, ClassValue baseClass):
                Bond(baseClass.Class.Node, EdgeKinds.Inherits);
                break;
            case (UseKind.Annotation, ClassValue hinted):
                Bond(hinted.Class.Node, EdgeKinds.References);
                break;
            case (UseKind.FieldType, ClassValue held):
                Bond(held.Class.Node, EdgeKinds.Has);
                break;
            case (UseKind.Assigned, InstanceValue stored):
                Bond(stored.Class.Node, EdgeKinds.Has);
                break;
        }
        if (Bonded == before) Unresolved++;

        void Bond(NodeInfo target, string kind)
        {
            if (ReferenceEquals(target, source)) return;
            _project.AddEdge(source, target, kind, confidence);
            Bonded++;
        }
    }

    // ------------------------------------------------------------------ evaluation

    private Resolved Eval(Ref reference, Scope scope, int depth)
    {
        if (depth > MaxDepth) return Resolved.None;

        Resolved result;
        if (reference.Receiver == null)
        {
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
            ClassValue cls => ClassMember(cls.Class, name, depth + 1),
            InstanceValue instance => ClassMember(instance.Class, name, depth + 1),
            FieldValue { Type: { } held } => Member(held, name, depth + 1),
            // Past an attribute of unknown type (User.objects.filter) we still only know it's User's.
            FieldValue unknown => new Resolved(unknown, false),
            _ => Resolved.None,
        };
    }

    /// <summary>
    /// A class attribute, searching its bases too. An attribute the repo never assigns (set dynamically
    /// or by a base outside the repo) still counts as an attribute of the class.
    /// </summary>
    private Resolved ClassMember(PyClass cls, string name, int depth)
    {
        foreach (var k in Bases(cls, depth + 1))
        {
            if (k.Nested.TryGetValue(name, out var nested)) return new Resolved(new ClassValue(nested), false);
            if (k.Methods.TryGetValue(name, out var method)) return new Resolved(new FunctionValue(method), false);
            if (k.Fields.TryGetValue(name, out var fields))
            {
                var held = FromBindings(fields, depth + 1);
                return new Resolved(new FieldValue(k, held.Value), held.Inferred);
            }
        }
        return new Resolved(new FieldValue(cls, null), false);
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
