using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace EdgeRetails.Infrastructure.Services;

internal interface ISequenceAuthorityCustody
{
    void ValidateDirectory(string directory);
    void ValidateFile(string path);
}

internal sealed class WindowsSequenceAuthorityCustody : ISequenceAuthorityCustody
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000,
        GenericExecute = 0x20000000, GenericAll = 0x10000000;
    private const uint FileMutation = 0x000D0156; // data, append, EA, attributes, delete, DAC, owner
    private const uint AncestorMutation = 0x000D0040; // delete child, delete, DAC, owner

    public void ValidateDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows()) { throw new InvalidOperationException("Sequence authority requires Windows custody validation."); }
        var volume = Path.GetPathRoot(Path.GetFullPath(directory));
        if (string.IsNullOrEmpty(volume) || volume.StartsWith(@"\\", StringComparison.Ordinal)
            || !IsLocalFixedVolume(volume, new DriveInfo(volume).DriveType))
        {
            throw new InvalidDataException("Sequence authority requires custody on a fixed local volume.");
        }
        for (DirectoryInfo? current = new(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            RequireRegular(current.FullName, directory: true);
            ValidateSecurity(current.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), AncestorMutation);
        }
        // The immediate directory also controls newly created temporary artifacts.
        var leaf = new DirectoryInfo(directory);
        ValidateSecurity(leaf.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), FileMutation | 0x40);
    }

    internal static bool IsLocalFixedVolume(string volume, DriveType type)
        => !string.IsNullOrEmpty(volume) && !volume.StartsWith(@"\\", StringComparison.Ordinal) && type == DriveType.Fixed;

    public void ValidateFile(string path)
    {
        if (!OperatingSystem.IsWindows()) { throw new InvalidOperationException("Sequence authority requires Windows custody validation."); }
        RequireRegular(path, directory: false);
        ValidateSecurity(new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), FileMutation);
    }

    internal static void RequireRegular(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        ValidateAttributes(attributes, directory);
    }

    internal static void ValidateAttributes(FileAttributes attributes, bool directory)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
        {
            throw new InvalidDataException("Sequence authority path has invalid custody.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool Trusted(SecurityIdentifier sid) => sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    [SupportedOSPlatform("windows")]
    private static void ValidateSecurity(FileSystemSecurity security, uint dangerous)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted(owner))
        {
            throw new InvalidDataException("Sequence authority owner is not trusted.");
        }
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null) { throw new InvalidDataException("Sequence authority has no restrictive ACL."); }
        var rules = new List<SequenceCustodyAccessRule>();
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            if ((ace.AceFlags & AceFlags.InheritOnly) != 0) { continue; }
            if (ace is not CommonAce common || common.IsCallback || common.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
            {
                throw new InvalidDataException("Sequence authority ACL contains unsupported access rules.");
            }
            // OWNER RIGHTS applies only to the already verified trusted owner.
            var trusted = Trusted(common.SecurityIdentifier) || common.SecurityIdentifier.Value == "S-1-3-4";
            rules.Add(new(common.SecurityIdentifier.Value, MapGenericRights(unchecked((uint)common.AccessMask)),
                common.AceQualifier == AceQualifier.AccessDenied, trusted));
        }
        if (HasUntrustedMutation(rules, dangerous)) { throw new InvalidDataException("Sequence authority permits untrusted mutation."); }
    }

    internal static uint MapGenericRights(uint rights)
    {
        var mapped = rights & ~(GenericRead | GenericWrite | GenericExecute | GenericAll);
        if ((rights & GenericRead) != 0) { mapped |= 0x00120089; }
        if ((rights & GenericWrite) != 0) { mapped |= 0x00120116; }
        if ((rights & GenericExecute) != 0) { mapped |= 0x001200A0; }
        if ((rights & GenericAll) != 0) { mapped |= 0x001F01FF; }
        return mapped;
    }

    internal static bool HasUntrustedMutation(IEnumerable<SequenceCustodyAccessRule> rules, uint dangerous = FileMutation)
    {
        // Evaluate ordered ACEs for each untrusted SID. Cross-group deny overlap is
        // conservatively rejected rather than assuming unknown group membership.
        var ordered = rules.Where(x => !x.InheritOnly).ToArray();
        foreach (var sid in ordered.Where(x => !x.Trusted && !x.Deny).Select(x => x.Sid).Distinct(StringComparer.Ordinal))
        {
            uint resolved = 0;
            foreach (var rule in ordered.Where(x => x.Sid == sid))
            {
                var effective = rule.Rights & dangerous & ~resolved;
                if (!rule.Deny && effective != 0) { return true; }
                resolved |= effective;
            }
        }
        return false;
    }
}

internal readonly record struct SequenceCustodyAccessRule(string Sid, uint Rights, bool Deny, bool Trusted, bool InheritOnly = false);

// Deliberately internal: normal DI always constructs WindowsSequenceAuthorityCustody.
// Tests must supply an owned directory, not redirect production custody via an environment variable.
internal sealed class OwnedSequenceAuthorityCustody : ISequenceAuthorityCustody
{
    private readonly string _root;
    public OwnedSequenceAuthorityCustody(string root)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!_root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || _root.Length <= temp.Length)
        {
            throw new InvalidOperationException("Sequence fixture must have an owned temporary root.");
        }
        ValidateDirectory(_root);
        if (OperatingSystem.IsWindows())
        {
            var owner = new DirectoryInfo(_root).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            if (owner is not SecurityIdentifier sid || WindowsIdentity.GetCurrent().User is not SecurityIdentifier currentUser || !sid.Equals(currentUser))
            {
                throw new InvalidOperationException("Sequence fixture root is not owned by the current test identity.");
            }
        }
    }
    public void ValidateDirectory(string directory)
    {
        RequireOwned(directory);
        for (DirectoryInfo? current = new(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            WindowsSequenceAuthorityCustody.RequireRegular(current.FullName, directory: true);
        }
    }
    public void ValidateFile(string path)
    {
        RequireOwned(path);
        ValidateDirectory(Path.GetDirectoryName(path)!);
        WindowsSequenceAuthorityCustody.RequireRegular(path, directory: false);
    }
    private void RequireOwned(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.Equals(_root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Sequence fixture escaped its owned root.");
        }
    }
}
