START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'identity') THEN
            CREATE SCHEMA identity;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE TABLE system.installation_state (
        installation_id uuid NOT NULL,
        setup_status integer NOT NULL,
        selected_module character varying(80),
        started_at timestamp with time zone NOT NULL,
        completed_at timestamp with time zone,
        version bigint NOT NULL,
        singleton_key character varying(20) NOT NULL DEFAULT 'PRIMARY',
        CONSTRAINT pk_installation_state PRIMARY KEY (installation_id),
        CONSTRAINT ck_installation_state_singleton CHECK (singleton_key = 'PRIMARY')
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE TABLE identity.permissions (
        id uuid NOT NULL,
        key character varying(160) NOT NULL,
        description character varying(500),
        is_active boolean NOT NULL,
        CONSTRAINT pk_permissions PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE TABLE identity.roles (
        id uuid NOT NULL,
        name character varying(80) NOT NULL,
        is_system boolean NOT NULL,
        is_active boolean NOT NULL,
        version bigint NOT NULL,
        CONSTRAINT pk_roles PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE TABLE identity.role_permissions (
        id uuid NOT NULL,
        role_id uuid NOT NULL,
        permission_id uuid NOT NULL,
        CONSTRAINT pk_role_permissions PRIMARY KEY (id),
        CONSTRAINT fk_role_permissions_permissions_permission_id FOREIGN KEY (permission_id) REFERENCES identity.permissions (id) ON DELETE CASCADE,
        CONSTRAINT fk_role_permissions_roles_role_id FOREIGN KEY (role_id) REFERENCES identity.roles (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE TABLE identity.users (
        id uuid NOT NULL,
        display_name character varying(160) NOT NULL,
        role_id uuid NOT NULL,
        pin_hash bytea NOT NULL,
        pin_salt bytea NOT NULL,
        pin_iterations integer NOT NULL,
        pin_algorithm character varying(40) NOT NULL,
        status integer NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        version bigint NOT NULL,
        CONSTRAINT pk_users PRIMARY KEY (id),
        CONSTRAINT ck_user_pin_iterations_positive CHECK (pin_iterations > 0),
        CONSTRAINT fk_users_roles_role_id FOREIGN KEY (role_id) REFERENCES identity.roles (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE TABLE identity.user_permission_overrides (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        permission_id uuid NOT NULL,
        is_allowed boolean NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        version bigint NOT NULL,
        CONSTRAINT pk_user_permission_overrides PRIMARY KEY (id),
        CONSTRAINT fk_user_permission_overrides_permissions_permission_id FOREIGN KEY (permission_id) REFERENCES identity.permissions (id) ON DELETE CASCADE,
        CONSTRAINT fk_user_permission_overrides_users_user_id FOREIGN KEY (user_id) REFERENCES identity.users (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE TABLE identity.user_sessions (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        client_session_id uuid NOT NULL,
        started_at timestamp with time zone NOT NULL,
        ended_at timestamp with time zone,
        is_revoked boolean NOT NULL,
        CONSTRAINT pk_user_sessions PRIMARY KEY (id),
        CONSTRAINT fk_user_sessions_users_user_id FOREIGN KEY (user_id) REFERENCES identity.users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE UNIQUE INDEX ix_installation_state_singleton_key ON system.installation_state (singleton_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE UNIQUE INDEX ix_permissions_key ON identity.permissions (key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE INDEX ix_role_permissions_permission_id ON identity.role_permissions (permission_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE UNIQUE INDEX ix_role_permissions_role_id_permission_id ON identity.role_permissions (role_id, permission_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE UNIQUE INDEX ix_roles_name ON identity.roles (name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE INDEX ix_user_permission_overrides_permission_id ON identity.user_permission_overrides (permission_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE UNIQUE INDEX ix_user_permission_overrides_user_id_permission_id ON identity.user_permission_overrides (user_id, permission_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE UNIQUE INDEX ix_user_sessions_client_session_id ON identity.user_sessions (client_session_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE INDEX ix_user_sessions_user_id_ended_at ON identity.user_sessions (user_id, ended_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE INDEX ix_users_role_id ON identity.users (role_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    CREATE INDEX ix_users_status_display_name ON identity.users (status, display_name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM system.__ef_migrations_history WHERE "MigrationId" = '20260920111318_Sprint7Phase1SetupIdentity') THEN
    INSERT INTO system.__ef_migrations_history ("MigrationId", "ProductVersion")
    VALUES ('20260920111318_Sprint7Phase1SetupIdentity', '10.0.12');
    END IF;
END $EF$;
COMMIT;

