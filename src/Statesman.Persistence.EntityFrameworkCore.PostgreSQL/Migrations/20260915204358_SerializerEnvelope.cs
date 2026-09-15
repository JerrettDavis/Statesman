using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Statesman.Persistence.EntityFrameworkCore.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class SerializerEnvelope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnvelopeJson",
                table: "StatesmanRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnvelopeJson",
                table: "StatesmanHeads",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnvelopeJson",
                table: "StatesmanRecords");

            migrationBuilder.DropColumn(
                name: "EnvelopeJson",
                table: "StatesmanHeads");
        }
    }
}
