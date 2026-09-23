using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Repositories;

public sealed class TerminalRepository : ITerminalRepository
{
    private readonly EdgeRetailsDbContext _db;

    public TerminalRepository(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public async Task<Terminal?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.Terminals.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    public async Task<Terminal?> GetByCodeAsync(string terminalCode, CancellationToken cancellationToken = default)
    {
        var code = terminalCode.Trim().ToUpperInvariant();
        return await _db.Terminals.FirstOrDefaultAsync(t => t.TerminalCode == code, cancellationToken);
    }

    public async Task<IReadOnlyList<Terminal>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Terminals.OrderBy(t => t.Name).ToListAsync(cancellationToken);
    }

    public async Task<int> GetActiveCountAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Terminals.CountAsync(t => t.Status == TerminalStatus.Active, cancellationToken);
    }

    public async Task AddAsync(Terminal terminal, CancellationToken cancellationToken = default)
    {
        _db.Terminals.Add(terminal);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Terminal terminal, CancellationToken cancellationToken = default)
    {
        var entry = _db.Entry(terminal);
        if (entry.State == EntityState.Detached)
        {
            _db.Terminals.Attach(terminal);
            entry.State = EntityState.Modified;
        }
        await _db.SaveChangesAsync(cancellationToken);
    }
}
