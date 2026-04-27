using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwitchArchivist.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChannelConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TwitchLogin = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TwitchUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OutputDirectory = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArchiveJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChannelConfigurationId = table.Column<int>(type: "INTEGER", nullable: false),
                    TriggerSource = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    VodId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OutputPath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArchiveJobs_ChannelConfigurations_ChannelConfigurationId",
                        column: x => x.ChannelConfigurationId,
                        principalTable: "ChannelConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventSubscriptionStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
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
                    table.PrimaryKey("PK_EventSubscriptionStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventSubscriptionStates_ChannelConfigurations_ChannelConfigurationId",
                        column: x => x.ChannelConfigurationId,
                        principalTable: "ChannelConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StreamSessionStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChannelConfigurationId = table.Column<int>(type: "INTEGER", nullable: false),
                    LastKnownStreamId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastOnlineUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastOfflineUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastProcessedOfflineMessageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StreamSessionStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StreamSessionStates_ChannelConfigurations_ChannelConfigurationId",
                        column: x => x.ChannelConfigurationId,
                        principalTable: "ChannelConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveJobs_ChannelConfigurationId",
                table: "ArchiveJobs",
                column: "ChannelConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelConfigurations_TwitchLogin",
                table: "ChannelConfigurations",
                column: "TwitchLogin",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventSubscriptionStates_ChannelConfigurationId_SubscriptionType",
                table: "EventSubscriptionStates",
                columns: new[] { "ChannelConfigurationId", "SubscriptionType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StreamSessionStates_ChannelConfigurationId",
                table: "StreamSessionStates",
                column: "ChannelConfigurationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchiveJobs");

            migrationBuilder.DropTable(
                name: "EventSubscriptionStates");

            migrationBuilder.DropTable(
                name: "StreamSessionStates");

            migrationBuilder.DropTable(
                name: "ChannelConfigurations");
        }
    }
}
