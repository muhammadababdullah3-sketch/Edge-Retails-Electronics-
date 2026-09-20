using System.Security.Cryptography;
using System.Text;

namespace EdgeRetails.Desktop.Services;

public sealed class DemoIdentityAccount
{
    public required string Id { get; init; }
    public required string DisplayName { get; set; }
    public required string RoleName { get; init; }
    public required string Initials { get; set; }
    public required string AvatarLetter { get; set; }
    public required bool IsOnline { get; init; }
    public required bool IsPrimary { get; init; }
}

public interface IDemoIdentityService
{
    IReadOnlyList<DemoIdentityAccount> Accounts { get; }

    bool VerifyPin(string accountId, string candidatePin);

    void ConfigureOwner(string displayName, string pin);
}

public sealed class DemoIdentityService : IDemoIdentityService
{
    private const int Pbkdf2Iterations = 120_000;

    private sealed class Credential
    {
        public required byte[] Salt { get; init; }
        public required byte[] Hash { get; init; }
    }

    private readonly List<DemoIdentityAccount> _accounts;
    private readonly Dictionary<string, Credential> _credentials =
        new(StringComparer.Ordinal);

    public DemoIdentityService()
    {
        _accounts =
        [
            new()
            {
                Id = "owner",
                DisplayName = "Abdullah",
                RoleName = "Owner",
                Initials = "A",
                AvatarLetter = "A",
                IsOnline = true,
                IsPrimary = true
            },
            new()
            {
                Id = "cashier",
                DisplayName = "Ali",
                RoleName = "Cashier",
                Initials = "A",
                AvatarLetter = "L",
                IsOnline = true,
                IsPrimary = false
            }
        ];

        _credentials["owner"] = new Credential
        {
            Salt = Convert.FromBase64String("IafE0/ARi5puWMIhf9lDCg=="),
            Hash = Convert.FromBase64String(
                "hQQzjM7MGN24ywCdXGOwUsrujR7nGIv0RWP+lMAYHlg=")
        };

        _credentials["cashier"] = new Credential
        {
            Salt = Convert.FromBase64String("myfR7mA+THwVH9zSRlkegg=="),
            Hash = Convert.FromBase64String(
                "mWDViPuvgzEx/UeAk20MFPPfSPw6N3de85MJIzrlqGQ=")
        };
    }

    public IReadOnlyList<DemoIdentityAccount> Accounts => _accounts;

    public bool VerifyPin(string accountId, string candidatePin)
    {
        if (!_credentials.TryGetValue(accountId, out var credential) ||
            string.IsNullOrEmpty(candidatePin))
        {
            return false;
        }

        var candidateHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(candidatePin),
            credential.Salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            credential.Hash.Length);

        return CryptographicOperations.FixedTimeEquals(
            candidateHash,
            credential.Hash);
    }

    public void ConfigureOwner(string displayName, string pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ValidatePin(pin);

        var owner = _accounts.Single(account => account.Id == "owner");
        owner.DisplayName = displayName.Trim();
        owner.Initials = BuildInitials(owner.DisplayName);
        owner.AvatarLetter = owner.Initials[..1];

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            32);

        _credentials["owner"] = new Credential
        {
            Salt = salt,
            Hash = hash
        };
    }

    private static void ValidatePin(string pin)
    {
        if (pin.Length != 4 || !pin.All(char.IsAsciiDigit))
        {
            throw new InvalidOperationException(
                "Owner PIN must be exactly 4 numeric digits.");
        }
    }

    private static string BuildInitials(string displayName)
    {
        var words = displayName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return words.Length switch
        {
            0 => "U",
            1 => words[0][..1].ToUpperInvariant(),
            _ => string.Concat(
                words[0][..1],
                words[^1][..1]).ToUpperInvariant()
        };
    }
}
