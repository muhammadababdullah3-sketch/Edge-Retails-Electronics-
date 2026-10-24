import os
import re

handlers = [
    ("src/EdgeRetails.Application/Features/Sales/CompleteSaleHandler.cs", "CompleteSaleCommand"),
    ("src/EdgeRetails.Application/Features/Sales/SaleReturnHandler.cs", "ProcessSaleReturnCommand"),
    ("src/EdgeRetails.Application/Features/Sales/CommercialExchangeHandler.cs", "ProcessCommercialExchangeCommand"),
    ("src/EdgeRetails.Application/Features/Sales/PosDraftHandlers.cs", "SavePosDraftCommand"),
    ("src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs", "CreatePurchaseCommand"),
    ("src/EdgeRetails.Application/Features/Purchasing/PurchaseReturnHandler.cs", "ProcessPurchaseReturnCommand"),
    ("src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs", "ReceiveProductIntakeCommand"),
    ("src/EdgeRetails.Application/Features/Purchasing/VoidPurchaseHandler.cs", "VoidPurchaseCommand"),
    ("src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs", "CreateStockAdjustmentCommand"),
    ("src/EdgeRetails.Application/Features/Inventory/StocktakeHandlers.cs", "ReconcileStocktakeCommand"),
    ("src/EdgeRetails.Application/Features/Inventory/FoundInventoryUnitHandler.cs", "AdmitFoundUnitCommand"),
    ("src/EdgeRetails.Application/Features/Finance/CashSessionHandlers.cs", "OpenCashSessionCommand"),
    ("src/EdgeRetails.Application/Features/Finance/ExpenseHandlers.cs", "RecordExpenseCommand"),
    ("src/EdgeRetails.Application/Features/Finance/SupplierAccountHandlers.cs", "RecordSupplierPaymentCommand"),
    ("src/EdgeRetails.Application/Features/Thaka/ThakaHandlers.cs", "AcceptThakaCustodyCommand"),
    ("src/EdgeRetails.Application/Features/Warranty/WarrantyHandlers.cs", "InitiateWarrantyClaimCommand"),
]

for path, cmd in handlers:
    full_path = os.path.abspath(path)
    with open(full_path, "r", encoding="utf-8") as f:
        text = f.read()

    # find record definition
    m = re.search(r"public\s+sealed\s+record\s+([A-Za-z0-9_]+)\s*\((.*?)\);", text, re.DOTALL)
    if not m:
        # try without sealed
        m = re.search(r"public\s+record\s+([A-Za-z0-9_]+)\s*\((.*?)\);", text, re.DOTALL)

    print(f"\n=== {cmd} in {path} ===")
    # find where cmd is defined
    all_records = re.findall(r"public\s+(?:sealed\s+)?record\s+([A-Za-z0-9_]+)\s*\((.*?)\);", text, re.DOTALL)
    found = False
    for rname, rparams in all_records:
        if rname == cmd or cmd in rname:
            found = True
            params = [p.strip() for p in rparams.split(",") if p.strip()]
            print(f"Record: {rname}")
            for p in params:
                print(f"  - {p}")
            break
    if not found:
        print("  Could not find record regex; searching for lines with name:")
        for line in text.split("\n"):
            if cmd in line:
                print(f"  {line.strip()}")
