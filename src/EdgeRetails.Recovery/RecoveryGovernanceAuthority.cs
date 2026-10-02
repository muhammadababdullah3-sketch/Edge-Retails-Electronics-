using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EdgeRetails.Recovery;

/// <summary>
/// Keeps the recovery signing private key in the current Windows user's non-exportable CNG store.
/// Only the public verification key is written to the protected machine-wide Server trust folder.
/// </summary>
internal static class RecoveryGovernanceAuthority
{
    private const string CngKeyName = "EdgeRetails.Recovery.Governance.v1";
    private const string RequiredServerVersion = "1.0.11";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string MetadataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EdgeRetails", "RecoveryGovernance");

    private static string MetadataPath => Path.Combine(MetadataDirectory, "authority.json");

    private static string TrustDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "EdgeRetails", "recovery");

    private static string TrustPath => Path.Combine(TrustDirectory, "trust.json");
    private static string MachineApplicationDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EdgeRetails");

    public static bool TryOpenSigner(out RSA? signer, out string issuerId, out string status)
    {
        signer = null;
        issuerId = string.Empty;
        status = "Recovery governance authority is not provisioned.";
        if (!OperatingSystem.IsWindows() || !File.Exists(MetadataPath) || !File.Exists(TrustPath))
        {
            return false;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<AuthorityMetadata>(File.ReadAllText(MetadataPath), JsonOptions);
            var trust = JsonSerializer.Deserialize<TrustFile>(File.ReadAllText(TrustPath), JsonOptions);
            if (metadata is null || trust?.RecoveryAuthorization is null ||
                string.IsNullOrWhiteSpace(metadata.IssuerId) || metadata.KeyName != CngKeyName ||
                !string.Equals(metadata.IssuerId, trust.RecoveryAuthorization.IssuerId, StringComparison.Ordinal))
            {
                status = "Recovery governance authority is incomplete or does not match the Shop Server trust configuration.";
                return false;
            }

            var cngKey = CngKey.Open(metadata.KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey);
            var cngSigner = new RSACng(cngKey);
            if (cngSigner.KeySize < 3072 || cngSigner.Key.ExportPolicy != CngExportPolicies.None ||
                !RecoveryGovernanceKeyBinding.MatchesSignerPublicKey(cngSigner, trust.RecoveryAuthorization.PublicKeyPem))
            {
                cngSigner.Dispose();
                status = "Recovery governance key is unavailable or does not match the provisioned public key.";
                return false;
            }

            signer = cngSigner;
            issuerId = metadata.IssuerId;
            status = $"Governance signer is available. Issuer: {issuerId}. Private key remains in Windows protected key storage.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or ArgumentException or SecurityException or InvalidOperationException or NotSupportedException)
        {
            status = "Recovery governance authority could not be opened safely.";
            return false;
        }
    }

    public static string PreparePublicProvisioningFile()
    {
        if (File.Exists(TrustPath))
        {
            throw new InvalidOperationException("A Recovery trust configuration already exists. Refusing to replace or rotate it automatically.");
        }

        Directory.CreateDirectory(MetadataDirectory);
        ProtectMetadataDirectory();
        EnsureNotReparsePoint(MetadataDirectory);

        AuthorityMetadata metadata;
        if (File.Exists(MetadataPath))
        {
            metadata = JsonSerializer.Deserialize<AuthorityMetadata>(File.ReadAllText(MetadataPath), JsonOptions)
                ?? throw new InvalidOperationException("Governance metadata is invalid; automatic key rotation is prohibited.");
            if (metadata.KeyName != CngKeyName || string.IsNullOrWhiteSpace(metadata.IssuerId))
            {
                throw new InvalidOperationException("Governance metadata does not identify the approved signer.");
            }
        }
        else
        {
            if (CngKey.Exists(CngKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey))
            {
                throw new InvalidOperationException("An untracked governance CNG key already exists. Manual governance review is required.");
            }
            metadata = CreateUserGovernanceKey();
        }

        using var signer = OpenUserSigner(metadata);
        if (signer.KeySize != 3072 || signer.Key.ExportPolicy != CngExportPolicies.None)
        {
            throw new CryptographicException("The governance signing key is not the required non-exportable 3072-bit key.");
        }

        var publicKeyPem = ExportPublicKeyPem(signer.ExportParameters(false));
        var trust = new TrustFile(new RecoveryTrustConfiguration(metadata.IssuerId, publicKeyPem));
        var provisioningPath = Path.Combine(MetadataDirectory, "provisioning-" + Guid.NewGuid().ToString("N") + ".json");
        WriteNewJsonFile(provisioningPath, trust);
        return provisioningPath;
    }

    public static void ProvisionPublicTrustAndRestartServer(string provisioningPath)
    {
        EnsureWindowsAdministrator();
        EnsureServerCanBeSafelyRestarted();
        if (File.Exists(TrustPath))
        {
            throw new InvalidOperationException("A Recovery trust configuration already exists. Refusing to replace or rotate it automatically.");
        }

        var fullPath = Path.GetFullPath(provisioningPath);
        var metadataDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var metadataRoot = Path.GetFullPath(metadataDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(metadataRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith("provisioning-", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            !IsGovernanceMetadataDirectory(metadataDirectory) ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("The Recovery public-key provisioning input is outside its approved protected directory.");
        }

        EnsureMetadataDirectoryAcl(metadataDirectory);
        var authorityPath = Path.Combine(metadataDirectory, "authority.json");
        if ((File.GetAttributes(authorityPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Governance authority metadata cannot be a reparse point.");
        }
        var metadata = JsonSerializer.Deserialize<AuthorityMetadata>(File.ReadAllText(authorityPath), JsonOptions)
            ?? throw new InvalidOperationException("Governance metadata is unavailable.");
        var trust = JsonSerializer.Deserialize<TrustFile>(File.ReadAllText(fullPath), JsonOptions)
            ?? throw new InvalidOperationException("Public Recovery trust input is invalid.");
        var recoveryTrust = trust.RecoveryAuthorization;
        if (metadata.KeyName != CngKeyName || string.IsNullOrWhiteSpace(metadata.IssuerId) ||
            recoveryTrust is null || !string.Equals(metadata.IssuerId, recoveryTrust.IssuerId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(recoveryTrust.PublicKeyPem) ||
            recoveryTrust.PublicKeyPem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("Public Recovery trust input failed issuer or key-material validation.");
        }
        using (var publicKey = RSA.Create())
        {
            publicKey.ImportFromPem(recoveryTrust.PublicKeyPem);
            if (publicKey.KeySize != 3072)
            {
                throw new CryptographicException("Recovery verification key must be RSA 3072-bit.");
            }
        }

        // The elevation boundary receives a staged public key. Bind it to the protected
        // current-user CNG signer immediately before provisioning so replacing that file
        // cannot make the Shop Server trust a different issuer key.
        using (var signer = OpenUserSigner(metadata))
        {
            if (signer.KeySize != 3072 || signer.Key.ExportPolicy != CngExportPolicies.None ||
                !RecoveryGovernanceKeyBinding.MatchesSignerPublicKey(signer, recoveryTrust.PublicKeyPem))
            {
                throw new CryptographicException("Staged Recovery verification key does not match the non-exportable governance signer.");
            }
        }

        EnsureCommonApplicationDataParentIsSafe();
        Directory.CreateDirectory(MachineApplicationDirectory);
        ProtectMachineApplicationDirectory();
        Directory.CreateDirectory(TrustDirectory);
        ProtectTrustDirectory();
        var trustTemp = TrustPath + "." + Guid.NewGuid().ToString("N") + ".new";
        WriteNewJsonFile(trustTemp, trust);
        ProtectTrustFile(trustTemp);
        File.Move(trustTemp, TrustPath, overwrite: false);

        try
        {
            VerifyTrustFileAcl();
            RestartShopServer(metadata.IssuerId);
        }
        catch (Exception exception)
        {
            // The public trust file is already durable. Do not remove it or claim rollback;
            // leave the Shop Server stopped if readiness or loopback verification failed.
            if (!TryStopShopServer())
            {
                throw new InvalidOperationException(
                    "Recovery trust was written, but post-write verification failed and the Shop Server could not be confirmed stopped. Treat deployment as failed and inspect the service state before continuing.",
                    exception);
            }
            throw;
        }
    }

    public static void DeleteProvisioningFile(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var metadataRoot = Path.GetFullPath(MetadataDirectory) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(metadataRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(fullPath).StartsWith("provisioning-", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) == 0)
            {
                File.Delete(fullPath);
            }
        }
        catch
        {
            // The file contains public key data only; cleanup failure must not hide the result.
        }
    }

    private static AuthorityMetadata CreateUserGovernanceKey()
    {
        var create = new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyUsage = CngKeyUsages.Signing,
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = CngKeyCreationOptions.None
        };
        create.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None));
        using var created = CngKey.Create(CngAlgorithm.Rsa, CngKeyName, create);
        using var signer = new RSACng(created);
        if (signer.KeySize != 3072 || signer.Key.ExportPolicy != CngExportPolicies.None)
        {
            throw new CryptographicException("Windows did not create the required non-exportable 3072-bit governance key.");
        }
        var metadata = new AuthorityMetadata("ER-RECOVERY-" + Guid.NewGuid().ToString("N"), CngKeyName);
        WriteNewJsonFile(MetadataPath, metadata);
        return metadata;
    }

    private static RSACng OpenUserSigner(AuthorityMetadata metadata)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Recovery signing requires Windows CNG.");
        }
        var key = CngKey.Open(metadata.KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey);
        return new RSACng(key);
    }

    private static void WriteNewJsonFile<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(value, JsonOptions));
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static string ExportPublicKeyPem(RSAParameters parameters)
    {
        using var publicKey = RSA.Create();
        publicKey.ImportParameters(parameters);
        return publicKey.ExportSubjectPublicKeyInfoPem();
    }

    private static void EnsureWindowsAdministrator()
    {
        if (!OperatingSystem.IsWindows() || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException("Provisioning the Shop Server Recovery trust requires normal Windows administrator elevation.");
        }
    }

    private static void ProtectMetadataDirectory()
    {
        var identity = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Current Windows user SID is unavailable.");
        EnsureNotReparsePoint(MetadataDirectory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(identity);
        AddDirectoryRule(security, identity, FileSystemRights.FullControl);
        AddDirectoryRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl);
        AddDirectoryRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl);
        new DirectoryInfo(MetadataDirectory).SetAccessControl(security);
        var expected = new Dictionary<SecurityIdentifier, FileSystemRights>();
        expected[identity] = FileSystemRights.FullControl;
        expected[new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)] = FileSystemRights.FullControl;
        expected[new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)] = FileSystemRights.FullControl;
        VerifyDirectoryAcl(MetadataDirectory, expected, identity);
    }

    private static bool IsGovernanceMetadataDirectory(string path)
    {
        var governance = new DirectoryInfo(path);
        return string.Equals(governance.Name, "RecoveryGovernance", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(governance.Parent?.Name, "EdgeRetails", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(governance.Parent?.Parent?.Name, "Local", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(governance.Parent?.Parent?.Parent?.Name, "AppData", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureMetadataDirectoryAcl(string path)
    {
        EnsureNotReparsePoint(path);
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new UnauthorizedAccessException("Governance metadata owner could not be verified.");
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var expected = new Dictionary<SecurityIdentifier, FileSystemRights>();
        expected[owner] = FileSystemRights.FullControl;
        expected[admins] = FileSystemRights.FullControl;
        expected[system] = FileSystemRights.FullControl;
        VerifyDirectoryAcl(path,
            expected, owner);
    }

    private static void ProtectTrustDirectory()
    {
        var security = new DirectorySecurity();
        EnsureNotReparsePoint(TrustDirectory);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.SetOwner(admins);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        AddDirectoryRule(security, admins, FileSystemRights.FullControl);
        AddDirectoryRule(security, system, FileSystemRights.FullControl);
        AddDirectoryRule(security, users, FileSystemRights.ReadAndExecute);
        new DirectoryInfo(TrustDirectory).SetAccessControl(security);
        VerifyDirectoryAcl(TrustDirectory,
            new Dictionary<SecurityIdentifier, FileSystemRights>
            {
                [admins] = FileSystemRights.FullControl,
                [system] = FileSystemRights.FullControl,
                [users] = FileSystemRights.ReadAndExecute
            }, admins);
    }

    private static void ProtectMachineApplicationDirectory()
    {
        EnsureNotReparsePoint(MachineApplicationDirectory);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(admins);
        AddDirectoryRule(security, admins, FileSystemRights.FullControl);
        AddDirectoryRule(security, system, FileSystemRights.FullControl);
        AddDirectoryRule(security, users, FileSystemRights.ReadAndExecute);
        new DirectoryInfo(MachineApplicationDirectory).SetAccessControl(security);
        VerifyDirectoryAcl(MachineApplicationDirectory,
            new Dictionary<SecurityIdentifier, FileSystemRights>
            {
                [admins] = FileSystemRights.FullControl,
                [system] = FileSystemRights.FullControl,
                [users] = FileSystemRights.ReadAndExecute
            }, admins);
    }

    private static void EnsureCommonApplicationDataParentIsSafe()
    {
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        EnsureNotReparsePoint(commonData);
        var info = new DirectoryInfo(commonData);
        var ancestors = new Stack<DirectoryInfo>();
        for (var current = info.Parent; current is not null; current = current.Parent)
        {
            ancestors.Push(current);
        }
        while (ancestors.TryPop(out var ancestor))
        {
            EnsureNotReparsePoint(ancestor.FullName);
        }
        var security = new DirectoryInfo(commonData).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var creatorOwner = new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);
        if (!security.AreAccessRulesProtected || !system.Equals(security.GetOwner(typeof(SecurityIdentifier))))
        {
            throw new UnauthorizedAccessException("ProgramData owner or ACL protection could not be verified.");
        }

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                !(admins.Equals(rule.IdentityReference) || system.Equals(rule.IdentityReference) ||
                  users.Equals(rule.IdentityReference) || creatorOwner.Equals(rule.IdentityReference)))
            {
                throw new UnauthorizedAccessException("ProgramData ACL contains an unapproved principal or deny rule.");
            }

            if (users.Equals(rule.IdentityReference))
            {
                var forbidden = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                if ((rule.FileSystemRights & forbidden) != 0)
                {
                    throw new UnauthorizedAccessException("ProgramData grants untrusted principals authority to replace machine application data.");
                }
            }
        }
    }

    private static void VerifyTrustFileAcl()
    {
        if ((File.GetAttributes(TrustPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Recovery trust configuration cannot be a reparse point.");
        }
        var security = new FileInfo(TrustPath).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        if (!security.AreAccessRulesProtected || !admins.Equals(security.GetOwner(typeof(SecurityIdentifier))))
        {
            throw new UnauthorizedAccessException("Recovery trust file ACL protection could not be verified.");
        }
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                !(admins.Equals(rule.IdentityReference) || system.Equals(rule.IdentityReference) || users.Equals(rule.IdentityReference)))
            {
                throw new UnauthorizedAccessException("Recovery trust file ACL contains an unapproved principal or deny rule.");
            }
        }
        if (!rules.Cast<FileSystemAccessRule>().Any(rule => admins.Equals(rule.IdentityReference) &&
                rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl) ||
            !rules.Cast<FileSystemAccessRule>().Any(rule => system.Equals(rule.IdentityReference) &&
                rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl) ||
            !rules.Cast<FileSystemAccessRule>().Any(rule => users.Equals(rule.IdentityReference) &&
                rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & FileSystemRights.Read) == FileSystemRights.Read))
        {
            throw new UnauthorizedAccessException("Recovery trust file ACL does not provide the approved public-read and administrator-write permissions.");
        }
    }

    private static void ProtectTrustFile(string path)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(admins);
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void EnsureNotReparsePoint(string path)
    {
        var info = new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Recovery authority storage cannot use a reparse-point directory.");
        }
    }

    private static void VerifyDirectoryAcl(string path, Dictionary<SecurityIdentifier, FileSystemRights> expectedRights, SecurityIdentifier expectedOwner)
    {
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!security.AreAccessRulesProtected || !expectedOwner.Equals(security.GetOwner(typeof(SecurityIdentifier))))
        {
            throw new UnauthorizedAccessException("Recovery authority storage ACL protection could not be verified.");
        }
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                !expectedRights.Keys.Any(sid => sid.Equals(rule.IdentityReference)))
            {
                throw new UnauthorizedAccessException("Recovery authority storage ACL contains an unapproved principal or deny rule.");
            }
        }
        foreach (var entry in expectedRights)
        {
            if (!rules.Cast<FileSystemAccessRule>().Any(rule => entry.Key.Equals(rule.IdentityReference) &&
                    rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & entry.Value) == entry.Value))
            {
                throw new UnauthorizedAccessException("Recovery authority storage ACL is missing a required full-control principal.");
            }
        }
    }

    private static void AddDirectoryRule(DirectorySecurity security, IdentityReference identity, FileSystemRights rights) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));

    private static void RestartShopServer(string expectedIssuerId)
    {
        var state = ReadServerServiceState();
        if (!string.Equals(state, "STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            _ = RunSc("stop", "EdgeRetailsServer");
            WaitForServerServiceState("STOPPED");
        }
        _ = RunSc("start", "EdgeRetailsServer");
        WaitForServerServiceState("RUNNING");
        WaitForServerReadiness(expectedIssuerId);
    }

    private static void EnsureServerCanBeSafelyRestarted()
    {
        var state = ReadServerServiceState();
        if (!string.Equals(state, "RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("EdgeRetailsServer must already be running before Recovery trust provisioning.");
        }
        var configuration = RunSc("qc", "EdgeRetailsServer");
        if (!configuration.Contains("AUTO_START", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("EdgeRetailsServer is not configured for automatic startup; no trust change was made.");
        }
        var imagePath = Regex.Match(configuration, """BINARY_PATH_NAME\s*:\s*(?:"([^"]+EdgeRetails\.Server\.exe)"|(.+?EdgeRetails\.Server\.exe))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var executable = imagePath.Success ? (imagePath.Groups[1].Success ? imagePath.Groups[1].Value : imagePath.Groups[2].Value).Trim() : string.Empty;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!RecoveryServerRuntimePolicy.IsApprovedServerImagePath(executable, programFiles) ||
            !RecoveryServerRuntimePolicy.HasNoReparsePointComponents(executable, programFiles))
        {
            throw new InvalidOperationException("Recovery trust provisioning requires the Server binary and every path component to be the exact non-reparse Program Files installation; no trust change was made.");
        }
        var version = FileVersionInfo.GetVersionInfo(executable);
        if (!RecoveryServerRuntimePolicy.IsApprovedServerVersion(
                version.FileVersion, version.ProductVersion, RequiredServerVersion))
        {
            throw new InvalidOperationException($"Recovery trust provisioning requires the installed Release {RequiredServerVersion} Server binary; no trust change was made.");
        }
        if (!VerifyServerReadiness(expectedIssuerId: null))
        {
            throw new InvalidOperationException("EdgeRetailsServer readiness is not healthy; no trust change was made.");
        }
    }

    private static void WaitForServerReadiness(string expectedIssuerId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (VerifyServerReadiness(expectedIssuerId))
            {
                return;
            }
            System.Threading.Thread.Sleep(500);
        }
        throw new TimeoutException("The Shop Server did not report healthy readiness with the provisioned Recovery issuer.");
    }

    private static bool VerifyServerReadiness(string? expectedIssuerId)
    {
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            if (!RecoveryServerRuntimePolicy.HasOnlyLoopbackListeners(listeners))
            {
                return false;
            }

            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:7150/"), Timeout = TimeSpan.FromSeconds(3) };
            using var readyResponse = client.GetAsync("api/system/ready").GetAwaiter().GetResult();
            if (!readyResponse.IsSuccessStatusCode)
            {
                return false;
            }
            using var readiness = JsonDocument.Parse(readyResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = readiness.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "Ready" ||
                !root.TryGetProperty("canConnect", out var canConnect) || !canConnect.GetBoolean() ||
                !root.TryGetProperty("hasPendingMigrations", out var pending) || pending.GetBoolean() ||
                !root.TryGetProperty("maintenanceState", out var maintenance) || maintenance.GetString() != "Normal")
            {
                return false;
            }
            if (expectedIssuerId is null)
            {
                return true;
            }
            using var contextResponse = client.GetAsync("api/recovery/context").GetAwaiter().GetResult();
            if (!contextResponse.IsSuccessStatusCode)
            {
                return false;
            }
            using var context = JsonDocument.Parse(contextResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return context.RootElement.TryGetProperty("recoveryIssuerId", out var issuer) &&
                   string.Equals(issuer.GetString(), expectedIssuerId, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryStopShopServer()
    {
        try
        {
            if (!string.Equals(ReadServerServiceState(), "STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                _ = RunSc("stop", "EdgeRetailsServer");
                WaitForServerServiceState("STOPPED");
            }
            return string.Equals(ReadServerServiceState(), "STOPPED", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // The caller reports the failed restart; this containment attempt must never hide it.
            return false;
        }
    }

    private static string ReadServerServiceState()
    {
        var result = RunSc("query", "EdgeRetailsServer");
        var match = Regex.Match(result, @"STATE\s*:\s*\d+\s+(\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException("The EdgeRetailsServer service state could not be verified.");
        }
        return match.Groups[1].Value;
    }

    private static void WaitForServerServiceState(string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (string.Equals(ReadServerServiceState(), expected, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            System.Threading.Thread.Sleep(500);
        }
        throw new TimeoutException("EdgeRetailsServer did not reach the required service state.");
    }

    private static string RunSc(string command, string serviceName)
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", $"{command} {serviceName}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Windows Service Control could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Windows Service Control could not change or verify the Shop Server service.");
        }
        return output + error;
    }

    private sealed record AuthorityMetadata(string IssuerId, string KeyName);
    private sealed record RecoveryTrustConfiguration(string IssuerId, string PublicKeyPem);
    private sealed record TrustFile(RecoveryTrustConfiguration RecoveryAuthorization);
}
