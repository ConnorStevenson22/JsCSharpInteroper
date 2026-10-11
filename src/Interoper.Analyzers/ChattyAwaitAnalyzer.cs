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
                start.RegisterSyntaxNodeAction(new Analysis(jsObject, jsScope, task).Run,
                    SyntaxKind.Block, SyntaxKind.SwitchSection, SyntaxKind.CompilationUnit);
            }
        });
    }

    /// <summary>The statements of a block, a switch section or the top-level statements.</summary>
    private static IEnumerable<StatementSyntax> Statements(SyntaxNode node) => node switch
    {
        BlockSyntax b => b.Statements,
        SwitchSectionSyntax s => s.Statements,
        CompilationUnitSyntax u => u.Members.OfType<GlobalStatementSyntax>().Select(g => g.Statement),
        _ => Enumerable.Empty<StatementSyntax>(),
    };

    private sealed class Analysis(INamedTypeSymbol jsObject, INamedTypeSymbol jsScope, INamedTypeSymbol task)
    {
        /// <summary>One report per run of writes, on its second statement.</summary>
        public void Run(SyntaxNodeAnalysisContext context)
        {
            var run = 0;
            foreach (var statement in Statements(context.Node))
            {
                run = IsWrite(context.SemanticModel, statement, context.CancellationToken) ? run + 1 : 0;
                if (run == 2 && !InBatch(context.SemanticModel, statement, context.CancellationToken))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, statement.GetLocation()));
                }
            }
        }

        /// <summary>
        /// <c>await x.SetZoomAsync(5);</c>, with or without <c>.ConfigureAwait(…)</c>, where <c>x</c> is a live object
        /// and the method returns <c>Task</c>.
        /// </summary>
        private bool IsWrite(SemanticModel model, StatementSyntax statement, CancellationToken cancellationToken)
        {
            if (statement is not ExpressionStatementSyntax { Expression: AwaitExpressionSyntax { Expression: InvocationExpressionSyntax call } })
            {
                return false;
            }
            if (call is { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait", Expression: InvocationExpressionSyntax inner } }
                && model.GetSymbolInfo(call, cancellationToken).Symbol is IMethodSymbol configureAwait
                && SymbolEqualityComparer.Default.Equals(configureAwait.ContainingType, task))
            {
                call = inner;
            }
            return model.GetSymbolInfo(call, cancellationToken).Symbol is IMethodSymbol { IsStatic: false } method
                && SymbolEqualityComparer.Default.Equals(method.ReturnType, task)
                && DerivesFromJsObject(method.ContainingType);
        }

        private bool DerivesFromJsObject(INamedTypeSymbol? type)
        {
            for (; type is not null; type = type.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(type, jsObject))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Inside <c>await using (scope.BeginBatch())</c> (or of a local it initialised), or after
        /// <c>await using var batch = scope.BeginBatch();</c>, in the same function: a lambda or local function may run
        /// after the batch.
        /// </summary>
        private bool InBatch(SemanticModel model, StatementSyntax statement, CancellationToken cancellationToken)
        {
            for (SyntaxNode node = statement; node.Parent is { } parent; node = parent)
            {
                switch (parent)
                {
                    case UsingStatementSyntax u when u.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)
                        && (IsBeginBatch(u.Expression) || Declares(u.Declaration)):
                        return true;
                    case GlobalStatementSyntax:
                        continue; // a top-level statement: its compilation unit holds the ones before it
                    case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or MemberDeclarationSyntax:
                        return false;
                }
                // The statements before this one (a top-level one is in a GlobalStatementSyntax).
                if (Statements(parent).TakeWhile(s => s != node && s.Parent != node).Any(IsUsingBatch))
                {
                    return true;
                }
            }
            return false;

            bool IsUsingBatch(StatementSyntax s) =>
                s is LocalDeclarationStatementSyntax l && l.UsingKeyword.IsKind(SyntaxKind.UsingKeyword) && l.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)
                && Declares(l.Declaration);

            bool Declares(VariableDeclarationSyntax? d) => d is not null && d.Variables.Any(v => IsBeginBatch(v.Initializer?.Value));

            // scope.BeginBatch(), or a local it initialised: var batch = scope.BeginBatch(); await using (batch) { … }
            bool IsBeginBatch(ExpressionSyntax? e) => e is not null && model.GetSymbolInfo(e, cancellationToken).Symbol switch
            {
                IMethodSymbol { Name: "BeginBatch" } m => e is InvocationExpressionSyntax && SymbolEqualityComparer.Default.Equals(m.ContainingType, jsScope),
                ILocalSymbol local => local.DeclaringSyntaxReferences.Any(r => r.GetSyntax(cancellationToken) is VariableDeclaratorSyntax v && IsBeginBatch(v.Initializer?.Value)),
                _ => false,
            };
        }
    }
}
