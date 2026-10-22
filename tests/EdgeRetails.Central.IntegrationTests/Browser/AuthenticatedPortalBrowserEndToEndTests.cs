using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Support;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.IntegrationTests.Browser;

/// <summary>
/// Runs the real Central portal JavaScript in headless Chromium while a loopback
/// HTTP listener forwards every browser request into WebApplicationFactory's
/// TestServer handler. The database must be a freshly provisioned, owned,
/// disposable PostgreSQL 18 target; this test never creates, migrates, or drops it.
/// </summary>
public sealed class AuthenticatedPortalBrowserEndToEndTests
{
    private static readonly string[] RequiredCapabilities =
    [
        "dashboard.view", "queues.view", "sessions.view", "support.view", "support.create", "support.reply",
        "security.manage", "notifications.view"
    ];

    [Fact]
    public async Task AuthenticatedPortal_PagesQueue_ReadsCustomer360_CreatesRepliesAndPersistsAfterReload()
    {
        var evidenceDirectory = RequiredPath("CENTRAL_PORTAL_BROWSER_EVIDENCE_DIR");
        var python = Path.GetFullPath(Required("CENTRAL_PLAYWRIGHT_PYTHON"));
        Assert.True(File.Exists(python), $"SFR-11 requires an existing Playwright Python executable: {python}");
        var dbConnection = Required("CENTRAL_DATABASE_CONNECTION_STRING");
        ValidateOwnedDatabaseConfiguration(dbConnection);
        ValidateRuntimeConfiguration();

        Directory.CreateDirectory(evidenceDirectory);
        evidenceDirectory = Path.GetFullPath(evidenceDirectory);
        var manifestPath = Path.Combine(evidenceDirectory, "run-manifest.json");
        var runId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var customerId = Guid.NewGuid();
        var shopId = Guid.NewGuid();
        var foreignShopId = Guid.NewGuid();
        var shopCode = $"E2E{Guid.NewGuid():N}"[..11].ToUpperInvariant();
        var username = $"sfr11_e2e_{Guid.NewGuid():N}";
        const string syntheticPassword = "Central-browser-dev-credential-2026!";
        var mfaSecret = new TotpService().GenerateSecret();
        var initialQueueIds = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray();
        var queueMarker = $"SFR11 synthetic queue {runId:N}";
        var ticketSubjectPrefix = $"SFR11 browser {runId:N}";
        var replyText = $"Synthetic staff reply persisted for browser run {runId:D}";
        var attachmentObjectId = Guid.NewGuid();
        var attachmentBytes = Encoding.UTF8.GetBytes("SYNTHETIC_ENCRYPTED_SUPPORT_CIPHERTEXT_FIXTURE_V1");
        var attachmentSha256 = Convert.ToHexStringLower(SHA256.HashData(attachmentBytes));
        var attachmentRetentionPolicyId = Guid.Parse("d0d8959b-278f-4cda-a81c-3eb88732f071");
        var unlinkedTicketId = Guid.NewGuid();
        using var testDatabase = CreateFactory(dbConnection);
        var manifest = new
        {
            schema = "CENTRAL_PORTAL_BROWSER_E2E_V1",
            runId,
            status = "STARTED",
            startedAtUtc = createdAt,
            evidenceDirectory,
            browser = "Playwright Python / Chromium",
            transport = "loopback HttpListener -> WebApplicationFactory TestServer handler",
            database = new
            {
                host = Environment.GetEnvironmentVariable("CENTRAL_PG_HOST"),
                port = Environment.GetEnvironmentVariable("CENTRAL_PG_PORT"),
                database = Environment.GetEnvironmentVariable("CENTRAL_PG_DATABASE"),
                runtimeRole = Environment.GetEnvironmentVariable("CENTRAL_PG_ROLE"),
                migrationRole = Environment.GetEnvironmentVariable("CENTRAL_PG_MIGRATION_ROLE"),
                ownershipMarker = Environment.GetEnvironmentVariable("CENTRAL_PG_OWNERSHIP_MARKER"),
                dataDirectory = Environment.GetEnvironmentVariable("CENTRAL_PG_DATA_DIR")
            },
            seeded = new { customerId, shopId, shopCode, username, queueTicketIds = initialQueueIds, queueMarker },
            evidenceFiles = new[]
            {
                "run-manifest.json", "db-before.json", "db-after.json", "browser-fixture.json",
                "browser-events.json", "browser-result.json", "browser-run.log", "portal-trace.zip",
                "screenshots/01-authenticated-dashboard.png", "screenshots/02-queue-page-1.png",
                "screenshots/03-queue-page-2.png", "screenshots/04-customer-360.png",
                "screenshots/05-support-created.png", "screenshots/06-support-reply.png",
                "screenshots/07-reloaded-support-history.png", "screenshots/08-recovery-worker-status.png",
                "screenshots/09-login-mfa-session.png", "screenshots/10-logout-revoked.png", "proxy-events.json"
            }
        };
        await WriteJsonAsync(manifestPath, manifest);

        try
        {
            var factory = testDatabase;
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var appliedMigrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            Assert.NotEmpty(appliedMigrations);
            Assert.Equal(0, await db.SupportTickets.CountAsync());
            await WriteJsonAsync(Path.Combine(evidenceDirectory, "db-before.json"), new
            {
                runId,
                capturedAtUtc = DateTimeOffset.UtcNow,
                appliedMigrations,
                supportTicketCount = await db.SupportTickets.CountAsync(),
                operationRecordCount = await db.CentralOperationRecords.CountAsync(),
                shopCount = await db.Shops.CountAsync()
            });

            var user = AdminUser.Create(username, new PasswordHasher().HashPassword(syntheticPassword),
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), mfaSecret, true, createdAt);
            var role = AdminRole.Create($"SFR11 browser {runId:N}", "Synthetic least-privilege browser role", RequiredCapabilities);
            var assignment = new AdminUserRole(user.Id, role.Id, createdAt);
            var shop = Shop.Create(customerId, shopCode, $"Synthetic SFR11 Browser Shop {runId:N}", createdAt);
            typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
            var foreignShop = Shop.Create(Guid.NewGuid(), $"F{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
                $"Synthetic Foreign Attachment Shop {runId:N}", createdAt);
            typeof(Shop).GetProperty("Id")!.SetValue(foreignShop, foreignShopId);
            var objectStorage = factory.Services.GetRequiredService<IObjectStorageProvider>();
            var attachmentStoragePath = await objectStorage.SaveCiphertextAsync(shopId, attachmentObjectId,
                new MemoryStream(attachmentBytes));
            var attachmentRecord = EncryptedObjectRecord.Create(
                attachmentObjectId, shopId, Guid.NewGuid(), EncryptedObjectRecord.PurposeSupportAttachment,
                attachmentBytes.Length, "application/octet-stream", attachmentSha256, attachmentStoragePath,
                attachmentRetentionPolicyId, null, createdAt, createdAt.AddDays(30));

            db.AdminUsers.Add(user);
            db.AdminRoles.Add(role);
            db.AdminUserRoles.Add(assignment);
            db.Shops.Add(shop);
            db.Shops.Add(foreignShop);
            db.EncryptedObjects.Add(attachmentRecord);
            for (var index = 0; index < initialQueueIds.Length; index++)
            {
                db.SupportTickets.Add(SupportTicket.Create(
                    initialQueueIds[index], shopId, Guid.NewGuid(),
                    $"{queueMarker} {index + 1:D2}", "Synthetic queue row for browser paging acceptance.",
                    new List<Guid>(), 1, createdAt.AddMilliseconds(index + 1)));
            }
            await db.SaveChangesAsync();

            var browserFixture = new
            {
                baseUrl = "",
                shopId,
                foreignShopId,
                shopCode,
                attachmentObjectId,
                attachmentBytesBase64 = Convert.ToBase64String(attachmentBytes),
                attachmentSha256,
                unlinkedTicketId,
                queueIdsPageOne = initialQueueIds.Take(20).Select(id => id.ToString("D")).ToArray(),
                queueIdsPageTwo = initialQueueIds.Skip(20).Select(id => id.ToString("D")).ToArray(),
                queueMarker,
                ticketSubjectPrefix,
                replyText
            };

            await using var proxy = await TestServerLoopbackProxy.StartAsync(factory);
            var fixturePath = Path.Combine(evidenceDirectory, "browser-fixture.json");
            await WriteJsonAsync(fixturePath, new
            {
                baseUrl = proxy.BaseUrl,
                browserFixture.shopId,
                browserFixture.foreignShopId,
                browserFixture.shopCode,
                browserFixture.attachmentObjectId,
                browserFixture.attachmentBytesBase64,
                browserFixture.attachmentSha256,
                browserFixture.unlinkedTicketId,
                browserFixture.queueIdsPageOne,
                browserFixture.queueIdsPageTwo,
                browserFixture.queueMarker,
                browserFixture.ticketSubjectPrefix,
                browserFixture.replyText
            });
            var pythonScriptPath = Path.Combine(evidenceDirectory, "portal-browser-e2e.py");
            await File.WriteAllTextAsync(pythonScriptPath, BrowserScript, new UTF8Encoding(false));

            var totp = new TotpService().GenerateCode(mfaSecret, DateTimeOffset.UtcNow);
            var browserRun = await RunPythonAsync(python, pythonScriptPath, fixturePath, evidenceDirectory,
                new Dictionary<string, string>
                {
                    ["CENTRAL_BROWSER_TEST_USERNAME"] = username,
                    ["CENTRAL_BROWSER_TEST_PASSWORD"] = syntheticPassword,
                    ["CENTRAL_BROWSER_TEST_TOTP"] = totp
                });
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "browser-run.log"), browserRun.Output, new UTF8Encoding(false));
            await WriteJsonAsync(Path.Combine(evidenceDirectory, "proxy-events.json"), proxy.Events);
            Assert.Equal(0, browserRun.ExitCode);

            var browserResultPath = Path.Combine(evidenceDirectory, "browser-result.json");
            Assert.True(File.Exists(browserResultPath), "Browser harness did not produce browser-result.json.");
            using var browserResult = JsonDocument.Parse(await File.ReadAllTextAsync(browserResultPath));
            Assert.Equal("PASS", browserResult.RootElement.GetProperty("result").GetString());
            Assert.Equal(username, browserResult.RootElement.GetProperty("displayedUsername").GetString());
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "recovery-worker-status");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "login-mfa-session-logout");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "auth-trace-secret-redaction");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "support-attachment-download");
            Assert.True(browserResult.RootElement.GetProperty("crossShopCreateDenied").GetBoolean());
            Assert.True(browserResult.RootElement.GetProperty("unlinkedDownloadDenied").GetBoolean());
            Assert.Equal(attachmentBytes.Length, browserResult.RootElement.GetProperty("downloadedCiphertextLength").GetInt32());
            Assert.Equal(attachmentSha256, browserResult.RootElement.GetProperty("downloadedCiphertextSha256").GetString());
            var createdTicketId = Guid.Parse(browserResult.RootElement.GetProperty("ticketId").GetString()!);
            var subject = browserResult.RootElement.GetProperty("subject").GetString()!;

            var afterTicket = await db.SupportTickets.AsNoTracking().SingleAsync(ticket => ticket.Id == createdTicketId);
            Assert.Equal(shopId, afterTicket.ShopId);
            Assert.Equal(subject, afterTicket.Subject);
            Assert.Equal(2, afterTicket.RowVersion);
            Assert.Contains(replyText, afterTicket.RepliesJson, StringComparison.Ordinal);
            Assert.Contains(user.Id.ToString("D"), afterTicket.RepliesJson, StringComparison.OrdinalIgnoreCase);
            var operationRecords = await db.CentralOperationRecords.AsNoTracking()
                .Where(record => record.ShopId == shopId &&
                    (record.OperationType == "support.create" || record.OperationType == "support.reply"))
                .OrderBy(record => record.CreatedAtUtc)
                .Select(record => new
                {
                    record.OperationType,
                    record.State,
                    record.ResultReference,
                    record.ShopId,
                    record.CreatedAtUtc
                }).ToArrayAsync();
            Assert.Contains(operationRecords, record => record.OperationType == "support.create" &&
                record.ResultReference == $"support/tickets/{createdTicketId:D}");
            Assert.Contains(operationRecords, record => record.OperationType == "support.reply" &&
                record.ResultReference == createdTicketId.ToString("D"));
            var persistedAttachment = await db.EncryptedObjects.AsNoTracking().SingleAsync(record => record.Id == attachmentObjectId);
            Assert.Equal(shopId, persistedAttachment.ShopId);
            Assert.Equal(EncryptedObjectRecord.PurposeSupportAttachment, persistedAttachment.Purpose);
            Assert.Equal(attachmentSha256, persistedAttachment.CiphertextSha256);
            using (var attachmentIds = JsonDocument.Parse(afterTicket.AttachmentObjectIdsJson))
            {
                Assert.Contains(attachmentObjectId.ToString("D"), attachmentIds.RootElement.EnumerateArray()
                    .Select(id => id.GetString()!), StringComparer.OrdinalIgnoreCase);
            }
            Assert.False(await db.SupportTickets.AsNoTracking().AnyAsync(ticket => ticket.Subject.Contains("Synthetic cross-shop attachment")));

            var consumedChallenge = await db.LoginChallenges.AsNoTracking()
                .SingleAsync(challenge => challenge.AdminUserId == user.Id);
            Assert.True(consumedChallenge.IsConsumed);
            Assert.NotNull(consumedChallenge.ConsumedAtUtc);
            var persistedLoginUser = await db.AdminUsers.AsNoTracking().SingleAsync(candidate => candidate.Id == user.Id);
            Assert.NotNull(persistedLoginUser.LastLoginAtUtc);
            var loginSession = await db.AdminSessions.AsNoTracking().SingleAsync(candidate => candidate.AdminUserId == user.Id);
            Assert.True(loginSession.MfaSatisfied);
            Assert.True(loginSession.IsRevoked);
            Assert.NotNull(loginSession.RevokedAtUtc);
            Assert.Equal(RequiredCapabilities.Order(StringComparer.Ordinal), loginSession.GetCapabilities().Order(StringComparer.Ordinal));
            var loginEvidence = new
            {
                adminUserId = user.Id,
                username,
                mfaEnabled = user.MfaEnabled,
                lastLoginAtUtc = persistedLoginUser.LastLoginAtUtc,
                challengeConsumed = consumedChallenge.IsConsumed,
                challengeConsumedAtUtc = consumedChallenge.ConsumedAtUtc,
                mfaSatisfied = loginSession.MfaSatisfied,
                sessionRevoked = loginSession.IsRevoked,
                sessionRevokedAtUtc = loginSession.RevokedAtUtc,
                capabilities = loginSession.GetCapabilities().Order(StringComparer.Ordinal).ToArray()
            };

            var persistedTicketCount = await db.SupportTickets.CountAsync();
            Assert.Equal(initialQueueIds.Length + 1, persistedTicketCount);
            await WriteJsonAsync(Path.Combine(evidenceDirectory, "db-after.json"), new
            {
                runId,
                capturedAtUtc = DateTimeOffset.UtcNow,
                supportTicketCount = persistedTicketCount,
                authentication = loginEvidence,
                attachment = new
                {
                    persistedAttachment.Id,
                    persistedAttachment.ShopId,
                    persistedAttachment.Purpose,
                    persistedAttachment.ContentLength,
                    persistedAttachment.CiphertextSha256,
                    ticketLinked = afterTicket.AttachmentObjectIdsJson.Contains(attachmentObjectId.ToString("D"), StringComparison.OrdinalIgnoreCase),
                    crossShopCreateDenied = browserResult.RootElement.GetProperty("crossShopCreateDenied").GetBoolean(),
                    unlinkedDownloadDenied = browserResult.RootElement.GetProperty("unlinkedDownloadDenied").GetBoolean(),
                    downloadedCiphertextSha256 = browserResult.RootElement.GetProperty("downloadedCiphertextSha256").GetString()
                },
                seededQueueTicketIds = initialQueueIds,
                createdTicket = new
                {
                    id = afterTicket.Id,
                    afterTicket.ShopId,
                    afterTicket.Subject,
                    afterTicket.Status,
                    afterTicket.RowVersion,
                    afterTicket.UpdatedAtUtc,
                    afterTicket.RepliesJson
                },
                operationRecords
            });

            await WriteJsonAsync(manifestPath, new
            {
                schema = "CENTRAL_PORTAL_BROWSER_E2E_V1",
                runId,
                status = "PASS",
                startedAtUtc = createdAt,
                completedAtUtc = DateTimeOffset.UtcNow,
                evidenceDirectory,
                browser = "Playwright Python / Chromium",
                transport = "loopback HttpListener -> WebApplicationFactory TestServer handler",
                database = manifest.database,
                seeded = manifest.seeded,
                createdTicketId,
                assertions = new[]
                {
                    "server-validated cookie session and exact capabilities",
                    "queue pages 1 and 2 match separately seeded persisted IDs",
                    "Customer 360 returns and renders the seeded persisted shop",
                    "browser-created support ticket is persisted for that shop",
                    "staff reply is persisted with authenticated admin identity",
                    "ticket and reply remain visible after a full browser reload",
                    "support.create/support.reply operation outcomes persisted",
                    "security.manage admin sees live worker observation through notifications.view authorization",
                    "recovery UI distinguishes worker health from backup/restore attestation",
                    "real password login, MFA challenge consumption, secure session cookie, identity display and logout revocation",
                    "browser console/page errors and Central API network results captured"
                },
                evidenceFiles = manifest.evidenceFiles
            });
        }
        catch (Exception exception)
        {
            await WriteJsonAsync(manifestPath, new
            {
                schema = "CENTRAL_PORTAL_BROWSER_E2E_V1",
                runId,
                status = "FAIL",
                startedAtUtc = createdAt,
                failedAtUtc = DateTimeOffset.UtcNow,
                evidenceDirectory,
                failure = exception.ToString(),
                database = manifest.database,
                seeded = manifest.seeded
            });
            throw new InvalidOperationException($"SFR-11 browser acceptance failed; inspect evidence at {evidenceDirectory}.", exception);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string connectionString) => new BrowserPortalFactory(connectionString);

    private static void ValidateOwnedDatabaseConfiguration(string connectionString)
    {
        var required = new[]
        {
            "CENTRAL_PG_HOST", "CENTRAL_PG_PORT", "CENTRAL_PG_DATABASE", "CENTRAL_PG_ROLE",
            "CENTRAL_PG_MIGRATION_ROLE", "CENTRAL_PG_PID", "CENTRAL_PG_BINARY",
            "CENTRAL_PG_OWNERSHIP_MARKER", "CENTRAL_PG_DATA_DIR"
        };
        foreach (var name in required)
        {
            _ = Required(name);
        }
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        Assert.Equal("127.0.0.1", Required("CENTRAL_PG_HOST"));
        Assert.Equal(int.Parse(Required("CENTRAL_PG_PORT")), builder.Port);
        Assert.Equal(Required("CENTRAL_PG_DATABASE"), builder.Database);
        Assert.Equal(Required("CENTRAL_PG_ROLE"), builder.Username);
        Assert.NotEqual(5432, builder.Port);
        Assert.NotEqual(7150, builder.Port);
        Assert.False(string.Equals("postgres", builder.Database, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Required("CENTRAL_PG_OWNERSHIP_MARKER")));
    }

    private static void ValidateRuntimeConfiguration()
    {
        var required = new[]
        {
            "CENTRAL_SIGNER_KEY_STORE_ROOT", "CENTRAL_OBJECT_STORAGE_ROOT",
            "CENTRAL_OUTBOX_ENABLED", "CENTRAL_OUTBOX_BATCH_SIZE", "CENTRAL_OUTBOX_MAX_ATTEMPTS",
            "CENTRAL_OUTBOX_POLL_MS", "CENTRAL_OUTBOX_LEASE_SECONDS", "CENTRAL_OUTBOX_RETRY_BASE_MS",
            "CENTRAL_OUTBOX_RETRY_MAX_MS", "CENTRAL_STORAGE_RECONCILIATION_ENABLED",
            "CENTRAL_STORAGE_RECONCILIATION_BATCH_SIZE", "CENTRAL_STORAGE_RECONCILIATION_POLL_MS",
            "CENTRAL_STORAGE_PENDING_GRACE_SECONDS"
        };
        foreach (var name in required)
        {
            _ = Required(name);
        }
        Assert.True(Directory.Exists(Required("CENTRAL_SIGNER_KEY_STORE_ROOT")));
        Assert.True(Directory.Exists(Required("CENTRAL_OBJECT_STORAGE_ROOT")));
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"SFR-11 requires explicit owner-provisioned {name}; no fallback is permitted.");

    private static string RequiredPath(string name)
    {
        var value = Path.GetFullPath(Required(name));
        Directory.CreateDirectory(value);
        return value;
    }

    private static async Task WriteJsonAsync(string path, object value) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

    private static async Task<(int ExitCode, string Output)> RunPythonAsync(string python, string script, string fixture, string evidence,
        IReadOnlyDictionary<string, string> childEnvironment)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(script);
        process.StartInfo.ArgumentList.Add(fixture);
        process.StartInfo.ArgumentList.Add(evidence);
        foreach (var (name, value) in childEnvironment)
        {
            process.StartInfo.Environment[name] = value;
        }
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await stdout) + Environment.NewLine + (await stderr));
    }

    private sealed class BrowserPortalFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:CentralDatabase", connectionString);
        }
    }

    private sealed class TestServerLoopbackProxy : IAsyncDisposable
    {
        private readonly WebApplication _proxy;
        private readonly HttpClient _testServerClient;
        private readonly X509Certificate2 _certificate;
        private readonly List<object> _events;

        private TestServerLoopbackProxy(WebApplication proxy, HttpClient client, X509Certificate2 certificate, string baseUrl, List<object> events)
        {
            _proxy = proxy;
            _testServerClient = client;
            _certificate = certificate;
            BaseUrl = baseUrl;
            _events = events;
        }

        public string BaseUrl { get; }
        public IReadOnlyList<object> Events => _events.ToArray();

        public static async Task<TestServerLoopbackProxy> StartAsync(WebApplicationFactory<Program> factory)
        {
            var certificate = CreateLoopbackCertificate();
            var client = new HttpClient(factory.Server.CreateHandler());
            var port = ReserveLoopbackPort();
            var events = new List<object>();
            var eventsGate = new object();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
            var proxy = builder.Build();
            proxy.Run(async context =>
            {
                var incoming = context.Request;
                var targetUri = new Uri($"https://127.0.0.1:{port}{incoming.Path}{incoming.QueryString}");
                using var forwarded = new HttpRequestMessage(new HttpMethod(incoming.Method), targetUri);
                if (incoming.ContentLength is > 0)
                {
                    using var body = new MemoryStream();
                    await incoming.Body.CopyToAsync(body, context.RequestAborted);
                    forwarded.Content = new ByteArrayContent(body.ToArray());
                }
                foreach (var header in incoming.Headers)
                {
                    if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    {
                        forwarded.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                    }
                }
                try
                {
                    using var response = await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                    context.Response.StatusCode = (int)response.StatusCode;
                    foreach (var header in response.Headers)
                    {
                        CopyResponseHeader(context.Response, header.Key, header.Value);
                    }
                    foreach (var header in response.Content.Headers)
                    {
                        CopyResponseHeader(context.Response, header.Key, header.Value);
                    }
                    var bytes = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
                    context.Response.ContentLength = bytes.Length;
                    if (bytes.Length > 0)
                    {
                        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
                    }
                    lock (eventsGate)
                    {
                        events.Add(new { incoming.Method, Url = targetUri.ToString(), response.StatusCode, response.Content.Headers.ContentType?.MediaType });
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lock (eventsGate)
                    {
                        events.Add(new { incoming.Method, Url = targetUri.ToString(), error = exception.GetType().Name });
                    }
                    context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                    await context.Response.WriteAsync("TestServer proxy failed.", context.RequestAborted);
                }
            });
            await proxy.StartAsync();
            var addresses = proxy.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = addresses?.SingleOrDefault(value => value.StartsWith("https://127.0.0.1:", StringComparison.OrdinalIgnoreCase));
            var baseUrl = address is null ? $"https://127.0.0.1:{port}/" : address.TrimEnd('/') + "/";
            return new TestServerLoopbackProxy(proxy, client, certificate, baseUrl, events);
        }

        private static int ReserveLoopbackPort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        private static X509Certificate2 CreateLoopbackCertificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Central SFR11 loopback test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(2));
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), password: null);
        }

        private static void CopyResponseHeader(HttpResponse response, string name, IEnumerable<string> values)
        {
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            foreach (var value in values)
            {
                response.Headers.Append(name, value);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _proxy.StopAsync();
            await _proxy.DisposeAsync();
            _testServerClient.Dispose();
            _certificate.Dispose();
        }
    }

    private const string BrowserScript = """
import asyncio, json, os, pathlib, sys, traceback, uuid, zipfile, tempfile, re
from collections import Counter
from playwright.async_api import async_playwright

fixture_path = pathlib.Path(sys.argv[1])
evidence = pathlib.Path(sys.argv[2])
fixture = json.loads(fixture_path.read_text(encoding='utf-8'))
auth_username = os.environ['CENTRAL_BROWSER_TEST_USERNAME']
auth_password = os.environ['CENTRAL_BROWSER_TEST_PASSWORD']
auth_totp = os.environ['CENTRAL_BROWSER_TEST_TOTP']
secrets_to_redact = [auth_username, auth_password, auth_totp, 'intentionally-invalid-password']
events = {"consoleErrors": [], "pageErrors": [], "requests": [], "responses": []}
result_path = evidence / "browser-result.json"
screenshots = evidence / "screenshots"
screenshots.mkdir(parents=True, exist_ok=True)

def redact_trace():
    trace_path = evidence / 'portal-trace.zip'
    if not trace_path.exists():
        return
    temporary = trace_path.with_suffix('.redacted.zip')
    with zipfile.ZipFile(trace_path, 'r') as source, zipfile.ZipFile(temporary, 'w', zipfile.ZIP_DEFLATED) as target:
        for item in source.infolist():
            data = source.read(item.filename)
            for secret in secrets_to_redact:
                if secret:
                    data = data.replace(secret.encode('utf-8'), b'[REDACTED]')
            target.writestr(item, data)
    temporary.replace(trace_path)
    with zipfile.ZipFile(trace_path, 'r') as sanitized:
        for item in sanitized.infolist():
            data = sanitized.read(item.filename)
            for secret in secrets_to_redact:
                assert not secret.encode('utf-8') in data, 'Sensitive authentication value remained in browser trace.'

async def main():
    browser = None
    context = None
    page = None
    ticket_id = None
    subject = f"{fixture['ticketSubjectPrefix']} {uuid.uuid4()}"
    reply_text = fixture['replyText']
    try:
        async with async_playwright() as p:
            launch_options = {"headless": True}
            chromium_executable = os.environ.get("CENTRAL_PLAYWRIGHT_CHROMIUM_EXECUTABLE")
            if chromium_executable:
                assert pathlib.Path(chromium_executable).is_file(), f"Configured Chromium executable not found: {chromium_executable}"
                launch_options["executable_path"] = chromium_executable
            browser = await p.chromium.launch(**launch_options)
            context = await browser.new_context(viewport={"width": 1440, "height": 1000}, ignore_https_errors=True)
            page = await context.new_page()
            page.on('console', lambda message: events['consoleErrors'].append(message.text) if message.type == 'error' else None)
            page.on('pageerror', lambda error: events['pageErrors'].append(str(error)))
            page.on('request', lambda request: events['requests'].append({"method": request.method, "url": request.url}) if '/api/admin/v1/' in request.url else None)
            page.on('response', lambda response: events['responses'].append({"status": response.status, "url": response.url}) if '/api/admin/v1/' in response.url else None)

            await page.goto(fixture['baseUrl'], wait_until='networkidle')
            await context.tracing.start(screenshots=True, snapshots=True, sources=True)
            await page.locator('#auth-wrapper').wait_for(state='visible')
            await page.locator('#login-username').fill(auth_username)
            await page.locator('#login-password').fill('intentionally-invalid-password')
            await page.locator('#login-form button[type="submit"]').click()
            await page.locator('#login-error').wait_for(state='visible')
            assert await page.locator('#mfa-step').is_hidden(), 'Invalid password unexpectedly advanced to MFA.'
            await page.locator('#login-username').fill(auth_username)
            await page.locator('#login-password').fill(auth_password)
            await page.locator('#login-form button[type="submit"]').click()
            await page.locator('#mfa-step').wait_for(state='visible')
            await page.locator('#mfa-totp').fill(auth_totp)
            await page.locator('#mfa-form button[type="submit"]').click()
            await page.locator('#app-shell').wait_for(state='visible')
            assert await page.locator('#auth-wrapper').is_hidden(), 'Server rejected the completed password/MFA login.'
            assert (await page.locator('#user-display-name').inner_text()).strip() == auth_username, 'Portal identity does not match the authenticated server user.'
            assert (await page.locator('#user-avatar-text').inner_text()).strip() == auth_username[:2].upper(), 'Portal identity avatar does not match the authenticated server user.'
            session_cookie = next((cookie for cookie in await context.cookies() if cookie['name'] == 'central_session'), None)
            assert session_cookie is not None, 'MFA did not create the Central browser session cookie.'
            assert session_cookie['httpOnly'] and session_cookie['secure'] and session_cookie['sameSite'] == 'Strict', 'Session cookie security attributes were not enforced.'
            secrets_to_redact.append(session_cookie['value'])
            capabilities = await page.locator('#session-capabilities').inner_text()
            for capability in ['dashboard.view', 'queues.view', 'support.view', 'support.create', 'support.reply']:
                assert capability in capabilities, f'Expected server session capability {capability} was not rendered.'
            for capability in ['security.manage', 'notifications.view']:
                assert capability in capabilities, f'Expected recovery worker capability {capability} was not rendered.'
            await page.screenshot(path=str(screenshots / '01-authenticated-dashboard.png'), full_page=True)
            await page.screenshot(path=str(screenshots / '09-login-mfa-session.png'), full_page=True)

            await page.locator('[data-queue="SUPPORT_INBOX"]').click()
            await page.wait_for_function("document.querySelector('#dashboard-queue-status')?.textContent?.includes('records in this snapshot; page 1.')")
            page_one = await page.locator('#dashboard-queue-tbody tr').evaluate_all("rows => rows.map(row => row.cells[0]?.textContent?.trim()).filter(Boolean)")
            assert page_one == fixture['queueIdsPageOne'], f'Queue page 1 mismatch: {page_one}'
            await page.screenshot(path=str(screenshots / '02-queue-page-1.png'), full_page=True)
            await page.locator('#dashboard-queue-next').click()
            await page.wait_for_function("document.querySelector('#dashboard-queue-status')?.textContent?.includes('records in this snapshot; page 2.')")
            page_two = await page.locator('#dashboard-queue-tbody tr').evaluate_all("rows => rows.map(row => row.cells[0]?.textContent?.trim()).filter(Boolean)")
            assert page_two == fixture['queueIdsPageTwo'], f'Queue page 2 mismatch: {page_two}'
            await page.screenshot(path=str(screenshots / '03-queue-page-2.png'), full_page=True)

            await page.locator('[data-view="customer360"]').click()
            await page.locator('#customer-search-input').fill(str(fixture['shopId']))
            await page.locator('#btn-customer-search').click()
            await page.locator('#customer-360-details').get_by_text(fixture['shopCode'], exact=True).wait_for()
            await page.screenshot(path=str(screenshots / '04-customer-360.png'), full_page=True)

            await page.locator('[data-view="recovery"]').click()
            await page.locator('#recovery-refresh').click()
            await page.wait_for_function("document.querySelector('#recovery-worker-status')?.textContent?.includes('Outbox worker:')")
            worker_observation = await page.locator('#recovery-worker-status').inner_text()
            assert 'Observed ' in worker_observation, f'No server observation timestamp was rendered: {worker_observation}'
            recovery_scope = await page.locator('#recovery-status').inner_text()
            assert 'does not prove a backup, restore, host identity, or database target' in recovery_scope
            await page.screenshot(path=str(screenshots / '08-recovery-worker-status.png'), full_page=True)

            await page.locator('[data-view="support"]').click()
            cross_shop_result = await page.evaluate(r'''async ({ foreignShopId, attachmentObjectId }) => {
                const payload = { shopId: foreignShopId, subject: `Synthetic cross-shop attachment ${crypto.randomUUID()}`,
                    summary: 'Cross-shop attachment must be rejected by persisted shop ownership.',
                    attachmentObjectIds: [attachmentObjectId], redactionProfileVersion: 1 };
                const operation = await supportCommandBody('cross-shop-negative', payload, 'support.create', 0, AppState.currentUser.id);
                const response = await apiFetch('/api/admin/v1/support/tickets', {
                    method: 'POST', headers: { 'X-Client-Operation-Id': operation.id }, body: operation.body
                });
                const body = await response.json();
                return { status: response.status, code: body.code };
            }''', { 'foreignShopId': fixture['foreignShopId'], 'attachmentObjectId': fixture['attachmentObjectId'] })
            assert cross_shop_result == { 'status': 400, 'code': 'SUPPORT_ATTACHMENT_ACCESS_DENIED' }, \
                f'Cross-shop attachment reference was not denied: {cross_shop_result}'
            await page.locator('#support-shop-id').fill(str(fixture['shopId']))
            await page.locator('#support-subject').fill(subject)
            await page.locator('#support-summary').fill(f'Synthetic browser workflow {subject}')
            await page.locator('#support-redaction-version').fill('1')
            await page.locator('#support-attachments').fill(str(fixture['attachmentObjectId']))
            await page.locator('#support-create-submit').click()
            await page.get_by_text(__import__('re').compile(r'Ticket ([0-9a-f-]{36}) was saved by the server\.', __import__('re').I)).wait_for()
            create_message = await page.locator('#support-create-status').inner_text()
            match = __import__('re').search(r'Ticket ([0-9a-f-]{36}) was saved by the server\.', create_message, __import__('re').I)
            assert match, f'No persisted ticket identity returned: {create_message}'
            ticket_id = match.group(1)
            await page.screenshot(path=str(screenshots / '05-support-created.png'), full_page=True)
            row = page.locator('#support-tbody tr').filter(has_text=subject)
            await row.get_by_role('button', name='Open Ticket').click()
            await page.locator('#support-reply-text').fill(reply_text)
            await page.locator('#support-reply-form').get_by_role('button', name='Save Reply').click()
            await page.get_by_text('Reply saved in the portal. No external message was sent.').wait_for()
            await page.screenshot(path=str(screenshots / '06-support-reply.png'), full_page=True)

            await page.reload(wait_until='networkidle')
            await page.locator('[data-view="support"]').click()
            await page.locator('#support-tbody').get_by_text(subject, exact=True).wait_for()
            row = page.locator('#support-tbody tr').filter(has_text=subject)
            await row.get_by_role('button', name='Open Ticket').click()
            await page.locator('#support-history').get_by_text(reply_text, exact=True).wait_for()
            attachment_link = page.locator('#support-ticket-attachments').get_by_role('link', name='Download encrypted attachment')
            await attachment_link.wait_for()
            attachment_href = await attachment_link.get_attribute('href')
            expected_attachment_path = f"/api/admin/v1/support/tickets/{ticket_id}/attachments/{fixture['attachmentObjectId']}/download"
            assert attachment_href == expected_attachment_path, f'Portal attachment link did not preserve ticket/object identity: {attachment_href}'
            await page.screenshot(path=str(screenshots / '07-reloaded-support-history.png'), full_page=True)
            async with page.expect_download() as download_info:
                await attachment_link.click()
            attachment_download = await download_info.value
            downloaded_bytes = pathlib.Path(await attachment_download.path()).read_bytes()
            assert downloaded_bytes == __import__('base64').b64decode(fixture['attachmentBytesBase64']), 'Downloaded attachment bytes differ from the encrypted fixture.'
            downloaded_sha256 = __import__('hashlib').sha256(downloaded_bytes).hexdigest()
            assert downloaded_sha256 == fixture['attachmentSha256'], 'Downloaded ciphertext digest mismatch.'
            attachment_response = await page.evaluate(r'''async (href) => {
                const response = await apiFetch(href);
                const bytes = new Uint8Array(await response.arrayBuffer());
                return { status: response.status, objectId: response.headers.get('x-object-id'), digest: response.headers.get('digest'),
                    bytesBase64: btoa(Array.from(bytes, value => String.fromCharCode(value)).join('')) };
            }''', attachment_href)
            assert attachment_response['status'] == 200, f'Authorized linked attachment API returned {attachment_response["status"]}.'
            assert attachment_response['objectId'] == fixture['attachmentObjectId'], 'Download response object identity does not match the persisted link.'
            assert attachment_response['digest'] == 'sha-256=' + __import__('base64').b64encode(bytes.fromhex(fixture['attachmentSha256'])).decode('ascii'), 'Download ciphertext digest did not match persisted metadata.'
            assert __import__('base64').b64decode(attachment_response['bytesBase64']) == downloaded_bytes, 'Anchor download and same-origin API bytes differed.'
            unlinked_result = await page.evaluate(r'''async ({ ticketId, objectId }) => {
                const response = await apiFetch(`/api/admin/v1/support/tickets/${ticketId}/attachments/${objectId}/download`);
                const body = await response.json();
                return { status: response.status, code: body.code };
            }''', { 'ticketId': fixture['unlinkedTicketId'], 'objectId': fixture['attachmentObjectId'] })
            assert unlinked_result == { 'status': 404, 'code': 'SUPPORT_ATTACHMENT_NOT_FOUND' }, \
                f'Unlinked ticket could access another ticket attachment: {unlinked_result}'

            await page.locator('#btn-logout').click()
            await page.locator('#auth-wrapper').wait_for(state='visible')
            assert await page.locator('#app-shell').is_hidden(), 'Logout left the protected portal visible.'
            await page.screenshot(path=str(screenshots / '10-logout-revoked.png'), full_page=True)
            await page.reload(wait_until='networkidle')
            await page.locator('#auth-wrapper').wait_for(state='visible')
            assert await page.locator('#app-shell').is_hidden(), 'Revoked session restored portal access after reload.'

            assert not events['pageErrors'], f'Browser page errors: {events["pageErrors"]}'
            for marker in ['/api/admin/v1/queues/SUPPORT_INBOX', '/api/admin/v1/dashboard/customer360/', '/api/admin/v1/support/tickets', '/api/admin/v1/notifications/status']:
                assert any(marker in response['url'] and 200 <= response['status'] < 300 for response in events['responses']), f'No successful Central API response for {marker}'
            assert any('/api/admin/v1/support/tickets' in response['url'] and response['status'] == 400 for response in events['responses']), 'Cross-shop attachment creation was not rejected by Central.'
            assert any('/attachments/' in response['url'] and '/download' in response['url'] and response['status'] == 200 for response in events['responses']), 'Linked ciphertext attachment download was not authorized.'
            assert any('/attachments/' in response['url'] and '/download' in response['url'] and response['status'] == 404 for response in events['responses']), 'Unlinked attachment download was not denied.'
            assert any('/auth/login' in response['url'] and response['status'] == 401 for response in events['responses']), 'Invalid password was not denied by Central.'
            assert any('/auth/login' in response['url'] and 200 <= response['status'] < 300 for response in events['responses']), 'Valid login did not issue an MFA challenge.'
            assert any('/auth/mfa' in response['url'] and 200 <= response['status'] < 300 for response in events['responses']), 'MFA proof was not accepted.'
            assert any('/auth/logout' in response['url'] and 200 <= response['status'] < 300 for response in events['responses']), 'Logout was not acknowledged.'
            assert any('/portal/session' in response['url'] and response['status'] == 401 for response in events['responses']), 'Revoked session was accepted after logout.'
            expected_401s = [response for response in events['responses'] if response['status'] == 401 and
                ('/portal/session' in response['url'] or '/auth/login' in response['url'])]
            assert len(expected_401s) == 3, f'Expected initial anonymous, invalid-password, and post-logout denials; observed {len(expected_401s)}.'
            console_statuses = [int(match.group(1)) for error in events['consoleErrors']
                if (match := re.search(r'status of (\d+)', error))]
            expected_console_statuses = Counter([401] * len(expected_401s) + [400, 404])
            assert Counter(console_statuses) == expected_console_statuses, \
                f'Browser console failures did not match expected anonymous/invalid-auth/cross-shop/unlinked denials: {events["consoleErrors"]}'
            await context.tracing.stop(path=str(evidence / 'portal-trace.zip'))
            redact_trace()
            payload = {"result": "PASS", "ticketId": ticket_id, "subject": subject, "replyText": reply_text,
                "queuePageOne": page_one, "queuePageTwo": page_two, "capabilities": capabilities,
                "displayedUsername": auth_username,
                "crossShopCreateDenied": cross_shop_result['status'] == 400 and cross_shop_result['code'] == 'SUPPORT_ATTACHMENT_ACCESS_DENIED',
                "unlinkedDownloadDenied": unlinked_result['status'] == 404 and unlinked_result['code'] == 'SUPPORT_ATTACHMENT_NOT_FOUND',
                "attachmentObjectId": fixture['attachmentObjectId'],
                "downloadedCiphertextLength": len(downloaded_bytes),
                "downloadedCiphertextSha256": downloaded_sha256,
                "checks": ["login-mfa-session-logout", "session-identity", "auth-trace-secret-redaction", "session", "queue-paging", "customer-360", "recovery-worker-status", "support-create", "support-attachment-download", "support-attachment-cross-shop-denial", "support-attachment-unlinked-download-denial", "support-reply", "reload-persistence", "console-network"], "events": events}
            result_path.write_text(json.dumps(payload, indent=2), encoding='utf-8')
            (evidence / 'browser-events.json').write_text(json.dumps(events, indent=2), encoding='utf-8')
            print(json.dumps({"result": "PASS", "ticketId": ticket_id, "checks": payload['checks']}))
    except Exception as error:
        if page is not None:
            try: await page.screenshot(path=str(screenshots / 'failure.png'), full_page=True)
            except Exception: pass
        if context is not None:
            try: await context.tracing.stop(path=str(evidence / 'portal-trace.zip'))
            except Exception: pass
        try: redact_trace()
        except Exception: pass
        payload = {"result": "FAIL", "message": str(error), "traceback": traceback.format_exc(), "events": events}
        result_path.write_text(json.dumps(payload, indent=2), encoding='utf-8')
        (evidence / 'browser-events.json').write_text(json.dumps(events, indent=2), encoding='utf-8')
        print(json.dumps(payload), file=sys.stderr)
        raise
    finally:
        if browser is not None:
            await browser.close()

asyncio.run(main())
""";
}
