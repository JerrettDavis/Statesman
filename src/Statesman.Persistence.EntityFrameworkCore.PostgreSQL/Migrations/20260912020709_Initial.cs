using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Statesman.Persistence.EntityFrameworkCore.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StatesmanHeads",
                columns: table => new
                {
                    Root = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Partition = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    GlobalPosition = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Operation = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ValueType = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: true),
                    FreshUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ServeUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "text", nullable: true),
                    CausationId = table.Column<string>(type: "text", nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: false),
                    ErrorJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatesmanHeads", x => new { x.Root, x.Path, x.Partition });
                });

            migrationBuilder.CreateTable(
                name: "StatesmanLeases",
                columns: table => new
                {
                    LeaseId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatesmanLeases", x => x.LeaseId);
                });

            migrationBuilder.CreateTable(
                name: "StatesmanRecords",
                columns: table => new
                {
                    Root = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Partition = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    GlobalPosition = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Operation = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ValueType = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: true),
                    FreshUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ServeUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "text", nullable: true),
                    CausationId = table.Column<string>(type: "text", nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: false),
                    ErrorJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatesmanRecords", x => new { x.Root, x.Path, x.Partition, x.Revision });
                });

            migrationBuilder.CreateTable(
                name: "StatesmanSequences",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatesmanSequences", x => x.Name);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StatesmanRecords_GlobalPosition",
                table: "StatesmanRecords",
                column: "GlobalPosition",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StatesmanHeads");

            migrationBuilder.DropTable(
                name: "StatesmanLeases");

            migrationBuilder.DropTable(
                name: "StatesmanRecords");

            migrationBuilder.DropTable(
                name: "StatesmanSequences");
        }
    }
}
