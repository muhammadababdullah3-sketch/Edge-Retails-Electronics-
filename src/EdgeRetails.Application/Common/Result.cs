namespace EdgeRetails.Application.Common;

public sealed record Error(string Code, string Message);

public readonly record struct Result
{
    private Result(bool isSuccess, Error? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public Error? Error { get; }

    public static Result Success() => new(true, null);

    public static Result Failure(string code, string message) =>
        new(false, new Error(code, message));
}

public readonly record struct Result<T>
{
    private Result(bool isSuccess, T? value, Error? error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public bool IsSuccess { get; }
    public T? Value { get; }
    public Error? Error { get; }

    public static Result<T> Success(T value) => new(true, value, null);

    public static Result<T> Failure(string code, string message) =>
        new(false, default, new Error(code, message));
}
