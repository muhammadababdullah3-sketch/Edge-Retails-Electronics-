using System;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(EdgeRetailsDbContext))]
    [Migration("20261026103000_SupervisorApprovalGrants")]
    public partial class SupervisorApprovalGrants : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS posting.supervisor_approval_grants (
                    id uuid NOT NULL,
                    token_hash character varying(128) NOT NULL,
                    supervisor_id uuid NOT NULL,
                    posting_date date NOT NULL,
                    operation_type character varying(64) NOT NULL,
                    reason character varying(1000) NOT NULL,
                    expires_at_utc timestamp with time zone NOT NULL,
                    created_at_utc timestamp with time zone NOT NULL,
                    consumed boolean NOT NULL DEFAULT FALSE,
                    consumed_at_utc timestamp with time zone NULL,
                    CONSTRAINT pk_supervisor_approval_grants PRIMARY KEY (id)
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ix_supervisor_approval_grants_token_hash
                    ON posting.supervisor_approval_grants (token_hash);

                CREATE INDEX IF NOT EXISTS ix_supervisor_approval_grants_consumed_expires_at_utc
                    ON posting.supervisor_approval_grants (consumed, expires_at_utc);
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS posting.supervisor_approval_grants;");
        }
    }
}
