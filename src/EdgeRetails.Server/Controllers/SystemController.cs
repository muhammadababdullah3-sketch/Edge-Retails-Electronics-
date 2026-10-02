using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Production;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SystemController : ControllerBase
{
    private readonly OperationStatusQueryHandler _operationStatusHandler;

    public SystemController(OperationStatusQueryHandler operationStatusHandler)
    {
        _operationStatusHandler = operationStatusHandler;
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new
        {
            status = "Healthy",
            timestamp = DateTimeOffset.UtcNow,
            protocolVersion = TerminalProtocol.CurrentProtocolVersion
        });
    }

    [HttpGet("ready")]
    public async Task<IActionResult> Ready(
        [FromServices] EdgeRetails.Application.Abstractions.IDatabaseReadinessService readinessService,
        [FromServices] IProductionMaintenanceBarrier maintenanceBarrier,
        CancellationToken cancellationToken)
    {
        var check = await readinessService.CheckAsync(cancellationToken);
        if (!check.IsReady)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "NotReady",
                canConnect = check.CanConnect,
                hasPendingMigrations = check.HasPendingMigrations,
                failureReason = check.FailureReason,
                timestamp = DateTimeOffset.UtcNow
            });
        }

        ProductionMaintenanceState maintenanceState;
        try
        {
            maintenanceState = await maintenanceBarrier.GetStateAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "NotReady",
                code = "system.maintenance_state_unavailable",
                failureReason = "Production maintenance state could not be verified safely.",
                timestamp = DateTimeOffset.UtcNow
            });
        }

        if (maintenanceState != ProductionMaintenanceState.Normal)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "NotReady",
                code = "system.maintenance_active",
                maintenanceState = maintenanceState.ToString(),
                failureReason = maintenanceState == ProductionMaintenanceState.RecoveryRequired
                    ? "Restore recovery is required before normal startup."
                    : "A restore operation is active; normal startup is paused.",
                timestamp = DateTimeOffset.UtcNow
            });
        }

        return Ok(new
        {
            status = "Ready",
            canConnect = check.CanConnect,
            hasPendingMigrations = check.HasPendingMigrations,
            maintenanceState = maintenanceState.ToString(),
            timestamp = DateTimeOffset.UtcNow
        });
    }

    [HttpGet("version")]
    public IActionResult Version()
    {
        return Ok(new
        {
            server = "EdgeRetails.Server",
            version = "1.0.0",
            protocolVersion = TerminalProtocol.CurrentProtocolVersion,
            minSupportedProtocolVersion = TerminalProtocol.MinimumSupportedProtocolVersion
        });
    }

    [HttpGet("operations/{clientOperationId:guid}")]
    public async Task<IActionResult> GetOperationStatus(
        [FromRoute] Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        if (clientOperationId == Guid.Empty)
        {
            return BadRequest(new { code = "validation.client_operation_id_required", message = "ClientOperationId cannot be empty." });
        }

        var terminal = HttpContext.Items["CurrentTerminal"] as Terminal;
        if (terminal is null)
        {
            return StatusCode(StatusCodes.Status401Unauthorized, new
            {
                code = "auth.terminal_id_missing",
                message = "X-Terminal-Id header is required for authenticated terminal requests."
            });
        }

        var actor = HttpContext.GetActorContext();

        var query = new OperationStatusQuery(
            ClientOperationId: clientOperationId,
            ActorId: actor?.UserId,
            TerminalId: terminal.Id,
            RequireIdentityScope: actor is not null,
            RequireCanonicalOutcome: true);

        var result = await _operationStatusHandler.HandleAsync(query, cancellationToken);

        if (result.IsSuccess && result.Value is not null)
        {
            var value = result.Value;

            // When called with valid terminal authentication but WITHOUT a user session:
            // Return minimal recovery data only: clientOperationId, found, status, wasCommitted, operationType, safe reference (documentNumber/entityId if committed).
            // Do NOT expose sensitive customer, supplier, financial totals, or user details.
            if (actor is null)
            {
                if (!value.Found || !value.WasCommitted)
                {
                    return Ok(new
                    {
                        clientOperationId = value.ClientOperationId,
                        found = value.Found,
                        status = value.Status,
                        wasCommitted = false,
                        operationType = value.OperationType
                    });
                }

                return Ok(new
                {
                    clientOperationId = value.ClientOperationId,
                    found = true,
                    status = value.Status,
                    wasCommitted = true,
                    operationType = value.OperationType,
                    documentNumber = value.DocumentNumber,
                    entityId = value.EntityId
                });
            }

            // Authenticated user session present: return full canonical operation status
            if (!value.Found)
            {
                return Ok(new
                {
                    clientOperationId = value.ClientOperationId,
                    found = false,
                    operationType = value.OperationType,
                    wasCommitted = false,
                    status = value.Status,
                    timestamp = value.Timestamp
                });
            }

            return Ok(value);
        }

        var statusCode = result.Error?.Code switch
        {
            var c when c != null && (c.StartsWith("auth.") || c.StartsWith("authorization.")) => StatusCodes.Status403Forbidden,
            var c when c != null && c.Contains("not_found") => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = result.Error?.Code, message = result.Error?.Message });
    }
}
