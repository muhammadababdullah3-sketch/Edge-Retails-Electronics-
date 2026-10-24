DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'central') THEN
        CREATE SCHEMA central;
    END IF;
END $EF$;


CREATE TABLE central.admin_roles (
    "Id" uuid NOT NULL,
    "Name" character varying(64) NOT NULL,
    "Description" character varying(256) NOT NULL,
    "CapabilitiesJson" text NOT NULL,
    CONSTRAINT "PK_admin_roles" PRIMARY KEY ("Id")
);


CREATE TABLE central.admin_users (
    "Id" uuid NOT NULL,
    "Username" character varying(128) NOT NULL,
    "PasswordHash" character varying(256) NOT NULL,
    "Salt" character varying(128) NOT NULL,
    "MfaSecret" character varying(128),
    "MfaEnabled" boolean NOT NULL,
    "IsActive" boolean NOT NULL,
    "SecurityRevision" integer NOT NULL,
    "RowVersion" integer NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "LastLoginAtUtc" timestamp with time zone,
    CONSTRAINT "PK_admin_users" PRIMARY KEY ("Id")
);


CREATE TABLE central.audit_records (
    "Id" uuid NOT NULL,
    "TimestampUtc" timestamp with time zone NOT NULL,
    "ActorId" character varying(128) NOT NULL,
    "ActorUsername" character varying(128),
    "SessionId" uuid,
    "Action" character varying(128) NOT NULL,
    "Resource" character varying(128) NOT NULL,
    "ResourceId" character varying(128),
    "Reason" character varying(512),
    "CorrelationId" character varying(128) NOT NULL,
    "ClientIp" character varying(64),
    "UserAgent" character varying(256),
    "PayloadSha256" character varying(64),
    "DetailsJson" text NOT NULL,
    "IsSuccess" boolean NOT NULL,
    CONSTRAINT "PK_audit_records" PRIMARY KEY ("Id")
);


CREATE TABLE central.operation_records (
    "Id" uuid NOT NULL,
    "ClientOperationId" uuid NOT NULL,
    "OperationType" character varying(128) NOT NULL,
    "ActorScope" character varying(256) NOT NULL,
    "ShopId" uuid,
    "TargetResource" text,
    "FingerprintVersion" integer NOT NULL,
    "PayloadSha256" character varying(64) NOT NULL,
    "CanonicalPayload" text NOT NULL,
    "ExpectedVersion" integer NOT NULL,
    "PolicyVersion" integer NOT NULL,
    "Reason" character varying(512) NOT NULL,
    "SensitiveApprovalId" uuid,
    "State" character varying(32) NOT NULL,
    "OutcomeId" uuid NOT NULL,
    "ResultReference" character varying(256),
    "ResourceVersion" integer NOT NULL,
    "AuthorityRevision" integer NOT NULL,
    "ErrorCode" character varying(64),
    "ErrorMessageKey" character varying(128),
    "Retryable" boolean NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "CompletedAtUtc" timestamp with time zone,
    CONSTRAINT "PK_operation_records" PRIMARY KEY ("Id")
);


CREATE TABLE central.outbox_consumer_effects (
    "MessageId" uuid NOT NULL,
    "EventType" text NOT NULL,
    "AggregateId" text NOT NULL,
    "PayloadJson" text NOT NULL,
    CONSTRAINT "PK_outbox_consumer_effects" PRIMARY KEY ("MessageId")
);


CREATE TABLE central.outbox_messages (
    "Id" uuid NOT NULL,
    "OccurredOnUtc" timestamp with time zone NOT NULL,
    "EventType" character varying(128) NOT NULL,
    "AggregateType" character varying(128) NOT NULL,
    "AggregateId" character varying(128) NOT NULL,
    "PayloadJson" text NOT NULL,
    "Status" character varying(32) NOT NULL,
    "ProcessedAtUtc" timestamp with time zone,
    "RetryCount" integer NOT NULL,
    "LastError" character varying(1024),
    CONSTRAINT "PK_outbox_messages" PRIMARY KEY ("Id")
);


CREATE TABLE central.signing_keys (
    "KeyId" text NOT NULL,
    "KeyVersion" integer NOT NULL,
    "AlgorithmProfileId" character varying(32) NOT NULL,
    "Purpose" character varying(64) NOT NULL,
    "PublicKeyPem" text NOT NULL,
    "ValidFromUtc" timestamp with time zone NOT NULL,
    "ValidUntilUtc" timestamp with time zone NOT NULL,
    "Status" character varying(32) NOT NULL,
    "EmergencyRolloverRevision" integer NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_signing_keys" PRIMARY KEY ("KeyId")
);


CREATE TABLE central.signing_requests (
    "Id" uuid NOT NULL,
    "IssuanceRequestId" uuid NOT NULL,
    "Purpose" character varying(64) NOT NULL,
    "KeyId" character varying(128) NOT NULL,
    "KeyVersion" integer NOT NULL,
    "AuthorityRevision" integer NOT NULL,
    "PayloadSha256" character varying(64) NOT NULL,
    "CanonicalPayload" text NOT NULL,
    "Signature" character varying(1024) NOT NULL,
    "Status" text NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "SignedAtUtc" timestamp with time zone,
    CONSTRAINT "PK_signing_requests" PRIMARY KEY ("Id")
);


CREATE TABLE central.step_up_grants (
    "Id" uuid NOT NULL,
    "SessionId" uuid NOT NULL,
    "ActionScope" character varying(128) NOT NULL,
    "TargetPayloadSha256" character varying(64) NOT NULL,
    "ChallengeId" uuid NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "IsConsumed" boolean NOT NULL,
    "ConsumedAtUtc" timestamp with time zone,
    CONSTRAINT "PK_step_up_grants" PRIMARY KEY ("Id")
);


CREATE TABLE central.trust_sets (
    "TrustSetVersion" integer GENERATED BY DEFAULT AS IDENTITY,
    "EmergencyRolloverRevision" integer NOT NULL,
    "SignedByTrustedRootKeyId" character varying(128) NOT NULL,
    "Signature" character varying(1024) NOT NULL,
    "KeysJson" text NOT NULL,
    "RevokedKeyIdsJson" text NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_trust_sets" PRIMARY KEY ("TrustSetVersion")
);


CREATE TABLE central.admin_sessions (
    "Id" uuid NOT NULL,
    "AdminUserId" uuid NOT NULL,
    "TokenHash" character varying(64) NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "AbsoluteExpiresAtUtc" timestamp with time zone NOT NULL,
    "LastActivityAtUtc" timestamp with time zone NOT NULL,
    "MfaSatisfied" boolean NOT NULL,
    "SecurityRevisionAtCreation" integer NOT NULL,
    "IsRevoked" boolean NOT NULL,
    "RevokedAtUtc" timestamp with time zone,
    "RevocationReason" character varying(256),
    "CapabilitiesJson" text NOT NULL,
    CONSTRAINT "PK_admin_sessions" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_admin_sessions_admin_users_AdminUserId" FOREIGN KEY ("AdminUserId") REFERENCES central.admin_users ("Id") ON DELETE CASCADE
);


CREATE TABLE central.admin_user_roles (
    "AdminUserId" uuid NOT NULL,
    "RoleId" uuid NOT NULL,
    "AssignedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_admin_user_roles" PRIMARY KEY ("AdminUserId", "RoleId"),
    CONSTRAINT "FK_admin_user_roles_admin_roles_RoleId" FOREIGN KEY ("RoleId") REFERENCES central.admin_roles ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_admin_user_roles_admin_users_AdminUserId" FOREIGN KEY ("AdminUserId") REFERENCES central.admin_users ("Id") ON DELETE CASCADE
);


CREATE TABLE central.login_challenges (
    "Id" uuid NOT NULL,
    "AdminUserId" uuid NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "IsConsumed" boolean NOT NULL,
    "ConsumedAtUtc" timestamp with time zone,
    "FailedAttempts" integer NOT NULL,
    CONSTRAINT "PK_login_challenges" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_login_challenges_admin_users_AdminUserId" FOREIGN KEY ("AdminUserId") REFERENCES central.admin_users ("Id") ON DELETE CASCADE
);


CREATE UNIQUE INDEX "IX_admin_roles_Name" ON central.admin_roles ("Name");


CREATE INDEX "IX_admin_sessions_AdminUserId" ON central.admin_sessions ("AdminUserId");


CREATE INDEX "IX_admin_sessions_ExpiresAtUtc" ON central.admin_sessions ("ExpiresAtUtc");


CREATE UNIQUE INDEX "IX_admin_sessions_TokenHash" ON central.admin_sessions ("TokenHash");


CREATE INDEX "IX_admin_user_roles_RoleId" ON central.admin_user_roles ("RoleId");


CREATE UNIQUE INDEX "IX_admin_users_Username" ON central.admin_users ("Username");


CREATE INDEX "IX_audit_records_ActorId" ON central.audit_records ("ActorId");


CREATE INDEX "IX_audit_records_CorrelationId" ON central.audit_records ("CorrelationId");


CREATE INDEX "IX_audit_records_TimestampUtc" ON central.audit_records ("TimestampUtc");


CREATE INDEX "IX_login_challenges_AdminUserId" ON central.login_challenges ("AdminUserId");


CREATE INDEX "IX_login_challenges_ExpiresAtUtc" ON central.login_challenges ("ExpiresAtUtc");


CREATE INDEX "IX_operation_records_ActorScope_OperationType_PayloadSha256" ON central.operation_records ("ActorScope", "OperationType", "PayloadSha256");


CREATE UNIQUE INDEX "IX_operation_records_ClientOperationId" ON central.operation_records ("ClientOperationId");


CREATE INDEX "IX_operation_records_CreatedAtUtc" ON central.operation_records ("CreatedAtUtc");


CREATE INDEX "IX_outbox_messages_Status_OccurredOnUtc" ON central.outbox_messages ("Status", "OccurredOnUtc");


CREATE INDEX "IX_signing_keys_Status" ON central.signing_keys ("Status");


CREATE INDEX "IX_signing_requests_IssuanceRequestId" ON central.signing_requests ("IssuanceRequestId");


CREATE INDEX "IX_signing_requests_KeyId_KeyVersion" ON central.signing_requests ("KeyId", "KeyVersion");


CREATE INDEX "IX_step_up_grants_ExpiresAtUtc" ON central.step_up_grants ("ExpiresAtUtc");


CREATE INDEX "IX_step_up_grants_SessionId_ActionScope_TargetPayloadSha256" ON central.step_up_grants ("SessionId", "ActionScope", "TargetPayloadSha256");


