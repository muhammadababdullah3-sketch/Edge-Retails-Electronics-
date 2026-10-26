using EdgeRetails.Application.Features.Finance;

namespace EdgeRetails.Desktop.Services;

public interface ICashSessionClient
{
    Task<CashSessionSummaryDto?> GetCurrentAsync(CancellationToken cancellationToken = default);
    Task<Guid> OpenAsync(decimal openingCash, string? note, CancellationToken cancellationToken = default);
    Task<CashClosingResult> CloseAsync(Guid sessionId, decimal countedCash, string? note, CancellationToken cancellationToken = default);
}

public sealed class RemoteCashSessionClient(DesktopApiClient apiClient, Func<Guid> actorId) : ICashSessionClient
{
    public async Task<CashSessionSummaryDto?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await apiClient.GetAsync<CashSessionSummaryDto>("/api/finance/cash-session/current", cancellationToken);
        }
        catch (DesktopApiException ex) when (ex.Code == "cash.session_not_found")
        {
            return null;
        }
    }

    public async Task<Guid> OpenAsync(decimal openingCash, string? note, CancellationToken cancellationToken = default)
    {
        var command = new OpenCashSessionCommand(openingCash, actorId(), note);
        return await apiClient.PostAsync<OpenCashSessionCommand, Guid>("/api/finance/cash-session/open", command, cancellationToken);
    }

    public async Task<CashClosingResult> CloseAsync(
        Guid sessionId,
        decimal countedCash,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var command = new CloseCashSessionCommand(sessionId, countedCash, actorId(), note);
        return await apiClient.PostAsync<CloseCashSessionCommand, CashClosingResult>(
            "/api/finance/cash-session/close",
            command,
            cancellationToken);
    }
}
