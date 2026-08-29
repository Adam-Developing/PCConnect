using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PCConnect.Infrastructure.Database.Migrations;

[DbContext(typeof(PCConnectDbContext))]
[Migration("202608290001_AllowAuditUserAnonymization")]
public sealed class AllowAuditUserAnonymization : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE OR REPLACE FUNCTION enforce_audit_event_immutability() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'UPDATE' THEN
                IF OLD.user_id IS NOT NULL
                   AND NEW.user_id IS NULL
                   AND (to_jsonb(OLD) - 'user_id') = (to_jsonb(NEW) - 'user_id') THEN
                    RETURN NEW;
                END IF;
            END IF;
            RAISE EXCEPTION '% is append-only', TG_TABLE_NAME;
        END;
        $$;

        DROP TRIGGER audit_events_immutable ON audit_events;
        CREATE TRIGGER audit_events_immutable
            BEFORE UPDATE OR DELETE ON audit_events
            FOR EACH ROW EXECUTE FUNCTION enforce_audit_event_immutability();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TRIGGER audit_events_immutable ON audit_events;
        CREATE TRIGGER audit_events_immutable
            BEFORE UPDATE OR DELETE ON audit_events
            FOR EACH ROW EXECUTE FUNCTION reject_immutable_change();
        DROP FUNCTION enforce_audit_event_immutability();
        """);
}
