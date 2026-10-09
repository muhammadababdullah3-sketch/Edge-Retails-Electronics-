using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace EdgeRetails.UnitTests;

public sealed class TrackingArchitectureDriftTests
{
    [Fact]
    public void ProductionAssemblies_HaveOnePhysicalConstructorAndTrackingCodeAllocator()
    {
        var calls = ReadProductionCalls().ToArray();
        var constructors = calls.Where(x => x.OpCode == OpCodes.Newobj && x.TargetType == "EdgeRetails.Domain.Inventory.InventoryUnit").ToArray();
        Assert.Single(constructors);
        Assert.Equal("EdgeRetails.Infrastructure.Services.PhysicalUnitCreationAuthority", constructors[0].Owner);
        var generators = calls.Where(x => x.TargetName == "BuildTrackingCode" && x.TargetType == "EdgeRetails.Domain.Catalog.TraceabilityCodeRules").ToArray();
        Assert.Single(generators);
        Assert.Equal("EdgeRetails.Infrastructure.Services.PhysicalUnitCreationAuthority", generators[0].Owner);
        Assert.All(calls.Where(x => x.TargetType == "EdgeRetails.Domain.Inventory.InventoryUnit" &&
            x.TargetName is "set_TrackingCode" or "set_ItemSequence" or "set_ProductSkuSnapshot" or "set_SupplierCodeSnapshot"),
            x => Assert.Equal("EdgeRetails.Infrastructure.Services.PhysicalUnitCreationAuthority", x.Owner));
    }

    [Fact]
    public void ManufacturerNormalizationWrappers_DoNotImplementAnotherUnicodeNormalizer()
    {
        var forbidden = ReadProductionCalls().Where(x =>
            (x.Method.Contains("Serial", StringComparison.OrdinalIgnoreCase) || x.Method.Contains("Imei", StringComparison.OrdinalIgnoreCase)) &&
            x.Method.Contains("Normaliz", StringComparison.OrdinalIgnoreCase) &&
            x.TargetType == "System.String" && x.TargetName == "Normalize" &&
            x.Owner != "EdgeRetails.Domain.Catalog.IdentityNormalizationRules");
        Assert.Empty(forbidden);
    }

    [Fact]
    public void SequenceWritesOutsideAuthority_AreOnlyInitialValueOrExistingHighWaterRecovery()
    {
        var setters = ReadProductionCalls().Where(x => x.TargetType == "EdgeRetails.Domain.Catalog.SupplierProduct" && x.TargetName == "set_NextItemSequence");
        foreach (var call in setters)
        {
            if (call.Owner is "EdgeRetails.Infrastructure.Services.PhysicalUnitCreationAuthority" or "EdgeRetails.Infrastructure.Services.MachineSequenceHighWaterService")
            {
                continue;
            }
            // AUTHORIZED_ASSERTION_ALIGNMENT: atomic aggregate now initializes the
            // same new pair at 1. The exact constant IL invariant below is retained.
            Assert.Contains(call.Owner, new[] { "EdgeRetails.Application.Features.Purchasing.CreatePurchaseHandler", "EdgeRetails.Application.Features.Catalog.SetSupplierProductActiveHandler", "EdgeRetails.Application.Features.Catalog.SaveProductAggregateHandler" });
            Assert.Equal(OpCodes.Conv_I8, call.Previous);
            Assert.Equal(OpCodes.Ldc_I4_1, call.BeforePrevious);
        }
    }

    private static IEnumerable<Call> ReadProductionCalls()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "EdgeRetails.sln")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root);
        var configuration = AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "Release" : "Debug";
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(x => (OpCode)x.GetValue(null)!).ToDictionary(x => unchecked((ushort)x.Value));
        foreach (var project in Directory.EnumerateDirectories(Path.Combine(root.FullName, "src"), "EdgeRetails.*")
            .Where(x => File.Exists(Path.Combine(x, Path.GetFileName(x) + ".csproj"))))
        {
            var name = Path.GetFileName(project);
            var framework = name is "EdgeRetails.Desktop" or "EdgeRetails.Recovery" ? "net10.0-windows" : "net10.0";
            var path = Path.Combine(project, "bin", configuration, framework, name + ".dll");
            Assert.True(File.Exists(path), "Build the solution before architecture certification: " + path);
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.MethodDefinitions)
            {
                var method = metadata.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }
                var owner = RootName(metadata, method.GetDeclaringType());
                var methodName = metadata.GetString(method.Name);
                var bytes = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                var offset = 0;
                var previous = default(OpCode);
                var beforePrevious = default(OpCode);
                while (offset < bytes.Length)
                {
                    ushort value = bytes[offset++];
                    if (value == 0xFE)
                    {
                        value = (ushort)(0xFE00 | bytes[offset++]);
                    }
                    var opcode = opcodes[value];
                    if (opcode.OperandType == OperandType.InlineMethod)
                    {
                        var token = BitConverter.ToInt32(bytes, offset);
                        var target = MethodName(metadata, MetadataTokens.EntityHandle(token));
                        yield return new Call(owner, methodName, target.Type, target.Name, opcode, previous, beforePrevious);
                    }
                    offset += opcode.OperandType switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                        _ => 4
                    };
                    beforePrevious = previous;
                    previous = opcode;
                }
            }
        }
    }

    private static (string Type, string Name) MethodName(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.MethodSpecification)
        {
            return MethodName(reader, reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
        }
        if (handle.Kind == HandleKind.MethodDefinition)
        {
            var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
            return (RootName(reader, method.GetDeclaringType()), reader.GetString(method.Name));
        }
        var member = reader.GetMemberReference((MemberReferenceHandle)handle);
        var type = member.Parent.Kind switch
        {
            HandleKind.TypeDefinition => RootName(reader, (TypeDefinitionHandle)member.Parent),
            HandleKind.TypeReference => ReferenceName(reader, (TypeReferenceHandle)member.Parent),
            _ => "<generic>"
        };
        return (type, reader.GetString(member.Name));
    }

    private static string RootName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        return type.GetDeclaringType().IsNil
            ? reader.GetString(type.Namespace) + "." + reader.GetString(type.Name)
            : RootName(reader, type.GetDeclaringType());
    }

    private static string ReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
    }

    private sealed record Call(string Owner, string Method, string TargetType, string TargetName, OpCode OpCode, OpCode Previous, OpCode BeforePrevious);
}
