using EdgeRetails.Central.Application.DTOs;

namespace EdgeRetails.Central.Application.Abstractions;

public interface IOperationLedgerService
{
    Task<OutcomeDto> ExecuteOrReplayAsync<TCommand, TResult>(
        string actorScope,
        Guid? shopId,
        CommandMetaDto meta,
        TCommand payload,
        Func<TCommand, Task<(TResult Result, string? ResultReference, int ResourceVersion, int AuthorityRevision)>> executeAsync,
        string? targetResource = null,
        CancellationToken cancellationToken = default);

    Task<(OutcomeDto Outcome, TResult Result)> ExecuteAndCaptureOrReplayAsync<TCommand, TResult>(
        string actorScope,
        Guid? shopId,
        CommandMetaDto meta,
        TCommand payload,
        Func<TCommand, Task<(TResult Result, string? ResultReference, int ResourceVersion, int AuthorityRevision)>> executeAsync,
        Func<EdgeRetails.Central.Domain.Operations.CentralOperationRecord, Task<TResult>>? recoverResultAsync = null,
        string? targetResource = null,
        CancellationToken cancellationToken = default);

    Task<OperationStatusDto?> GetOperationStatusAsync(
        Guid clientOperationId,
        string actorScope,
        CancellationToken cancellationToken = default);

    Task<OutcomeDto?> GetOutcomeAsync(
        Guid clientOperationId,
        string actorScope,
        CancellationToken cancellationToken = default);

    Task<OutcomeDto?> GetLicensingOutcomeAsync(
        Guid clientOperationId,
        string? shopScope,
        CancellationToken cancellationToken = default);
}
