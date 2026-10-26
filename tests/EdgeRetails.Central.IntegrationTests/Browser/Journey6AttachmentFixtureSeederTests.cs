using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.IntegrationTests.Browser;

/// <summary>
/// Adds a real stored ciphertext object and a foreign shop to the owned Journey 6
/// fixture. The browser uses both for linked download and ownership denials.
/// </summary>
public sealed class Journey6AttachmentFixtureSeederTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task SeedOwnedSupportAttachmentBoundaries()
    {
        var fixturePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_FIXTURE_OUTPUT")
            ?? throw new InvalidOperationException("Explicit Journey 6 fixture output path is required.");
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(fixturePath))?.AsObject()
            ?? throw new InvalidOperationException("Journey 6 fixture is invalid.");
        if (fixture.ContainsKey("attachmentObjectId"))
        {
            throw new InvalidOperationException("Journey 6 attachment fixture already exists; refusing reuse.");
        }

        var shopId = Guid.Parse(fixture["shopId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Journey 6 shop is missing."));
        var unlinkedTicketId = Guid.Parse(fixture["queuePageOneEntityIds"]?.AsArray()[0]?.GetValue<string>()
            ?? throw new InvalidOperationException("Journey 6 unlinked ticket is missing."));
        var foreignShop = Shop.Create(Guid.NewGuid(), $"JF{Guid.NewGuid():N}"[..12],
            "Synthetic Journey 6 foreign shop", DateTimeOffset.UtcNow);
        var objectId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes($"SYNTHETIC_JOURNEY6_ENCRYPTED_ATTACHMENT_{Guid.NewGuid():N}");
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var retentionPolicyId = Guid.Parse(Environment.GetEnvironmentVariable("CENTRAL_STORAGE_RETENTION_POLICY_ID")
            ?? throw new InvalidOperationException("Journey 6 retention policy is required."));

        using (var scope = factory.Services.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageProvider>();
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            await using var stream = new MemoryStream(bytes);
            var path = await storage.SaveCiphertextAsync(shopId, objectId, stream);
            db.Shops.Add(foreignShop);
            db.EncryptedObjects.Add(EncryptedObjectRecord.Create(objectId, shopId, Guid.NewGuid(),
                EncryptedObjectRecord.PurposeSupportAttachment, bytes.Length, "application/octet-stream",
                digest, path, retentionPolicyId, "synthetic-journey6-fixture", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            Assert.True(await db.EncryptedObjects.AsNoTracking().AnyAsync(x => x.Id == objectId && x.ShopId == shopId));
        }

        fixture["foreignShopId"] = foreignShop.Id.ToString("D");
        fixture["attachmentObjectId"] = objectId.ToString("D");
        fixture["attachmentBytesBase64"] = Convert.ToBase64String(bytes);
        fixture["attachmentSha256"] = digest;
        fixture["unlinkedTicketId"] = unlinkedTicketId.ToString("D");
        await File.WriteAllTextAsync(fixturePath, fixture.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
