import os
import re

handlers = [
    ("CompleteSaleCommand", "src/EdgeRetails.Application/Features/Sales/CompleteSaleHandler.cs"),
    ("ProcessSaleReturnCommand", "src/EdgeRetails.Application/Features/Sales/SaleReturnHandler.cs"),
    ("ProcessCommercialExchangeCommand", "src/EdgeRetails.Application/Features/Sales/CommercialExchangeHandler.cs"),
    ("SavePosDraftCommand", "src/EdgeRetails.Application/Features/Sales/PosDraftHandlers.cs"),
    ("CreatePurchaseCommand", "src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs"),
    ("ProcessPurchaseReturnCommand", "src/EdgeRetails.Application/Features/Purchasing/PurchaseReturnHandler.cs"),
    ("ReceiveProductIntakeCommand", "src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs"),
    ("VoidPurchaseCommand", "src/EdgeRetails.Application/Features/Purchasing/VoidPurchaseHandler.cs"),
    ("AdjustStockCommand", "src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs"),
    ("ReconcileStocktakeCommand", "src/EdgeRetails.Application/Features/Inventory/StocktakeHandlers.cs"),
    ("AdmitFoundUnitCommand", "src/EdgeRetails.Application/Features/Inventory/FoundInventoryUnitHandler.cs"),
    ("OpenCashSessionCommand", "src/EdgeRetails.Application/Features/Finance/CashSessionHandlers.cs"),
    ("RecordExpenseCommand", "src/EdgeRetails.Application/Features/Finance/ExpenseHandlers.cs"),
    ("RecordSupplierPaymentCommand", "src/EdgeRetails.Application/Features/Finance/SupplierAccountHandlers.cs"),
    ("AcceptThakaCustodyCommand", "src/EdgeRetails.Application/Features/Thaka/ThakaHandlers.cs"),
    ("InitiateWarrantyClaimCommand", "src/EdgeRetails.Application/Features/Warranty/WarrantyHandlers.cs"),
]

for cmd_name, rel_path in handlers:
    full_path = os.path.abspath(rel_path)
    if not os.path.exists(full_path):
        print(f"MISSING: {cmd_name} in {rel_path}")
        continue

    with open(full_path, "r", encoding="utf-8") as f:
        lines = f.readlines()

    content = "".join(lines)

    # Check command declaration
    cmd_decl_line = -1
    for i, line in enumerate(lines):
        if cmd_name in line and ("record" in line or "class" in line):
            cmd_decl_line = i + 1
            break

    # Check ClientOperationId
    has_op_id = "ClientOperationId" in content

    # Check IOperationOutcomeLedger
    has_ledger = "IOperationOutcomeLedger" in content
    ledger_optional = "IOperationOutcomeLedger?" in content

    # Check Transaction
    has_tx_runner = "_transactions.ExecuteAsync" in content or "ITransactionRunner" in content
    has_uow = "_unitOfWork.SaveChangesAsync" in content

    # Check locks
    has_op_lock = "_operationLock" in content or "IOperationLock" in content
    has_res_lock = "_resourceLock" in content or "IResourceLock" in content

    print(f"[{cmd_name}] {rel_path} (decl line {cmd_decl_line})")
    print(f"  ClientOperationId: {has_op_id}")
    print(f"  IOperationOutcomeLedger: {has_ledger} (Optional: {ledger_optional})")
    print(f"  TransactionRunner: {has_tx_runner}, UnitOfWork: {has_uow}")
    print(f"  OperationLock: {has_op_lock}, ResourceLock: {has_res_lock}")
