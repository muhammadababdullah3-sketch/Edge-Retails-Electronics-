using EdgeRetails.Central.Application.Services;

namespace EdgeRetails.Central.UnitTests.Identity;

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void HashPassword_GeneratesSaltedPbkdf2Hash()
    {
        const string password = "AdminStrongPassword#2026";
        var hash = _hasher.HashPassword(password);

        Assert.NotNull(hash);
        var parts = hash.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal("100000", parts[0]);
    }

    [Fact]
    public void VerifyPassword_ReturnsTrueForCorrectPassword()
    {
        const string password = "AdminStrongPassword#2026";
        var hash = _hasher.HashPassword(password);

        Assert.True(_hasher.VerifyPassword(password, hash));
    }

    [Fact]
    public void VerifyPassword_ReturnsFalseForWrongPassword()
    {
        const string password = "AdminStrongPassword#2026";
        var hash = _hasher.HashPassword(password);

        Assert.False(_hasher.VerifyPassword("WrongPassword#999", hash));
    }
}
