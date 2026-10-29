using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Valence.Core;
using Valence.Schema;

namespace Valence.Probe.CSharp;

/// <summary>
/// Outside effects (the Boundary lens): calls and constructions of code outside the repo that touch the
/// network, files, processes, the environment, databases, native or dynamic code, classified by
/// <see cref="EffectCatalog"/> and recorded on the member making them. Calls into types whose references
/// didn't load (e.g. GodotSharp not restored) are matched by the type's short name, as "likely".
/// </summary>
internal sealed partial class CSharpAnalyzer
{
    /// <summary>A call or <c>new</c> of a resolved method outside the repo.</summary>
    private void RecordCallEffect(string owner, IMethodSymbol method, SyntaxNode at, ArgumentListSyntax? arguments, string confidence)
    {
        method = method.ReducedFrom ?? method;  // extension methods are declared on their static class
        if (method.ContainingType is not { } type) return;
        var typeKey = TypeKey(type);
        var isConstructor = method.MethodKind == MethodKind.Constructor;
        if (isConstructor && EffectCatalog.NeedsPathArgument(typeKey)
            && method.Parameters.FirstOrDefault()?.Type.SpecialType != SpecialType.System_String)
        {
            return;
        }
        var member = isConstructor ? "new" : method.Name;
        if (EffectCatalog.DotNet(typeKey, member, ModeText(arguments)) is not { } kind) return;
        RecordEffect(owner, kind, isConstructor ? "new " + typeKey : typeKey + "." + member, at, confidence);
    }

    /// <summary><c>FileAccess.Open(...)</c> where FileAccess didn't resolve: a static call on a type name.</summary>
    private void RecordUnresolvedCallEffect(Scope scope, string owner, InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax access) return;
        if (LastName(access.Expression) is not { Length: > 0 } typeName || !char.IsUpper(typeName[0])) return;
        // Only when the receiver itself is unknown: a resolved local, field or namespace isn't a missing type.
        if (scope.Model.GetSymbolInfo(access.Expression).Symbol != null) return;
        var member = access.Name.Identifier.ValueText;
        if (EffectCatalog.DotNetByShortName(typeName, member, ModeText(invocation.ArgumentList)) is not { } kind) return;
        RecordEffect(owner, kind, $"{typeName}.{member}", invocation, Confidence.Likely);
    }

    /// <summary><c>new HttpRequest()</c> where HttpRequest didn't resolve.</summary>
    private void RecordUnresolvedCreationEffect(string owner, BaseObjectCreationExpressionSyntax creation)
    {
        if (creation is not ObjectCreationExpressionSyntax { Type: var type } || LastName(type) is not { Length: > 0 } typeName) return;
        if (EffectCatalog.DotNetByShortName(typeName, "new", ModeText(creation.ArgumentList)) is not { } kind) return;
        RecordEffect(owner, kind, "new " + typeName, creation, Confidence.Likely);
    }

    private void RecordEffect(string owner, string kind, string api, SyntaxNode at, string confidence)
    {
        if (!_members.TryGetValue(owner, out var member)) return;
        member.Effects = EffectCatalog.Add(member.Effects, kind, api, LineOf(at), confidence);
    }

    /// <summary>[DllImport] / [LibraryImport] methods run native code.</summary>
    private static bool IsNativeImport(IMethodSymbol method) =>
        method.GetDllImportData() != null
        || method.GetAttributes().Any(a => a.AttributeClass?.Name is "LibraryImportAttribute" or "LibraryImport" or "DllImportAttribute");

    /// <summary>"Namespace.Outer.Type", without generic arguments: what the catalog matches.</summary>
    private static string TypeKey(INamedTypeSymbol type)
    {
        var name = type.Name;
        for (var outer = type.ContainingType; outer != null; outer = outer.ContainingType) name = outer.Name + "." + name;
        return type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + name : name;
    }

    /// <summary>
    /// The simple arguments (names and member accesses) as text, for calls whose effect depends on a mode:
    /// <c>FileMode.Create</c>, <c>ModeFlags.Write</c>. Literals (paths) and other expressions are skipped.
    /// </summary>
    private static string? ModeText(ArgumentListSyntax? arguments)
    {
        if (arguments == null || arguments.Arguments.Count < 2) return null;
        var text = new StringBuilder();
        foreach (var argument in arguments.Arguments)
        {
            if (argument.Expression is MemberAccessExpressionSyntax or IdentifierNameSyntax)
            {
                text.Append(argument.Expression).Append(' ');
            }
        }
        return text.Length > 0 ? text.ToString() : null;
    }
}
