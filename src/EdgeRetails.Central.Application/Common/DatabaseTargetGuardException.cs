namespace EdgeRetails.Central.Application.Common;

public class DatabaseTargetGuardException : Exception
{
    public string ViolationCode { get; }

    public DatabaseTargetGuardException(string violationCode, string message)
        : base(message)
    {
        ViolationCode = violationCode;
    }
}
