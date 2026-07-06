# CLAUDE.md

## Project overview

Demonstrates and fixes an EF Core bug: `ExecuteDelete` / `ExecuteUpdate` route bulk
DML through a view instead of the underlying physical table when an entity is mapped
with `ToTable("EDIMessage").ToView("kvw_EDIMessage")`.

## Branches

| Branch | EF Core | .NET | Status |
|--------|---------|------|--------|
| `master` | 7.x | net8.0 | workaround via custom visitor |
| `efcore10` | 10.x | net10.0 | native fix, no workaround needed |

## Build and test

```bash
# master branch (EF Core 7, .NET 8)
dotnet build
dotnet test

# efcore10 branch (EF Core 10, .NET 10)
~/.dotnet10/dotnet build
~/.dotnet10/dotnet test
```

The .NET 10 SDK is installed at `~/.dotnet10/`. The system `dotnet` (8.x) is used on master.

## Key files

```
TableSplit/
  Models/EDIMessage.cs                       entity model
  Data/AppDbContext.cs                       dual mapping: ToTable().ToView()
  Data/AppDbContextFactory.cs               design-time factory for migrations
  Data/Migrations/                           SQLite migration (creates table, archive table, view)
  Data/ViewToTableRedirectVisitor.cs        [master only] custom EF Core visitor
  Data/ViewToTableRedirectVisitorFactory.cs [master only] factory registered via ReplaceService<>

TableSplit.Tests/
  EDIMessageTests.cs                         6 xUnit tests covering SELECT, DELETE, UPDATE
```

## Database schema (created by migration)

```sql
-- physical table (INSERT/UPDATE/DELETE target)
CREATE TABLE EDIMessage ( Id, MessageType, SenderId, ReceiverId, Content, Status, CreatedAt, IsArchived )

-- archive table (managed outside EF, read-only from EF's perspective)
CREATE TABLE EDIMessage_Archive ( same columns )

-- unified read surface
CREATE VIEW kvw_EDIMessage AS
  SELECT ... FROM EDIMessage
  UNION ALL
  SELECT ... FROM EDIMessage_Archive
```

## The bug (EF Core 7–9)

`ExecuteDelete` / `ExecuteUpdate` on a `ToTable().ToView()` entity generate SQL
targeting the view, not the physical table:

```sql
-- generated (wrong)
DELETE FROM "kvw_EDIMessage" AS "k" WHERE "k"."Status" = 'Processed'
UPDATE "kvw_EDIMessage" AS "k" SET "Status" = @p WHERE ...
```

Databases reject DML on UNION ALL views.

## The workaround (master — EF Core 7)

`ViewToTableRedirectVisitor` overrides two virtual hooks in
`RelationalQueryableMethodTranslatingExpressionVisitor`:

- `IsValidSelectExpressionForExecuteDelete`
- `IsValidSelectExpressionForExecuteUpdate`

It redirects the DELETE/UPDATE target to the physical table by:
1. Creating a new `TableExpression` for the physical table via reflection (internal ctor)
2. Copying the alias from the original view `TableExpression` so WHERE column references stay valid
3. Replacing the entry in `SelectExpression._tables` so the reference-equality check
   in `QuerySqlGenerator.VisitDelete/VisitUpdate` passes

Registered via:
```csharp
optionsBuilder.ReplaceService<
    IQueryableMethodTranslatingExpressionVisitorFactory,
    ViewToTableRedirectVisitorFactory>();
```

## EF Core version compatibility

| Version | `IsValidSelectExpressionForExecuteDelete` 2nd param | Bug present |
|---------|---------------------------------------------------|-------------|
| 7.x | `EntityShaperExpression` | Yes |
| 8.x | `StructuralTypeShaperExpression` | Yes |
| 9.x | `StructuralTypeShaperExpression` | Yes |
| 10.x | removed (new single-arg overload) | **No — fixed** |

If upgrading from master to EF Core 10: delete the two visitor files and remove the
`ReplaceService<>()` call. Everything else is unchanged.

## Migrations

```bash
# Scaffold a new migration (uses AppDbContextFactory, targets SQLite)
dotnet ef migrations add <Name> --project TableSplit --startup-project TableSplit

# The migration creates EDIMessage_Archive and kvw_EDIMessage via raw SQL
# because EF Core does not manage archive tables or views natively
```
