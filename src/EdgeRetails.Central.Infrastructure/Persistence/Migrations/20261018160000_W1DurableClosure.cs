using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;
[DbContext(typeof(CentralDbContext))]
[Migration("20261018160000_W1DurableClosure")]
public sealed class W1DurableClosure : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.AddColumn<string>("TargetResource", "operation_records", schema: "central", type: "text", nullable: true);
        m.Sql("""
            CREATE TABLE central.outbox_consumer_effects (
              "MessageId" uuid PRIMARY KEY, "EventType" text NOT NULL,
              "AggregateId" text NOT NULL, "PayloadJson" text NOT NULL);
            CREATE FUNCTION central.reject_audit_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'AUDIT_IMMUTABILITY_VIOLATION: audit_records are append-only' USING ERRCODE = '42501'; END $$;
            CREATE TRIGGER immutable_audit BEFORE UPDATE OR DELETE OR TRUNCATE ON central.audit_records
            FOR EACH STATEMENT EXECUTE FUNCTION central.reject_audit_mutation();
            """);
    }
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Audit evidence/durable receipt deletion requires separately authorized recovery, never silent downgrade.");
}
