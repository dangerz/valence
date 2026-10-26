using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Valence.Schema;

namespace Valence.Probe.CSharp;

/// <summary>
/// Member-level flow: methods, constructors, properties, fields and events/signals, the ordered
/// calls and reads/writes between them, event wiring, and entry points (engine callbacks, Main,
/// handlers of external events). Godot signals are recognized by name, so this works even when
/// Godot's source generators (which we never run) haven't produced the SignalName/event members.
/// </summary>
internal sealed partial class CSharpAnalyzer
{
    private static readonly SymbolDisplayFormat MemberIdFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
                       | SymbolDisplayMemberOptions.IncludeParameters
                       | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                              | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                       | SymbolDisplayMemberOptions.IncludeType
                       | SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
                          | SymbolDisplayParameterOptions.IncludeName
                          | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>Godot virtual callbacks the engine calls, and when.</summary>
    private static readonly Dictionary<string, string> GodotCallbacks = new(StringComparer.Ordinal)
    {
        ["_Process"] = Triggers.Frame,
        ["_PhysicsProcess"] = Triggers.Physics,
        ["_Input"] = Triggers.Input,
        ["_UnhandledInput"] = Triggers.Input,
        ["_UnhandledKeyInput"] = Triggers.Input,
        ["_ShortcutInput"] = Triggers.Input,
        ["_GuiInput"] = Triggers.Input,
        ["_Draw"] = Triggers.Draw,
        ["_Ready"] = Triggers.Startup,
        ["_EnterTree"] = Triggers.Startup,
        ["_ExitTree"] = Triggers.Shutdown,
    };

    private const string SignalSuffix = "EventHandler";

    private readonly List<INamedTypeSymbol> _typeSymbols = new();
    private readonly Dictionary<string, MemberInfo> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Source, string Target, string Kind), MemberEdgeAccumulator> _memberEdges = new();
    private readonly Dictionary<string, int> _nextOrder = new(StringComparer.Ordinal);

    /// <summary>Virtual/abstract/interface member id → repo members that override or implement it.</summary>
    private readonly Dictionary<string, HashSet<string>> _implementations = new(StringComparer.Ordinal);

    /// <summary>(type id, signal name) → synthetic event member id for Godot [Signal] delegates.</summary>
    private readonly Dictionary<(string TypeId, string Name), string> _signals = new();
    private readonly Dictionary<(string Member, string Trigger), EntryPointInfo> _entryPoints = new();

    private sealed class MemberEdgeAccumulator
    {
        public int Weight;
        public int Order;
        public int Line;
        public string Strength = Confidence.Certain;
    }

    // ---------------------------------------------------------------- declaration

    private void DeclareMembers(INamedTypeSymbol type, NodeInfo node)
    {
        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared) continue;
            var kind = MemberKindOf(member);
            if (kind == null) continue;

            var id = MemberIdOf(member);
            if (_members.ContainsKey(id)) continue;
            var location = member.Locations.FirstOrDefault(l => l.IsInSource);
            var info = new MemberInfo
            {
                Id = id,
                Name = MemberName(member),
                Kind = kind,
                Visibility = VisibilityOf(member.DeclaredAccessibility),
                IsStatic = member.IsStatic,
                Signature = member.ToDisplayString(SignatureFormat),
                File = location?.SourceTree is { } tree && IsRepoFile(tree) ? RepoScanner.Relative(_root, tree.FilePath) : node.File,
                Line = location != null ? location.GetLineSpan().StartLinePosition.Line + 1 : node.Line,
            };
            if (member.IsOverride) info.Attributes["override"] = "true";
            if (member.IsAbstract) info.Attributes["abstract"] = "true";
            _members[id] = info;
            node.Members.Add(info);

            if (member is IMethodSymbol method) DetectEntryPoint(type, method, info);
        }

        // Godot: [Signal] delegate void DiedEventHandler(...) declares signal "Died".
        foreach (var nested in type.GetTypeMembers())
        {
            if (nested.TypeKind != TypeKind.Delegate || !nested.Name.EndsWith(SignalSuffix, StringComparison.Ordinal)) continue;
            if (!HasSignalAttribute(nested)) continue;

            var name = nested.Name[..^SignalSuffix.Length];
            var id = $"{node.Id}.{name}";
            if (_members.ContainsKey(id)) continue;
            var parameters = nested.DelegateInvokeMethod?.Parameters
                .Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}") ?? Enumerable.Empty<string>();
            var location = nested.Locations.FirstOrDefault(l => l.IsInSource);
            var info = new MemberInfo
            {
                Id = id,
                Name = name,
                Kind = MemberKinds.Event,
                Visibility = VisibilityOf(nested.DeclaredAccessibility),
                Signature = $"signal {name}({string.Join(", ", parameters)})",
                File = node.File,
                Line = location != null ? location.GetLineSpan().StartLinePosition.Line + 1 : node.Line,
            };
            info.Attributes["signal"] = "true";
            _members[id] = info;
            node.Members.Add(info);
            _signals[(node.Id, name)] = id;
        }
    }

    private static bool HasSignalAttribute(INamedTypeSymbol type)
    {
        if (type.GetAttributes().Any(a => a.AttributeClass?.Name is "Signal" or "SignalAttribute")) return true;
        // Unresolved attribute classes (no GodotSharp in the NuGet cache): check the syntax.
        return type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<DelegateDeclarationSyntax>()
            .SelectMany(d => d.AttributeLists.SelectMany(l => l.Attributes))
            .Any(a => LastName(a.Name) is "Signal" or "SignalAttribute");
    }

    private void DetectEntryPoint(INamedTypeSymbol type, IMethodSymbol method, MemberInfo info)
    {
        if (method.IsOverride && GodotCallbacks.TryGetValue(method.Name, out var trigger))
        {
            AddEntryPoint(info.Id, trigger, $"{type.Name}.{method.Name}", "godot");
        }
        else if (method.IsStatic && method.Name == "Main" && method.MethodKind == MethodKind.Ordinary)
        {
            AddEntryPoint(info.Id, Triggers.Startup, $"{type.Name}.Main", "dotnet");
        }
    }

    private void AddEntryPoint(string memberId, string trigger, string label, string source) =>
        _entryPoints.TryAdd((memberId, trigger), new EntryPointInfo
        {
            Member = memberId,
            Trigger = trigger,
            Label = label,
            Source = source,
        });

    /// <summary>Maps every virtual/abstract/interface member to the repo members that override/implement it.</summary>
    private void BuildImplementationMap()
    {
        foreach (var type in _typeSymbols)
        {
            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared) continue;
                var implementation = MemberIdOf(member);
                if (!_members.ContainsKey(implementation)) continue;
                // Walk the whole override chain so a call to the root virtual reaches every override.
                for (var overridden = Overridden(member); overridden != null; overridden = Overridden(overridden))
                {
                    AddImplementation(MemberIdOf(overridden), implementation);
                }
            }

            foreach (var iface in type.AllInterfaces)
            {
                if (!_nodes.ContainsKey(IdOf(iface))) continue;
                foreach (var ifaceMember in iface.GetMembers())
                {
                    if (type.FindImplementationForInterfaceMember(ifaceMember) is not { } implementation) continue;
                    var implementationId = MemberIdOf(implementation);
                    if (_members.ContainsKey(implementationId)) AddImplementation(MemberIdOf(ifaceMember), implementationId);
                }
            }
        }
    }

    private static ISymbol? Overridden(ISymbol member) => member switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol evt => evt.OverriddenEvent,
        _ => null,
    };

    private void AddImplementation(string baseMember, string implementation)
    {
        if (baseMember == implementation) return;
        if (!_implementations.TryGetValue(baseMember, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _implementations[baseMember] = set;
        }
        set.Add(implementation);
    }

    // ---------------------------------------------------------------- flow edges

    /// <summary>The member a declaration's body belongs to (method, accessor's property, field initializer).</summary>
    private string? OwnerOf(SemanticModel model, MemberDeclarationSyntax member)
    {
        ISymbol? symbol = member switch
        {
            BaseMethodDeclarationSyntax method => model.GetDeclaredSymbol(method),
            BasePropertyDeclarationSyntax property => model.GetDeclaredSymbol(property),
            BaseFieldDeclarationSyntax field when field.Declaration.Variables.Count > 0 =>
                model.GetDeclaredSymbol(field.Declaration.Variables[0]),
            _ => null,
        };
        if (symbol == null) return null;
        var id = MemberIdOf(symbol);
        return _members.ContainsKey(id) ? id : null;
    }

    private void VisitMemberLevel(Scope scope, string owner, SyntaxNode node)
    {
        switch (node)
        {
            case InvocationExpressionSyntax invocation:
                HandleInvocation(scope, owner, invocation);
                break;
            case BaseObjectCreationExpressionSyntax creation:
                if (scope.Model.GetSymbolInfo(creation).Symbol is IMethodSymbol constructor)
                {
                    AddMemberEdge(owner, constructor, MemberEdgeKinds.Creates, creation, Confidence.Certain);
                }
                break;
            case AssignmentExpressionSyntax assignment when assignment.IsKind(SyntaxKind.AddAssignmentExpression):
                HandleSubscription(scope, owner, assignment);
                break;
            case IdentifierNameSyntax { IsVar: true }:
                break;
            case SimpleNameSyntax name:
                HandleName(scope, owner, name);
                break;
        }
    }

    private void HandleName(Scope scope, string owner, SimpleNameSyntax name)
    {
        var symbol = scope.Model.GetSymbolInfo(name).Symbol;
        switch (symbol)
        {
            case IPropertySymbol or IFieldSymbol:
                AddMemberEdge(owner, symbol, IsWrite(name) ? MemberEdgeKinds.Writes : MemberEdgeKinds.Reads, name, Confidence.Certain);
                break;
            case IMethodSymbol method when name.Parent is ArgumentSyntax argument && !IsWiringArgument(argument):
                // A method group passed as a delegate (list.ForEach(Process), Task.Run(Work)) is probably called.
                AddMemberEdge(owner, method, MemberEdgeKinds.Calls, name, Confidence.Likely);
                AddImplementationEdges(owner, method, name);
                break;
        }
    }

    private void HandleInvocation(Scope scope, string owner, InvocationExpressionSyntax invocation)
    {
        var model = scope.Model;
        var info = model.GetSymbolInfo(invocation);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        var confidence = info.Symbol != null ? Confidence.Certain : Confidence.Likely;

        if (symbol is IMethodSymbol method)
        {
            if (method.MethodKind == MethodKind.DelegateInvoke)
            {
                if (RaisedEvent(model, invocation) is { } raised) AddMemberEdge(owner, raised, MemberEdgeKinds.Raises, invocation, Confidence.Certain);
                return;
            }
            if (IsRepoMember(method))
            {
                AddMemberEdge(owner, method, MemberEdgeKinds.Calls, invocation, confidence);
                AddImplementationEdges(owner, method, invocation);
                return;
            }
        }

        // Not a call into repo code. Godot wiring is recognized by name so it works without GodotSharp.
        var invoked = InvokedName(invocation);
        if (invoked == "Connect") HandleConnect(scope, invocation);
        else if (invoked != null && invoked.StartsWith("EmitSignal", StringComparison.Ordinal)) HandleEmitSignal(scope, owner, invocation, invoked);
    }

    /// <summary>A call through a virtual/abstract/interface member may land on any override.</summary>
    private void AddImplementationEdges(string owner, IMethodSymbol method, SyntaxNode at)
    {
        if (!_implementations.TryGetValue(MemberIdOf(method), out var implementations)) return;
        foreach (var implementation in implementations)
        {
            AddMemberEdge(owner, implementation, MemberEdgeKinds.Calls, at, Confidence.Likely);
        }
    }

    /// <summary>EmitSignal(SignalName.Died, ...) / EmitSignal("Died") / EmitSignalDied(...).</summary>
    private void HandleEmitSignal(Scope scope, string owner, InvocationExpressionSyntax invocation, string invoked)
    {
        var arguments = invocation.ArgumentList.Arguments;
        var signal = invoked == "EmitSignal"
            ? arguments.Count > 0 ? SymbolNameFrom(arguments[0].Expression) : null
            : invoked["EmitSignal".Length..];
        if (string.IsNullOrEmpty(signal)) return;
        if (FindEvent(ReceiverType(scope, invocation), signal) is { } target)
        {
            AddMemberEdge(owner, target, MemberEdgeKinds.Raises, invocation, Confidence.Likely);
        }
    }

    /// <summary>node.Connect(SignalName.Died, Callable.From(OnDied)) and friends.</summary>
    private void HandleConnect(Scope scope, InvocationExpressionSyntax invocation)
    {
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count < 2) return;
        var signal = SymbolNameFrom(arguments[0].Expression);
        if (string.IsNullOrEmpty(signal)) return;
        var owner = OwnerOfNode(scope, invocation);
        if (HandlerFrom(scope, owner, arguments[1].Expression) is not { } handler) return;

        var receiver = ReceiverType(scope, invocation);
        if (FindEvent(receiver, signal) is { } target)
        {
            AddMemberEdge(target, handler.Id, MemberEdgeKinds.Triggers, invocation, handler.Strength);
        }
        else
        {
            AddExternalEventEntry(receiver, signal, handler.Id);
        }
    }

    /// <summary>evt += Handler, for C# events and Godot's generated signal events.</summary>
    private void HandleSubscription(Scope scope, string owner, AssignmentExpressionSyntax assignment)
    {
        var model = scope.Model;
        var left = model.GetSymbolInfo(assignment.Left).Symbol;
        if (left is IPropertySymbol or IFieldSymbol or ILocalSymbol or IParameterSymbol) return; // arithmetic or delegate field
        if (HandlerFrom(scope, owner, assignment.Right) is not { } handler) return;

        string? eventName;
        INamedTypeSymbol? receiver;
        if (left is IEventSymbol evt)
        {
            if (IsRepoMember(evt))
            {
                AddMemberEdge(MemberIdOf(evt), handler.Id, MemberEdgeKinds.Triggers, assignment, handler.Strength);
                return;
            }
            eventName = evt.Name;
            receiver = evt.ContainingType;
        }
        else if (assignment.Left is MemberAccessExpressionSyntax access)
        {
            eventName = access.Name.Identifier.ValueText;
            receiver = access.Expression is ThisExpressionSyntax or BaseExpressionSyntax
                ? scope.Type
                : model.GetTypeInfo(access.Expression).Type as INamedTypeSymbol;
        }
        else if (assignment.Left is IdentifierNameSyntax identifier)
        {
            eventName = identifier.Identifier.ValueText;
            receiver = scope.Type;
        }
        else
        {
            return;
        }

        if (FindEvent(receiver, eventName) is { } target)
        {
            AddMemberEdge(target, handler.Id, MemberEdgeKinds.Triggers, assignment, handler.Strength);
        }
        else
        {
            AddExternalEventEntry(receiver, eventName, handler.Id);
        }
    }

    /// <summary>An event that isn't repo code (Button.Pressed, Timer.timeout, ...) makes its handler an entry point.</summary>
    private void AddExternalEventEntry(INamedTypeSymbol? receiver, string eventName, string handlerId)
    {
        if (!_members.TryGetValue(handlerId, out var handler)) return;
        var receiverName = receiver is { TypeKind: not TypeKind.Error } ? receiver.Name : "?";
        AddEntryPoint(handlerId, Triggers.Event, $"{receiverName}.{eventName} → {handler.Name}", "csharp");
    }

    private (string Id, string Strength)? HandlerFrom(Scope scope, string? owner, ExpressionSyntax expression)
    {
        var model = scope.Model;
        switch (expression)
        {
            // new Callable(this, MethodName.OnDied) / new Callable(target, "OnDied")
            case ObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: 2 } creation:
            {
                var arguments = creation.ArgumentList!.Arguments;
                var target = arguments[0].Expression is ThisExpressionSyntax
                    ? scope.Type
                    : model.GetTypeInfo(arguments[0].Expression).Type as INamedTypeSymbol;
                var methodName = SymbolNameFrom(arguments[1].Expression);
                return methodName != null && FindMethod(target, methodName) is { } id ? (id, Confidence.Likely) : null;
            }
            // Callable.From(OnDied) / Callable.From(() => ...)
            case InvocationExpressionSyntax invocation when InvokedName(invocation) == "From"
                                                            && invocation.ArgumentList.Arguments.Count > 0:
                return HandlerFrom(scope, owner, invocation.ArgumentList.Arguments[0].Expression);
            // Lambdas run as part of the method that declared them.
            case AnonymousFunctionExpressionSyntax:
                return owner != null ? (owner, Confidence.Likely) : null;
            default:
            {
                var info = model.GetSymbolInfo(expression);
                return (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is IMethodSymbol method && IsRepoMember(method)
                    ? (MemberIdOf(method), info.Symbol != null ? Confidence.Certain : Confidence.Likely)
                    : null;
            }
        }
    }

    /// <summary>Evt(...), Evt.Invoke(...), Evt?.Invoke(...) → the event being raised.</summary>
    private static ISymbol? RaisedEvent(SemanticModel model, InvocationExpressionSyntax invocation)
    {
        var target = invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Invoke" } access => access.Expression,
            MemberBindingExpressionSyntax { Name.Identifier.ValueText: "Invoke" } =>
                (invocation.Parent as ConditionalAccessExpressionSyntax)?.Expression ?? invocation.Expression,
            var other => other,
        };
        return model.GetSymbolInfo(target).Symbol as IEventSymbol;
    }

    /// <summary>Repo event (C# event or Godot signal) with this name on the type or its repo base types.</summary>
    private string? FindEvent(INamedTypeSymbol? type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var typeId = IdOf(current);
            if (_signals.TryGetValue((typeId, name), out var signal)) return signal;
            foreach (var evt in current.GetMembers(name).OfType<IEventSymbol>())
            {
                var id = MemberIdOf(evt);
                if (_members.ContainsKey(id)) return id;
            }
        }
        return null;
    }

    private string? FindMethod(INamedTypeSymbol? type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var method in current.GetMembers(name).OfType<IMethodSymbol>())
            {
                var id = MemberIdOf(method);
                if (_members.ContainsKey(id)) return id;
            }
        }
        return null;
    }

    private string? OwnerOfNode(Scope scope, SyntaxNode node)
    {
        var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => !IsTypeDeclaration(m));
        return member != null ? OwnerOf(scope.Model, member) : null;
    }

    private static INamedTypeSymbol? ReceiverType(Scope scope, InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax access && access.Expression is not (ThisExpressionSyntax or BaseExpressionSyntax)
            ? scope.Model.GetTypeInfo(access.Expression).Type as INamedTypeSymbol
            : scope.Type;

    private void AddMemberEdge(string source, ISymbol target, string kind, SyntaxNode at, string confidence) =>
        AddMemberEdge(source, MemberIdOf(target), kind, at, confidence);

    private void AddMemberEdge(string source, string target, string kind, SyntaxNode at, string confidence)
    {
        if (source == target || !_members.ContainsKey(target) || !_members.ContainsKey(source)) return;
        var key = (source, target, kind);
        if (!_memberEdges.TryGetValue(key, out var edge))
        {
            var order = _nextOrder.GetValueOrDefault(source);
            _nextOrder[source] = order + 1;
            edge = new MemberEdgeAccumulator { Order = order, Line = LineOf(at), Strength = confidence };
            _memberEdges[key] = edge;
        }
        edge.Weight++;
        edge.Strength = Confidence.Strongest(edge.Strength, confidence) ?? Confidence.Certain;
    }

    private List<MemberEdgeInfo> MemberEdgesOutput() => _memberEdges
        .Select(kv => new MemberEdgeInfo
        {
            Source = kv.Key.Source,
            Target = kv.Key.Target,
            Kind = kv.Key.Kind,
            Weight = kv.Value.Weight,
            Order = kv.Value.Order,
            Line = kv.Value.Line,
            Confidence = kv.Value.Strength == Confidence.Certain ? null : kv.Value.Strength,
        })
        .OrderBy(e => e.Source, StringComparer.Ordinal)
        .ThenBy(e => e.Order)
        .ToList();

    // ---------------------------------------------------------------- helpers

    private bool IsRepoMember(ISymbol symbol) => _members.ContainsKey(MemberIdOf(symbol));

    private static string MemberIdOf(ISymbol symbol)
    {
        if (symbol is IMethodSymbol method)
        {
            method = method.ReducedFrom ?? method;
            method = method.PartialDefinitionPart ?? method;
            symbol = method.OriginalDefinition;
        }
        else
        {
            symbol = symbol.OriginalDefinition;
        }
        return Prefix + symbol.ToDisplayString(MemberIdFormat);
    }

    private static string? MemberKindOf(ISymbol member) => member switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => MemberKinds.Constructor,
        IMethodSymbol
        {
            MethodKind: MethodKind.Ordinary or MethodKind.UserDefinedOperator or MethodKind.Conversion
            or MethodKind.Destructor or MethodKind.ExplicitInterfaceImplementation
        } => MemberKinds.Method,
        IPropertySymbol => MemberKinds.Property,
        IFieldSymbol => MemberKinds.Field,
        IEventSymbol => MemberKinds.Event,
        _ => null,
    };

    private static string MemberName(ISymbol member)
    {
        if (member is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor }) return member.ContainingType.Name;
        if (member is IPropertySymbol { IsIndexer: true }) return "this[]";
        var name = member.Name;
        var dot = name.LastIndexOf('.'); // explicit interface implementations: "IFoo.Bar"
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }

    private static string? InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>SignalName.Died / MethodName.OnDied / "Died" / nameof(Died) → the name.</summary>
    private static string? SymbolNameFrom(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
        InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } nameOf
            when nameOf.ArgumentList.Arguments.Count == 1 => LastName(nameOf.ArgumentList.Arguments[0].Expression),
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => null,
    };

    private static string? LastName(ExpressionSyntax expression) => expression switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>Arguments to Connect(...) / Callable.From(...) / new Callable(...) are wiring, not calls.</summary>
    private static bool IsWiringArgument(ArgumentSyntax argument)
    {
        var parent = argument.Parent?.Parent;
        return parent switch
        {
            InvocationExpressionSyntax invocation => InvokedName(invocation) is "Connect" or "From",
            ObjectCreationExpressionSyntax creation => LastName(creation.Type) == "Callable",
            _ => false,
        };
    }

    private static bool IsWrite(SimpleNameSyntax name)
    {
        SyntaxNode current = name;
        if (current.Parent is MemberAccessExpressionSyntax access && access.Name == current) current = access;
        return current.Parent switch
        {
            AssignmentExpressionSyntax assignment => assignment.Left == current,
            PrefixUnaryExpressionSyntax prefix =>
                prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression),
            PostfixUnaryExpressionSyntax postfix =>
                postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression),
            ArgumentSyntax argument =>
                argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword),
            _ => false,
        };
    }
}
