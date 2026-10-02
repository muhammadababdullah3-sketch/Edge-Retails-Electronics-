using EdgeRetails.Application.Features.Setup;

namespace EdgeRetails.Desktop.Services;

public sealed class RemoteBackendSetupService(DesktopApiClient apiClient) : IBackendSetupService
{
    private Guid? _pendingOperationId;

    public async Task CompleteFirstSetupAsync(
        string shopName,
        string ownerName,
        string phone,
        string address,
        string ownerPin,
        string selectedModule,
        string signedLicenseContent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(signedLicenseContent))
        {
            throw new InvalidOperationException(
                "A signed Edge Retails license (.erlic) is required for First Setup.");
        }

        var operationId = _pendingOperationId ??= Guid.CreateVersion7();
        try
        {
            await apiClient.PostAsync<BootstrapSetupRequest, FirstSetupBootstrapResult>(
                "/api/setup/bootstrap",
                new BootstrapSetupRequest(
                    shopName,
                    ownerName,
                    Normalize(phone),
                    Normalize(address),
                    ownerPin,
                    Normalize(selectedModule),
                    signedLicenseContent,
                    operationId),
                cancellationToken);
            _pendingOperationId = null;
        }
        catch (DesktopApiException ex) when (ex.Code == "setup.already_complete")
        {
            var state = await apiClient.GetAsync<SetupStateDto>(
                "/api/setup/state", cancellationToken);
            if (state.IsSetupRequired)
            {
                throw;
            }

            // A prior attempt may have committed before its response was lost.
            _pendingOperationId = null;
        }
        catch (DesktopApiException ex) when (IsDefinitiveClientRejection(ex.StatusCode))
        {
            // A canonical client rejection is definitive; the next submit is a new intent.
            _pendingOperationId = null;
            throw;
        }
    }

    private static bool IsDefinitiveClientRejection(System.Net.HttpStatusCode? statusCode) =>
        statusCode is System.Net.HttpStatusCode.BadRequest or
            System.Net.HttpStatusCode.Unauthorized or
            System.Net.HttpStatusCode.Forbidden or
            System.Net.HttpStatusCode.NotFound or
            System.Net.HttpStatusCode.Conflict or
            System.Net.HttpStatusCode.UnprocessableEntity;

    private sealed record SetupStateDto(bool IsSetupRequired);

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed record BootstrapSetupRequest(
        string ShopName,
        string OwnerName,
        string? Phone,
        string? Address,
        string OwnerPin,
        string? SelectedModule,
        string SignedLicenseContent,
        Guid ClientOperationId);
}
