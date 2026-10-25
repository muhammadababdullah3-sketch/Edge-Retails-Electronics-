using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Catalog;
using Xunit;

namespace EdgeRetails.UnitTests.Phase11;

public sealed class ProductPackBehaviorAuthorityTests
{
    [Fact]
    public void Cable_name_container_is_length_or_cable()
    {
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Cat6 Copper Cable",
            Sku = "CAT6",
            TrackingMode = TrackingMode.Container,
            IsActive = true
        };

        Assert.True(ProductPackBehaviorAuthority.IsCableOrLengthProduct(product));
    }

    [Fact]
    public void Count_pack_container_without_cable_hint_is_not_cable()
    {
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Hardware Box 12pk",
            Sku = "BOX12",
            TrackingMode = TrackingMode.Container,
            IsActive = true
        };

        Assert.False(ProductPackBehaviorAuthority.IsCableOrLengthProduct(product));
    }
}
