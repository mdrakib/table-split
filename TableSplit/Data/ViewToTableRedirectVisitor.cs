using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

#nullable disable warnings

namespace TableSplit.Data;

/// <summary>
/// Overrides the two virtual routing hooks EF Core 7 calls when generating
/// ExecuteDelete / ExecuteUpdate SQL.  By default both hooks return the view's
/// TableExpression (kvw_EDIMessage), causing "cannot modify view" errors.
///
/// We detect when the entity has a physical-table mapping and redirect the
/// DELETE/UPDATE target (and the matching FROM entry in the SelectExpression)
/// to that physical table, preserving the existing alias so column references
/// in WHERE clauses stay valid.
///
/// Internal-API surface: TableExpression's internal ctor (no public one exists)
/// and SelectExpression._tables (the writable list behind the read-only .Tables
/// property).  Everything else is public EF Core metadata APIs.
/// </summary>
internal sealed class ViewToTableRedirectVisitor : RelationalQueryableMethodTranslatingExpressionVisitor
{
    // Cached reflection pieces
    private static readonly ConstructorInfo TableExprCtor =
        typeof(TableExpression)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 1
                      && c.GetParameters()[0].ParameterType == typeof(ITableBase));

    private static readonly FieldInfo SelectTablesField =
        typeof(SelectExpression)
            .GetField("_tables", BindingFlags.NonPublic | BindingFlags.Instance)!;

    // Alias setter is internal — use the backing field (TableExpressionBase auto-property)
    private static readonly FieldInfo AliasBackingField =
        typeof(TableExpressionBase)
            .GetField("<Alias>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!;

    public ViewToTableRedirectVisitor(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
        QueryCompilationContext queryCompilationContext)
        : base(dependencies, relationalDependencies, queryCompilationContext) { }

    /// <summary>Called by TranslateExecuteDelete to resolve the DELETE target.</summary>
    protected override bool IsValidSelectExpressionForExecuteDelete(
        SelectExpression selectExpression,
        EntityShaperExpression shaper,
        out TableExpression tableExpression)
    {
        if (!base.IsValidSelectExpressionForExecuteDelete(selectExpression, shaper, out tableExpression))
            return false;

        RedirectIfView(selectExpression, shaper.EntityType, ref tableExpression);
        return true;
    }

    /// <summary>Called by TranslateExecuteUpdate to resolve the UPDATE target.</summary>
    /// <remarks>In EF Core 7 the second parameter is EntityShaperExpression, same as Delete.</remarks>
    protected override bool IsValidSelectExpressionForExecuteUpdate(
        SelectExpression selectExpression,
        EntityShaperExpression shaper,
        out TableExpression tableExpression)
    {
        if (!base.IsValidSelectExpressionForExecuteUpdate(selectExpression, shaper, out tableExpression))
            return false;

        RedirectIfView(selectExpression, shaper.EntityType, ref tableExpression);
        return true;
    }

    /// <summary>
    /// If the current tableExpression points to a view (its Name differs from the entity's
    /// physical table name), replace it — both the out parameter AND the matching entry
    /// inside SelectExpression._tables.
    ///
    /// QuerySqlGenerator.VisitDelete / VisitUpdate verify that deleteExpression.Table (==
    /// tableExpression) is the same object reference as selectExpression.Tables[idx].
    /// Updating both ensures that reference check passes and the generated SQL is coherent.
    ///
    /// The new TableExpression inherits the alias of the original (e.g. "k" for kvw_EDIMessage)
    /// so column references in the WHERE clause stay valid.
    /// </summary>
    private static void RedirectIfView(
        SelectExpression selectExpression,
        IEntityType entityType,
        ref TableExpression tableExpression)
    {
        var physicalTable = entityType.GetTableMappings().FirstOrDefault()?.Table;
        if (physicalTable is null || physicalTable.Name == tableExpression.Name)
            return;

        // Build a new TableExpression for the physical table, keeping the alias.
        var physicalTE = (TableExpression)TableExprCtor.Invoke(new object[] { physicalTable });
        AliasBackingField.SetValue(physicalTE, tableExpression.Alias);

        // Also replace the entry in SelectExpression._tables so the reference-equality
        // check in QuerySqlGenerator.VisitDelete / VisitUpdate passes.
        var tables = (List<TableExpressionBase>)SelectTablesField.GetValue(selectExpression)!;
        var idx = tables.IndexOf(tableExpression);
        if (idx >= 0) tables[idx] = physicalTE;

        tableExpression = physicalTE;
    }
}
