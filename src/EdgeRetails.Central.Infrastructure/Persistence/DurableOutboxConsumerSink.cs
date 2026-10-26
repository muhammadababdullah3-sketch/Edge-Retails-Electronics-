using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Infrastructure.Persistence;

public sealed class DurableOutboxConsumerSink(ICentralDbContext db) : IOutboxConsumerSink
{
    public async Task DeliverAsync(Guid messageId, string eventType, string aggregateType, string aggregateId, string payloadJson, CancellationToken cancellationToken = default)
    {
        // The receipt and operational read model commit together. Persisted domain
        // records, not PII-bearing event payloads, supply the target and shop.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var message = await db.CentralOutboxMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == messageId, cancellationToken)
            ?? throw new InvalidOperationException("consumer.event_missing");
        if (message.EventType != eventType || message.AggregateType != aggregateType ||
            message.AggregateId != aggregateId || message.PayloadJson != payloadJson)
        {
            throw new InvalidOperationException("consumer.event_payload_mismatch");
        }
        var existing = await db.CentralConsumerEffects.AsNoTracking().SingleOrDefaultAsync(e => e.MessageId == messageId, cancellationToken);
        if (existing is not null && (existing.EventType != eventType || existing.AggregateId != aggregateId || existing.PayloadJson != payloadJson))
        {
            throw new InvalidOperationException("consumer.persisted_effect_mismatch");
        }
        var notice = await ProjectAsync(messageId, eventType, aggregateId, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO central.outbox_consumer_effects (\"MessageId\", \"EventType\", \"AggregateId\", \"PayloadJson\") VALUES ({messageId}, {eventType}, {aggregateId}, {payloadJson}) ON CONFLICT (\"MessageId\") DO NOTHING", cancellationToken);
        if (notice is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO central.admin_notifications (\"Id\", \"Title\", \"Category\", \"TargetType\", \"TargetId\", \"ShopId\", \"RequiredCapability\", \"OccurredAtUtc\") VALUES ({notice.Id}, {notice.Title}, {notice.Category}, {notice.TargetType}, {notice.TargetId}, {notice.ShopId}, {notice.RequiredCapability}, {notice.OccurredAtUtc}) ON CONFLICT (\"Id\") DO NOTHING", cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private async Task<AdminNotification?> ProjectAsync(Guid messageId, string eventType, string aggregateId, CancellationToken ct)
    {
        var specification = eventType switch
        {
            "activation.token_redeemed" => ("Activation awaiting review", "Activation", "ActivationRedemption", "activation.review"),
            "activation.hardware_review_requested" => ("Hardware review requested", "Hardware", "HardwareReview", "hardware.review"),
            "activation.primary_transfer_initiated" => ("Primary transfer awaiting action", "Transfer", "PrimaryTransfer", "primary.transfer"),
            "payment.evidence_submitted" => ("Payment proof awaiting review", "Payment", "PaymentEvidence", "payment.review"),
            "approval.requested" => ("Separate approval requested", "Approval", "SensitiveApprovalRequest", "sensitive.approve"),
            "license.suspended" => ("License suspended", "License", "CommercialLicense", "license.view"),
            "license.revoked" => ("License revoked", "License", "CommercialLicense", "license.view"),
            _ => default
        };
        if (specification.Item1 is null)
        {
            return null;
        }

        if (!Guid.TryParse(aggregateId, out var target))
        {
            throw new InvalidOperationException("notification.target_invalid");
        }

        Guid? shop;
        if (specification.Item3 == "ActivationRedemption")
        {
            var redemption = await db.ActivationRedemptions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == target, ct)
                ?? throw new InvalidOperationException("notification.target_missing");
            shop = await db.Installations.Where(x => x.Id == redemption.InstallationId).Select(x => (Guid?)x.ShopId).SingleAsync(ct);
        }
        else if (specification.Item3 == "HardwareReview")
        {
            shop = await db.HardwareReviews.Where(x => x.Id == target).Select(x => (Guid?)x.ShopId).SingleAsync(ct);
        }
        else if (specification.Item3 == "PrimaryTransfer")
        {
            shop = await db.PrimaryTransfers.Where(x => x.Id == target).Select(x => (Guid?)x.ShopId).SingleAsync(ct);
        }
        else if (specification.Item3 == "PaymentEvidence")
        {
            shop = await db.PaymentEvidences.Where(x => x.Id == target).Select(x => (Guid?)x.ShopId).SingleAsync(ct);
        }
        else if (specification.Item3 == "CommercialLicense")
        {
            shop = await db.CommercialLicenses.Where(x => x.Id == target).Select(x => (Guid?)x.ShopId).SingleAsync(ct);
        }
        else
        {
            var approval = await db.SensitiveApprovalRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == target, ct)
                ?? throw new InvalidOperationException("notification.target_missing");
            shop = await db.CommercialLicenses.Where(x => x.Id == approval.TargetEntityId).Select(x => (Guid?)x.ShopId).SingleOrDefaultAsync(ct);
        }
        var occurred = await db.CentralOutboxMessages.Where(x => x.Id == messageId).Select(x => (DateTimeOffset?)x.OccurredOnUtc).SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("notification.event_missing");
        if (shop is Guid shopId && !await db.Shops.AnyAsync(x => x.Id == shopId, ct))
        {
            throw new InvalidOperationException("notification.shop_missing");
        }

        return new AdminNotification { Id = messageId, Title = specification.Item1, Category = specification.Item2,
            TargetType = specification.Item3, RequiredCapability = specification.Item4, TargetId = target, ShopId = shop, OccurredAtUtc = occurred };
    }

    public bool HasProcessed(Guid messageId) => db.CentralConsumerEffects.AsNoTracking().Any(e => e.MessageId == messageId);
    public int GetDeliveryCount(Guid messageId) => db.CentralConsumerEffects.AsNoTracking().Count(e => e.MessageId == messageId);
    public IReadOnlyCollection<Guid> DeliveredMessageIds => db.CentralConsumerEffects.AsNoTracking().Select(e => e.MessageId).ToArray();
}
