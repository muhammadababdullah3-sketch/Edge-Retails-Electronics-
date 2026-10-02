using System.Net.Http;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.ComponentModel;

namespace EdgeRetails.Recovery;

public partial class MainWindow : Window
{
    private const string RecoveryAction = "RESET_ACCOUNT_PIN";
    private const string RecoveryAlgorithm = "RS256";
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:7150/"),
        Timeout = TimeSpan.FromSeconds(15)
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private bool _ready;
    private bool _signerAvailable;
    private string _licenseId = string.Empty;
    private string _deviceId = string.Empty;
    private string _issuerId = string.Empty;
    private string? _serverIssuerId;

    public MainWindow()
    {
        InitializeComponent();
        LoadAuthorityStatus();
        Loaded += async (_, _) => await RefreshTargetsAsync();
    }

    private async Task RefreshTargetsAsync()
    {
        SetBusy(true);
        try
        {
            using var readinessResponse = await Client.GetAsync("api/system/ready");
            if (!readinessResponse.IsSuccessStatusCode)
            {
                StatusText.Text = "The installed Shop Server is not ready. Recovery is disabled.";
                _ready = false;
                return;
            }

            using var readiness = await JsonDocument.ParseAsync(await readinessResponse.Content.ReadAsStreamAsync());
            if (!readiness.RootElement.TryGetProperty("status", out var status) || status.GetString() != "Ready" ||
                !readiness.RootElement.TryGetProperty("hasPendingMigrations", out var pending) || pending.GetBoolean())
            {
                StatusText.Text = "The installed Shop Server is not ready or has pending migrations. Recovery is disabled.";
                _ready = false;
                return;
            }

            var context = await Client.GetFromJsonAsync<RecoveryContext>("api/recovery/context", JsonOptions);
            if (context is null || string.IsNullOrWhiteSpace(context.LicenseId) || string.IsNullOrWhiteSpace(context.DeviceId))
            {
                StatusText.Text = "The installed license and device binding could not be verified. Recovery is disabled.";
                _ready = false;
                return;
            }
            _licenseId = context.LicenseId;
            _deviceId = context.DeviceId;
            _serverIssuerId = context.RecoveryIssuerId;

            var owners = await Client.GetFromJsonAsync<OwnerTarget[]>("api/recovery/owner-pin", JsonOptions) ?? [];
            OwnerPicker.ItemsSource = owners;
            OwnerPicker.SelectedIndex = owners.Length == 1 ? 0 : -1;
            _ready = true;
            StatusText.Text = owners.Length == 0
                ? "No active Owner account is available."
                : _signerAvailable && string.Equals(_issuerId, _serverIssuerId, StringComparison.Ordinal)
                    ? "Shop Server and governance signer are ready. PIN recovery will issue a one-time authorization."
                    : _signerAvailable
                        ? "Governance signer and Shop Server issuer do not match. Recovery remains disabled."
                        : "Shop Server is ready. Provision the dedicated governance signing key before recovery.";
            RecoverButton.IsEnabled = _signerAvailable && string.Equals(_issuerId, _serverIssuerId, StringComparison.Ordinal);
        }
        catch
        {
            StatusText.Text = "The loopback Shop Server could not be reached. No account data was changed.";
            _ready = false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadAuthorityStatus();
        await RefreshTargetsAsync();
    }

    private async void Provision_Click(object sender, RoutedEventArgs e)
    {
        string? provisioningFile = null;
        try
        {
            provisioningFile = RecoveryGovernanceAuthority.PreparePublicProvisioningFile();
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                StatusText.Text = "The Recovery utility path could not be resolved.";
                RecoveryGovernanceAuthority.DeleteProvisioningFile(provisioningFile);
                return;
            }
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            start.ArgumentList.Add("--provision");
            start.ArgumentList.Add(provisioningFile);
            using var process = Process.Start(start);
            if (process is null)
            {
                StatusText.Text = "Governance provisioning was not started.";
                RecoveryGovernanceAuthority.DeleteProvisioningFile(provisioningFile);
                return;
            }
            StatusText.Text = "Complete the Windows elevation prompt. The Server will restart after public-key provisioning.";
            await process.WaitForExitAsync();
            RecoveryGovernanceAuthority.DeleteProvisioningFile(provisioningFile);
            if (process.ExitCode == 0)
            {
                LoadAuthorityStatus();
                await RefreshTargetsAsync();
            }
            else
            {
                LoadAuthorityStatus();
                StatusText.Text = "Governance provisioning or Server verification failed. Recovery remains disabled; verify service state before retrying.";
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            if (provisioningFile is not null)
            {
                RecoveryGovernanceAuthority.DeleteProvisioningFile(provisioningFile);
            }
            StatusText.Text = "Windows elevation was declined. The governance key remains protected in this Windows user profile; Server trust was not changed.";
        }
        catch
        {
            if (provisioningFile is not null)
            {
                RecoveryGovernanceAuthority.DeleteProvisioningFile(provisioningFile);
            }
            StatusText.Text = "Governance provisioning could not be started. No Recovery Authorization was issued.";
        }
    }

    private async void Recover_Click(object sender, RoutedEventArgs e)
    {
        var recoverySucceeded = false;
        var target = OwnerPicker.SelectedItem as OwnerTarget;
        var pin = NewPin.Password;
        var confirmation = ConfirmPin.Password;
        if (!_ready || !_signerAvailable || !string.Equals(_issuerId, _serverIssuerId, StringComparison.Ordinal) || target is null ||
            pin.Length != 4 || pin.Any(ch => !char.IsAsciiDigit(ch)) || pin != confirmation)
        {
            StatusText.Text = "Select an active Owner, verify the governance signer, and enter matching four-digit PINs.";
            NewPin.Clear();
            ConfirmPin.Clear();
            return;
        }

        var choice = MessageBox.Show(
            $"Reset the PIN for {target.DisplayName}? This ends the account's active sessions.",
            "Confirm Owner PIN recovery",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes)
        {
            NewPin.Clear();
            ConfirmPin.Clear();
            return;
        }

        SetBusy(true);
        try
        {
            if (!RecoveryGovernanceAuthority.TryOpenSigner(out var signer, out var issuerId, out var signerStatus) || signer is null)
            {
                _signerAvailable = false;
                AuthorityText.Text = signerStatus;
                StatusText.Text = "The governance signing key could not be used. No account data was changed.";
                return;
            }

            string authorization;
            using (signer)
            {
                var issuedAt = DateTimeOffset.UtcNow;
                var payload = new RecoveryAuthorizationPayload(
                    issuerId,
                    _licenseId,
                    _deviceId,
                    RecoveryAction,
                    issuedAt,
                    issuedAt.AddMinutes(5),
                    Guid.NewGuid(),
                    target.UserId);
                var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
                var signature = signer.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                authorization = JsonSerializer.Serialize(new SignedRecoveryAuthorization(
                    RecoveryAlgorithm,
                    Convert.ToBase64String(payloadBytes),
                    Convert.ToBase64String(signature)), JsonOptions);
                CryptographicOperations.ZeroMemory(signature);
                CryptographicOperations.ZeroMemory(payloadBytes);
            }

            using var response = await Client.PostAsJsonAsync("api/recovery/owner-pin",
                new RecoveryRequest(authorization, target.UserId, pin), JsonOptions);
            if (response.IsSuccessStatusCode)
            {
                StatusText.Text = "PIN recovery completed. Sign in to Edge Retails with the new PIN.";
                recoverySucceeded = true;
            }
            else
            {
                var error = await response.Content.ReadFromJsonAsync<RecoveryError>(JsonOptions);
                StatusText.Text = error?.Message ?? "PIN recovery was not completed.";
            }
        }
        catch
        {
            StatusText.Text = "PIN recovery could not be confirmed. Check account state before trying again.";
        }
        finally
        {
            NewPin.Clear();
            ConfirmPin.Clear();
            SetBusy(false);
        }

        if (recoverySucceeded)
        {
            MessageBox.Show(this,
                "Owner PIN recovery completed. Sign in to Edge Retails with the new PIN.",
                "PIN recovery completed",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
        ProvisionButton.IsEnabled = !busy;
        OwnerPicker.IsEnabled = !busy && _ready;
        NewPin.IsEnabled = !busy && _ready;
        ConfirmPin.IsEnabled = !busy && _ready;
        RecoverButton.IsEnabled = !busy && _ready && _signerAvailable &&
            string.Equals(_issuerId, _serverIssuerId, StringComparison.Ordinal);
    }

    private void LoadAuthorityStatus()
    {
        _signerAvailable = RecoveryGovernanceAuthority.TryOpenSigner(out var signer, out _issuerId, out var status);
        signer?.Dispose();
        AuthorityText.Text = status;
        RecoverButton.IsEnabled = _ready && _signerAvailable &&
            string.Equals(_issuerId, _serverIssuerId, StringComparison.Ordinal);
    }

    private sealed record OwnerTarget(Guid UserId, string DisplayName);
    private sealed record RecoveryContext(string LicenseId, string DeviceId, string? RecoveryIssuerId);
    private sealed record RecoveryAuthorizationPayload(
        string IssuerId, string LicenseId, string DeviceId, string Action,
        DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, Guid Nonce, Guid TargetUserId);
    private sealed record SignedRecoveryAuthorization(string Algorithm, string PayloadBase64, string SignatureBase64);
    private sealed record RecoveryRequest(string SignedAuthorization, Guid TargetUserId, string NewPin);
    private sealed record RecoveryError(string? Code, string? Message);
}
