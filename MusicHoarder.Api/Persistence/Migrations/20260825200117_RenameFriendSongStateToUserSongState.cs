using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <summary>
    /// Renames <c>FriendSongStates</c> to <c>UserSongStates</c>, preserving every row.
    ///
    /// <para>
    /// HAND-WRITTEN ON PURPOSE. The scaffolder emitted <c>DropTable</c> + <c>CreateTable</c>, which
    /// would silently delete every invited listener's likes and play counts — the entity was
    /// renamed, so EF sees an unrelated table appearing and the old one going away. A rename must
    /// move the data, so this is expressed as <c>ALTER ... RENAME</c> throughout. If you ever
    /// re-scaffold this migration, check for that pattern again before trusting it.
    /// </para>
    /// </summary>
    public partial class RenameFriendSongStateToUserSongState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "FriendSongStates",
                newName: "UserSongStates");

            // Postgres keeps the old index and constraint names when a table is renamed, so each
            // one is renamed explicitly. Names must match what the model snapshot expects, or a
            // later migration will try to drop and recreate them.
            migrationBuilder.RenameIndex(
                table: "UserSongStates",
                name: "IX_FriendSongStates_SongId",
                newName: "IX_UserSongStates_SongId");

            migrationBuilder.RenameIndex(
                table: "UserSongStates",
                name: "IX_FriendSongStates_UserId_LikedAtUtc",
                newName: "IX_UserSongStates_UserId_LikedAtUtc");

            migrationBuilder.RenameIndex(
                table: "UserSongStates",
                name: "IX_FriendSongStates_UserId_SongId",
                newName: "IX_UserSongStates_UserId_SongId");

            // MigrationBuilder has no RenameConstraint. Renaming the primary key also renames the
            // index backing it.
            migrationBuilder.Sql(
                @"ALTER TABLE ""UserSongStates"" RENAME CONSTRAINT ""PK_FriendSongStates"" TO ""PK_UserSongStates"";");
            migrationBuilder.Sql(
                @"ALTER TABLE ""UserSongStates"" RENAME CONSTRAINT ""FK_FriendSongStates_Songs_SongId"" TO ""FK_UserSongStates_Songs_SongId"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"ALTER TABLE ""UserSongStates"" RENAME CONSTRAINT ""FK_UserSongStates_Songs_SongId"" TO ""FK_FriendSongStates_Songs_SongId"";");
            migrationBuilder.Sql(
                @"ALTER TABLE ""UserSongStates"" RENAME CONSTRAINT ""PK_UserSongStates"" TO ""PK_FriendSongStates"";");

            migrationBuilder.RenameIndex(
                table: "UserSongStates",
                name: "IX_UserSongStates_UserId_SongId",
                newName: "IX_FriendSongStates_UserId_SongId");

            migrationBuilder.RenameIndex(
                table: "UserSongStates",
                name: "IX_UserSongStates_UserId_LikedAtUtc",
                newName: "IX_FriendSongStates_UserId_LikedAtUtc");

            migrationBuilder.RenameIndex(
                table: "UserSongStates",
                name: "IX_UserSongStates_SongId",
                newName: "IX_FriendSongStates_SongId");

            migrationBuilder.RenameTable(
                name: "UserSongStates",
                newName: "FriendSongStates");
        }
    }
}
