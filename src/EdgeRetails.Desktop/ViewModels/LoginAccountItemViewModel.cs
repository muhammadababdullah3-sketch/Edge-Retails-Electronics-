namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// Represents a user account choice displayed on the Edge Retails login card.
/// Credentials are intentionally owned by the identity service.
/// </summary>
public sealed class LoginAccountItemViewModel : ViewModelBase
{
    private bool _isSelected;

    public LoginAccountItemViewModel(
        string accountId,
        string displayName,
        string roleName,
        string initials,
        string? avatarLetter = null,
        bool isOnline = true,
        bool isPrimary = false)
    {
        AccountId = accountId;
        DisplayName = displayName;
        RoleName = roleName;
        Initials = initials;
        AvatarLetter = avatarLetter ?? initials;
        IsOnline = isOnline;
        IsPrimary = isPrimary;
    }

    public string AccountId { get; }
    public string DisplayName { get; }
    public string RoleName { get; }
    public string Initials { get; }
    public string AvatarLetter { get; }
    public bool IsOnline { get; }
    public bool IsPrimary { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
