using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace TableSplit.Data;

/// <summary>
/// Replaces EF Core's default IQueryableMethodTranslatingExpressionVisitorFactory
/// so that ViewToTableRedirectVisitor is used for every query compilation context.
/// Register via: optionsBuilder.ReplaceService&lt;
///     IQueryableMethodTranslatingExpressionVisitorFactory,
///     ViewToTableRedirectVisitorFactory&gt;()
/// </summary>
public sealed class ViewToTableRedirectVisitorFactory
    : RelationalQueryableMethodTranslatingExpressionVisitorFactory
{
    public ViewToTableRedirectVisitorFactory(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies)
        : base(dependencies, relationalDependencies) { }

    public override QueryableMethodTranslatingExpressionVisitor Create(
        QueryCompilationContext queryCompilationContext)
        => new ViewToTableRedirectVisitor(
            Dependencies,
            RelationalDependencies,
            queryCompilationContext);
}
