using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Commercial;

public class Customer : Entity
{
    public const string StatusActive = "ACTIVE";
    public const string StatusSuspended = "SUSPENDED";

    public string LegalName { get; private set; } = string.Empty;
    public string ContactEmail { get; private set; } = string.Empty;
    public string ContactPhone { get; private set; } = string.Empty;
    public string? ContactReference { get; private set; }
    public string Status { get; private set; } = StatusActive;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public int RowVersion { get; private set; } = 1;

    private Customer() { }

    public static Customer CreateFromContract(string displayName, string? contactReference, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return new Customer { Id = Guid.NewGuid(), LegalName = displayName,
            ContactReference = contactReference, CreatedAtUtc = now, Status = StatusActive };
    }

    public static Customer Create(string legalName, string contactEmail, string contactPhone, DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(legalName))
        {
            throw new CentralDomainException("INVALID_LEGAL_NAME", "customer.invalid_legal_name", "Legal name is required.");
        }
        if (string.IsNullOrWhiteSpace(contactEmail) || !contactEmail.Contains('@'))
        {
            throw new CentralDomainException("INVALID_EMAIL", "customer.invalid_email", "Valid contact email is required.");
        }

        return new Customer
        {
            Id = Guid.NewGuid(),
            LegalName = legalName.Trim(),
            ContactEmail = contactEmail.Trim().ToLowerInvariant(),
            ContactPhone = contactPhone?.Trim() ?? string.Empty,
            Status = StatusActive,
            CreatedAtUtc = createdAtUtc,
            RowVersion = 1
        };
    }

    public void SetStatus(string status)
    {
        if (status != StatusActive && status != StatusSuspended)
        {
            throw new CentralDomainException("INVALID_STATUS", "customer.invalid_status", $"Invalid customer status: {status}");
        }
        Status = status;
        RowVersion++;
    }
}
