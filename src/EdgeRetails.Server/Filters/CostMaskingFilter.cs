using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EdgeRetails.Application.Contracts.Packs;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EdgeRetails.Server.Filters;

/// <summary>
/// Server DTO transformation filter that redacts wholesale acquisition costs,
/// purchase reference costs, and remnant unit costs for Cashier sessions.
/// Privileged roles (Supervisor, Manager, Owner, Admin) retain full cost visibility.
/// Recursively traverses response envelopes, lists, dictionaries, and nested objects (MON-W3-005).
/// </summary>
public sealed class CostMaskingFilter : IAsyncResultFilter
{
    private static readonly HashSet<string> SensitiveCostPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnitCost",
        "AcquisitionCost",
        "ReferencePurchaseCost",
        "CarryingCost",
        "WholesaleCost",
        "PurchaseCost",
        "CostPrice",
        "Margin",
        "MarginPercentage",
        "CostBasis",
        "UnitCostSnapshot",
        "TotalCostSnapshot",
        "RemnantCost",
        "RemnantUnitCost"
    };

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var actor = context.HttpContext.GetActorContext();
        var isCashier = actor is not null && string.Equals(actor.RoleName, "Cashier", StringComparison.OrdinalIgnoreCase);
        var isPrivileged = actor is not null && (
            string.Equals(actor.RoleName, "Supervisor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actor.RoleName, "Manager", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actor.RoleName, "Owner", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actor.RoleName, "Admin", StringComparison.OrdinalIgnoreCase) ||
            actor.Permissions.Contains(PermissionKeys.InventoryManage) ||
            actor.Permissions.Contains(PermissionKeys.PurchasingManage));

        if (isCashier || !isPrivileged)
        {
            if (context.Result is ObjectResult objectResult && objectResult.Value is not null)
            {
                objectResult.Value = MaskValue(objectResult.Value, new HashSet<object>(ReferenceEqualityComparer.Instance));
            }
        }

        await next();
    }

    public static object MaskValue(object? value, HashSet<object>? visited = null)
    {
        if (value is null)
        {
            return null!;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
            type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) ||
            type == typeof(TimeOnly) || type == typeof(Guid))
        {
            return value;
        }

        visited ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (!visited.Add(value))
        {
            return value;
        }

        // Fast paths for known canonical records
        if (value is ProductManagementRowDto product)
        {
            return product with { ReferencePurchaseCost = null };
        }
        if (value is PackContainerDto pack)
        {
            return pack with { UnitCost = null };
        }
        if (value is ExactInventoryUnitDto exactUnit)
        {
            return exactUnit with { AcquisitionCost = 0m };
        }
        if (value is PackContainerQueryResponse packQuery)
        {
            return packQuery with { Items = packQuery.Items.Select(p => p with { UnitCost = null }).ToList() };
        }

        // Collections & Enumerable
        if (value is IDictionary dict)
        {
            var keys = dict.Keys.Cast<object>().ToList();
            foreach (var k in keys)
            {
                dict[k] = MaskValue(dict[k], visited);
            }
            return dict;
        }

        if (value is IList list && !list.IsReadOnly)
        {
            for (int i = 0; i < list.Count; i++)
            {
                list[i] = MaskValue(list[i], visited);
            }
            return list;
        }

        if (value is IEnumerable enumerable)
        {
            var items = new List<object?>();
            foreach (var item in enumerable)
            {
                items.Add(MaskValue(item, visited));
            }
            return items;
        }

        // Recursive property traversal on general objects and records
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in props)
        {
            if (!prop.CanRead)
            {
                continue;
            }

            if (IsSensitiveCostProperty(prop.Name))
            {
                if (prop.CanWrite)
                {
                    if (prop.PropertyType == typeof(decimal?))
                    {
                        prop.SetValue(value, null);
                    }
                    else if (prop.PropertyType == typeof(decimal))
                    {
                        prop.SetValue(value, 0m);
                    }
                }
            }
            else if (!prop.PropertyType.IsPrimitive && !prop.PropertyType.IsEnum &&
                     prop.PropertyType != typeof(string) && prop.PropertyType != typeof(decimal) &&
                     prop.PropertyType != typeof(DateTime) && prop.PropertyType != typeof(DateTimeOffset) &&
                     prop.PropertyType != typeof(Guid))
            {
                try
                {
                    var propVal = prop.GetValue(value);
                    if (propVal is not null)
                    {
                        var maskedProp = MaskValue(propVal, visited);
                        if (prop.CanWrite && !ReferenceEquals(propVal, maskedProp))
                        {
                            prop.SetValue(value, maskedProp);
                        }
                    }
                }
                catch
                {
                    // Ignore inaccessible or indexing properties
                }
            }
        }

        return value;
    }

    private static bool IsSensitiveCostProperty(string name)
    {
        return SensitiveCostPropertyNames.Contains(name);
    }
}
