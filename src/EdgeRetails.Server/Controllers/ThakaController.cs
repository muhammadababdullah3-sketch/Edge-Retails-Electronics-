using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ThakaController : ControllerBase
{
    private readonly IThakaReadService _thakaReads;
    private readonly SetThakaSuspensionHandler _suspensionHandler;
    private readonly CreateThakaProjectHandler _createProjectHandler;
    private readonly IssueThakaMaterialHandler _issueMaterialHandler;
    private readonly RecordThakaPaymentHandler _recordPaymentHandler;
    private readonly SettleThakaHandler _settleHandler;
    private readonly ReopenThakaHandler _reopenHandler;
    private readonly ReverseThakaMaterialHandler _reverseMaterialHandler;
    private readonly ReverseThakaPaymentHandler _reversePaymentHandler;

    public ThakaController(
        IThakaReadService thakaReads,
        CreateThakaProjectHandler createProjectHandler,
        IssueThakaMaterialHandler issueMaterialHandler,
        RecordThakaPaymentHandler recordPaymentHandler,
        SettleThakaHandler settleHandler,
        ReopenThakaHandler reopenHandler,
        ReverseThakaMaterialHandler reverseMaterialHandler,
        ReverseThakaPaymentHandler reversePaymentHandler,
        SetThakaSuspensionHandler suspensionHandler)
    {
        _thakaReads = thakaReads;
        _createProjectHandler = createProjectHandler;
        _issueMaterialHandler = issueMaterialHandler;
        _recordPaymentHandler = recordPaymentHandler;
        _settleHandler = settleHandler;
        _reopenHandler = reopenHandler;
        _reverseMaterialHandler = reverseMaterialHandler;
        _reversePaymentHandler = reversePaymentHandler;
        _suspensionHandler = suspensionHandler;
    }

    [HttpPost("projects/{id:guid}/suspension")]
    public async Task<IActionResult> SetSuspension(Guid id, SetThakaSuspensionRequest request, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var terminal = HttpContext.Items["CurrentTerminal"] as EdgeRetails.Domain.SystemConfiguration.Terminal;
        return ToActionResult(await _suspensionHandler.HandleAsync(new(request.ClientOperationId, id,
            request.IsSuspended, request.Reason, actor?.UserId ?? Guid.Empty, terminal?.Id, actor?.SessionId), cancellationToken));
    }

    [HttpGet("projects")]
    public async Task<IActionResult> GetProjects(
        [FromQuery] string? search,
        [FromQuery] ThakaProjectStatus? status,
        [FromQuery] int pageSize = 50,
        [FromQuery] DateOnly? beforeStartedOn = null,
        [FromQuery] Guid? beforeProjectId = null,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.ThakaManage);
        if (denied is not null)
        {
            return denied;
        }

        var query = new ThakaProjectPageQuery(
            Search: search,
            Status: status,
            PageSize: Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500),
            BeforeStartedOn: beforeStartedOn,
            BeforeProjectId: beforeProjectId);

        var page = await _thakaReads.GetProjectsPageAsync(query, cancellationToken);
        return Ok(page);
    }

    [HttpGet("projects/{id:guid}")]
    public async Task<IActionResult> GetProjectDetail(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.ThakaManage);
        if (denied is not null)
        {
            return denied;
        }

        var detail = await _thakaReads.GetProjectAsync(id, cancellationToken);
        if (detail is null)
        {
            return NotFound(new { code = "thaka.project_not_found", message = $"Thaka project '{id}' was not found." });
        }
        return Ok(detail);
    }

    [HttpGet("catalog")]
    public async Task<IActionResult> GetMaterialCatalog(CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.ThakaManage);
        if (denied is not null)
        {
            return denied;
        }

        var catalog = await _thakaReads.GetMaterialCatalogAsync(cancellationToken);
        return Ok(catalog);
    }

    [HttpPost("projects")]
    public async Task<IActionResult> CreateProject(
        [FromBody] CreateThakaProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ClientOperationId is not Guid operationId || operationId == Guid.Empty)
        {
            return BadRequest(new { code = "thaka.operation_id_required", message = "A Thaka project operation id is required." });
        }

        var actor = HttpContext.GetActorContext();
        var actorId = actor?.UserId ?? request.ActorId ?? Guid.Empty;
        var correlationId = request.CorrelationId ?? Guid.NewGuid();

        var command = new CreateThakaProjectCommand(
            CustomerId: request.CustomerId,
            ProjectName: request.ProjectName,
            SiteAddress: request.SiteAddress,
            Note: request.Note,
            StartedOn: request.StartedOn,
            ActorId: actorId,
            CorrelationId: correlationId,
            ClientOperationId: request.ClientOperationId ?? Guid.Empty);

        var result = await _createProjectHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("material-issue")]
    public async Task<IActionResult> IssueMaterial(
        [FromBody] IssueThakaMaterialRequest request,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var actorId = actor?.UserId ?? request.ActorId ?? Guid.Empty;

        var command = new IssueThakaMaterialCommand(
            ClientOperationId: request.ClientOperationId,
            ProjectId: request.ProjectId,
            ActorId: actorId,
            Note: request.Note,
            Lines: request.Lines);

        var result = await _issueMaterialHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("payments")]
    public async Task<IActionResult> RecordPayment(
        [FromBody] RecordThakaPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var actorId = actor?.UserId ?? request.ActorId ?? Guid.Empty;

        var command = new RecordThakaPaymentCommand(
            ClientOperationId: request.ClientOperationId,
            ProjectId: request.ProjectId,
            Amount: request.Amount,
            PaymentMethod: request.PaymentMethod,
            Reference: request.Reference,
            Note: request.Note,
            ActorId: actorId);

        var result = await _recordPaymentHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("settlement")]
    public async Task<IActionResult> SettleProject(
        [FromBody] SettleThakaRequest request,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var actorId = actor?.UserId ?? request.ActorId ?? Guid.Empty;

        var command = new SettleThakaCommand(
            ClientOperationId: request.ClientOperationId,
            ProjectId: request.ProjectId,
            SettlementDiscount: request.SettlementDiscount,
            FinalPaymentAmount: request.FinalPaymentAmount,
            PaymentMethod: request.PaymentMethod,
            PaymentReference: request.PaymentReference,
            ActorId: actorId);

        var result = await _settleHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("projects/{id:guid}/reopen")]
    public async Task<IActionResult> ReopenProject(
        [FromRoute] Guid id,
        [FromBody] ReopenThakaRequest request,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var actorId = actor?.UserId ?? request.ActorId ?? Guid.Empty;
        var correlationId = request.CorrelationId ?? Guid.NewGuid();

        var command = new ReopenThakaCommand(
            ProjectId: id,
            Reason: request.Reason,
            ActorId: actorId,
            CorrelationId: correlationId);

        var result = await _reopenHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("material-reversal")]
    public async Task<IActionResult> ReverseMaterial(
        [FromBody] ReverseThakaMaterialRequest request,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var actorId = actor?.UserId ?? request.ActorId ?? Guid.Empty;

        var command = new ReverseThakaMaterialCommand(
            ClientOperationId: request.ClientOperationId,
            ProjectId: request.ProjectId,
            MaterialIssueId: request.MaterialIssueId,
            Reason: request.Reason,
            ActorId: actorId);

        var result = await _reverseMaterialHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("payment-reversal")]
    public async Task<IActionResult> ReversePayment(
        [FromBody] ReverseThakaPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var actorId = actor?.UserId ?? request.ActorId ?? Guid.Empty;

        var command = new ReverseThakaPaymentCommand(
            ClientOperationId: request.ClientOperationId,
            ProjectId: request.ProjectId,
            PaymentId: request.PaymentId,
            Reason: request.Reason,
            ActorId: actorId);

        var result = await _reversePaymentHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var error = result.Error!;
        var statusCode = error.Code switch
        {
            var c when c.Contains("not_found") => StatusCodes.Status404NotFound,
            var c when c.Contains("mismatch") => StatusCodes.Status409Conflict,
            var c when c.Contains("locked") => StatusCodes.Status409Conflict,
            var c when c.Contains("duplicate") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") || c.StartsWith("authorization.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }
}

public sealed record SetThakaSuspensionRequest(Guid ClientOperationId, bool IsSuspended, string Reason);

public sealed record CreateThakaProjectRequest(
    Guid CustomerId,
    string ProjectName,
    string? SiteAddress = null,
    string? Note = null,
    DateOnly StartedOn = default,
    Guid? ActorId = null,
    Guid? CorrelationId = null,
    Guid? ClientOperationId = null);

public sealed record IssueThakaMaterialRequest(
    Guid ClientOperationId,
    Guid ProjectId,
    IReadOnlyList<IssueThakaMaterialLineInput> Lines,
    string? Note = null,
    Guid? ActorId = null);

public sealed record RecordThakaPaymentRequest(
    Guid ClientOperationId,
    Guid ProjectId,
    decimal Amount,
    ThakaPaymentMethod PaymentMethod,
    string? Reference = null,
    string? Note = null,
    Guid? ActorId = null);

public sealed record SettleThakaRequest(
    Guid ClientOperationId,
    Guid ProjectId,
    decimal SettlementDiscount,
    decimal FinalPaymentAmount,
    ThakaPaymentMethod PaymentMethod,
    string? PaymentReference = null,
    Guid? ActorId = null);

public sealed record ReopenThakaRequest(
    string Reason,
    Guid? ActorId = null,
    Guid? CorrelationId = null);

public sealed record ReverseThakaMaterialRequest(
    Guid ClientOperationId,
    Guid ProjectId,
    Guid MaterialIssueId,
    string Reason,
    Guid? ActorId = null);

public sealed record ReverseThakaPaymentRequest(
    Guid ClientOperationId,
    Guid ProjectId,
    Guid PaymentId,
    string Reason,
    Guid? ActorId = null);
