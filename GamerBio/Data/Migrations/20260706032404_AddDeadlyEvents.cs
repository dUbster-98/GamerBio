using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GamerBio.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeadlyEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deadly_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    BpmScore = table.Column<int>(type: "integer", nullable: false),
                    GsrScore = table.Column<int>(type: "integer", nullable: false),
                    LowVariabilityScore = table.Column<int>(type: "integer", nullable: false),
                    EmotionScore = table.Column<int>(type: "integer", nullable: false),
                    DominantEmotion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Bpm = table.Column<int>(type: "integer", nullable: false),
                    Gsr = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deadly_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deadly_events_OccurredAt",
                table: "deadly_events",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deadly_events");
        }
    }
}
