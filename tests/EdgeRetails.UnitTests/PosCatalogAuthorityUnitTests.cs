using EdgeRetails.Application.Features.Sales;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class PosCatalogAuthorityUnitTests
{
    [Fact]
    public void PosCatalogProductDto_PreservesAuthoritativeBrand_AndNullableSemantics()
    {
        var productWithBrand = new PosCatalogProductDto(
            Guid.NewGuid(), Guid.NewGuid(), "Samsung Galaxy S24", "SM-S921B", "Mobile Phones",
            "Pcs", 10m, 250000m, 220000m, true, Brand: "Samsung");

        var productWithoutBrand = new PosCatalogProductDto(
            Guid.NewGuid(), Guid.NewGuid(), "Generic USB Cable", "USB-01", "Accessories",
            "Pcs", 50m, 500m, 300m, false, Brand: null);

        Assert.Equal("Samsung", productWithBrand.Brand);
        Assert.Null(productWithoutBrand.Brand);
    }
}
