using EdgeRetails.Domain.Catalog;

namespace EdgeRetails.Application.Features.Sales;

public static class ProductPackBehaviorAuthority
{
    public static bool IsCableOrLengthProduct(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);

        if (product.TrackingMode == TrackingMode.Length)
        {
            return true;
        }

        if (product.TrackingMode != TrackingMode.Container)
        {
            return false;
        }

        return product.Name.Contains("Cable", StringComparison.OrdinalIgnoreCase) ||
               product.Name.Contains("Wire", StringComparison.OrdinalIgnoreCase) ||
               product.AttributesJson?.Contains("Length", StringComparison.OrdinalIgnoreCase) == true;
    }
}
