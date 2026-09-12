using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Statesman.Outbox.EntityFrameworkCore.SqlServer.Migrations
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
                    OutboxId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Position = table.Column<long>(type: "bigint", nullable: false)
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
