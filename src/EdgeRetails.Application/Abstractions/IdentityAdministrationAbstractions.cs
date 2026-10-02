using EdgeRetails.Domain.Identity;

namespace EdgeRetails.Application.Abstractions;

public interface IIdentityAdministrationRepository
{
    Task<bool> DisplayNameExistsAsync(string normalizedName, CancellationToken cancellationToken);
    void AddUser(User user);
}
