using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EdgeRetails.Central.Application.DTOs;

public class CreateCustomerCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("legalName")]
    [Required]
    public string LegalName { get; set; } = string.Empty;

    [JsonPropertyName("contactEmail")]
    [Required]
    [EmailAddress]
    public string ContactEmail { get; set; } = string.Empty;

    [JsonPropertyName("contactPhone")]
    public string ContactPhone { get; set; } = string.Empty;
}

public class RegisterShopCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("customerId")]
    public Guid CustomerId { get; set; }

    [JsonPropertyName("shopCode")]
    [Required]
    public string ShopCode { get; set; } = string.Empty;

    [JsonPropertyName("shopName")]
    [Required]
    public string ShopName { get; set; } = string.Empty;
}

public class CreatePriceSnapshotCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("shopId")]
    public Guid ShopId { get; set; }

    [JsonPropertyName("catalogItemId")]
    public Guid CatalogItemId { get; set; }

    [JsonPropertyName("authorityVersion")]
    public int AuthorityVersion { get; set; } = 1;
}

public class CreateOrderCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("customerId")]
    public Guid CustomerId { get; set; }

    [JsonPropertyName("snapshotId")]
    public Guid SnapshotId { get; set; }
}

public class InitUploadSessionCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("purpose")]
    [Required]
    public string Purpose { get; set; } = string.Empty;

    [JsonPropertyName("shopId")]
    public Guid ShopId { get; set; }

    [JsonPropertyName("targetEntityId")]
    public Guid TargetEntityId { get; set; }

    [JsonPropertyName("expectedBytes")]
    public long ExpectedBytes { get; set; }

    [JsonPropertyName("expectedSha256")]
    [Required]
    [RegularExpression("^[a-f0-9]{64}$")]
    public string ExpectedSha256 { get; set; } = string.Empty;
}

public class FinalizeUploadSessionCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("computedSha256")]
    [Required]
    [RegularExpression("^[a-f0-9]{64}$")]
    public string ComputedSha256 { get; set; } = string.Empty;

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "application/pdf";
}

public class ReviewPaymentCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("orderId")]
    public Guid OrderId { get; set; }

    [JsonPropertyName("evidenceId")]
    public Guid EvidenceId { get; set; }

    [JsonPropertyName("decision")]
    [Required]
    public string Decision { get; set; } = string.Empty; // APPROVE, REJECT, REQUEST_CLARIFICATION

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public class IssueLicenseCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("approvedOrderId")]
    public Guid ApprovedOrderId { get; set; }

    [JsonPropertyName("validFromUtc")]
    public DateTimeOffset ValidFromUtc { get; set; }

    [JsonPropertyName("validUntilUtc")]
    public DateTimeOffset? ValidUntilUtc { get; set; }
}

public class RenewLicenseCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("newValidUntilUtc")]
    public DateTimeOffset NewValidUntilUtc { get; set; }

    [JsonPropertyName("approvedOrderId")]
    public Guid? ApprovedOrderId { get; set; }
}

public class ExtendLicenseCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("extensionEndUtc")]
    public DateTimeOffset ExtensionEndUtc { get; set; }
}

public class RenewAddonCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("approvedOrderId")]
    public Guid ApprovedOrderId { get; set; }

    [JsonPropertyName("newValidUntilUtc")]
    public DateTimeOffset NewValidUntilUtc { get; set; }
}
