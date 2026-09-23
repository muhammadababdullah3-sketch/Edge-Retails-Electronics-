using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.Application.Features.Terminals;

public interface ITerminalRepository
{
    Task<Terminal?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Terminal?> GetByCodeAsync(string terminalCode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Terminal>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<int> GetActiveCountAsync(CancellationToken cancellationToken = default);
    Task AddAsync(Terminal terminal, CancellationToken cancellationToken = default);
    Task UpdateAsync(Terminal terminal, CancellationToken cancellationToken = default);
}
