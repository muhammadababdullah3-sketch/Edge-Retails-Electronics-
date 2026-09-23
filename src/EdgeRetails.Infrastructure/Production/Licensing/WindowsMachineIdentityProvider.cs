using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Application.Production.Licensing;
using Microsoft.Win32;

namespace EdgeRetails.Infrastructure.Production.Licensing;

public sealed class WindowsMachineIdentityProvider : IDeviceIdentityProvider
{
    public Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Production device binding requires Windows.");
        }

        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
        var machineGuid = key?.GetValue("MachineGuid") as string;
        if (string.IsNullOrWhiteSpace(machineGuid))
        {
            throw new InvalidOperationException("Windows MachineGuid is unavailable.");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("EdgeRetails|" + machineGuid.Trim()));
        return Task.FromResult(Convert.ToHexString(hash));
    }
}
