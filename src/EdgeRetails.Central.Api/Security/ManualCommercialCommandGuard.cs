using EdgeRetails.Central.Application.DTOs;

namespace EdgeRetails.Central.Api.Security;

public static class ManualCommercialCommandGuard
{
    public static bool IsValid(CommandMetaDto? meta, string expectedOperationType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOperationType);

        return meta is not null &&
            meta.ClientOperationId != Guid.Empty &&
            string.Equals(meta.OperationType, expectedOperationType, StringComparison.Ordinal) &&
            meta.FingerprintVersion == 1 &&
            IsSha256(meta.PayloadSha256) &&
            meta.ExpectedVersion >= 0 &&
            meta.PolicyVersion >= 0 &&
            !string.IsNullOrWhiteSpace(meta.Reason);
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
