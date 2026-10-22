using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class W2CommercialAndLicensing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "activation_redemptions",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeNonce = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ClientSignatureHex = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    RedeemedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activation_redemptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "activation_tokens",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShopId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHashSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FirstRedemptionExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IssuedByAdminId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RedeemedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activation_tokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "commercial_entitlements",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ValidUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commercial_entitlements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "commercial_licenses",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShopId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitialOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    TermKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ValidFromUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AuthorityRevision = table.Column<int>(type: "integer", nullable: false),
                    PrimaryCapacity = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastRenewedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commercial_licenses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "customers",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LegalName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ContactEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ContactPhone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "hardware_reviews",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShopId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportedMachineFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StoredMachineFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DiscrepancyReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReviewedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hardware_reviews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "installations",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShopId = table.Column<Guid>(type: "uuid", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PublicKeyPem = table.Column<string>(type: "text", nullable: false),
                    PublicKeyFingerprintSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Hostname = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OsPlatform = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RegisteredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "license_certificates",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShopId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineageRevision = table.Column<int>(type: "integer", nullable: false),
                    CertificateSerial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SignedEnvelopeJson = table.Column<string>(type: "text", nullable: false),
                    RS256Signature = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    DigestSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ValidFromUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OfflineGraceUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_certificates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShopId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseTarget = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TermKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BillingPeriodId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ApprovedEvidenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payment_evidences",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShopId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Sha256Digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    MimeType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ReviewStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_evidences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payment_review_records",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EvidenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewerAdminId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_review_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "plan_catalog_items",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PurchaseTarget = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TermKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BillingPeriodId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    RowVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_catalog_items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pricing_snapshots",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShopId = table.Column<Guid>(type: "uuid", nullable: false),
                    CatalogItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseTarget = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TermKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BillingPeriodId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ServerGeneratedDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AuthorityVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pricing_snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "primary_transfers",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShopId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransferReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    InitiatedByAdminId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    SensitiveApprovalId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_primary_transfers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sensitive_approval_requests",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetPayloadSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequesterAdminId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApproverAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActionScope = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StepUpSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sensitive_approval_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "shop_enforcement_policies",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShopId = table.Column<Guid>(type: "uuid", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    EnforcementMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExtensionEndUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AfterExtensionPolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseDurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    OfflineGraceSeconds = table.Column<int>(type: "integer", nullable: false),
                    RollbackToleranceSeconds = table.Column<int>(type: "integer", nullable: false),
                    CapabilityPolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SignedPolicyEnvelopeJson = table.Column<string>(type: "text", nullable: false),
                    RS256Signature = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    KeyId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ApprovedByAdminId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shop_enforcement_policies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "shops",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShopCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShopName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PrimaryCapacity = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shops", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "upload_sessions",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShopId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploaderPrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpectedBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedBytes = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpectedSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActualSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_upload_sessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_activation_redemptions_InstallationId",
                schema: "central",
                table: "activation_redemptions",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_activation_redemptions_TokenId",
                schema: "central",
                table: "activation_redemptions",
                column: "TokenId");

            migrationBuilder.CreateIndex(
                name: "IX_activation_tokens_LicenseId",
                schema: "central",
                table: "activation_tokens",
                column: "LicenseId");

            migrationBuilder.CreateIndex(
                name: "IX_activation_tokens_ShopId",
                schema: "central",
                table: "activation_tokens",
                column: "ShopId");

            migrationBuilder.CreateIndex(
                name: "IX_activation_tokens_TokenHashSha256",
                schema: "central",
                table: "activation_tokens",
                column: "TokenHashSha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_entitlements_LicenseId_Kind",
                schema: "central",
                table: "commercial_entitlements",
                columns: new[] { "LicenseId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_licenses_CustomerId",
                schema: "central",
                table: "commercial_licenses",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_commercial_licenses_ShopId",
                schema: "central",
                table: "commercial_licenses",
                column: "ShopId");

            migrationBuilder.CreateIndex(
                name: "IX_customers_ContactEmail",
                schema: "central",
                table: "customers",
                column: "ContactEmail");

            migrationBuilder.CreateIndex(
                name: "IX_hardware_reviews_InstallationId",
                schema: "central",
                table: "hardware_reviews",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_hardware_reviews_ShopId",
                schema: "central",
                table: "hardware_reviews",
                column: "ShopId");

            migrationBuilder.CreateIndex(
                name: "IX_installations_LicenseId",
                schema: "central",
                table: "installations",
                column: "LicenseId");

            migrationBuilder.CreateIndex(
                name: "IX_installations_ShopId",
                schema: "central",
                table: "installations",
                column: "ShopId");

            migrationBuilder.CreateIndex(
                name: "IX_license_certificates_CertificateSerial",
                schema: "central",
                table: "license_certificates",
                column: "CertificateSerial",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_license_certificates_InstallationId",
                schema: "central",
                table: "license_certificates",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_license_certificates_LicenseId",
                schema: "central",
                table: "license_certificates",
                column: "LicenseId");

            migrationBuilder.CreateIndex(
                name: "IX_orders_CustomerId_ShopId",
                schema: "central",
                table: "orders",
                columns: new[] { "CustomerId", "ShopId" });

            migrationBuilder.CreateIndex(
                name: "IX_orders_SnapshotId",
                schema: "central",
                table: "orders",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_evidences_OrderId",
                schema: "central",
                table: "payment_evidences",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_evidences_UploadSessionId",
                schema: "central",
                table: "payment_evidences",
                column: "UploadSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_review_records_EvidenceId",
                schema: "central",
                table: "payment_review_records",
                column: "EvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_review_records_OrderId",
                schema: "central",
                table: "payment_review_records",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_plan_catalog_items_Code",
                schema: "central",
                table: "plan_catalog_items",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pricing_snapshots_ShopId_CatalogItemId",
                schema: "central",
                table: "pricing_snapshots",
                columns: new[] { "ShopId", "CatalogItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_primary_transfers_ShopId",
                schema: "central",
                table: "primary_transfers",
                column: "ShopId");

            migrationBuilder.CreateIndex(
                name: "IX_primary_transfers_SourceInstallationId",
                schema: "central",
                table: "primary_transfers",
                column: "SourceInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_primary_transfers_TargetInstallationId",
                schema: "central",
                table: "primary_transfers",
                column: "TargetInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_approval_requests_Status",
                schema: "central",
                table: "sensitive_approval_requests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_sensitive_approval_requests_TargetEntityId_OperationType",
                schema: "central",
                table: "sensitive_approval_requests",
                columns: new[] { "TargetEntityId", "OperationType" });

            migrationBuilder.CreateIndex(
                name: "IX_shop_enforcement_policies_ShopId_PolicyVersion",
                schema: "central",
                table: "shop_enforcement_policies",
                columns: new[] { "ShopId", "PolicyVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_shops_CustomerId",
                schema: "central",
                table: "shops",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_shops_ShopCode",
                schema: "central",
                table: "shops",
                column: "ShopCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_upload_sessions_ShopId",
                schema: "central",
                table: "upload_sessions",
                column: "ShopId");

            migrationBuilder.CreateIndex(
                name: "IX_upload_sessions_TargetEntityId",
                schema: "central",
                table: "upload_sessions",
                column: "TargetEntityId");

            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'central_runtime_user') THEN
                        GRANT USAGE ON SCHEMA central TO central_runtime_user;
                        GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA central TO central_runtime_user;
                        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA central TO central_runtime_user;
                        REVOKE UPDATE, DELETE, TRUNCATE ON central.audit_records FROM central_runtime_user;
                    END IF;
                    IF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'central054_app') THEN
                        GRANT USAGE ON SCHEMA central TO central054_app;
                        GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA central TO central054_app;
                        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA central TO central054_app;
                        REVOKE UPDATE, DELETE, TRUNCATE ON central.audit_records FROM central054_app;
                    END IF;
                END
                $$;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activation_redemptions",
                schema: "central");

            migrationBuilder.DropTable(
                name: "activation_tokens",
                schema: "central");

            migrationBuilder.DropTable(
                name: "commercial_entitlements",
                schema: "central");

            migrationBuilder.DropTable(
                name: "commercial_licenses",
                schema: "central");

            migrationBuilder.DropTable(
                name: "customers",
                schema: "central");

            migrationBuilder.DropTable(
                name: "hardware_reviews",
                schema: "central");

            migrationBuilder.DropTable(
                name: "installations",
                schema: "central");

            migrationBuilder.DropTable(
                name: "license_certificates",
                schema: "central");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "central");

            migrationBuilder.DropTable(
                name: "payment_evidences",
                schema: "central");

            migrationBuilder.DropTable(
                name: "payment_review_records",
                schema: "central");

            migrationBuilder.DropTable(
                name: "plan_catalog_items",
                schema: "central");

            migrationBuilder.DropTable(
                name: "pricing_snapshots",
                schema: "central");

            migrationBuilder.DropTable(
                name: "primary_transfers",
                schema: "central");

            migrationBuilder.DropTable(
                name: "sensitive_approval_requests",
                schema: "central");

            migrationBuilder.DropTable(
                name: "shop_enforcement_policies",
                schema: "central");

            migrationBuilder.DropTable(
                name: "shops",
                schema: "central");

            migrationBuilder.DropTable(
                name: "upload_sessions",
                schema: "central");
        }
    }