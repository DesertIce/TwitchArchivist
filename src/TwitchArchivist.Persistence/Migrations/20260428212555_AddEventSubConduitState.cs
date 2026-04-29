using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchArchivist.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventSubConduitState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventSubConduits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TwitchConduitId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ShardCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventSubConduits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventSubConduitShards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventSubConduitId = table.Column<int>(type: "INTEGER", nullable: false),
                    ShardId = table.Column<int>(type: "INTEGER", nullable: false),
                    TransportSessionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastWelcomeUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastAssignmentUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventSubConduitShards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventSubConduitShards_EventSubConduits_EventSubConduitId",
                        column: x => x.EventSubConduitId,
                        principalTable: "EventSubConduits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventSubSubscriptionBindings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventSubConduitId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelConfigurationId = table.Column<int>(type: "INTEGER", nullable: false),
                    SubscriptionType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TwitchSubscriptionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastVerifiedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventSubSubscriptionBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventSubSubscriptionBindings_ChannelConfigurations_ChannelConfigurationId",
                        column: x => x.ChannelConfigurationId,
                        principalTable: "ChannelConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EventSubSubscriptionBindings_EventSubConduits_EventSubConduitId",
                        column: x => x.EventSubConduitId,
                        principalTable: "EventSubConduits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventSubConduits_TwitchConduitId",
                table: "EventSubConduits",
                column: "TwitchConduitId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventSubConduitShards_EventSubConduitId_ShardId",
                table: "EventSubConduitShards",
                columns: new[] { "EventSubConduitId", "ShardId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventSubSubscriptionBindings_ChannelConfigurationId_SubscriptionType",
                table: "EventSubSubscriptionBindings",
                columns: new[] { "ChannelConfigurationId", "SubscriptionType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventSubSubscriptionBindings_EventSubConduitId",
                table: "EventSubSubscriptionBindings",
                column: "EventSubConduitId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventSubConduitShards");

            migrationBuilder.DropTable(
                name: "EventSubSubscriptionBindings");

            migrationBuilder.DropTable(
                name: "EventSubConduits");
        }
    }
}
