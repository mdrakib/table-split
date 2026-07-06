using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TableSplit.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EDIMessage",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MessageType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    SenderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ReceiverId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EDIMessage", x => x.Id);
                });

            // Identical schema to EDIMessage; managed outside EF tracking (archival destination).
            migrationBuilder.Sql("""
                CREATE TABLE EDIMessage_Archive (
                    Id         INTEGER NOT NULL PRIMARY KEY,
                    MessageType TEXT    NOT NULL,
                    SenderId   TEXT    NOT NULL,
                    ReceiverId TEXT    NOT NULL,
                    Content    TEXT    NOT NULL,
                    Status     TEXT    NULL,
                    CreatedAt  TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
                    IsArchived INTEGER NOT NULL DEFAULT 1
                )
                """);

            // Unified read surface: active + archived rows.
            migrationBuilder.Sql("""
                CREATE VIEW kvw_EDIMessage AS
                SELECT Id, MessageType, SenderId, ReceiverId, Content, Status, CreatedAt, IsArchived
                  FROM EDIMessage
                UNION ALL
                SELECT Id, MessageType, SenderId, ReceiverId, Content, Status, CreatedAt, IsArchived
                  FROM EDIMessage_Archive
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS kvw_EDIMessage");
            migrationBuilder.Sql("DROP TABLE IF EXISTS EDIMessage_Archive");

            migrationBuilder.DropTable(
                name: "EDIMessage");
        }
    }
}
