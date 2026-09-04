using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchArchivist.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelCompressionRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CompressEnabled",
                table: "ChannelConfigurations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CompressVodCount",
                table: "ChannelConfigurations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompressEnabled",
                table: "ChannelConfigurations");

            migrationBuilder.DropColumn(
                name: "CompressVodCount",
                table: "ChannelConfigurations");
        }
    }
}
