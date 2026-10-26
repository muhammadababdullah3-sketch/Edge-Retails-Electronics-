using EdgeRetails.Central.Api.Controllers;
using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;

namespace EdgeRetails.Central.UnitTests.Identity;

public sealed class AuthControllerStepUpBindingTests
{
    private sealed class AuthService(Guid sessionId) : IAdminAuthService
    {
        public Task<PreAuthChallengeDto> StartLoginAsync(LoginRequestDto request, bool allowLocalPasswordBypass = false, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AdminSessionDto> CompleteMfaAsync(MfaRequestDto request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AdminSessionDto?> ValidateSessionAsync(Guid requestedId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AdminSessionDto?>(requestedId == sessionId ? new AdminSessionDto { SessionId = sessionId } : null);

        public Task<bool> LogoutAsync(Guid requestedId, string reason, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class StepUpService : IStepUpService
    {
        public StepUpRequestDto? ReceivedRequest { get; private set; }

        public Task<StepUpGrantDto> VerifyAndGrantStepUpAsync(StepUpRequestDto request, CancellationToken cancellationToken = default)
        {
            ReceivedRequest = request;
            return Task.FromResult(new StepUpGrantDto { SessionId = request.SessionId, StepUpGrantId = Guid.NewGuid() });
        }

        public Task<bool> ValidateStepUpGrantAsync(Guid sessionId, string actionScope, string targetPayloadSha256, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> ValidateAndConsumeStepUpGrantAsync(Guid sessionId, string actionScope, string targetPayloadSha256, Guid? grantId = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private static AuthController CreateController(Guid cookieSessionId, AuthService auth, StepUpService stepUp)
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ControllerActionDescriptor());
        var controller = new AuthController(auth, stepUp)
        {
            ControllerContext = new ControllerContext(actionContext)
        };
        controller.HttpContext.Request.Headers.Cookie = $"{CentralSessionCredentials.CookieName}={cookieSessionId:D}";
        return controller;
    }

    private static StepUpRequestDto Request(Guid sessionId) => new()
    {
        SessionId = sessionId,
        ActionScope = "license.revoke",
        TargetPayloadSha256 = new string('a', 64),
        Proof = "123456",
        ChallengeId = Guid.NewGuid()
    };

    [Fact]
    public async Task StepUp_DerivesEmptyBodySessionIdFromValidatedHttpOnlyCookie()
    {
        var sessionId = Guid.NewGuid();
        var auth = new AuthService(sessionId);
        var stepUp = new StepUpService();
        var controller = CreateController(sessionId, auth, stepUp);

        var response = await controller.StepUp(Request(Guid.Empty), CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        Assert.NotNull(stepUp.ReceivedRequest);
        Assert.Equal(sessionId, stepUp.ReceivedRequest!.SessionId);
    }

    [Fact]
    public async Task StepUp_RejectsBodySessionIdThatConflictsWithCookie()
    {
        var sessionId = Guid.NewGuid();
        var auth = new AuthService(sessionId);
        var stepUp = new StepUpService();
        var controller = CreateController(sessionId, auth, stepUp);

        var response = await controller.StepUp(Request(Guid.NewGuid()), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(response);
        Assert.Null(stepUp.ReceivedRequest);
    }
}
