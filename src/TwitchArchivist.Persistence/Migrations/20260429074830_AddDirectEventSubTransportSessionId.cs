using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchArchivist.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectEventSubTransportSessionId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TransportSessionId",
                table: "EventSubscriptionStates",
                type: "TEXT",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TransportSessionId",
                table: "EventSubscriptionStates");
        }
    }
}
