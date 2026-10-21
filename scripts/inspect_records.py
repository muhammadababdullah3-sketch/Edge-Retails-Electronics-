import os
import re

files = [
    "src/EdgeRetails.Application/Features/Sales/SaleReturnHandler.cs",
    "src/EdgeRetails.Application/Features/Sales/CommercialExchangeHandler.cs",
    "src/EdgeRetails.Application/Features/Purchasing/PurchaseReturnHandler.cs",
    "src/EdgeRetails.Application/Features/Inventory/StocktakeHandlers.cs",
    "src/EdgeRetails.Application/Features/Inventory/FoundInventoryUnitHandler.cs",
    "src/EdgeRetails.Application/Features/Finance/ExpenseHandlers.cs",
    "src/EdgeRetails.Application/Features/Finance/SupplierAccountHandlers.cs",
    "src/EdgeRetails.Application/Features/Thaka/ThakaHandlers.cs",
    "src/EdgeRetails.Application/Features/Warranty/WarrantyHandlers.cs",
]

for p in files:
    full_path = os.path.abspath(p)
    with open(full_path, "r", encoding="utf-8") as f:
        text = f.read()

    print(f"\n==========================================")
    print(f"File: {p}")
    records = re.findall(r"public\s+(?:sealed\s+)?record\s+([A-Za-z0-9_]+)", text)
    classes = re.findall(r"public\s+(?:sealed\s+)?class\s+([A-Za-z0-9_]+)", text)
    print("Records:", records)
    print("Classes:", classes)
