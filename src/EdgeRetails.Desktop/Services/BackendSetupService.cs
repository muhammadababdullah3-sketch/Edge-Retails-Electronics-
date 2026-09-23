using EdgeRetails.Application.Features.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Desktop.Services;

public interface IBackendSetupService
{
    Task CompleteFirstSetupAsync(
        string shopName,
        string ownerName,
        string phone,
        string address,
        string ownerPin,
        string selectedModule,
        CancellationToken cancellationToken = default);
}

public sealed class BackendSetupService : IBackendSetupService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public BackendSetupService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }
    public async Task CompleteFirstSetupAsync(
        string shopName,
        string ownerName,
        string phone,
        string address,
        string ownerPin,
        string selectedModule,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider
            .GetRequiredService<FirstSetupBootstrapHandler>();

        var result = await handler.HandleAsync(
            new FirstSetupBootstrapCommand(
                shopName,
                Normalize(phone),
                Normalize(address),
                ownerName,
                ownerPin,
                Normalize(selectedModule),
                Guid.CreateVersion7()),
            cancellationToken);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                result.Error?.Message ?? "First Setup could not be completed.");
        }
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
