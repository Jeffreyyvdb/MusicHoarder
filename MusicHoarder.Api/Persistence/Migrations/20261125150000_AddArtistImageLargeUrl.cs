using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations;

/// <summary>
/// Adds <c>ArtistImages.ImageUrlLarge</c>: the provider's largest portrait, kept next to the
/// grid-sized <c>ImageUrl</c> so hero views don't upscale a ~500px tile while grids stop
/// downloading 640–1000px portraits. Existing rows leave it null and serve <c>ImageUrl</c>
/// everywhere until their next refresh.
/// </summary>
public partial class _20261125150000_AddArtistImageLargeUrl : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ImageUrlLarge",
            table: "ArtistImages",
            type: "text",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ImageUrlLarge",
            table: "ArtistImages");
    }
}
