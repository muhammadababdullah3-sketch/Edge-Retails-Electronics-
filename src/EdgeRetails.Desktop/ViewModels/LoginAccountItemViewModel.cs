namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// Represents a user account choice displayed on the Edge Retails login card.
/// Credentials are intentionally owned by the identity service.
/// </summary>
public sealed class LoginAccountItemViewModel(
    string accountId,
    string displayName,
    string roleName,
    string initials,
    string? avatarLetter = null,
    bool isOnline = true,
    bool isPrimary = false) : ViewModelBase
{
    private bool _isSelected;

    public string AccountId { get; } = accountId;
    public string DisplayName { get; } = displayName;
    public string RoleName { get; } = roleName;
    public string Initials { get; } = initials;
    public string AvatarLetter { get; } = avatarLetter ?? initials;
    public bool IsOnline { get; } = isOnline;
    public bool IsPrimary { get; } = isPrimary;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
