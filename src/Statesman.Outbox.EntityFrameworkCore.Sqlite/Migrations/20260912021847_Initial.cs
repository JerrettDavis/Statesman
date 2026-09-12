using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Statesman.Outbox.EntityFrameworkCore.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StatesmanOutboxCursors",
                columns: table => new
                {
                    OutboxId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Position = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatesmanOutboxCursors", x => x.OutboxId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StatesmanOutboxCursors");
        }
    }
}
