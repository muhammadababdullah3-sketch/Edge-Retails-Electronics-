using System;
using System.Collections.Generic;

namespace EdgeRetails.Application.Contracts.Packs;

public sealed record PackContainerQueryRequest(
    Guid? ProductId = null,
    string? ConditionFilter = null,
    decimal? MinContinuousLength = null,
    int PageNumber = 1,
    int PageSize = 20);

public sealed record PackContainerDto(
    Guid ContainerId,
    string TrackingCode,
    string State,
    decimal InitialQuantity,
    decimal RemainingQuantity,
    decimal ContinuousLength,
    string UnitOfMeasure,
    string Condition,
    long ConcurrencyVersion,
    decimal? UnitCost = null);

public sealed record PackContainerQueryResponse(
    int TotalCount,
    IReadOnlyList<PackContainerDto> Items);

public sealed record PackAllocationRequest(
    Guid TransactionId,
    Guid LineItemId,
    Guid ContainerId,
    decimal RequestedQuantity,
    bool AllowSplitCut = false,
    long? ExpectedVersion = null);

public sealed record PackAllocationResponse(
    Guid AllocationId,
    decimal AllocatedQuantity,
    decimal RemainingQuantity,
    decimal ContinuousLength,
    bool IsRemnant);
