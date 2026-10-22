using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Licensing;

public record CommercialTerm
{
    public const string FixedTerm = "FIXED_TERM";
    public const string Lifetime = "LIFETIME";

    public string TermKind { get; init; }
    public DateTimeOffset ValidFromUtc { get; init; }
    public DateTimeOffset? ValidUntilUtc { get; init; }

    public CommercialTerm(string termKind, DateTimeOffset validFromUtc, DateTimeOffset? validUntilUtc)
    {
        if (termKind != FixedTerm && termKind != Lifetime)
        {
            throw new CentralDomainException("INVALID_TERM_KIND", "license.invalid_term_kind", $"Invalid term kind: {termKind}");
        }

        if (termKind == Lifetime && validUntilUtc.HasValue)
        {
            throw new CentralDomainException("LIFETIME_MUST_HAVE_NULL_EXPIRY", "license.lifetime_null_expiry", "Lifetime commercial term must have null expiry.");
        }

        if (termKind == FixedTerm && !validUntilUtc.HasValue)
        {
            throw new CentralDomainException("FIXED_TERM_REQUIRES_EXPIRY", "license.fixed_term_expiry_required", "Fixed term must specify a validUntilUtc date.");
        }

        TermKind = termKind;
        ValidFromUtc = validFromUtc;
        ValidUntilUtc = validUntilUtc;
    }
}
