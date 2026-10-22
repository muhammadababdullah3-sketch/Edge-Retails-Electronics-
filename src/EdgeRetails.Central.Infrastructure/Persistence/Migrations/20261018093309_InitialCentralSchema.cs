using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialCentralSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "central");

        migrationBuilder.CreateTable(
            name: "admin_roles",
            schema: "central",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                CapabilitiesJson = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_admin_roles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "admin_users",
            schema: "central",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                PasswordHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Salt = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                MfaSecret = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                MfaEnabled = table.Column<bool>(type: "boolean", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                SecurityRevision = table.Column<int>(type: "integer", nullable: false),
                RowVersion = table.Column<int>(type: "integer", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastLoginAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_admin_users", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "audit_records",
            schema: "central",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ActorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ActorUsername = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Resource = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ResourceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ClientIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                UserAgent = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                PayloadSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                DetailsJson = table.Column<string>(type: "text", nullable: false),
                IsSuccess = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_records", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "operation_records",
            schema: "central",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ClientOperationId = table.Column<Guid>(type: "uuid", nullable: false),
                OperationType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ActorScope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                ShopId = table.Column<Guid>(type: "uuid", nullable: true),
                FingerprintVersion = table.Column<int>(type: "integer", nullable: false),
                PayloadSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CanonicalPayload = table.Column<string>(type: "text", nullable: false),
                ExpectedVersion = table.Column<int>(type: "integer", nullable: false),
                PolicyVersion = table.Column<int>(type: "integer", nullable: false),
                Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                SensitiveApprovalId = table.Column<Guid>(type: "uuid", nullable: true),
                State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                OutcomeId = table.Column<Guid>(type: "uuid", nullable: false),
                ResultReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                ResourceVersion = table.Column<int>(type: "integer", nullable: false),
                AuthorityRevision = table.Column<int>(type: "integer", nullable: false),
                ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                ErrorMessageKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                Retryable = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_operation_records", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "outbox_messages",
            schema: "central",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OccurredOnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                EventType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                AggregateType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                AggregateId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                PayloadJson = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ProcessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RetryCount = table.Column<int>(type: "integer", nullable: false),
                LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_outbox_messages", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "signing_keys",
            schema: "central",
            columns: table => new
            {
                KeyId = table.Column<string>(type: "text", nullable: false),
                KeyVersion = table.Column<int>(type: "integer", nullable: false),
                AlgorithmProfileId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PublicKeyPem = table.Column<string>(type: "text", nullable: false),
                ValidFromUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ValidUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                EmergencyRolloverRevision = table.Column<int>(type: "integer", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_signing_keys", x => x.KeyId);
            });

        migrationBuilder.CreateTable(
            name: "signing_requests",
            schema: "central",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                IssuanceRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                Purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                KeyId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                KeyVersion = table.Column<int>(type: "integer", nullable: false),
                AuthorityRevision = table.Column<int>(type: "integer", nullable: false),
                PayloadSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CanonicalPayload = table.Column<string>(type: "text", nullable: false),
                Signature = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                Status = table.Column<string>(type: "text", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                SignedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_signing_requests", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "step_up_grants",
            schema: "central",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                ActionScope = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                TargetPayloadSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                IsConsumed = table.Column<bool>(type: "boolean", nullable: false),
                ConsumedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_step_up_grants", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "trust_sets",
            schema: "central",
            columns: table => new
            {
                TrustSetVersion = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                EmergencyRolloverRevision = table.Column<int>(type: "integer", nullable: false),
                SignedByTrustedRootKeyId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Signature = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                KeysJson = table.Column<string>(type: "text", nullable: false),
                RevokedKeyIdsJson = table.Column<string>(type: "text", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_trust_sets", x => x.TrustSetVersion);
            });

        migrationBuilder.CreateTable(
            name: "admin_sessions",
            schema: "central",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AdminUserId = table.Column<Guid>(type: "uuid", nullable: false),
                TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                AbsoluteExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastActivityAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                MfaSatisfied = table.Column<bool>(type: "boolean", nullable: false),
                SecurityRevisionAtCreation = table.Column<int>(type: "integer", nullable: false),
                IsRevoked = table.Column<bool>(type: "boolean", nullable: false),
                RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RevocationReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                CapabilitiesJson = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_admin_sessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_admin_sessions_admin_users_AdminUserId",
                    column: x => x.AdminUserId,
                    principalSchema: "central",
                    principalTable: "admin_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "admin_user_roles",
            schema: "central",
            columns: table => new
            {
                AdminUserId = table.Column<Guid>(type: "uuid", nullable: false),
                RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                AssignedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_admin_user_roles", x => new { x.AdminUserId, x.RoleId });
                table.ForeignKey(
                    name: "FK_admin_user_roles_admin_roles_RoleId",
                    column: x => x.RoleId,
                    principalSchema: "central",
                    principalTable: "admin_roles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_admin_user_roles_admin_users_AdminUserId",
                    column: x => x.AdminUserId,
                    principalSchema: "central",
                    principalTable: "admin_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "login_challenges",
            schema: "central",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AdminUserId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                IsConsumed = table.Column<bool>(type: "boolean", nullable: false),
                ConsumedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                FailedAttempts = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_login_challenges", x => x.Id);
                table.ForeignKey(
                    name: "FK_login_challenges_admin_users_AdminUserId",
                    column: x => x.AdminUserId,
                    principalSchema: "central",
                    principalTable: "admin_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_admin_roles_Name",
            schema: "central",
            table: "admin_roles",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_admin_sessions_AdminUserId",
            schema: "central",
            table: "admin_sessions",
            column: "AdminUserId");

        migrationBuilder.CreateIndex(
            name: "IX_admin_sessions_ExpiresAtUtc",
            schema: "central",
            table: "admin_sessions",
            column: "ExpiresAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_admin_sessions_TokenHash",
            schema: "central",
            table: "admin_sessions",
            column: "TokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_admin_user_roles_RoleId",
            schema: "central",
            table: "admin_user_roles",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "IX_admin_users_Username",
            schema: "central",
            table: "admin_users",
            column: "Username",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_audit_records_ActorId",
            schema: "central",
            table: "audit_records",
            column: "ActorId");

        migrationBuilder.CreateIndex(
            name: "IX_audit_records_CorrelationId",
            schema: "central",
            table: "audit_records",
            column: "CorrelationId");

        migrationBuilder.CreateIndex(
            name: "IX_audit_records_TimestampUtc",
            schema: "central",
            table: "audit_records",
            column: "TimestampUtc");

        migrationBuilder.CreateIndex(
            name: "IX_login_challenges_AdminUserId",
            schema: "central",
            table: "login_challenges",
            column: "AdminUserId");

        migrationBuilder.CreateIndex(
            name: "IX_login_challenges_ExpiresAtUtc",
            schema: "central",
            table: "login_challenges",
            column: "ExpiresAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_operation_records_ActorScope_OperationType_PayloadSha256",
            schema: "central",
            table: "operation_records",
            columns: new[] { "ActorScope", "OperationType", "PayloadSha256" });

        migrationBuilder.CreateIndex(
            name: "IX_operation_records_ClientOperationId",
            schema: "central",
            table: "operation_records",
            column: "ClientOperationId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_operation_records_CreatedAtUtc",
            schema: "central",
            table: "operation_records",
            column: "CreatedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_outbox_messages_Status_OccurredOnUtc",
            schema: "central",
            table: "outbox_messages",
            columns: new[] { "Status", "OccurredOnUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_signing_keys_Status",
            schema: "central",
            table: "signing_keys",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_signing_requests_IssuanceRequestId",
            schema: "central",
            table: "signing_requests",
            column: "IssuanceRequestId");

        migrationBuilder.CreateIndex(
            name: "IX_signing_requests_KeyId_KeyVersion",
            schema: "central",
            table: "signing_requests",
            columns: new[] { "KeyId", "KeyVersion" });

        migrationBuilder.CreateIndex(
            name: "IX_step_up_grants_ExpiresAtUtc",
            schema: "central",
            table: "step_up_grants",
            column: "ExpiresAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_step_up_grants_SessionId_ActionScope_TargetPayloadSha256",
            schema: "central",
            table: "step_up_grants",
            columns: new[] { "SessionId", "ActionScope", "TargetPayloadSha256" });

        migrationBuilder.Sql(@"
            DO $$
            BEGIN
                -- Login roles and credentials are provisioned explicitly by the
                -- owned environment administrator, never by schema migration.
                IF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'central_runtime_user') THEN
                    GRANT USAGE ON SCHEMA central TO central_runtime_user;
                    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA central TO central_runtime_user;
                    REVOKE UPDATE, DELETE, TRUNCATE ON central.audit_records FROM central_runtime_user;
                END IF;
            END
            $$;

            CREATE OR REPLACE FUNCTION central.prevent_audit_modification()
            RETURNS TRIGGER AS $$
            BEGIN
                RAISE EXCEPTION 'AUDIT_IMMUTABILITY_VIOLATION: Updates and deletes on audit_records are strictly prohibited at the database boundary.';
            END;
            $$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS trg_audit_records_immutable ON central.audit_records;
            CREATE TRIGGER trg_audit_records_immutable
            BEFORE UPDATE OR DELETE ON central.audit_records
            FOR EACH ROW EXECUTE FUNCTION central.prevent_audit_modification();
        ");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "admin_sessions",
            schema: "central");

        migrationBuilder.DropTable(
            name: "admin_user_roles",
            schema: "central");

        migrationBuilder.DropTable(
            name: "audit_records",
            schema: "central");

        migrationBuilder.DropTable(
            name: "login_challenges",
            schema: "central");

        migrationBuilder.DropTable(
            name: "operation_records",
            schema: "central");

        migrationBuilder.DropTable(
            name: "outbox_messages",
            schema: "central");

        migrationBuilder.DropTable(
            name: "signing_keys",
            schema: "central");

        migrationBuilder.DropTable(
            name: "signing_requests",
            schema: "central");

        migrationBuilder.DropTable(
            name: "step_up_grants",
            schema: "central");

        migrationBuilder.DropTable(
            name: "trust_sets",
            schema: "central");

        migrationBuilder.DropTable(
            name: "admin_roles",
            schema: "central");

        migrationBuilder.DropTable(
            name: "admin_users",
            schema: "central");
    }
}
