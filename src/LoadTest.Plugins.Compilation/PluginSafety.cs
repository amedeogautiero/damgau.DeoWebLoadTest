using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LoadTest.Plugins.Compilation;

/// <summary>
/// Elenco dei riferimenti consentiti ai plugin .csx (PROPOSTA P9: il contratto dice solo "il controller verifica i riferimenti consentiti").
/// Si compila contro gli assembly di implementazione del runtime del controller (TRUSTED_PLATFORM_ASSEMBLIES) filtrati per nome:
/// includono i "private" (System.Private.*) perché le facciate (System.Runtime...) rimandano a quelli.
/// Limite noto: l'IL emesso cita System.Private.CoreLib; va bene finché agenti e controller hanno lo stesso runtime .NET 10.
/// Per un IL citato contro System.Runtime servono i reference assembly (pacchetto Microsoft.NETCore.App.Ref): da decidere.
/// </summary>
public static class PluginReferences
{
    private static readonly HashSet<string> Exact = new(StringComparer.Ordinal)
    {
        "System.Runtime", "System.Private.CoreLib", "System.Private.Uri", "System.Memory", "System.Linq", "System.IO",
        "System.Net.Http", "System.Net.Primitives", "System.Text.Json", "System.Text.RegularExpressions",
        "System.Text.Encoding.Extensions", "System.Globalization", "LoadTest.Sdk"
    };

    private static readonly string[] Prefixes = ["System.Collections", "System.Threading", "System.Xml", "System.Private.Xml"];

    public static bool IsAllowed(string simpleName) =>
        Exact.Contains(simpleName) || Prefixes.Any(p => simpleName == p || simpleName.StartsWith(p + ".", StringComparison.Ordinal));

    /// <summary>Riferimenti di default: assembly del runtime in elenco più LoadTest.Sdk (l'assembly dell'host).</summary>
    public static IReadOnlyList<MetadataReference> FromRuntime()
    {
        var refs = new List<MetadataReference>();
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (IsAllowed(Path.GetFileNameWithoutExtension(path))) refs.Add(MetadataReference.CreateFromFile(path));

        var sdk = typeof(LoadTest.Sdk.IWebTestPlugin).Assembly.Location;
        if (string.IsNullOrEmpty(sdk)) throw new InvalidOperationException("impossibile localizzare LoadTest.Sdk (publish single-file?)");
        if (!refs.Any(r => string.Equals(r.Display, sdk, StringComparison.OrdinalIgnoreCase))) refs.Add(MetadataReference.CreateFromFile(sdk));
        return refs;
    }
}

/// <summary>
/// Secondo livello di difesa: anche con i riferimenti filtrati, System.Private.CoreLib contiene P/Invoke e Marshal.
/// Rifiuta l'uso di simboli in namespace/tipi vietati (PROPOSTA P9) e i metodi `extern`.
/// </summary>
internal sealed class ForbiddenApiWalker(SemanticModel model, string file, List<string> errors) : CSharpSyntaxWalker
{
    private static readonly string[] Namespaces =
        ["Microsoft.Win32", "System.Runtime.InteropServices", "System.Reflection.Emit", "System.Runtime.Loader"];

    private static readonly string[] Types = ["System.Diagnostics.Process", "System.Diagnostics.ProcessStartInfo"];

    public override void VisitIdentifierName(IdentifierNameSyntax node)
    {
        Check(model.GetSymbolInfo(node).Symbol, node);
        base.VisitIdentifierName(node);
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (node.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword)))
            Report(node, "metodo 'extern' (P/Invoke)");
        base.VisitMethodDeclaration(node);
    }

    private void Check(ISymbol? s, SyntaxNode node)
    {
        if (s is null) return;
        var ns = s.ContainingNamespace?.ToDisplayString() ?? "";
        foreach (var f in Namespaces)
            if (ns == f || ns.StartsWith(f + ".", StringComparison.Ordinal)) { Report(node, $"{s.ToDisplayString()} ({f})"); return; }

        var type = s as INamedTypeSymbol ?? s.ContainingType;
        if (type is not null && Types.Contains(type.ToDisplayString())) Report(node, type.ToDisplayString());
    }

    private void Report(SyntaxNode node, string what)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        errors.Add($"{file}({line}): API non consentita: {what}");
    }
}
