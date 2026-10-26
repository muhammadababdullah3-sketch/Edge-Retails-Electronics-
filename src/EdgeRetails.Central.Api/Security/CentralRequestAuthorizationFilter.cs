using System.Text.RegularExpressions;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EdgeRetails.Central.Api.Security;

/// <summary>
/// Endpoint-aware fail-closed MVC authorization filter.
/// Enforces no-store headers, explicit anonymous exceptions, internal signer boundary,
/// installation asymmetric proof requirement, CSRF/session credential integrity,
/// and exact granular capability permissions from effective040 without admin.manage bridge.
/// </summary>
public class CentralRequestAuthorizationFilter : IAsyncAuthorizationFilter
{
    private readonly IAdminAuthService _authService;
    private readonly InstallationRequestProofService? _installationProof;

    public CentralRequestAuthorizationFilter(IAdminAuthService authService, InstallationRequestProofService? installationProof = null)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _installationProof = installationProof;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var httpContext = context.HttpContext;
        var request = httpContext.Request;
        var response = httpContext.Response;
        var path = request.Path.Value ?? string.Empty;
        var method = request.Method.ToUpperInvariant();

        // 1. Enforce cache control no-store and pragma no-cache across all API endpoints
        SetNoStoreHeaders(response);
        try
        {
            response.OnStarting(() =>
            {
                SetNoStoreHeaders(response);
                return Task.CompletedTask;
            });
        }
        catch
        {
            // If headers have already started, suppress exception
        }

        // 2. Explicit anonymous exceptions: pre-authentication routes plus the opaque short-lived
        // backup grant content route, whose bearer capability is validated by its controller service.
        if (IsBackupGrantContentOperation(method, path))
        {
            return;
        }
        if (IsExplicitAnonymousException(method, path))
        {
            return;
        }

        // 3. Preserve backend internal signer boundary
        if (path.StartsWith("/internal/v1", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Headers.ContainsKey("Origin"))
            {
                context.Result = new ObjectResult(new
                {
                    code = "CROSS_ORIGIN_PROHIBITED",
                    safeMessageKey = "signer.cross_origin_prohibited",
                    message = "Cross-origin requests to internal signing endpoints are strictly prohibited."
                }) { StatusCode = StatusCodes.Status403Forbidden };
                return;
            }

            if (path.Equals("/internal/v1/sign", StringComparison.OrdinalIgnoreCase))
            {
                // The controller owns the signer-token decision and audits rejected calls.
                // The filter has already rejected browser Origin; duplicating token auth here
                // prevents the endpoint from recording its required denial event.
                return;
            }

            // Other /internal/v1 routes (e.g., key rotate/revoke) fall through to admin session check below
        }

        // These two bootstrap operations perform their complete authority decision from a
        // persisted, one-time challenge and asymmetric proof before returning data or mutating state.
        if (IsBootstrapProofControllerOperation(method, path))
        {
            return;
        }

        // 5. Installation operations with no asymmetric proof must fail INSTALLATION_AUTHORITY_NOT_ACTIVE
        if (IsInstallationOperation(method, path))
        {
            var allowPendingBootstrap = method == "GET" && (
                Regex.IsMatch(path, @"^/api/licensing/v1/activation/[^/]+/status$", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(path, @"^/api/licensing/v1/installations/[^/]+/delivery$", RegexOptions.IgnoreCase));
            if (_installationProof is null || !await _installationProof.AuthenticateAsync(request, allowPendingBootstrap, httpContext.RequestAborted))
            {
                context.Result = new UnauthorizedObjectResult(new
                {
                    code = "INSTALLATION_AUTHORITY_NOT_ACTIVE",
                    safeMessageKey = "auth.installation_required",
                    message = "Installation operations require a valid, fresh, persisted-nonce asymmetric proof bound to an active installation."
                });
            }
            return;
        }

        // 6. Admin session authentication and transport validation
        var credResult = CentralSessionCredentials.Parse(request);

        if (credResult.ErrorCode == "SESSION_CREDENTIAL_CONFLICT")
        {
            context.Result = new BadRequestObjectResult(new
            {
                code = "SESSION_CREDENTIAL_CONFLICT",
                safeMessageKey = "security.session_credential_conflict",
                message = "Conflicting session credentials provided across transports."
            });
            return;
        }

        if (credResult.ErrorCode == "SESSION_CREDENTIAL_INVALID")
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                code = "SESSION_INVALID",
                safeMessageKey = "auth.session_invalid",
                message = "The provided session credential is invalid or malformed."
            });
            return;
        }

        if (!credResult.IsPresent || credResult.SessionId == Guid.Empty)
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                code = "SESSION_REQUIRED",
                safeMessageKey = "auth.session_required",
                message = "An authenticated admin session is required."
            });
            return;
        }

        var session = await _authService.ValidateSessionAsync(credResult.SessionId, httpContext.RequestAborted);
        if (session is null)
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                code = "SESSION_INVALID",
                safeMessageKey = "auth.session_invalid",
                message = "The session is expired, revoked, or invalid."
            });
            return;
        }

        CentralSessionCredentials.NormalizeValidatedRequest(request, session.SessionId);
        httpContext.Items["AdminSession"] = session;

        // 7. Session management endpoints for authenticated session:
        // Portal bootstrap context and logout must work for any valid MFA session (capabilities come from the response).
        if (path.Equals("/api/admin/v1/auth/logout", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/admin/v1/portal/session", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Session administration uses precise capabilities; a generic admin.manage grant
        // never bridges into the session-management domain.
        var sessionPermission = path.Equals("/api/admin/v1/portal/sessions/revoke", StringComparison.OrdinalIgnoreCase)
            ? "sessions.revoke"
            : null;
        if (sessionPermission is not null &&
            !session.Capabilities.Any(c => string.Equals(c, sessionPermission, StringComparison.OrdinalIgnoreCase)))
        {
            context.Result = new ObjectResult(new
            {
                code = "FORBIDDEN",
                safeMessageKey = "auth.unauthorized_capability",
                message = $"The session does not possess the required capability '{sessionPermission}' for this endpoint."
            }) { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        // Session-management routes were checked against their dedicated capabilities above.
        if (sessionPermission is not null)
        {
            return;
        }

        // 8. Endpoint capability authorization: exact source permission identifiers from effective040; no admin.manage bridge
        var requiredPermission = ResolveRequiredPermission(method, path);
        if (requiredPermission is null)
        {
            // Fail closed for unmapped or unauthorized endpoints
            context.Result = new ObjectResult(new
            {
                code = "FORBIDDEN",
                safeMessageKey = "auth.unauthorized_endpoint",
                message = "Endpoint authorization denied."
            }) { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        var hasPermission = session.Capabilities.Any(c => string.Equals(c, requiredPermission, StringComparison.OrdinalIgnoreCase));

        // Key rotate/revoke can also be satisfied by security.manage
        if (!hasPermission && (requiredPermission == AdminCapabilities.SignerRotate || requiredPermission == AdminCapabilities.SignerRevoke))
        {
            hasPermission = session.Capabilities.Any(c => string.Equals(c, "security.manage", StringComparison.OrdinalIgnoreCase));
        }

        if (!hasPermission)
        {
            context.Result = new ObjectResult(new
            {
                code = "FORBIDDEN",
                safeMessageKey = "auth.unauthorized_capability",
                message = $"The session does not possess the required capability '{requiredPermission}' for this endpoint."
            }) { StatusCode = StatusCodes.Status403Forbidden };
        }
    }

    private static void SetNoStoreHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        response.Headers.Pragma = "no-cache";
        response.Headers["Expires"] = "0";
    }

    private static bool IsExplicitAnonymousException(string method, string path)
    {
        if (method != "POST")
        {
            return false;
        }

        return path.Equals("/api/admin/v1/auth/login", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/api/admin/v1/auth/mfa", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/api/activation/v1/challenge", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/api/licensing/v1/bootstrap/challenge", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBackupGrantContentOperation(string method, string path) =>
        method == "GET" && Regex.IsMatch(path,
            @"^/api/licensing/v1/backups/download-grants/[^/]+/content$",
            RegexOptions.IgnoreCase);

    private static bool IsSupportTicketMutation(string method, string path)
    {
        if (method != "POST")
        {
            return false;
        }

        return path.StartsWith("/api/support/v1/tickets", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/api/admin/v1/support/tickets", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInstallationOperation(string method, string path)
    {
        // Only routes whose declared auth surface is installation-bound require installation proof.
        if (method is not ("GET" or "POST" or "PUT" or "DELETE" or "PATCH"))
        {
            return false;
        }

        return method switch
        {
            "POST" when path.Equals("/api/licensing/v1/reconcile", StringComparison.OrdinalIgnoreCase) => true,
            "GET" when path.Equals("/api/licensing/v1/trust-set", StringComparison.OrdinalIgnoreCase) => true,
            "GET" when Regex.IsMatch(path, @"^/api/licensing/v1/operations/[^/]+$", RegexOptions.IgnoreCase) => true,
            "GET" when Regex.IsMatch(path, @"^/api/licensing/v1/activation/[^/]+/status$", RegexOptions.IgnoreCase) => true,
            "GET" when Regex.IsMatch(path, @"^/api/licensing/v1/installations/[^/]+/delivery$", RegexOptions.IgnoreCase) => true,
            "POST" when path.StartsWith("/api/licensing/v1/backups/", StringComparison.OrdinalIgnoreCase) => true,
            "GET" when path.StartsWith("/api/licensing/v1/backups/", StringComparison.OrdinalIgnoreCase) => true,
            "POST" when path.Equals("/api/licensing/v1/support/bundles", StringComparison.OrdinalIgnoreCase) => true,
            "PUT" when Regex.IsMatch(path, @"^/api/licensing/v1/uploads/[^/]+/content$", RegexOptions.IgnoreCase) => true,
            _ => false
        };
    }

    private static bool IsBootstrapProofControllerOperation(string method, string path) =>
        method == "POST" &&
        (path.Equals("/api/licensing/v1/primary/register", StringComparison.OrdinalIgnoreCase) ||
         path.Equals("/api/licensing/v1/activation/redeem", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves exact granular permission identifier from effective040 (CONTRACT_ROUTES.json).
    /// Central strictly avoids any generic admin.manage bridge or wildcard bypass.
    /// </summary>
    public static string? ResolveRequiredPermission(string method, string path)
    {
        if (method == "GET" && path.Equals("/api/admin/v1/backups", StringComparison.OrdinalIgnoreCase))
        {
            return "backup.download";
        }

        if (method == "GET" && path.Equals("/api/admin/v1/notifications/status", StringComparison.OrdinalIgnoreCase))
        {
            return "notifications.view";
        }

        if (method == "POST" && path.Equals("/api/commercial/v1/payments/review", StringComparison.OrdinalIgnoreCase))
        {
            return "payment.review";
        }
        // 1. Auth step-up
        if (method == "POST" && path.Equals("/api/admin/v1/auth/step-up", StringComparison.OrdinalIgnoreCase))
        {
            return "auth.stepup";
        }

        // 2. Operations Admin Lookup
        if (method == "GET" && Regex.IsMatch(path, @"^/api/admin/v1/operations/[^/]+$", RegexOptions.IgnoreCase))
        {
            return "operation.admin.lookup";
        }

        // 3. Activation Tokens Issue
        if (method == "POST" && (path.Equals("/api/admin/v1/activation-tokens", StringComparison.OrdinalIgnoreCase) ||
                                 path.Equals("/api/activation/v1/tokens", StringComparison.OrdinalIgnoreCase)))
        {
            return "activation.issue";
        }

        // 4. Customers
        if (method == "POST" && (path.Equals("/api/admin/v1/customers", StringComparison.OrdinalIgnoreCase) ||
                                 path.Equals("/api/commercial/v1/customers", StringComparison.OrdinalIgnoreCase)))
        {
            return "customer.create";
        }
        if (method == "GET" && (Regex.IsMatch(path, @"^/api/admin/v1/customers/[^/]+$", RegexOptions.IgnoreCase) ||
                                Regex.IsMatch(path, @"^/api/commercial/v1/customers/[^/]+$", RegexOptions.IgnoreCase)))
        {
            return "customer.view";
        }

        // 5. Shops
        if (method == "POST" && (path.Equals("/api/admin/v1/shops", StringComparison.OrdinalIgnoreCase) ||
                                 path.Equals("/api/commercial/v1/shops", StringComparison.OrdinalIgnoreCase)))
        {
            return "shop.register";
        }
        if (method == "GET" && (Regex.IsMatch(path, @"^/api/admin/v1/shops/[^/]+$", RegexOptions.IgnoreCase) ||
                                Regex.IsMatch(path, @"^/api/commercial/v1/shops/[^/]+$", RegexOptions.IgnoreCase)))
        {
            return "shop.view";
        }

        // 6. Pricing snapshots
        if (method == "POST" && (path.Equals("/api/admin/v1/pricing-snapshots", StringComparison.OrdinalIgnoreCase) ||
                                 path.Equals("/api/commercial/v1/pricing-snapshots", StringComparison.OrdinalIgnoreCase)))
        {
            return "price.snapshot";
        }

        // 7. Orders
        if (method == "POST" && (path.Equals("/api/admin/v1/orders", StringComparison.OrdinalIgnoreCase) ||
                                 path.Equals("/api/commercial/v1/orders", StringComparison.OrdinalIgnoreCase)))
        {
            return "order.create";
        }

        // 8. Payments review
        if (method == "POST" && Regex.IsMatch(path, @"^/api/admin/v1/payments/[^/]+/review$", RegexOptions.IgnoreCase))
        {
            return "payment.review";
        }

        // 9. Licenses
        if (method == "POST" && (path.Equals("/api/admin/v1/licenses/issue", StringComparison.OrdinalIgnoreCase) ||
                                  path.Equals("/api/commercial/v1/licenses/issue", StringComparison.OrdinalIgnoreCase)))
        {
            return "license.issue";
        }
        if (method == "POST" && (Regex.IsMatch(path, @"^/api/admin/v1/licenses/[^/]+/addons/[^/]+/renew$", RegexOptions.IgnoreCase) ||
                                  Regex.IsMatch(path, @"^/api/commercial/v1/licenses/[^/]+/addons/renew$", RegexOptions.IgnoreCase)))
        {
            return "addon.renew";
        }
        if (method == "POST" && (Regex.IsMatch(path, @"^/api/admin/v1/licenses/[^/]+/renew$", RegexOptions.IgnoreCase) ||
                                  Regex.IsMatch(path, @"^/api/commercial/v1/licenses/[^/]+/renew$", RegexOptions.IgnoreCase)))
        {
            return "license.renew";
        }
        if (method == "POST" && (Regex.IsMatch(path, @"^/api/admin/v1/licenses/[^/]+/extend$", RegexOptions.IgnoreCase) ||
                                  Regex.IsMatch(path, @"^/api/commercial/v1/licenses/[^/]+/extend$", RegexOptions.IgnoreCase)))
        {
            return "license.extend";
        }
        if (method == "POST" && (Regex.IsMatch(path, @"^/api/admin/v1/licenses/[^/]+/pause$", RegexOptions.IgnoreCase) ||
                                 Regex.IsMatch(path, @"^/api/lifecycle/v1/licenses/[^/]+/pause$", RegexOptions.IgnoreCase)))
        {
            return "license.pause";
        }
        if (method == "POST" && (Regex.IsMatch(path, @"^/api/admin/v1/licenses/[^/]+/resume$", RegexOptions.IgnoreCase) ||
                                 Regex.IsMatch(path, @"^/api/lifecycle/v1/licenses/[^/]+/resume$", RegexOptions.IgnoreCase)))
        {
            return "license.resume";
        }
        if (method == "POST" && (Regex.IsMatch(path, @"^/api/admin/v1/licenses/[^/]+/suspend$", RegexOptions.IgnoreCase) ||
                                 Regex.IsMatch(path, @"^/api/lifecycle/v1/licenses/[^/]+/suspend$", RegexOptions.IgnoreCase)))
        {
            return "license.suspend";
        }
        if (method == "POST" && (Regex.IsMatch(path, @"^/api/admin/v1/licenses/[^/]+/reinstate$", RegexOptions.IgnoreCase) ||
                                 Regex.IsMatch(path, @"^/api/lifecycle/v1/licenses/[^/]+/reinstate$", RegexOptions.IgnoreCase)))
        {
            return "license.reinstate";
        }
        if (method == "POST" && (Regex.IsMatch(path, @"^/api/admin/v1/licenses/[^/]+/revoke$", RegexOptions.IgnoreCase) ||
                                 Regex.IsMatch(path, @"^/api/lifecycle/v1/licenses/[^/]+/revoke$", RegexOptions.IgnoreCase)))
        {
            return "license.revoke";
        }
        if (method == "POST" && (Regex.IsMatch(path, @"^/api/admin/v1/licenses/[^/]+/policy$", RegexOptions.IgnoreCase) ||
                                 path.Equals("/api/lifecycle/v1/policies", StringComparison.OrdinalIgnoreCase)))
        {
            return "license.policy";
        }
        if (method == "GET" && Regex.IsMatch(path, @"^/api/admin/v1/licenses/[^/]+$", RegexOptions.IgnoreCase))
        {
            return "license.view";
        }

        // 10. Activation Review
        if (method == "POST" && (Regex.IsMatch(path, @"^/api/admin/v1/activations/[^/]+/review$", RegexOptions.IgnoreCase) ||
                                 Regex.IsMatch(path, @"^/api/activation/v1/redemptions/[^/]+/review$", RegexOptions.IgnoreCase)))
        {
            return "activation.review";
        }

        // 11. Hardware Review
        if (method == "POST" && (Regex.IsMatch(path, @"^/api/admin/v1/hardware-reviews/[^/]+/decide$", RegexOptions.IgnoreCase) ||
                                 Regex.IsMatch(path, @"^/api/activation/v1/hardware-reviews/[^/]+/decide$", RegexOptions.IgnoreCase)))
        {
            return "hardware.review";
        }
        if (method == "POST" && path.Equals("/api/activation/v1/hardware-reviews", StringComparison.OrdinalIgnoreCase))
        {
            return "hardware.review";
        }

        // 12. Primary Transfer
        if (method == "POST" && (Regex.IsMatch(path, @"^/api/admin/v1/transfers/[^/]+/decide$", RegexOptions.IgnoreCase) ||
                                 Regex.IsMatch(path, @"^/api/activation/v1/primary-transfers/[^/]+/approve$", RegexOptions.IgnoreCase) ||
                                 Regex.IsMatch(path, @"^/api/activation/v1/primary-transfers/[^/]+/complete$", RegexOptions.IgnoreCase) ||
                                 path.Equals("/api/activation/v1/primary-transfers", StringComparison.OrdinalIgnoreCase)))
        {
            return "primary.transfer";
        }

        // 13. Sensitive Approvals
        if (method == "POST" && path.Equals("/api/admin/v1/sensitive-approvals/unmask-pii", StringComparison.OrdinalIgnoreCase))
        {
            return "customer.view_sensitive";
        }

        if (method == "POST" && (Regex.IsMatch(path, @"^/api/admin/v1/sensitive-approvals/[^/]+/(?:decide|decision)$", RegexOptions.IgnoreCase) ||
                                 path.Equals("/api/admin/v1/sensitive-approvals", StringComparison.OrdinalIgnoreCase) ||
                                 path.Equals("/api/admin/v1/sensitive-approvals/unmask-pii", StringComparison.OrdinalIgnoreCase) ||
                                 path.Equals("/api/lifecycle/v1/sensitive-approvals", StringComparison.OrdinalIgnoreCase) ||
                                 Regex.IsMatch(path, @"^/api/lifecycle/v1/sensitive-approvals/[^/]+/decide$", RegexOptions.IgnoreCase)))
        {
            return "sensitive.approve";
        }

        // 14. Admin Users Management
        if (method == "POST" && (Regex.IsMatch(path, @"^/api/admin/v1/admin-users/[^/]+/commands$", RegexOptions.IgnoreCase) ||
                                 path.Equals("/api/admin/v1/admin-users", StringComparison.OrdinalIgnoreCase)))
        {
            return "admin.manage";
        }

        // 15. Dashboard, customer view, and queues
        if (method == "GET" && (path.Equals("/api/admin/v1/dashboard", StringComparison.OrdinalIgnoreCase) ||
                                path.Equals("/api/admin/v1/dashboard/metrics", StringComparison.OrdinalIgnoreCase) ||
                                Regex.IsMatch(path, @"^/api/admin/v1/dashboard/customer360/[^/]+$", RegexOptions.IgnoreCase)))
        {
            return "dashboard.view";
        }
        if (method == "GET" && (Regex.IsMatch(path, @"^/api/admin/v1/queues/[^/]+$", RegexOptions.IgnoreCase) ||
                                 Regex.IsMatch(path, @"^/api/admin/v1/dashboard/queues/[^/]+$", RegexOptions.IgnoreCase)))
        {
            return "queues.view";
        }

        // 17. Search
        if (method == "GET" && path.Equals("/api/admin/v1/search", StringComparison.OrdinalIgnoreCase))
        {
            return "search.masked";
        }

        // 18. Audit
        if (method == "GET" && path.Equals("/api/admin/v1/audit", StringComparison.OrdinalIgnoreCase))
        {
            return AdminCapabilities.AuditView;
        }

        // 19. Sessions
        if (method == "GET" && path.Equals("/api/admin/v1/sessions", StringComparison.OrdinalIgnoreCase))
        {
            return "sessions.view";
        }

        // 20. Notifications
        if (method == "GET" && path.Equals("/api/admin/v1/notifications/status", StringComparison.OrdinalIgnoreCase))
        {
            return "notifications.view";
        }
        if (method == "GET" && path.Equals("/api/admin/v1/notifications", StringComparison.OrdinalIgnoreCase))
        {
            return "notifications.view";
        }

        // 21. Support View
        if (method == "GET" && Regex.IsMatch(path, @"^/api/admin/v1/support/tickets/[^/]+/attachments/[^/]+/download$", RegexOptions.IgnoreCase))
        {
            return "support.view";
        }
        if (method == "GET" && (path.Equals("/api/admin/v1/support/tickets", StringComparison.OrdinalIgnoreCase) ||
                                path.Equals("/api/support/v1/tickets", StringComparison.OrdinalIgnoreCase)))
        {
            return "support.view";
        }
        if (method == "POST" && (path.Equals("/api/support/v1/tickets", StringComparison.OrdinalIgnoreCase) ||
                                  path.Equals("/api/admin/v1/support/tickets", StringComparison.OrdinalIgnoreCase)))
        {
            return "support.create";
        }
        if (method == "POST" && (Regex.IsMatch(path, @"^/api/support/v1/tickets/[^/]+/actions$", RegexOptions.IgnoreCase) ||
                                  Regex.IsMatch(path, @"^/api/admin/v1/support/tickets/[^/]+/actions$", RegexOptions.IgnoreCase) ||
                                  Regex.IsMatch(path, @"^/api/admin/v1/support/tickets/[^/]+/commands$", RegexOptions.IgnoreCase)))
        {
            return "support.reply";
        }

        // 22. Evidence and Uploads
        if (method == "POST" && (path.Equals("/api/admin/v1/evidence/uploads", StringComparison.OrdinalIgnoreCase) ||
                                 path.Equals("/api/commercial/v1/uploads/sessions", StringComparison.OrdinalIgnoreCase)))
        {
            return "evidence.upload.init";
        }
        if (method == "POST" && Regex.IsMatch(path, @"^/api/commercial/v1/uploads/sessions/[^/]+/chunks$", RegexOptions.IgnoreCase))
        {
            return "evidence.upload.init";
        }
        if (method == "POST" && (Regex.IsMatch(path, @"^/api/admin/v1/evidence/uploads/[^/]+/finalize$", RegexOptions.IgnoreCase) ||
                                 Regex.IsMatch(path, @"^/api/commercial/v1/uploads/sessions/[^/]+/finalize$", RegexOptions.IgnoreCase)))
        {
            return "evidence.upload.finalize";
        }
        if (method == "GET" && Regex.IsMatch(path, @"^/api/admin/v1/evidence/[^/]+/download$", RegexOptions.IgnoreCase))
        {
            return "evidence.download";
        }

        // 23. Key rotate and revoke
        if (method == "POST" && Regex.IsMatch(path, @"^/internal/v1/keys/[^/]+/rotate$", RegexOptions.IgnoreCase))
        {
            return AdminCapabilities.SignerRotate;
        }
        if (method == "POST" && Regex.IsMatch(path, @"^/internal/v1/keys/[^/]+/revoke$", RegexOptions.IgnoreCase))
        {
            return AdminCapabilities.SignerRevoke;
        }

        // 24. Action Preview
        if (method == "GET" && path.Equals("/api/admin/v1/sessions", StringComparison.OrdinalIgnoreCase))
        {
            return "sessions.view";
        }
        if (method == "POST" && path.Equals("/api/admin/v1/action-preview", StringComparison.OrdinalIgnoreCase))
        {
            return "sensitive.approve";
        }
        if (method == "POST" && path.Equals("/api/lifecycle/v1/policies", StringComparison.OrdinalIgnoreCase))
        {
            return "security.manage";
        }

        // 25. Additional activation / certificate issue
        if (method == "POST" && path.Equals("/api/activation/v1/certificates/issue", StringComparison.OrdinalIgnoreCase))
        {
            return "license.issue";
        }

        // Commercial catalog management is governed by the existing licensing capabilities.
        if (method == "POST" && path.Equals("/api/commercial/v1/catalog-items", StringComparison.OrdinalIgnoreCase))
        {
            return AdminCapabilities.LicensingManage;
        }
        if (method == "GET" && path.Equals("/api/commercial/v1/catalog-items", StringComparison.OrdinalIgnoreCase))
        {
            return AdminCapabilities.LicensingRead;
        }
        return null;
    }
}
