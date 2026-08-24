using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GamerBio.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakeBpmNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "Bpm",
                table: "biosignals",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "Bpm",
                table: "deadly_events",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Existing NULLs have no meaningful heart rate to fall back to, so
            // reverting collapses them to 0 — the value this column carried for
            // "no reading" before it became nullable.
            migrationBuilder.Sql("UPDATE biosignals SET \"Bpm\" = 0 WHERE \"Bpm\" IS NULL;");
            migrationBuilder.Sql("UPDATE deadly_events SET \"Bpm\" = 0 WHERE \"Bpm\" IS NULL;");

            migrationBuilder.AlterColumn<int>(
                name: "Bpm",
                table: "biosignals",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Bpm",
                table: "deadly_events",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
