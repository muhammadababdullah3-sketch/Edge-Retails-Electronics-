namespace EdgeRetails.Central.Domain.Common;

public class CentralDomainException : Exception
{
    public string Code { get; }
    public string SafeMessageKey { get; }

    public CentralDomainException(string code, string safeMessageKey, string message)
        : base(message)
    {
        Code = code;
        SafeMessageKey = safeMessageKey;
    }

    public CentralDomainException(string code, string safeMessageKey, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
        SafeMessageKey = safeMessageKey;
    }
}
