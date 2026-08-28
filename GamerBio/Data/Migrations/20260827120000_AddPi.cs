using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GamerBio.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Perfusion index [%] from the IR PPG waveform. Nullable because the
            // firmware reports it only while it can actually see a pulse — 0 would
            // read as "vessels fully constricted", i.e. maximum stress.
            migrationBuilder.AddColumn<double>(
                name: "Pi",
                table: "biosignals",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Pi",
                table: "deadly_events",
                type: "double precision",
                nullable: true);

            // Existing rows predate the factor, so 0 is the honest backfill: it is
            // what PI contributed to those events (nothing).
            migrationBuilder.AddColumn<int>(
                name: "PiScore",
                table: "deadly_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Pi", table: "biosignals");
            migrationBuilder.DropColumn(name: "Pi", table: "deadly_events");
            migrationBuilder.DropColumn(name: "PiScore", table: "deadly_events");
        }
    }
}
