using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Identity;

public class AdminRole : Entity
{
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string CapabilitiesJson { get; private set; } = "[]";

    public List<AdminUserRole> UserRoles { get; private set; } = new();

    private AdminRole() { }

    public static AdminRole Create(string name, string description, IEnumerable<string> capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var capsList = capabilities.Distinct(StringComparer.Ordinal).ToList();
        return new AdminRole
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Description = description,
            CapabilitiesJson = System.Text.Json.JsonSerializer.Serialize(capsList)
        };
    }

    public void UpdateCapabilities(IEnumerable<string> capabilities)
    {
        var capsList = capabilities.Distinct(StringComparer.Ordinal).ToList();
        CapabilitiesJson = System.Text.Json.JsonSerializer.Serialize(capsList);
    }

    public IReadOnlyList<string> GetCapabilities()
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(CapabilitiesJson) ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
