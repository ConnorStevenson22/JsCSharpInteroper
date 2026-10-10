using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Interoper.Analyzers;

/// <summary>
/// IOP3001 (mapping §8): two or more consecutive awaited writes to live objects outside a batch. On Server each is a
/// round trip; in a batch they are one (design §3.3). A write is an awaited call, as a statement, of an instance method
/// of a <c>JsObject</c>-derived type that returns <c>Task</c>; reads return <c>Task&lt;T&gt;</c> and aren't counted.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ChattyAwaitAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "IOP3001";

    private static readonly DiagnosticDescriptor Rule = new(
        Id,
        "Consecutive awaited JS writes outside a batch",
        "Each awaited JS write here is a round trip on Server; send them as one with 'await using (scope.BeginBatch())'",
        "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var compilation = start.Compilation;
            if (compilation.GetTypeByMetadataName("Interoper.JsObject") is { } jsObject
                && compilation.GetTypeByMetadataName("Interoper.JsScope") is { } jsScope
                && compilation.GetTypeByMetadataName("System.Threading.Tasks.Task") is { } task)
            {
                var types = new Types(jsObject, jsScope, task);
                start.RegisterSyntaxNodeAction(c => Analyze(c, types), SyntaxKind.Block, SyntaxKind.SwitchSection);
            }
        });
    }

    private sealed class Types(INamedTypeSymbol jsObject, INamedTypeSymbol jsScope, INamedTypeSymbol task)
    {
        public INamedTypeSymbol JsObject => jsObject;
        public INamedTypeSymbol JsScope => jsScope;
        public INamedTypeSymbol Task => task;
    }

    /// <summary>One report per run of writes, on its second statement.</summary>
    private static void Analyze(SyntaxNodeAnalysisContext context, Types types)
    {
        var statements = context.Node is BlockSyntax block ? block.Statements : ((SwitchSectionSyntax)context.Node).Statements;
        var run = 0;
        foreach (var statement in statements)
        {
            run = IsWrite(context.SemanticModel, statement, types, context.CancellationToken) ? run + 1 : 0;
            if (run == 2 && !InBatch(context.SemanticModel, statement, types, context.CancellationToken))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, statement.GetLocation()));
            }
        }
    }

    /// <summary><c>await x.SetZoomAsync(5);</c> where <c>x</c> is a live object and the method returns <c>Task</c>.</summary>
    private static bool IsWrite(SemanticModel model, StatementSyntax statement, Types types, CancellationToken cancellationToken) =>
        statement is ExpressionStatementSyntax { Expression: AwaitExpressionSyntax { Expression: InvocationExpressionSyntax call } }
        && model.GetSymbolInfo(call, cancellationToken).Symbol is IMethodSymbol { IsStatic: false } method
        && SymbolEqualityComparer.Default.Equals(method.ReturnType, types.Task)
        && DerivesFrom(method.ContainingType, types.JsObject);

    private static bool DerivesFrom(INamedTypeSymbol? type, INamedTypeSymbol baseType)
    {
        for (; type is not null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type, baseType))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Inside <c>await using (scope.BeginBatch())</c>, or after <c>await using var batch = scope.BeginBatch();</c>, in
    /// the same function: a lambda or local function may run after the batch.
    /// </summary>
    private static bool InBatch(SemanticModel model, StatementSyntax statement, Types types, CancellationToken cancellationToken)
    {
        bool IsBeginBatch(ExpressionSyntax? e) =>
            e is InvocationExpressionSyntax
            && model.GetSymbolInfo(e, cancellationToken).Symbol is IMethodSymbol { Name: "BeginBatch" } m
            && SymbolEqualityComparer.Default.Equals(m.ContainingType, types.JsScope);

        bool Declares(VariableDeclarationSyntax? d) => d is not null && d.Variables.Any(v => IsBeginBatch(v.Initializer?.Value));

        for (SyntaxNode node = statement; node.Parent is { } parent; node = parent)
        {
            switch (parent)
            {
                case UsingStatementSyntax u when u.AwaitKeyword != default && (IsBeginBatch(u.Expression) || Declares(u.Declaration)):
                    return true;
                case BlockSyntax b when b.Statements.TakeWhile(s => s != node).Any(s => IsUsingBatch(s)):
                    return true;
                case SwitchSectionSyntax s when s.Statements.TakeWhile(x => x != node).Any(x => IsUsingBatch(x)):
                    return true;
                case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or MemberDeclarationSyntax:
                    return false;
            }
        }
        return false;

        bool IsUsingBatch(StatementSyntax s) =>
            s is LocalDeclarationStatementSyntax l && l.UsingKeyword != default && l.AwaitKeyword != default && Declares(l.Declaration);
    }
}
