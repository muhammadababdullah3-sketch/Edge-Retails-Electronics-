namespace EdgeRetails.Application.Exceptions;

public sealed class IdempotencyPayloadMismatchException : Exception
{
    public string Code => "idempotency.payload_mismatch";
    public Guid ClientOperationId { get; }
    public string ExistingFingerprint { get; }
    public string IncomingFingerprint { get; }

    public IdempotencyPayloadMismatchException(
        Guid clientOperationId,
        string existingFingerprint,
        string incomingFingerprint)
        : base($"The operation payload does not match the original submission for client operation ID '{clientOperationId}'. Existing fingerprint: '{existingFingerprint}', incoming fingerprint: '{incomingFingerprint}'.")
    {
        ClientOperationId = clientOperationId;
        ExistingFingerprint = existingFingerprint;
        IncomingFingerprint = incomingFingerprint;
    }
}

public sealed class IdempotencyOperationTypeMismatchException : Exception
{
    public string Code => "idempotency.operation_type_mismatch";
    public Guid ClientOperationId { get; }
    public string ExistingOperationType { get; }
    public string IncomingOperationType { get; }

    public IdempotencyOperationTypeMismatchException(
        Guid clientOperationId,
        string existingOperationType,
        string incomingOperationType)
        : base($"The operation type does not match the original submission for client operation ID '{clientOperationId}'. Existing type: '{existingOperationType}', incoming type: '{incomingOperationType}'.")
    {
        ClientOperationId = clientOperationId;
        ExistingOperationType = existingOperationType;
        IncomingOperationType = incomingOperationType;
    }
}

public sealed class IdempotencyContextMismatchException : Exception
{
    public string Code => "idempotency.context_mismatch";
    public Guid ClientOperationId { get; }
    public string ContextKey { get; }
    public string ExistingValue { get; }
    public string IncomingValue { get; }

    public IdempotencyContextMismatchException(
        Guid clientOperationId,
        string contextKey,
        string existingValue,
        string incomingValue)
        : base($"The operation context '{contextKey}' does not match the original submission for client operation ID '{clientOperationId}'. Existing value: '{existingValue}', incoming value: '{incomingValue}'.")
    {
        ClientOperationId = clientOperationId;
        ContextKey = contextKey;
        ExistingValue = existingValue;
        IncomingValue = incomingValue;
    }
}

