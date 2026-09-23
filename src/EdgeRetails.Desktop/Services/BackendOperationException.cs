namespace EdgeRetails.Desktop.Services;

public sealed class BackendOperationException : InvalidOperationException
{
    public BackendOperationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
