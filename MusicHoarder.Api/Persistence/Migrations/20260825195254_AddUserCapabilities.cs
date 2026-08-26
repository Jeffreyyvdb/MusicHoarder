using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Capabilities",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing members (Role = 2) keep the behaviour they had before capabilities existed:
            // they could like and record plays through the shared surface, so they get
            // Capability.TrackListening (1 << 1 = 2). Everything else stays an explicit admin
            // decision. Admin and Demo rows keep 0 — an Admin holds every flag implicitly via
            // CurrentUser.Effective, and Demo is meant to hold none.
            migrationBuilder.Sql(@"UPDATE ""Users"" SET ""Capabilities"" = 2 WHERE ""Role"" = 2;");

            // NOTE: the scaffolder also emitted two no-op UpdateData calls for the HasData seed
            // rows (empty column and value arrays). They were removed deliberately — Npgsql
            // renders them as `UPDATE "Users" SET  WHERE "Id" = ...`, which is a syntax error that
            // fails the whole migration on startup. The model snapshot does not depend on them.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Capabilities",
                table: "Users");
        }
    }
}
