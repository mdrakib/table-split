using Microsoft.EntityFrameworkCore;
using TableSplit.Models;

namespace TableSplit.Data;

public class AppDbContext : DbContext
{
    public DbSet<EDIMessage> EDIMessages { get; set; } = null!;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EDIMessage>(eb =>
        {
            // Reads (SELECT) routed through kvw_EDIMessage view.
            // Writes (INSERT/UPDATE/DELETE via SaveChanges) target EDIMessage table.
            eb.ToTable("EDIMessage")
              .ToView("kvw_EDIMessage");

            eb.HasKey(e => e.Id);
            eb.Property(e => e.MessageType).HasMaxLength(50).IsRequired();
            eb.Property(e => e.SenderId).HasMaxLength(100).IsRequired();
            eb.Property(e => e.ReceiverId).HasMaxLength(100).IsRequired();
            eb.Property(e => e.Content).IsRequired();
            eb.Property(e => e.Status).HasMaxLength(50);
            eb.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });
    }
}
