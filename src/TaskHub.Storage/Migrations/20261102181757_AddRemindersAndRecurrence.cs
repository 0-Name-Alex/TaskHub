using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskHub.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddRemindersAndRecurrence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastReminderUtc",
                table: "tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NextReminderUtc",
                table: "tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Recurrence",
                table: "tasks",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReminderOffsets",
                table: "tasks",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "RepeatReminderMinutes",
                table: "tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tasks_NextReminderUtc",
                table: "tasks",
                column: "NextReminderUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tasks_NextReminderUtc",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "LastReminderUtc",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "NextReminderUtc",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "Recurrence",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "ReminderOffsets",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RepeatReminderMinutes",
                table: "tasks");
        }
    }
}
