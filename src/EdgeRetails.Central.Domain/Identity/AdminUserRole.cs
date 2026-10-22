namespace EdgeRetails.Central.Domain.Identity;

public class AdminUserRole
{
    public Guid AdminUserId { get; private set; }
    public AdminUser AdminUser { get; private set; } = null!;

    public Guid RoleId { get; private set; }
    public AdminRole Role { get; private set; } = null!;

    public DateTimeOffset AssignedAtUtc { get; private set; }

    private AdminUserRole() { }

    public AdminUserRole(Guid adminUserId, Guid roleId, DateTimeOffset assignedAtUtc)
    {
        AdminUserId = adminUserId;
        RoleId = roleId;
        AssignedAtUtc = assignedAtUtc;
    }
}
