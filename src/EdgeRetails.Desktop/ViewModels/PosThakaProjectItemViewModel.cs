namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// Represents an active Thaka project selectable during Material Issue mode.
/// </summary>
public sealed class PosThakaProjectItemViewModel : ViewModelBase
{
    public PosThakaProjectItemViewModel(
        string id,
        string name,
        string customerName,
        string location,
        decimal budget,
        decimal totalMaterialIssued)
    {
        Id = id;
        Name = name;
        CustomerName = customerName;
        Location = location;
        Budget = budget;
        TotalMaterialIssued = totalMaterialIssued;
    }

    public string Id { get; }

    public string Name { get; }

    public string CustomerName { get; }

    public string Location { get; }

    public decimal Budget { get; }

    public decimal TotalMaterialIssued { get; }

    public string DisplayText => $"{Name} ({CustomerName})";

    public string Subtitle => $"{Location} • Budget Rs. {Budget:N0}";
}
