using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TableSplit.Data;
using TableSplit.Models;
using Xunit.Abstractions;

namespace TableSplit.Tests;

/// <summary>
/// EDIMessage is mapped to both the EDIMessage table (writes) and kvw_EDIMessage view (reads)
/// via ToTable("EDIMessage").ToView("kvw_EDIMessage").
///
/// Tests document which operations work and which fail under this mapping in EF Core 7.
/// </summary>
public class EDIMessageTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDbContext _ctx;
    private readonly ITestOutputHelper _out;

    public EDIMessageTests(ITestOutputHelper output)
    {
        _out = output;
        _dbPath = Path.Combine(Path.GetTempPath(), $"edi_test_{Guid.NewGuid():N}.db");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .ReplaceService<
                Microsoft.EntityFrameworkCore.Query.IQueryableMethodTranslatingExpressionVisitorFactory,
                TableSplit.Data.ViewToTableRedirectVisitorFactory>()
            .LogTo(msg => _out.WriteLine(msg), LogLevel.Information)
            .EnableSensitiveDataLogging()
            .Options;

        _ctx = new AppDbContext(options);
        _ctx.Database.Migrate();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private EDIMessage MakeMessage(string type, string status, bool isArchived = false) => new()
    {
        MessageType = type,
        SenderId    = "SENDER",
        ReceiverId  = "RECV",
        Content     = $"payload for {type}",
        Status      = status,
        CreatedAt   = DateTime.UtcNow,
        IsArchived  = isArchived
    };

    private Task SeedArchiveAsync(int id, string status) =>
        _ctx.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO EDIMessage_Archive
                (Id, MessageType, SenderId, ReceiverId, Content, Status, CreatedAt, IsArchived)
            VALUES
                ({id}, 'X12-ARC', 'ARC_SENDER', 'ARC_RECV',
                 'Archived payload', '{status}', CURRENT_TIMESTAMP, 1)
            """);

    private async Task<int> CountInTableAsync(string tableName)
    {
        using var conn = _ctx.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\"";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        using var conn = _ctx.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync();
        return v == DBNull.Value ? null : v?.ToString();
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// EXPECTED TO PASS.
    /// SELECT is routed through kvw_EDIMessage (UNION ALL of both tables).
    /// </summary>
    [Fact]
    public async Task View_Returns_Records_From_Both_Tables()
    {
        _ctx.EDIMessages.Add(MakeMessage("X12-850", "Pending"));
        await _ctx.SaveChangesAsync();

        await SeedArchiveAsync(id: 1000, status: "Archived");

        var all = await _ctx.EDIMessages.AsNoTracking().ToListAsync();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, m => m.Status == "Pending");
        Assert.Contains(all, m => m.Status == "Archived");
    }

    /// <summary>
    /// EXPECTED TO FAIL in EF Core 7.
    /// ExecuteDelete generates: DELETE FROM "kvw_EDIMessage" WHERE ...
    /// SQLite rejects deleting from a UNION ALL view.
    /// </summary>
    [Fact]
    public async Task ExecuteDelete_Targets_Main_Table_Only()
    {
        _ctx.EDIMessages.Add(MakeMessage("X12-810", "Processed"));
        await _ctx.SaveChangesAsync();

        await SeedArchiveAsync(id: 2000, status: "Processed");

        // EF Core 7 generates: DELETE FROM "kvw_EDIMessage" WHERE "Status" = 'Processed'
        int deleted = await _ctx.EDIMessages
            .Where(m => m.Status == "Processed")
            .ExecuteDeleteAsync();

        Assert.Equal(1, deleted);
        Assert.Equal(0, await CountInTableAsync("EDIMessage"));
        Assert.Equal(1, await CountInTableAsync("EDIMessage_Archive"));
    }

    /// <summary>
    /// EXPECTED TO FAIL in EF Core 7.
    /// ExecuteUpdate generates: UPDATE "kvw_EDIMessage" SET ... WHERE ...
    /// SQLite rejects updating a UNION ALL view.
    /// </summary>
    [Fact]
    public async Task ExecuteUpdate_Targets_Main_Table_Only()
    {
        _ctx.EDIMessages.Add(MakeMessage("X12-856", "Pending"));
        await _ctx.SaveChangesAsync();

        await SeedArchiveAsync(id: 3000, status: "Pending");

        // EF Core 7 generates: UPDATE "kvw_EDIMessage" SET "Status" = 'Processing' WHERE "Status" = 'Pending'
        int updated = await _ctx.EDIMessages
            .Where(m => m.Status == "Pending")
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "Processing"));

        Assert.Equal(1, updated);

        string? mainStatus    = await ScalarAsync("SELECT Status FROM EDIMessage LIMIT 1");
        string? archiveStatus = await ScalarAsync("SELECT Status FROM EDIMessage_Archive WHERE Id = 3000");

        Assert.Equal("Processing", mainStatus);
        Assert.Equal("Pending",    archiveStatus);
    }

    /// <summary>
    /// EXPECTED TO FAIL in EF Core 7 (same root cause as ExecuteDelete above).
    /// </summary>
    [Fact]
    public async Task ExecuteDelete_With_No_Match_Returns_Zero()
    {
        _ctx.EDIMessages.Add(MakeMessage("X12-997", "Pending"));
        await _ctx.SaveChangesAsync();

        int deleted = await _ctx.EDIMessages
            .Where(m => m.Status == "NonExistentStatus")
            .ExecuteDeleteAsync();

        Assert.Equal(0, deleted);
        Assert.Equal(1, await CountInTableAsync("EDIMessage"));
    }

    /// <summary>
    /// EXPECTED TO FAIL in EF Core 7 (same root cause as ExecuteUpdate above).
    /// </summary>
    [Fact]
    public async Task ExecuteUpdate_Updates_Multiple_Rows_In_Main_Table()
    {
        _ctx.EDIMessages.AddRange(
            MakeMessage("X12-850", "Pending"),
            MakeMessage("X12-850", "Pending"),
            MakeMessage("X12-850", "Done")
        );
        await _ctx.SaveChangesAsync();

        int updated = await _ctx.EDIMessages
            .Where(m => m.Status == "Pending")
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "Acknowledged"));

        Assert.Equal(2, updated);

        int untouched = await _ctx.EDIMessages.AsNoTracking()
            .CountAsync(m => m.Status == "Done");
        Assert.Equal(1, untouched);
    }

    /// <summary>
    /// EXPECTED TO PASS.
    /// LINQ composition over the view (SELECT with WHERE pushed down as subquery) works fine.
    /// </summary>
    [Fact]
    public async Task View_Supports_Linq_Composition()
    {
        _ctx.EDIMessages.Add(MakeMessage("X12-850", "Pending"));
        _ctx.EDIMessages.Add(MakeMessage("X12-810", "Done"));
        await _ctx.SaveChangesAsync();

        await SeedArchiveAsync(id: 4000, status: "Pending");
        await SeedArchiveAsync(id: 4001, status: "Done");

        var pendingOnly = await _ctx.EDIMessages
            .AsNoTracking()
            .Where(m => m.Status == "Pending")
            .ToListAsync();

        Assert.Equal(2, pendingOnly.Count);
        Assert.All(pendingOnly, m => Assert.Equal("Pending", m.Status));
    }

    public void Dispose()
    {
        _ctx.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}
