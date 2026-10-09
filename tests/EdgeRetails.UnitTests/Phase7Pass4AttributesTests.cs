using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;

namespace EdgeRetails.UnitTests;

// NEW_COVERAGE: optional typed specifications, explicit extensions, legacy compatibility.
public sealed class Phase7Pass4AttributesTests
{
    [Theory]
    [InlineData("{}", 1)]
    [InlineData("{\"legacy_custom\":\"old value\",\"wattage\":\"legacy text\"}", 1)]
    [InlineData("{\"profile\":\"fan\",\"sweep\":0.01,\"blade_count\":1,\"wattage\":0.01}", 2)]
    [InlineData("{\"profile\":\"fan\",\"sweep\":10000,\"blade_count\":100,\"wattage\":100000}", 2)]
    [InlineData("{\"profile\":\"cable\",\"cores\":1000,\"conductor_cross_section\":10000,\"roll_length\":1000000}", 2)]
    [InlineData("{\"profile\":\"bulb\",\"color_temperature\":1000,\"socket_type\":\"E27\"}", 2)]
    [InlineData("{\"profile\":\"bulb\",\"color_temperature\":20000}", 2)]
    [InlineData("{\"x_vendor_note\":null,\"x_feature\":true}", 2)]
    public void ValidOptionalProfilesAndLegacyRemainEditable(string json, int version) => AttributesPolicy.Validate(json, version);

    [Theory]
    [InlineData("{}", 3, "catalog.attributes_schema_version_unsupported")]
    [InlineData("{\"ratedVoltage\":\"banana\"}", 2, "catalog.attributes_type_invalid")]
    [InlineData("{\"wattage\":true}", 2, "catalog.attributes_type_invalid")]
    [InlineData("{\"unknown\":12}", 2, "catalog.attributes_key_unknown")]
    [InlineData("{\"wattage\":0}", 2, "catalog.attributes_range_invalid")]
    [InlineData("{\"wattage\":100000.01}", 2, "catalog.attributes_range_invalid")]
    [InlineData("{\"blade_count\":1.5}", 2, "catalog.attributes_range_invalid")]
    [InlineData("{\"color_temperature\":999}", 2, "catalog.attributes_range_invalid")]
    [InlineData("{\"color_temperature\":20001}", 2, "catalog.attributes_range_invalid")]
    [InlineData("{\"profile\":\"cable\",\"sweep\":1200}", 2, "catalog.attributes_profile_mismatch")]
    [InlineData("{\"profile\":\"fan\",\"socket_type\":\"E27\"}", 2, "catalog.attributes_profile_mismatch")]
    [InlineData("{\"profile\":\"unknown\"}", 2, "catalog.attributes_profile_invalid")]
    [InlineData("{\"profile\":12}", 2, "catalog.attributes_type_invalid")]
    [InlineData("{\" profile \":12}", 2, "catalog.attributes_type_invalid")]
    [InlineData("{\"wattage\":12,\"WATTAGE\":13}", 2, "catalog.attributes_duplicate_key")]
    [InlineData("{\"legacy\":1,\"legacy\":2}", 1, "catalog.attributes_duplicate_key")]
    [InlineData("{\"x_note\":[]}", 2, "catalog.attributes_structure_invalid")]
    [InlineData("{\"color\":{}}", 1, "catalog.attributes_structure_invalid")]
    [InlineData("{\"wattage\":}", 2, "catalog.attributes_json_malformed")]
    public void InvalidSpecificationsFailControlled(string json, int version, string code)
    {
        Assert.Equal(code, Assert.Throws<BusinessRuleException>(() => AttributesPolicy.Validate(json, version)).Code);
    }

    [Theory]
    [InlineData(TrackingMode.Serialized, false, false, false)]
    [InlineData(TrackingMode.Serialized, true, false, true)]
    [InlineData(TrackingMode.Serialized, false, true, true)]
    [InlineData(TrackingMode.IndividualPiece, false, false, true)]
    [InlineData(TrackingMode.Container, false, false, true)]
    [InlineData(TrackingMode.Quantity, true, false, false)]
    [InlineData(TrackingMode.Length, false, true, false)]
    public void IdentityVisibilityDoesNotImplyMandatoryManufacturerOverlay(TrackingMode mode, bool serial, bool imei, bool valid)
    {
        var product = new Product { TrackingMode = mode, SerialTrackingEnabled = serial, ImeiTrackingEnabled = imei };
        if (valid)
        {
            product.ValidateTrackingPolicy();
        }
        else
        {
            Assert.Throws<BusinessRuleException>(product.ValidateTrackingPolicy);
        }
    }
}
