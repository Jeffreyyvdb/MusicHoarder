using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDuplicateDetectionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """ALTER TABLE "Songs" ADD COLUMN IF NOT EXISTS "Bitrate" integer""");

            migrationBuilder.Sql(
                """ALTER TABLE "Songs" ADD COLUMN IF NOT EXISTS "DuplicateOfId" integer""");

            migrationBuilder.Sql(
                """ALTER TABLE "Songs" ADD COLUMN IF NOT EXISTS "IsDuplicate" boolean NOT NULL DEFAULT false""");

            migrationBuilder.Sql(
                """CREATE INDEX IF NOT EXISTS "IX_Songs_DeletedAtUtc_IsDuplicate" ON "Songs" ("DeletedAtUtc", "IsDuplicate")""");

            migrationBuilder.Sql(
                """CREATE INDEX IF NOT EXISTS "IX_Songs_DuplicateOfId" ON "Songs" ("DuplicateOfId")""");

            migrationBuilder.Sql(
                """CREATE INDEX IF NOT EXISTS "IX_Songs_Fingerprint" ON "Songs" ("Fingerprint")""");

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint WHERE conname = 'FK_Songs_Songs_DuplicateOfId'
                    ) THEN
                        ALTER TABLE "Songs"
                            ADD CONSTRAINT "FK_Songs_Songs_DuplicateOfId"
                            FOREIGN KEY ("DuplicateOfId") REFERENCES "Songs" ("Id")
                            ON DELETE SET NULL;
                    END IF;
                END
                $$
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Songs_Songs_DuplicateOfId",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Songs_DeletedAtUtc_IsDuplicate",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Songs_DuplicateOfId",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Songs_Fingerprint",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "Bitrate",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "DuplicateOfId",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "IsDuplicate",
                table: "Songs");
        }
    }
}
