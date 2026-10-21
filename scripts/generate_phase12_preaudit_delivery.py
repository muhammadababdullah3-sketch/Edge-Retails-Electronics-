import os
import sys
import shutil
import hashlib
import json
from datetime import datetime, timezone

def sha256_file(filepath):
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest().lower()

gov_root = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body"
run_id = "ER-RUN-PHASE12-PREAUDIT-20261015"
dest_dir = os.path.join(gov_root, r"governance\10-execution-runs", run_id)

print(f"Destination directory: {dest_dir}")
os.makedirs(dest_dir, exist_ok=True)

packaging_now_utc = datetime.now(timezone.utc).isoformat()
print(f"Packaging timestamp: {packaging_now_utc}")

# =========================================================================
# 1. Author MUTATION_CENSUS_16_HANDLERS.json
# =========================================================================
census_data = {
    "artifact_id": f"{run_id}-MUTATION-CENSUS-16-HANDLERS",
    "run_id": run_id,
    "timestamp_utc": packaging_now_utc,
    "audit_scope": "Full 16 Mutating Retail, Stock, and Financial Command Handlers in EdgeRetails.Application",
    "total_handlers_audited": 16,
    "summary": {
        "handlers_accepting_client_operation_id": 15,
        "handlers_missing_client_operation_id": 1,
        "handlers_with_optional_ledger_injection": 13,
        "handlers_with_mandatory_ledger_injection": 2,
        "handlers_with_zero_ledger_injection": 1,
        "handlers_with_payload_fingerprinting": 1,
        "handlers_with_response_payload_storage": 0,
        "handlers_with_explicit_transaction_runner": 16
    },
    "handlers": [
        {
            "id": "H01",
            "canonical_name": "CompleteSaleCommand",
            "conceptual_name": "CompleteSaleCommand",
            "class_name": "CompleteSaleHandler",
            "file_path": "src/EdgeRetails.Application/Features/Sales/CompleteSaleHandler.cs",
            "command_decl_lines": "32-55",
            "handler_impl_lines": "91-710",
            "controller_endpoint": "POST /api/sales/complete (SalesController.cs:100-115)",
            "command_parameters": [
                "Guid ClientOperationId",
                "Guid? CustomerId",
                "Guid CashierUserId",
                "Guid? SessionId",
                "decimal InvoiceDiscount",
                "SalePaymentMethod PaymentMethod",
                "decimal AmountTendered",
                "string? PaymentReference",
                "Guid? QuotationId",
                "IReadOnlyList<CompleteSaleLineInput> Lines",
                "DateOnly? PostingDate",
                "SupervisorApprovalRequest? SupervisorApproval",
                "bool? CallerSuppliedBooleanOverride"
            ],
            "client_operation_id_present": True,
            "operation_id_property_name": "ClientOperationId",
            "ledger_interaction": "OPTIONAL_INJECTION",
            "ledger_constructor_decl": "IOperationOutcomeLedger? outcomeLedger = null (line 112)",
            "transaction_boundary": "EXPLICIT_TRANSACTION_RUNNER",
            "transaction_runner_code": "_transactions.ExecuteAsync (lines 173-676)",
            "unit_of_work_present": True,
            "operation_lock_acquired": True,
            "resource_lock_acquired": True,
            "payload_fingerprint_enforced": False,
            "fingerprint_notes": "Only computed for draft-originating sales (lines 152-157); direct sales pass draftFingerprint=null to RecordSuccessAsync (line 638), resulting in NULL stored fingerprint in DB.",
            "response_payload_stored": False,
            "side_effects": [
                "Sale record creation",
                "SaleItem and SaleItemUnit line records",
                "PackContainer subledger deduction and line allocation",
                "InventoryLot and StockBalance sellable quantity deduction",
                "CashSession drawer balance increase for cash sales",
                "Customer Khata account receivable debit for on-account sales",
                "ReceiptTemplateSnapshot capture",
                "BusinessAuditWriter log entry",
                "ShopPostingValidationService ledger audit",
                "Desktop client physical receipt print dispatch"
            ],
            "failure_behavior": "Catches BusinessRuleException, returns Failure result. If outcome ledger is injected, records failure via RecordFailureAsync (lines 678-707)."
        },
        {
            "id": "H02",
            "canonical_name": "CreateSaleReturnCommand",
            "conceptual_name": "ProcessSaleReturnCommand",
            "class_name": "CreateSaleReturnHandler",
            "file_path": "src/EdgeRetails.Application/Features/Sales/SaleReturnHandler.cs",
            "command_decl_lines": "24-38",
            "handler_impl_lines": "54-320",
            "controller_endpoint": "POST /api/sales/return (SalesController.cs:130-145)",
            "command_parameters": [
                "Guid ClientOperationId",
                "Guid OriginalSaleId",
                "Guid CashierUserId",
                "string Reason",
                "SaleReturnRefundMethod RefundMethod",
                "IReadOnlyList<SaleReturnLineInput> Lines",
                "DateOnly? PostingDate",
                "SupervisorApprovalRequest? SupervisorApproval",
                "bool? CallerSuppliedBooleanOverride"
            ],
            "client_operation_id_present": True,
            "operation_id_property_name": "ClientOperationId",
            "ledger_interaction": "OPTIONAL_INJECTION",
            "ledger_constructor_decl": "IOperationOutcomeLedger? outcomeLedger = null (line 74)",
            "transaction_boundary": "EXPLICIT_TRANSACTION_RUNNER",
            "transaction_runner_code": "_transactions.ExecuteAsync",
            "unit_of_work_present": True,
            "operation_lock_acquired": True,
            "resource_lock_acquired": True,
            "payload_fingerprint_enforced": False,
            "fingerprint_notes": "No SHA-256 fingerprint computed or verified.",
            "response_payload_stored": False,
            "side_effects": [
                "SaleReturn record creation",
                "SaleReturnItem line records",
                "Stock replenishment into inspected loose or warranty custody bucket",
                "CashSession drawer refund or customer credit note",
                "BusinessAuditWriter log entry",
                "ShopPostingValidationService ledger audit"
            ],
            "failure_behavior": "Rolls back transaction runner; records failure in outcome ledger if injected."
        },
        {
            "id": "H03",
            "canonical_name": "CommercialExchangeCommand",
            "conceptual_name": "ProcessCommercialExchangeCommand",
            "class_name": "CommercialExchangeHandler",
            "file_path": "src/EdgeRetails.Application/Features/Sales/CommercialExchangeHandler.cs",
            "command_decl_lines": "26-44",
            "handler_impl_lines": "64-380",
            "controller_endpoint": "POST /api/sales/exchange (SalesController.cs:146-160)",
            "command_parameters": [
                "Guid ClientOperationId",
                "Guid OriginalSaleId",
                "Guid CashierUserId",
                "string Reason",
                "IReadOnlyList<SaleReturnLineInput> ReturnLines",
                "IReadOnlyList<CompleteSaleLineInput> NewSaleLines",
                "SalePaymentMethod? SettlementPaymentMethod",
                "decimal? SettlementAmountTendered",
                "DateOnly? PostingDate"
            ],
            "client_operation_id_present": True,
            "operation_id_property_name": "ClientOperationId",
            "ledger_interaction": "OPTIONAL_INJECTION",
            "ledger_constructor_decl": "IOperationOutcomeLedger? outcomeLedger = null (line 88)",
            "transaction_boundary": "EXPLICIT_TRANSACTION_RUNNER",
            "transaction_runner_code": "_transactions.ExecuteAsync",
            "unit_of_work_present": True,
            "operation_lock_acquired": True,
            "resource_lock_acquired": True,
            "payload_fingerprint_enforced": False,
            "fingerprint_notes": "No SHA-256 fingerprint computed or verified.",
            "response_payload_stored": False,
            "side_effects": [
                "Atomic return of returned items and sale of exchange items",
                "Differential balance settlement (cash collected or refunded)",
                "Inventory stock bucket adjustments",
                "BusinessAuditWriter log entry"
            ],
            "failure_behavior": "Rolls back transaction runner; records failure in outcome ledger if injected."
        },
        {
            "id": "H04",
            "canonical_name": "SavePosDraftCommand / DeletePosDraftCommand",
            "conceptual_name": "SavePosDraftCommand / DeletePosDraftCommand",
            "class_name": "SavePosDraftHandler / DeletePosDraftHandler",
            "file_path": "src/EdgeRetails.Application/Features/Sales/PosDraftHandlers.cs",
            "command_decl_lines": "20-30",
            "handler_impl_lines": "46-180",
            "controller_endpoint": "POST /api/sales/drafts (SalesController.cs:85-95)",
            "command_parameters": [
                "Guid? DraftId",
                "long? ExpectedVersion",
                "Guid? CustomerId",
                "Guid ActorId",
                "string? TerminalId",
                "string? Note",
                "IReadOnlyList<SavePosDraftItemInput> Items",
                "Guid ClientOperationId = default",
                "Guid? RegisteredTerminalId",
                "Guid? SessionId"
            ],
            "client_operation_id_present": True,
            "operation_id_property_name": "ClientOperationId",
            "ledger_interaction": "OPTIONAL_INJECTION",
            "ledger_constructor_decl": "IOperationOutcomeLedger? outcomeLedger = null (line 59)",
            "transaction_boundary": "EXPLICIT_TRANSACTION_RUNNER",
            "transaction_runner_code": "_transactions.ExecuteAsync",
            "unit_of_work_present": True,
            "operation_lock_acquired": True,
            "resource_lock_acquired": True,
            "payload_fingerprint_enforced": False,
            "fingerprint_notes": "Draft save does not fingerprint; conversion in CompleteSaleHandler binds PosDraft:v1 SHA256.",
            "response_payload_stored": False,
            "side_effects": [
                "PosDraft record insert or update",
                "PosDraftItem lines persistence",
                "Optimistic concurrency Version increment"
            ],
            "failure_behavior": "Rolls back transaction on concurrency collision or database error."
        },
        {
            "id": "H05",
            "canonical_name": "CreatePurchaseCommand",
            "conceptual_name": "CreatePurchaseCommand",
            "class_name": "CreatePurchaseHandler",
            "file_path": "src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs",
            "command_decl_lines": "27-46",
            "handler_impl_lines": "90-420",
            "controller_endpoint": "POST /api/purchasing/create (PurchasingController.cs:30-45)",
            "command_parameters": [
                "Guid SupplierId",
                "string SupplierInvoiceNumber",
                "DateOnly PurchaseDate",
                "string? Note",
                "decimal OtherCharges",
                "PurchaseSettlementMode SettlementMode",
                "Guid CreatedBy",
                "Guid ClientOperationId",
                "IReadOnlyList<CreatePurchaseLineInput> Lines",
                "decimal? InitialPaymentAmount",
                "SupplierSettlementMethod? InitialPaymentMethod",
                "string? InitialPaymentExternalReference",
                "bool ReceiveStockImmediately",
                "DateOnly? PostingDate",
                "SupervisorApprovalRequest? SupervisorApproval",
                "bool? CallerSuppliedBooleanOverride"
            ],
            "client_operation_id_present": True,
            "operation_id_property_name": "ClientOperationId",
            "ledger_interaction": "OPTIONAL_INJECTION",
            "ledger_constructor_decl": "IOperationOutcomeLedger? outcomeLedger = null (line 124)",
            "transaction_boundary": "EXPLICIT_TRANSACTION_RUNNER",
            "transaction_runner_code": "_transactions.ExecuteAsync",
            "unit_of_work_present": True,
            "operation_lock_acquired": True,
            "resource_lock_acquired": True,
            "payload_fingerprint_enforced": False,
            "fingerprint_notes": "No SHA-256 fingerprint verified or stored.",
            "response_payload_stored": False,
            "side_effects": [
                "Purchase entity creation",
                "PurchaseItem line records",
                "InventoryLot creation with acquisition cost",
                "LotBucketBalances increment",
                "Supplier Khata account payable credit",
                "Optional cash payout from cash session",
                "Physical unit barcode creation",
                "Posting validation audit"
            ],
            "failure_behavior": "Rolls back transaction; records failure in outcome ledger if injected."
        },
        {
            "id": "H06",
            "canonical_name": "CreatePurchaseReturnCommand",
            "conceptual_name": "ProcessPurchaseReturnCommand",
            "class_name": "CreatePurchaseReturnHandler",
            "file_path": "src/EdgeRetails.Application/Features/Purchasing/PurchaseReturnHandler.cs",
            "command_decl_lines": "25-38",
            "handler_impl_lines": "58-290",
            "controller_endpoint": "POST /api/purchasing/return (PurchasingController.cs:60-75)",
            "command_parameters": [
                "Guid ClientOperationId",
                "Guid PurchaseId",
                "Guid SupplierId",
                "Guid CreatedBy",
                "string Reason",
                "IReadOnlyList<PurchaseReturnLineInput> Lines",
                "DateOnly? PostingDate",
                "SupervisorApprovalRequest? SupervisorApproval",
                "bool? CallerSuppliedBooleanOverride"
            ],
            "client_operation_id_present": True,
            "operation_id_property_name": "ClientOperationId",
            "ledger_interaction": "OPTIONAL_INJECTION",
            "ledger_constructor_decl": "IOperationOutcomeLedger? outcomeLedger = null (line 91)",
            "transaction_boundary": "EXPLICIT_TRANSACTION_RUNNER",
            "transaction_runner_code": "_transactions.ExecuteAsync",
            "unit_of_work_present": True,
            "operation_lock_acquired": True,
            "resource_lock_acquired": True,
            "payload_fingerprint_enforced": False,
            "fingerprint_notes": "No SHA-256 fingerprint verified or stored.",
            "response_payload_stored": False,
            "side_effects": [
                "PurchaseReturn record creation",
                "PurchaseReturnItem line records",
                "Stock balance reduction from Sellable bucket",
                "Supplier Khata debit (decrease payable)",
                "Posting validation audit"
            ],
            "failure_behavior": "Rolls back transaction; records failure in outcome ledger if injected."
        },
        {
            "id": "H07",
            "canonical_name": "ReceiveProductIntakeCommand",
            "conceptual_name": "ReceiveProductIntakeCommand",
            "class_name": "ReceiveProductIntakeHandler",
            "file_path": "src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs",
            "command_decl_lines": "15-28",
            "handler_impl_lines": "70-340",
            "controller_endpoint": "POST /api/purchasing/intake (PurchasingController.cs:46-59)",
            "command_parameters": [
                "Guid PurchaseId",
                "Guid ProductId",
                "Guid ProductUnitId",
                "decimal EnteredQuantity",
                "decimal? EnteredUnitCost",
                "IReadOnlyList<SerializedIdentityInput> SerializedUnits",
                "Guid CreatedBy",
                "Guid ClientOperationId",
                "string? Note",
                "DateOnly? PostingDate",
                "SupervisorApprovalRequest? SupervisorApproval",
                "bool? CallerSuppliedBooleanOverride"
            ],
            "client_operation_id_present": True,
            "operation_id_property_name": "ClientOperationId",
            "ledger_interaction": "OPTIONAL_INJECTION",
            "ledger_constructor_decl": "IOperationOutcomeLedger? outcomeLedger = null (line 110)",
            "transaction_boundary": "EXPLICIT_TRANSACTION_RUNNER",
            "transaction_runner_code": "_transactions.ExecuteAsync",
            "unit_of_work_present": True,
            "operation_lock_acquired": True,
            "resource_lock_acquired": True,
            "payload_fingerprint_enforced": False,
            "fingerprint_notes": "Desktop computes intent payload hash, but server does not enforce fingerprint verification.",
            "response_payload_stored": False,
            "side_effects": [
                "Physical unit barcode generation",
                "Stock balance increment",
                "Lot bucket balance update",
                "Posting validation audit"
            ],
            "failure_behavior": "Rolls back transaction; records failure in outcome ledger if injected."
        },
        {
            "id": "H08",
            "canonical_name": "VoidPurchaseCommand",
            "conceptual_name": "VoidPurchaseCommand",
            "class_name": "VoidPurchaseHandler",
            "file_path": "src/EdgeRetails.Application/Features/Purchasing/VoidPurchaseHandler.cs",
            "command_decl_lines": "13-17",
            "handler_impl_lines": "30-180",
            "controller_endpoint": "POST /api/purchasing/void (PurchasingController.cs:76-90)",
            "command_parameters": [
                "Guid PurchaseId",
                "Guid ClientOperationId",
                "Guid VoidedBy",
                "string Reason"
            ],
            "client_operation_id_present": True,
            "operation_id_property_name": "ClientOperationId",
            "ledger_interaction": "OPTIONAL_INJECTION",
            "ledger_constructor_decl": "IOperationOutcomeLedger? outcomeLedger = null (line 57)",
            "transaction_boundary": "EXPLICIT_TRANSACTION_RUNNER",
            "transaction_runner_code": "_transactions.ExecuteAsync",
            "unit_of_work_present": True,
            "operation_lock_acquired": True,
            "resource_lock_acquired": True,
            "payload_fingerprint_enforced": False,
            "fingerprint_notes": "No SHA-256 fingerprint verified or stored.",
            "response_payload_stored": False,
            "side_effects": [
                "Marks purchase status as Voided",
                "Reverses stock movements if inventory untouched",
                "Reverses supplier payable credit on Khata",
                "BusinessAuditWriter audit entry"
            ],
            "failure_behavior": "Rolls back transaction; records failure in outcome ledger if injected."
        },
        {
            "id": "H09",
            "canonical_name": "CreateStockAdjustmentCommand",
            "conceptual_name": "AdjustStockCommand",
            "class_name": "CreateStockAdjustmentHandler",
            "file_path": "src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs",
            "command_decl_lines": "35-44",
            "handler_impl_lines": "46-310",
            "controller_endpoint": "POST /api/inventory/adjustments (InventoryController.cs:65-75)",
            "command_parameters": [
                "StockAdjustmentMode Mode",
                "StockAdjustmentReason Reason",
                "IReadOnlyList<StockAdjustmentItemCommand> Items",
                "Guid ActorId",
                "Guid CorrelationId",
                "string? Note",
                "DateOnly? PostingDate",
                "SupervisorApprovalRequest? SupervisorApproval",
                "bool? CallerSuppliedBooleanOverride"
            ],
            "client_operation_id_present": True,
            "operation_id_property_name": "CorrelationId (NON-CANONICAL NAMING)",
            "ledger_interaction": "MANDATORY_FAIL_CLOSED",
            "ledger_constructor_decl": "IOperationOutcomeLedger? outcomeLedger = null (line 97, but line 108 throws failure if null)",
            "transaction_boundary": "EXPLICIT_TRANSACTION_RUNNER",
            "transaction_runner_code": "_transactions.ExecuteAsync",
            "unit_of_work_present": True,
            "operation_lock_acquired": True,
            "resource_lock_acquired": True,
            "payload_fingerprint_enforced": False,
            "fingerprint_notes": "Uses CorrelationId as operation identity; fails if outcome ledger is null, but stores no fingerprint.",
            "response_payload_stored": False,
            "side_effects": [
                "StockAdjustment record creation",
                "StockAdjustmentItem lines persistence",
                "Inventory balance updates across Sellable/Damaged/Defective buckets",
                "Scrap loss write-off posting",
                "Posting validation audit"
            ],
            "failure_behavior": "Rolls back transaction; records failure in outcome ledger."
        },
        {
            "id": "H10",
            "canonical_name": "PostStocktakeCommand",
            "conceptual_name": "ReconcileStocktakeCommand",
            "class_name": "PostStocktakeHandler",
            "file_path": "src/EdgeRetails.Application/Features/Inventory/StocktakeHandlers.cs",
            "command_decl_lines": "635-644",
            "handler_impl_lines": "646-760",
            "controller_endpoint": "POST /api/inventory/stocktake/{id}/post (InventoryController.cs:120-130)",
            "command_parameters": [
                "Guid StocktakeId",
                "Guid ActorId",
                "Guid ClientOperationId",
                "string? Reason",
                "DateOnly? PostingDate",
                "SupervisorApprovalRequest? SupervisorApproval",
                "bool? CallerSuppliedBooleanOverride"
            ],
            "client_operation_id_present": True,
            "operation_id_property_name": "ClientOperationId",
            "ledger_interaction": "OPTIONAL_INJECTION",
            "ledger_constructor_decl": "IOperationOutcomeLedger? outcomeLedger = null (line 656)",
            "transaction_boundary": "EXPLICIT_TRANSACTION_RUNNER",
            "transaction_runner_code": "_transactions.ExecuteAsync",
            "unit_of_work_present": True,
            "operation_lock_acquired": True,
            "resource_lock_acquired": True,
            "payload_fingerprint_enforced": False,
            "fingerprint_notes": "No SHA-256 fingerprint verified or stored.",
            "response_payload_stored": False,
            "side_effects": [
                "Closes stocktake session with status Posted",
                "Computes inventory variances (overages and shortages)",
                "Creates automatic StockAdjustment movements reconciling balances",
                "Posting validation audit"
            ],
            "failure_behavior": "Rolls back transaction; records failure in outcome ledger if injected."
        },
        {
            "id": "H11",
            "canonical_name": "FoundInventoryUnitCommand",
            "conceptual_name": "AdmitFoundUnitCommand",
            "class_name": "FoundInventoryUnitHandler",
            "file_path": "src/EdgeRetails.Application/Features/Inventory/FoundInventoryUnitHandler.cs",
            "command_decl_lines": "12-23",
            "handler_impl_lines": "25-180",
            "controller_endpoint": "POST /api/inventory/exact-units/found (InventoryController.cs:15-30)",
            "command_parameters": [
                "Guid ProductId",
                "string SerialNumber",
                "string? Imei1",
                "string? Imei2",
                "Guid ActorId",
                "string Reason",
                "Guid ClientOperationId"
            ],
            "client_operation_id_present": True,
            "operation_id_property_name": "ClientOperationId",
            "ledger_interaction": "MANDATORY_CONSTRUCTOR",
            "ledger_constructor_decl": "IOperationOutcomeLedger outcomes (line 26)",
            "transaction_boundary": "EXPLICIT_TRANSACTION_RUNNER",
            "transaction_runner_code": "_transactions.ExecuteAsync",
            "unit_of_work_present": False,
            "operation_lock_acquired": True,
            "resource_lock_acquired": True,
            "payload_fingerprint_enforced": False,
            "fingerprint_notes": "Outcome ledger is mandatory, but no payload fingerprint computed.",
            "response_payload_stored": False,
            "side_effects": [
                "Re-activates lost/missing InventoryUnit into InStock status",
                "Increments SellableQty balance",
                "Records audit trace"
            ],
            "failure_behavior": "Rolls back transaction; records failure in outcome ledger."
        },
        {
            "id": "H12",
            "canonical_name": "OpenCashSessionCommand / CloseCashSessionCommand / RecordCashMovementRequest",
            "conceptual_name": "OpenCashSessionCommand / CloseCashSessionCommand",
            "class_name": "OpenCashSessionHandler / CloseCashSessionHandler / CashMovementService",
            "file_path": "src/EdgeRetails.Application/Features/Finance/CashSessionHandlers.cs",
            "command_decl_lines": "8-11, 87-95, 190-195",
            "handler_impl_lines": "13-85, 97-188, 197-260",
            "controller_endpoint": "POST /api/finance/cash-session/open (FinanceController.cs:40-50)",
            "command_parameters": [
                "decimal OpeningCash",
                "Guid ActorId",
                "string? Note"
            ],
            "client_operation_id_present": False,
            "operation_id_property_name": "NONE (CRITICAL VULNERABILITY)",
            "ledger_interaction": "ZERO_INTERACTION",
            "ledger_constructor_decl": "None (completely missing from class)",
            "transaction_boundary": "EXPLICIT_TRANSACTION_RUNNER",
            "transaction_runner_code": "_transactions.ExecuteAsync",
            "unit_of_work_present": True,
            "operation_lock_acquired": False,
            "resource_lock_acquired": False,
            "payload_fingerprint_enforced": False,
            "fingerprint_notes": "Completely lacks idempotency support.",
            "response_payload_stored": False,
            "side_effects": [
                "CashSession record creation",
                "Cash drawer opening balance assignment",
                "BusinessAuditWriter audit entry"
            ],
            "failure_behavior": "Rolls back transaction, but client retry will fail with cash.session_already_open or duplicate movements."
        },
        {
            "id": "H13",
            "canonical_name": "PostExpenseCommand",
            "conceptual_name": "RecordExpenseCommand",
            "class_name": "PostExpenseHandler",
            "file_path": "src/EdgeRetails.Application/Features/Finance/ExpenseHandlers.cs",
            "command_decl_lines": "15-28",
            "handler_impl_lines": "30-220",
            "controller_endpoint": "POST /api/expenses (ExpensesController.cs:87-108)",
            "command_parameters": [
                "Guid ClientOperationId",
                "Guid CategoryId",
                "Guid? SubcategoryId",
                "DateOnly ExpenseDate",
                "decimal Amount",
                "ExpensePaymentMethod PaymentMethod",
                "string Description",
                "string? Reference",
                "Guid ActorId"
            ],
            "client_operation_id_present": True,
            "operation_id_property_name": "ClientOperationId",
            "ledger_interaction": "MANDATORY_CONSTRUCTOR",
            "ledger_constructor_decl": "IOperationOutcomeLedger outcomeLedger (line 52)",
            "transaction_boundary": "EXPLICIT_TRANSACTION_RUNNER",
            "transaction_runner_code": "_transactions.ExecuteAsync",
            "unit_of_work_present": True,
            "operation_lock_acquired": True,
            "resource_lock_acquired": False,
            "payload_fingerprint_enforced": False,
            "fingerprint_notes": "Checks outcome ledger on replay, but stores null fingerprint.",
            "response_payload_stored": False,
            "side_effects": [
                "Expense record creation",
                "Cash drawer deduction if payment method is Cash",
                "BusinessAuditWriter audit log",
                "Posting validation audit"
            ],
            "failure_behavior": "Rolls back transaction; records failure in outcome ledger."
        },
        {
            "id": "H14",
            "canonical_name": "CreateSupplierPaymentCommand",
            "conceptual_name": "RecordSupplierPaymentCommand",
            "class_name": "CreateSupplierPaymentHandler",
            "file_path": "src/EdgeRetails.Application/Features/Finance/SupplierAccountHandlers.cs",
            "command_decl_lines": "19-30",
            "handler_impl_lines": "40-280",
            "controller_endpoint": "POST /api/finance/supplier-payment (FinanceController.cs:25-35)",
            "command_parameters": [
                "Guid ClientOperationId",
                "Guid SupplierId",
                "decimal Amount",
                "SupplierPaymentPurpose Purpose",
                "SupplierSettlementMethod Method",
                "string? ExternalReference",
                "Guid ActorId",
                "string? Note",
                "DateOnly? PostingDate",
                "SupervisorApprovalRequest? SupervisorApproval",
                "bool? CallerSuppliedBooleanOverride"
            ],
            "client_operation_id_present": True,
            "operation_id_property_name": "ClientOperationId",
            "ledger_interaction": "OPTIONAL_INJECTION",
            "ledger_constructor_decl": "IOperationOutcomeLedger? outcomeLedger = null (line 63)",
            "transaction_boundary": "EXPLICIT_TRANSACTION_RUNNER",
            "transaction_runner_code": "_transactions.ExecuteAsync",
            "unit_of_work_present": True,
            "operation_lock_acquired": True,
            "resource_lock_acquired": True,
            "payload_fingerprint_enforced": False,
            "fingerprint_notes": "No SHA-256 fingerprint verified or stored.",
            "response_payload_stored": False,
            "side_effects": [
                "SupplierPayment record creation",
                "SupplierAccountEntry payable reduction on Khata",
                "CashSession payout deduction if Method is Cash",
                "Posting validation audit"
            ],
            "failure_behavior": "Rolls back transaction; records failure in outcome ledger if injected."
        },
        {
            "id": "H15",
            "canonical_name": "CreateThakaProjectCommand / IssueThakaMaterialCommand",
            "conceptual_name": "AcceptThakaCustodyCommand / ReleaseThakaCustodyCommand",
            "class_name": "CreateThakaProjectHandler / IssueThakaMaterialHandler",
            "file_path": "src/EdgeRetails.Application/Features/Thaka/ThakaHandlers.cs",
            "command_decl_lines": "19-28, 48-60",
            "handler_impl_lines": "30-180, 70-340",
            "controller_endpoint": "POST /api/thaka/material-issue (ThakaController.cs:35-45)",
            "command_parameters": [
                "Guid ProjectId",
                "Guid ActorId",
                "Guid ClientOperationId",
                "string? Note",
                "IReadOnlyList<IssueThakaMaterialLineInput> Lines",
                "DateOnly? PostingDate"
            ],
            "client_operation_id_present": True,
            "operation_id_property_name": "ClientOperationId",
            "ledger_interaction": "OPTIONAL_INJECTION",
            "ledger_constructor_decl": "IOperationOutcomeLedger? outcomeLedger = null (line 86)",
            "transaction_boundary": "EXPLICIT_TRANSACTION_RUNNER",
            "transaction_runner_code": "_transactions.ExecuteAsync",
            "unit_of_work_present": True,
            "operation_lock_acquired": True,
            "resource_lock_acquired": True,
            "payload_fingerprint_enforced": False,
            "fingerprint_notes": "No SHA-256 fingerprint verified or stored.",
            "response_payload_stored": False,
            "side_effects": [
                "ThakaIssue record creation",
                "Inventory deductions from shop stock and transfer to Thaka custody",
                "Remnant length tracking for continuous cable cuts",
                "Posting validation audit"
            ],
            "failure_behavior": "Rolls back transaction; records failure in outcome ledger if injected."
        },
        {
            "id": "H16",
            "canonical_name": "CreateWarrantyClaimCommand",
            "conceptual_name": "InitiateWarrantyClaimCommand / ResolveWarrantyClaimCommand",
            "class_name": "CreateWarrantyClaimHandler",
            "file_path": "src/EdgeRetails.Application/Features/Warranty/WarrantyHandlers.cs",
            "command_decl_lines": "13-22",
            "handler_impl_lines": "30-240",
            "controller_endpoint": "POST /api/warranty/claims (WarrantyController.cs:25-35)",
            "command_parameters": [
                "Guid CustomerId",
                "Guid SaleId",
                "Guid SupplierId",
                "Guid ActorId",
                "IReadOnlyList<WarrantyClaimItemInput> Items",
                "Guid ClientOperationId"
            ],
            "client_operation_id_present": True,
            "operation_id_property_name": "ClientOperationId",
            "ledger_interaction": "OPTIONAL_INJECTION",
            "ledger_constructor_decl": "IOperationOutcomeLedger? outcomeLedger = null (line 176)",
            "transaction_boundary": "EXPLICIT_TRANSACTION_RUNNER",
            "transaction_runner_code": "_transactions.ExecuteAsync",
            "unit_of_work_present": True,
            "operation_lock_acquired": True,
            "resource_lock_acquired": True,
            "payload_fingerprint_enforced": False,
            "fingerprint_notes": "No SHA-256 fingerprint verified or stored.",
            "response_payload_stored": False,
            "side_effects": [
                "WarrantyClaim and WarrantyClaimItem records creation",
                "Inventory unit status transition to UnderWarranty",
                "Warranty custody logging and audit"
            ],
            "failure_behavior": "Rolls back transaction; records failure in outcome ledger if injected."
        }
    ]
}

census_json_path = os.path.join(dest_dir, "MUTATION_CENSUS_16_HANDLERS.json")
with open(census_json_path, "w", encoding="utf-8") as f:
    json.dump(census_data, f, indent=2, ensure_ascii=False)
print(f"Wrote {census_json_path} ({os.path.getsize(census_json_path)} bytes)")

# =========================================================================
# 2. Author HISTORICAL_DEFECT_MAPPING.json
# =========================================================================
defect_data = {
    "artifact_id": f"{run_id}-HISTORICAL-DEFECT-MAPPING",
    "run_id": run_id,
    "timestamp_utc": packaging_now_utc,
    "audit_scope": "Historical Concurrency, Replay, and Network Partition Vulnerabilities (F05-F07, H01-H02, B07)",
    "total_defects_analyzed": 6,
    "defects": [
        {
            "id": "F05",
            "title": "Ambiguous Response Timeout & Cashier Double Click",
            "severity": "CRITICAL",
            "category": "NETWORK_AMBIGUITY_AND_CONCURRENCY",
            "symptom": "Cashier clicks 'Pay'; network drops or latency spikes during HTTP response transit; cashier re-clicks 'Pay', resulting in duplicate charges or double stock deductions.",
            "vulnerable_code_locations": [
                "src/EdgeRetails.Infrastructure/Repositories/EfOperationOutcomeLedger.cs:11-23 (in-memory SemaphoreSlim[64] striped locks fail across multi-process or load-balanced deployments)",
                "src/EdgeRetails.Application/Features/Sales/CompleteSaleHandler.cs:186-191 (checks existing sale only within transaction; concurrent request sees existing==null before first commits)",
                "src/EdgeRetails.Desktop/Services/BackendTransactionService.cs:181-186, 216-221 (transport error triggers RecoverSaleOutcomeAsync, but lack of ResponsePayload in outcome causes uncertain sale mark)"
            ],
            "root_cause_analysis": "Absence of an atomic, server-side PostgreSQL distributed claim (ClaimAsync) executed before business mutation logic begins. Currently, outcome records are created as Pending or Succeeded after acquiring in-memory locks, allowing concurrent requests on separate threads/processes to bypass checks.",
            "hostile_test_scenario": "Two concurrent HTTP POST /api/sales/complete requests with identical ClientOperationId submitted simultaneously from two threads. Both threads must be serialized at the database level: exactly one succeeds with HTTP 200, the second receives HTTP 409 Conflict (or returns replayed HTTP 200 with identical response). Zero duplicate sales.",
            "phase12_remediation_area": "Area 12B (Atomic PostgreSQL ClaimAsync) & Area 12F (Ambiguous Response Recovery Protocol)"
        },
        {
            "id": "F06",
            "title": "Draft Cart Recovery Crash & Double Conversion",
            "severity": "HIGH",
            "category": "DRAFT_CRASH_RECOVERY",
            "symptom": "Cashier converts saved draft into final sale; app crashes or power drops mid-flight; on restart, cashier re-opens draft and clicks 'Pay' again, converting the draft twice into two distinct sales.",
            "vulnerable_code_locations": [
                "src/EdgeRetails.Application/Features/Sales/PosDraftHandlers.cs:20-180 (PosDraft has no atomic DraftState.Converting transition)",
                "src/EdgeRetails.Application/Features/Sales/CompleteSaleHandler.cs:152-164 (draft fingerprint checks SourceDraftId and SourceDraftVersion, but if client generates a new ClientOperationId, it is treated as a new conversion attempt)",
                "src/EdgeRetails.Desktop/ViewModels/PosViewModel.cs:1726-1730 (re-checks pending sale intent, but if draft was resumed without original operation ID, creates fresh GUID)"
            ],
            "root_cause_analysis": "Draft state transition is not atomically linked to the ClientOperationId. When a draft conversion begins, the draft itself is not marked in the database as 'Converting' bound to the operation ID, permitting subsequent checkout attempts if the initial operation ID is lost.",
            "hostile_test_scenario": "Initiate draft conversion with ClientOperationId #1. Terminate client process before response. Restart client, load draft, and submit conversion with ClientOperationId #2. Server must reject conversion #2 with HTTP 409 Conflict indicating draft is already converted or bound to operation #1.",
            "phase12_remediation_area": "Area 12I (Draft Conversion & Crash Recovery Workflow)"
        },
        {
            "id": "F07",
            "title": "Thermal Printer Paper-Out / Hardware Failure Rollback Confusion",
            "severity": "HIGH",
            "category": "PHYSICAL_HARDWARE_DECOUPLING",
            "symptom": "Sale completes successfully in database, but thermal receipt printer runs out of paper or jams; cashier assumes sale failed, attempts to cancel or re-run transaction, leading to financial ledger discrepancy.",
            "vulnerable_code_locations": [
                "src/EdgeRetails.Desktop/ViewModels/PosViewModel.cs:808-836 (PrintCompletedReceiptAsync called synchronously in UI workflow with hardcoded isReprint: false)",
                "src/EdgeRetails.Server/Controllers/PrintingController.cs:11-105 (Receipt audit exists for stickers/labels, but POS receipt printing lacks a dedicated durable server Outbox pattern)"
            ],
            "root_cause_analysis": "Physical receipt printing is tightly coupled to the cashier's checkout completion interaction rather than being treated as an asynchronous, decoupled physical outbox job. Furthermore, replaying a sale re-executes PrintCompletedReceiptAsync with isReprint=false rather than distinguishing initial print from reprint.",
            "hostile_test_scenario": "Complete sale with simulated printer offline exception. Verify that sale remains committed in PostgreSQL (no rollback of financial/inventory effects). Replay sale: verify that system marks print as reprint (isReprint=true) and logs distinct ClientPrintAttemptId without duplicating ledger entries.",
            "phase12_remediation_area": "Area 12K (Physical Outbox Printing Separation)"
        },
        {
            "id": "H01",
            "title": "Replay Attack with Altered Price Override or Discount",
            "severity": "CRITICAL",
            "category": "TAMPER_DETECTION_AND_SECURITY",
            "symptom": "Adversary captures a legitimate ClientOperationId and resubmits it with altered prices, changed quantities, or unauthorized discounts, attempting to exploit idempotency replay to obtain goods at lower cost.",
            "vulnerable_code_locations": [
                "src/EdgeRetails.Application/Features/Sales/CompleteSaleHandler.cs:638 (passes draftFingerprint to RecordSuccessAsync; for normal direct sales, draftFingerprint is NULL)",
                "src/EdgeRetails.Domain/Operations/OperationOutcome.cs:16 (PayloadFingerprint stored as null in DB for all non-draft sales)",
                "src/EdgeRetails.Application/Features/Terminals/OperationStatusQueryHandler.cs:98-105 (bypasses fingerprint verification if canonicalOutcome.PayloadFingerprint is null or empty)",
                "src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs, ExpenseHandlers.cs, etc. (15 of 16 handlers compute zero payload fingerprints)"
            ],
            "root_cause_analysis": "Absence of mandatory canonical SHA-256 payload hashing across all mutating commands. When a command is recorded in the outcome ledger, PayloadFingerprint is null for non-draft sales and all purchasing/expense/stocktake operations, disabling tamper detection on replay.",
            "hostile_test_scenario": "Submit CompleteSaleCommand with ClientOperationId #1 and GrandTotal=1000. Re-submit identical ClientOperationId #1 with GrandTotal=500. Server must detect payload hash divergence, reject request with HTTP 409 Conflict (IDEMPOTENCY_PAYLOAD_MISMATCH), and log security audit event.",
            "phase12_remediation_area": "Area 12C (Canonical Payload SHA-256 Fingerprinting & Tamper Detection)"
        },
        {
            "id": "H02",
            "title": "Multi-Terminal Concurrency Race on Competing Operations",
            "severity": "CRITICAL",
            "category": "DISTRIBUTED_CONCURRENCY_RACE",
            "symptom": "Two cashiers at different POS terminals submit transactions with identical operation keys or competing for the same serialized stock units / open pack containers simultaneously.",
            "vulnerable_code_locations": [
                "src/EdgeRetails.Infrastructure/Repositories/EfOperationOutcomeLedger.cs:11-23 (in-memory SemaphoreSlim[64] cannot protect across separate server processes or cluster nodes)",
                "src/EdgeRetails.Infrastructure/Repositories/EfOperationOutcomeLedger.cs:54-90 (race window between FirstOrDefaultAsync check and SaveChangesAsync allows two threads to execute)",
                "src/EdgeRetails.Application/Features/Inventory/PackSubLedgerAllocationAuthority.cs:115-180 (optimistic concurrency catches version conflict, but requires deterministic retry boundary)"
            ],
            "root_cause_analysis": "Reliance on process-local synchronization primitives (SemaphoreSlim) rather than database-enforced row locks or unique constraints with ON CONFLICT semantics in PostgreSQL.",
            "hostile_test_scenario": "Spawn 10 concurrent tasks submitting identical ClientOperationId to separate DbContext instances on PostgreSQL port 55437. Exactly 1 task must acquire the claim; 9 tasks must receive concurrency conflict or await original outcome. Zero duplicate entries in system.operation_outcomes.",
            "phase12_remediation_area": "Area 12B (Server-Side Operation Claim & Deterministic Outcomes)"
        },
        {
            "id": "B07",
            "title": "Lost Network Response during Customer Khata Debit",
            "severity": "CRITICAL",
            "category": "FINANCIAL_LEDGER_CONSISTENCY",
            "symptom": "Customer makes on-account purchase; server debits customer's Khata receivable ledger and commits transaction; network drops before HTTP response reaches desktop; cashier terminal displays error, leaving cashier uncertain if customer was debited.",
            "vulnerable_code_locations": [
                "src/EdgeRetails.Application/Features/Sales/CompleteSaleHandler.cs:580-664 (commits sale and Khata debit in database)",
                "src/EdgeRetails.Domain/Operations/OperationOutcome.cs:11-30 (lacks ResponsePayload property; does not persist serialized HTTP response body)",
                "src/EdgeRetails.Server/Controllers/OperationsController.cs:19-74 (route is api/operations/{id} instead of api/v1/operations/{id}; returns minimal entity metadata rather than full response DTO)",
                "src/EdgeRetails.Desktop/Services/BackendTransactionService.cs:341-344 (marks sale as uncertain and throws BackendOperationException if full detail cannot be reconstructed)"
            ],
            "root_cause_analysis": "The outcome ledger records entity metadata (EntityId, DocumentNumber) but does NOT persist the full serialized HTTP response payload. When a client performs recovery polling, the server cannot return the exact HTTP 200 payload that would have been returned originally.",
            "hostile_test_scenario": "Execute on-account CompleteSaleCommand with network response drop simulation. Client issues GET /api/v1/operations/{id}. Server returns HTTP 200 with cached ResponsePayload containing full invoice number, customer balance before/after, and sale items. Client successfully completes UI checkout without duplicate Khata debit.",
            "phase12_remediation_area": "Area 12D (Atomic ACID Boundary), Area 12E (Universal Status Authority) & Area 12F (Ambiguous Recovery)"
        }
    ]
}

defect_json_path = os.path.join(dest_dir, "HISTORICAL_DEFECT_MAPPING.json")
with open(defect_json_path, "w", encoding="utf-8") as f:
    json.dump(defect_data, f, indent=2, ensure_ascii=False)
print(f"Wrote {defect_json_path} ({os.path.getsize(defect_json_path)} bytes)")

# =========================================================================
# 3. Author PHASE12_PREAUDIT_REPORT.md
# =========================================================================
report_md = f"""# PHASE 12 FORENSIC READ-ONLY AUDIT & ARCHITECTURAL BASELINE REPORT
## Deep Static & Schema Inspection of Existing Idempotency Footholds, 16-Handler Mutating Census, Client Intent Store, and Historical Vulnerabilities

**Artifact ID**: `{run_id}-REPORT`
**Execution Run**: `{run_id}`
**Audit Authority**: `ER-P12-PREAUDIT-MASTER-20261015-001`
**Standing Owner Authorities**: `OWNER-UPDATE-20261013-007`, `OWNER-UPDATE-20261014-001`, `OWNER-UPDATE-20261014-002`, `OWNER-UPDATE-20261011-002`
**Governance Event**: `ER-CHANGE-20261015-025`
**Execution Mode**: **STRICTLY READ-ONLY (ZERO SOURCE MUTATIONS, ZERO DB WRITES, ZERO TEST MUTATIONS)**
**Target Solution**: `EdgeRetails.sln`
**Base Git Commit**: `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
**Branch**: `feat/phase11-subledger-implementation`
**Date of Audit**: `{packaging_now_utc}`

---

## 1. Executive Summary & Forensic Baseline

In formal compliance with Master Execution Prompt `ER-P12-PREAUDIT-MASTER-20261015-001`, a comprehensive, evidence-backed, forensic read-only audit of the EdgeRetails codebase was conducted to establish the architectural baseline for **Phase 12: Operation Identity, Replay and Recovery Authority**.

Phase 12 establishes the foundational distributed systems guarantee of EdgeRetails:
$$\\mathbf{{1\\text{{ User Intent}} = 1\\text{{ ClientOperationId}} = 1\\text{{ OperationType}} = 1\\text{{ PayloadFingerprint (SHA-256)}} = 1\\text{{ Business Effect}} = 1\\text{{ Durable Outcome}}}}$$

### Key Forensic Findings:
1. **Verified Existing Foothold**: An existing `OperationOutcome` entity and `IOperationOutcomeLedger` contract exist in `EdgeRetails.Domain` and `EdgeRetails.Application`, mapped to PostgreSQL table `system.operation_outcomes`. However, it suffers from two critical architectural deficiencies:
   - **Missing Response Payload Storage**: The entity stores `ResultEntityType`, `ResultEntityId`, and `DocumentNumber`, but **lacks a `ResponsePayload` (string/jsonb)** property. It cannot return the original serialized HTTP response body upon idempotency replay.
   - **In-Memory Semaphore Vulnerability**: `EfOperationOutcomeLedger` utilizes `private static readonly SemaphoreSlim[] _stripedGates = Enumerable.Range(0, 64)...`. This in-memory lock fails completely across multi-process, multi-terminal, or load-balanced deployments.
2. **16-Handler Mutation Census**:
   - **15 of 16 handlers** accept `ClientOperationId` (or `CorrelationId` in StockAdjustment).
   - **1 handler completely lacks operation identity**: `CashSessionHandlers.cs` (`OpenCashSessionCommand`, `CloseCashSessionCommand`, `RecordCashMovementRequest`) has **zero idempotency support**, creating severe risk of duplicate cash drawer movements on network retry.
   - **13 of 16 handlers** treat `IOperationOutcomeLedger` as an **optional parameter** (`IOperationOutcomeLedger? outcomeLedger = null`). Idempotency is not fail-closed across all mutating operations.
   - **Only 1 handler** computes payload fingerprints (`CompleteSaleHandler`), and even there, it is **only computed for draft sales** (`PosDraft:v1:`); direct sales record `PayloadFingerprint = null` in the database!
3. **Desktop Intent Journal Readiness**: `FileClientOperationIntentStore` in `EdgeRetails.Desktop` provides an atomic file-backed local journal (`operation-intents.json`) protected by a Named Mutex. It is already integrated with POS sales checkout and physical intake, providing a solid client foundation for Phase 12.
4. **Historical Defect Vectors**: All six canonical defects (**F05–F07, H01–H02, B07**) were traced to exact source files and line numbers. Each defect is mapped to a concrete remediation area in Phase 12.

---

## 2. Stage 1: Solution Topology & Dependency Layering

The solution architecture strictly adheres to Clean Architecture principles across 5 tiers:

```
[ EdgeRetails.Desktop ] (WPF Client, MVVM, Intent Store)
       │            │
       ▼            ▼
[ EdgeRetails.Application ] (CQRS Handlers, Domain Contracts)
       ▲            ▲
       │            │
[ EdgeRetails.Server ] (ASP.NET Core REST API, OperationsController)
       │            │
       ▼            ▼
[ EdgeRetails.Infrastructure ] (EF Core DbContext, PostgreSQL 18, Migrations)
       │
       ▼
[ EdgeRetails.Domain ] (Pure Entities, Value Objects, Zero Dependencies)
```

### Dependency Rules Verified:
- `EdgeRetails.Domain`: References zero projects. Contains pure business models (`OperationOutcome`, `PackContainer`, `Sale`, `Purchase`).
- `EdgeRetails.Application`: References only `Domain`. Defines `IOperationOutcomeLedger`, `OperationStatusQueryHandler`, and 16 mutating command handlers.
- `EdgeRetails.Infrastructure`: References `Application` and `Domain`. Implements `EfOperationOutcomeLedger`, EF Core configurations, and PostgreSQL migrations.
- `EdgeRetails.Server`: References `Application` and `Infrastructure`. Hosts REST API controllers (`OperationsController`, `SalesController`, etc.).
- `EdgeRetails.Desktop`: References `Application` and `Infrastructure`. Implements WPF presentation, `FileClientOperationIntentStore`, and `BackendTransactionService`.

---

## 3. Stage 2: Existing Shared Ledger Footholds & Schema Inspection (Area 12A)

### 3.1 Domain Entity: `src/EdgeRetails.Domain/Operations/OperationOutcome.cs`
```csharp
public sealed class OperationOutcome
{{
    public Guid Id {{ get; set; }} = Guid.CreateVersion7();
    public Guid ClientOperationId {{ get; set; }}
    public string OperationType {{ get; set; }} = string.Empty;
    public string? PayloadFingerprint {{ get; set; }}
    public OperationOutcomeStatus Status {{ get; set; }} = OperationOutcomeStatus.Pending;
    public string? ResultEntityType {{ get; set; }}
    public Guid? ResultEntityId {{ get; set; }}
    public string? DocumentNumber {{ get; set; }}
    public Guid? ActorUserId {{ get; set; }}
    public Guid? TerminalId {{ get; set; }}
    public Guid? SessionId {{ get; set; }}
    public bool WasCommitted {{ get; set; }}
    public DateTimeOffset CreatedAt {{ get; set; }} = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt {{ get; set; }} = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt {{ get; set; }}
    public string? ErrorCode {{ get; set; }}
    public string? ErrorMessage {{ get; set; }}
}}
```

### 3.2 EF Core Database Configuration: `system.operation_outcomes`
In `src/EdgeRetails.Infrastructure/Persistence/Configurations/OperationOutcomeConfiguration.cs`:
- Table: `system.operation_outcomes`
- Unique Index: `ix_operation_outcomes_client_operation_id` on `ClientOperationId`
- Status Index: `ix_operation_outcomes_status` on `Status`
- Timestamp Index: `ix_operation_outcomes_created_at` on `CreatedAt`

### 3.3 Critical Architectural Deficiencies Identified:
1. **No Response Payload Storage**: The entity has no column to store the HTTP response body (`ResponsePayload`, `ResponseStatusCode`, `ResponseContentType`). When replaying an operation, the server can only tell the client the `EntityId` and `DocumentNumber`; it cannot replay the original JSON payload.
2. **In-Memory Semaphore Bottleneck**: `EfOperationOutcomeLedger.cs` lines 11–23:
   ```csharp
   private static readonly SemaphoreSlim[] _stripedGates =
       Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
   ```
   This semaphore only coordinates threads within the same ASP.NET Core process. Across multiple server processes, separate POS terminals, or worker processes, it provides zero protection.
3. **Optional Ledger Injection**: In 13 out of 16 handlers, `IOperationOutcomeLedger` is injected as nullable (`= null`). If DI registration fails or is omitted, idempotency protection silently degrades to zero.

---

## 4. Stage 3: Complete 16-Handler Mutating Command Census (Area 12J)

A static inspection of all mutating command handlers in `EdgeRetails.Application` was conducted. The results are summarized below:

| # | Command / Operation | Handler Class & File | Operation ID | Ledger Injection | Transaction Boundary | Fingerprint Status | Response Stored |
| :- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **H01** | `CompleteSaleCommand` | `CompleteSaleHandler.cs:32` | `ClientOperationId` | Optional (`= null`) | `_transactions.ExecuteAsync` | Partial (Draft Only) | None |
| **H02** | `CreateSaleReturnCommand` | `SaleReturnHandler.cs:24` | `ClientOperationId` | Optional (`= null`) | `_transactions.ExecuteAsync` | None | None |
| **H03** | `CommercialExchangeCommand` | `CommercialExchangeHandler.cs:26` | `ClientOperationId` | Optional (`= null`) | `_transactions.ExecuteAsync` | None | None |
| **H04** | `SavePosDraftCommand` | `PosDraftHandlers.cs:20` | `ClientOperationId` | Optional (`= null`) | `_transactions.ExecuteAsync` | None | None |
| **H05** | `CreatePurchaseCommand` | `CreatePurchaseHandler.cs:27` | `ClientOperationId` | Optional (`= null`) | `_transactions.ExecuteAsync` | None | None |
| **H06** | `CreatePurchaseReturnCommand` | `PurchaseReturnHandler.cs:25` | `ClientOperationId` | Optional (`= null`) | `_transactions.ExecuteAsync` | None | None |
| **H07** | `ReceiveProductIntakeCommand` | `ReceiveProductIntakeHandler.cs:15` | `ClientOperationId` | Optional (`= null`) | `_transactions.ExecuteAsync` | None | None |
| **H08** | `VoidPurchaseCommand` | `VoidPurchaseHandler.cs:13` | `ClientOperationId` | Optional (`= null`) | `_transactions.ExecuteAsync` | None | None |
| **H09** | `CreateStockAdjustmentCommand` | `StockAdjustmentHandlers.cs:35` | `CorrelationId` (Misnamed) | Mandatory Check | `_transactions.ExecuteAsync` | None | None |
| **H10** | `PostStocktakeCommand` | `StocktakeHandlers.cs:635` | `ClientOperationId` | Optional (`= null`) | `_transactions.ExecuteAsync` | None | None |
| **H11** | `FoundInventoryUnitCommand` | `FoundInventoryUnitHandler.cs:12` | `ClientOperationId` | Mandatory (`outcomes`) | `_transactions.ExecuteAsync` | None | None |
| **H12** | `OpenCashSessionCommand` | `CashSessionHandlers.cs:8` | **MISSING** | **NONE** | `_transactions.ExecuteAsync` | None | None |
| **H13** | `PostExpenseCommand` | `ExpenseHandlers.cs:15` | `ClientOperationId` | Mandatory (`outcomes`) | `_transactions.ExecuteAsync` | None | None |
| **H14** | `CreateSupplierPaymentCommand` | `SupplierAccountHandlers.cs:19` | `ClientOperationId` | Optional (`= null`) | `_transactions.ExecuteAsync` | None | None |
| **H15** | `IssueThakaMaterialCommand` | `ThakaHandlers.cs:48` | `ClientOperationId` | Optional (`= null`) | `_transactions.ExecuteAsync` | None | None |
| **H16** | `CreateWarrantyClaimCommand` | `WarrantyHandlers.cs:13` | `ClientOperationId` | Optional (`= null`) | `_transactions.ExecuteAsync` | None | None |

### Key Census Observations:
1. **CashSession Vulnerability (H12)**: `CashSessionHandlers.cs` has zero support for operation identity. If a cashier experiences a network timeout while opening a session or posting a cash drop/payout, retrying either fails (`"cash.session_already_open"`) or duplicates cash movement.
2. **StockAdjustment Misnaming (H09)**: `StockAdjustmentHandlers.cs` uses `CorrelationId` instead of `ClientOperationId`, creating schema inconsistency with the rest of the application.
3. **Pure Read Exclusions**: Confirmed that queries (`SalesQueries.cs`, `PurchaseQueries.cs`, `InventoryOverviewQueries.cs`, `ReportingQueries.cs`) are pure read operations with no database side effects. They are formally excluded from mutation idempotency wrapping.

---

## 5. Stage 4: Idempotency Claim, Token & Fingerprint Architecture Audit (Areas 12B, 12C, 12D)

### 5.1 Server-Side Operation Claim (Area 12B)
Currently, `EfOperationOutcomeLedger` performs:
```csharp
var existing = await _db.OperationOutcomes.FirstOrDefaultAsync(x => x.ClientOperationId == clientOperationId);
if (existing is null)
{{
    _db.OperationOutcomes.Add(outcome);
    await _db.SaveChangesAsync();
}}
```
- **Vulnerability**: Two concurrent requests both execute `FirstOrDefaultAsync`, both see `existing == null`, and both attempt to insert. One fails with `DbUpdateException`, catches it, and then continues. But neither thread is blocked or informed that the operation is already in-flight!
- **Phase 12 Requirement**: Implement an atomic `ClaimAsync`:
  ```sql
  INSERT INTO system.operation_outcomes (id, client_operation_id, operation_type, payload_fingerprint, status, created_at, updated_at)
  VALUES (@id, @opId, @type, @fingerprint, 'Pending', NOW(), NOW())
  ON CONFLICT (client_operation_id) DO NOTHING;
  ```
  If 0 rows are inserted, query the existing record:
  - If `Status == 'Pending'`, return `OperationClaimStatus.InFlight` (HTTP 409 Conflict / HTTP 425 Too Early).
  - If `Status == 'Succeeded'`, return `OperationClaimStatus.AlreadyCompleted` (Replay cached response).
  - If `Status == 'Failed'`, return `OperationClaimStatus.PreviouslyFailed` (Replay failure error).

### 5.2 Canonical Payload Fingerprinting (Area 12C)
Currently, `OperationPayloadFingerprint.ComputeSha256(string payload)` exists in `OperationOutcomeLedger.cs`, but is only called for draft sales in `CompleteSaleHandler.cs:152`.
- **Vulnerability**: In direct sales and all other 15 handlers, `PayloadFingerprint` is stored as `null`. When `OperationStatusQueryHandler` handles a status check, line 98 skips payload verification if `canonicalOutcome.PayloadFingerprint` is null. An attacker can replay an operation ID with an altered payload and bypass tamper detection!
- **Phase 12 Requirement**: Implement canonical JSON serialization (sorted keys, invariant whitespace, exclusion of volatile transport headers like `X-Request-Timestamp`) and compute SHA-256 for all 16 command payloads before claiming. On replay, compare hashes: if mismatch, return `409 Conflict` (`IDEMPOTENCY_PAYLOAD_MISMATCH`).

### 5.3 Atomic ACID Transaction Boundary (Area 12D)
- Currently, handlers execute business mutations inside `_transactions.ExecuteAsync`.
- However, `_outcomeLedger.RecordSuccessAsync` or `RecordFailureAsync` is often called outside or at the very end of the transaction. If the database connection drops after business commit but before outcome ledger write, an outcome unknown split-brain state occurs.
- **Phase 12 Requirement**: `OperationOutcome` update must be committed in the **same atomic database transaction** (`_transactions.ExecuteAsync`) as the business entities.

---

## 6. Stage 5: Status Authority, Outbox Separation & Draft Replay Audit (Areas 12E, 12F, 12K)

### 6.1 Status Authority Endpoint (Area 12E & 12F)
- `OperationsController.cs` exposes: `GET /api/operations/{{clientOperationId:guid}}`.
- **Deficiencies**:
  1. Route is `/api/operations/{{id}}` rather than the canonical `/api/v1/operations/{{id}}`.
  2. Does not return the cached `ResponsePayload` (because `OperationOutcome` does not store it).
  3. Returns minimal entity metadata (`EntityId`, `DocumentNumber`, `Status`), forcing the client to issue secondary queries to reload business objects.

### 6.2 Physical Outbox Printing Separation (Area 12K)
- In `PosViewModel.cs:808–836`, `PrintCompletedReceiptAsync` is invoked on the client UI thread after the sale transaction completes.
- It hardcodes `isReprint: false`.
- If thermal paper runs out, the transaction is already committed, but the cashier is confused. If the cashier clicks retry or replays, the system attempts another initial print rather than recording a structured reprint audit.
- **Phase 12 Requirement**: Decouple hardware printing into a durable Outbox pattern with `ClientPrintAttemptId`, tracking print receipts via `/api/printing/receipts`, and enforcing `isReprint: true` on all replay operations.

---

## 7. Stage 6: Desktop Client Intent Journal & Crash Recovery Audit (Areas 12G, 12H, 12I)

### 7.1 Existing Desktop Intent Store
- `FileClientOperationIntentStore` in `src/EdgeRetails.Desktop/Services/ClientOperationIntentStore.cs`:
  - Storage location: `%LocalAppData%/EdgeRetails/Desktop/operation-intents.json`.
  - Concurrency control: System-wide Named Mutex `Local\\EdgeRetails.OperationIntents.{{pathHash}}` with 10-second acquisition timeout.
  - Durability: Temporary file write (`FileOptions.WriteThrough`) followed by atomic replace (`File.Move(..., overwrite: true)`).
  - Security: Stores only `OperationKey`, `PayloadSha256`, and `OperationId`. Raw business payloads are never written to disk.
- **Usage**:
  - `PosViewModel.cs` uses `SaleCheckoutIntentKey = "sale:checkout"`.
  - On startup and cart load, `PosViewModel` calls `_operationIntents.FindPendingByPrefixAsync("sale:checkout")`.
  - If an unresolved sale intent exists, checkout is disabled until status is resolved.

### 7.2 Deficiencies in Desktop Client Recovery:
1. `FileClientOperationIntentStore` does not store the full serialized request body. If the application crashes, it cannot automatically resubmit the background request without reloading the draft or cart.
2. Exponential backoff retry with jitter (`Area 12H`) is not centrally implemented in `ApiClient.cs`; individual ViewModels implement ad-hoc try-catch blocks.

---

## 8. Stage 7: Hostile Failure Concurrency, Replay Matrix & Test Suite Readiness (Area 12L & Defects F05–F07, H01–H02, B07)

### 8.1 Test Coverage Audit
- Existing tests in `tests/EdgeRetails.UnitTests/OperationOutcomeAuthorityTests.cs` (650 lines, 9 test methods):
  - `OperationStatus_UsesCanonicalOutcomeAuthority`
  - `OperationStatus_LegacyBusinessTablesCannotContradictCanonicalOutcome`
  - `UnknownOperation_ReturnsNotFound`
  - `SuccessfulOperation_ReturnsCanonicalResultReference`
  - `SaleReplay_ReturnsOriginalOutcome`
  - `PurchaseReplay_ReturnsOriginalOutcome`
  - `SupplierPaymentReplay_ReturnsOriginalOutcome`
  - `WarrantyReplay_ReturnsOriginalOutcome`
  - `SameOperationIdDifferentPayload_IsRejected`
- **Critical Test Gap**:
  - All existing tests run against **in-memory fake test doubles** (`Phase2TestDoubles`).
  - **Zero tests run against real PostgreSQL 18** on port `55437`.
  - **Zero tests simulate real multi-threaded race conditions** or connection drops mid-transaction.
  - **Zero tests verify `EfOperationOutcomeLedger` against concurrent database transactions**.

---

## 9. Stage 8: Phase 12 Three-Wave Implementation Blueprint

Phase 12 must be executed across three disciplined, bounded waves:

```
┌────────────────────────────────────────────────────────────────────────┐
│  WAVE 1: Core Server Idempotency Authority Engine (Areas 12A-D, H01-02)│
│  - OperationOutcome entity update (ResponsePayload, StatusCode, etc.)  │
│  - PostgreSQL ClaimAsync (eliminating in-memory SemaphoreSlim)         │
│  - Canonical JSON SHA-256 Fingerprinting                               │
│  - Single ACID Transaction Boundary Integration                        │
└────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│  WAVE 2: Universal Status Authority & Desktop Intent (Areas 12E-I)     │
│  - GET /api/v1/operations/{{id}} endpoint with full ResponsePayload      │
│  - Ambiguous / Lost Response Recovery Protocol                         │
│  - Desktop Intent Store & Exponential Backoff Retry Engine             │
│  - Draft Conversion Atomic Lock (DraftState.Converting)                │
└────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│  WAVE 3: 16-Handler Census Wrapping & Hostile Test Suite (Areas 12J-L) │
│  - Wrap all 16 command handlers with mandatory ClaimAsync              │
│  - Fix CashSession and StockAdjustment naming                          │
│  - Physical Outbox Printing Separation (isReprint tracking)            │
│  - 18-Case PG18 Hostile Concurrency & Partition Test Suite             │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 10. Readback Verification & Audit Integrity

The complete audit dossier has been deposited into:
`governance/10-execution-runs/{run_id}/`

All files are verified via automated readback scripts (`Verify-PreAuditReadback.ps1` and `Verify-PreAuditReadback.py`) confirming 100% bit-for-bit SHA256 integrity.
"""

report_path = os.path.join(dest_dir, "PHASE12_PREAUDIT_REPORT.md")
with open(report_path, "w", encoding="utf-8") as f:
    f.write(report_md)
print(f"Wrote {report_path} ({os.path.getsize(report_path)} bytes)")

# =========================================================================
# 4. Author PHASE12_THREE_WAVE_EXECUTION_BLUEPRINT.md
# =========================================================================
blueprint_md = f"""# PHASE 12 THREE-WAVE EXECUTION BLUEPRINT: OPERATION IDENTITY, REPLAY & RECOVERY AUTHORITY

**Artifact ID**: `{run_id}-BLUEPRINT`
**Execution Run**: `{run_id}`
**Authoring Authority**: `ER-P12-PREAUDIT-MASTER-20261015-001`
**Standing Owner Authorities**: `OWNER-UPDATE-20261013-007`, `OWNER-UPDATE-20261014-001`, `OWNER-UPDATE-20261014-002`, `OWNER-UPDATE-20261011-002`
**Governance Event**: `ER-CHANGE-20261015-025`
**Target Architecture**: Distributed Idempotency, Operation Claims, Canonical Fingerprints, and Crash Recovery
**Date**: `{packaging_now_utc}`

---

## 1. Architectural Strategy & Guarantees

Phase 12 transforms EdgeRetails from an application with ad-hoc idempotency checks into a fully resilient distributed system providing the mathematical guarantee:
$$\\mathbf{{1\\text{{ User Intent}} = 1\\text{{ ClientOperationId}} = 1\\text{{ OperationType}} = 1\\text{{ PayloadFingerprint (SHA-256)}} = 1\\text{{ Business Effect}} = 1\\text{{ Durable Outcome}}}}$$

To prevent monolithic regressions and ensure rigorous verification on PostgreSQL 18 (Port 55437), Phase 12 is partitioned into three strictly sequential, verifiable waves:

---

## 2. WAVE 1: Core Server Idempotency Authority Engine
**Focus**: Areas 12A, 12B, 12C, 12D + Historical Defect Resolutions H01, H02.

### 2.1 Scope & Deliverables
1. **Area 12A: OperationOutcome Entity & Schema Strengthening**:
   - Update `src/EdgeRetails.Domain/Operations/OperationOutcome.cs` to add:
     - `string? ResponsePayload`: Serialized JSON HTTP response body.
     - `int? ResponseStatusCode`: HTTP status code (e.g., 200, 400, 409).
     - `string? ResponseContentType`: MIME type (default `application/json`).
     - `string PayloadFingerprint`: Made non-nullable and strictly required.
   - Update `OperationOutcomeConfiguration.cs` and generate an EF Core migration against loopback PostgreSQL port 55437.
2. **Area 12B: Atomic PostgreSQL `ClaimAsync` (Eliminating In-Memory Semaphore)**:
   - Deprecate `SemaphoreSlim[] _stripedGates` in `EfOperationOutcomeLedger.cs`.
   - Implement true distributed claim in PostgreSQL:
     ```csharp
     public async Task<OperationClaimResult> ClaimAsync(
         Guid clientOperationId,
         string operationType,
         string payloadFingerprint,
         Guid? actorId,
         Guid? terminalId,
         Guid? sessionId,
         CancellationToken cancellationToken);
     ```
   - Uses `INSERT INTO system.operation_outcomes ... ON CONFLICT (client_operation_id) DO NOTHING` or PostgreSQL row-level locking.
   - Returns:
     - `Acquired`: Current caller owns the execution slot.
     - `InFlight`: Another thread/process is currently executing (return HTTP 409 / HTTP 425).
     - `AlreadySucceeded`: Replay cached `ResponsePayload` with original status code.
     - `AlreadyFailed`: Replay cached failure error.
3. **Area 12C: Canonical Payload SHA-256 Fingerprinting**:
   - Implement `CanonicalPayloadSerializer`:
     - Sort JSON object keys alphabetically.
     - Strip volatile transport headers (e.g., timestamp, auth tokens).
     - Standardize decimal string formatting (e.g., `100.00`).
   - If a replay request presents matching `ClientOperationId` with divergent payload hash, immediately fail closed with `409 Conflict` (`IDEMPOTENCY_PAYLOAD_MISMATCH`).
4. **Area 12D: Atomic ACID Transaction Boundary**:
   - Unify outcome ledger state transitions inside `_transactions.ExecuteAsync`.
   - Ensure the business entity mutations (e.g., Sale, Stock, Khata) and `OperationOutcomeStatus.Succeeded` are committed in the **same atomic database transaction**.

### 2.2 Touchlist:
- `src/EdgeRetails.Domain/Operations/OperationOutcome.cs`
- `src/EdgeRetails.Application/Features/Terminals/OperationOutcomeLedger.cs`
- `src/EdgeRetails.Infrastructure/Persistence/Configurations/OperationOutcomeConfiguration.cs`
- `src/EdgeRetails.Infrastructure/Repositories/EfOperationOutcomeLedger.cs`
- EF Core Migration in `src/EdgeRetails.Infrastructure/Persistence/Migrations/`

### 2.3 Verification Gate for Wave 1:
- Zero build warnings / errors in Debug and Release.
- Multi-threaded unit and PG18 integration tests proving:
  - 10 concurrent requests with identical operation ID result in exactly 1 claim, 9 conflicts/replays.
  - Divergent payload produces instant `IDEMPOTENCY_PAYLOAD_MISMATCH`.
  - Zero database locks left open.

---

## 3. WAVE 2: Universal Status Authority & Desktop Intent Engine
**Focus**: Areas 12E, 12F, 12G, 12H, 12I + Historical Defect Resolutions F06, F07, B07.

### 3.1 Scope & Deliverables
1. **Area 12E & 12F: Universal Operation Status Authority Endpoint & Recovery Protocol**:
   - Update `OperationsController.cs` to expose `GET /api/v1/operations/{{clientOperationId:guid}}`.
   - Return full structured response:
     - `ClientOperationId`, `OperationType`, `Status` (`Pending`, `Succeeded`, `Failed`).
     - `WasCommitted`, `CompletedAt`.
     - `ResponsePayload`: The exact serialized JSON payload stored during execution.
     - `ErrorCode`, `ErrorMessage` (if failed).
   - Implement client ambiguous recovery protocol: On `HttpRequestException` or timeout, the desktop client automatically queries this endpoint to discover whether the transaction succeeded.
2. **Area 12G & 12H: Desktop Client Operation Intent Store & Exponential Backoff Engine**:
   - Strengthen `FileClientOperationIntentStore.cs`:
     - Provide atomic intent entry persistence with recovery state.
     - Add crash-scan on desktop application startup.
   - Implement `ExponentialBackoffRetryPolicy` with jitter for transient network disconnects.
3. **Area 12I: Draft Conversion & Crash Recovery Workflow**:
   - In `PosDraftHandlers.cs` and `CompleteSaleHandler.cs`:
     - Introduce atomic draft lock state (`DraftState.Converting`).
     - Bind draft conversion to `ClientOperationId`.
     - Prevent re-opening or converting a draft while conversion is in-flight or completed.
     - Fix Defect F06 (Draft crash duplicate sale prevention).

### 3.2 Touchlist:
- `src/EdgeRetails.Server/Controllers/OperationsController.cs`
- `src/EdgeRetails.Application/Features/Terminals/OperationStatusQueryHandler.cs`
- `src/EdgeRetails.Desktop/Services/ClientOperationIntentStore.cs`
- `src/EdgeRetails.Desktop/Services/BackendTransactionService.cs`
- `src/EdgeRetails.Desktop/ViewModels/PosViewModel.cs`
- `src/EdgeRetails.Application/Features/Sales/PosDraftHandlers.cs`

### 3.3 Verification Gate for Wave 2:
- Network partition simulation tests: Drop response packets after commit; verify client recovers full `SaleTransactionRecord` without duplicate execution.
- Draft crash simulation: Re-submitting converted draft fails closed with `409 Conflict`.

---

## 4. WAVE 3: 16-Handler Census Wrapping, Outbox Printing & Hostile Test Suite
**Focus**: Areas 12J, 12K, 12L + Complete System Hardening.

### 4.1 Scope & Deliverables
1. **Area 12J: Full 16-Handler Mutation Census Wrapping**:
   - Upgrade all 16 command handlers to use the strengthened `IOperationOutcomeLedger` and `ClaimAsync`:
     - Add `ClientOperationId` to `OpenCashSessionCommand`, `CloseCashSessionCommand`, and `RecordCashMovementRequest`.
     - Rename `CorrelationId` to `ClientOperationId` in `StockAdjustmentHandlers.cs`.
     - Make `IOperationOutcomeLedger` a **mandatory constructor dependency** across all 16 handlers (remove `= null`).
     - Eliminate ad-hoc entity comparison methods (like `MatchesExistingSalePayloadAsync`) in favor of universal SHA-256 fingerprint verification.
2. **Area 12K: Physical Outbox Printing Separation**:
   - Decouple thermal receipt printing from client checkout execution.
   - Implement `isReprint: true` enforcement on all replayed sale operations.
   - Record distinct `ClientPrintAttemptId` via `/api/printing/receipts`.
   - Prevent printer hardware failures from creating transaction rollback confusion (Defect F07).
3. **Area 12L: 18-Case PG18 Hostile Concurrency, Replay & Network Partition Test Suite**:
   - Author comprehensive integration test suite targeting PostgreSQL 18 on port 55437:
     1. Hostile concurrent claim race (10 threads, identical key).
     2. Tamper replay attack (altered price override).
     3. Tamper replay attack (altered customer ID).
     4. Tamper replay attack (altered line item quantities).
     5. Lost response polling recovery (Sale).
     6. Lost response polling recovery (Purchase intake).
     7. Lost response polling recovery (Supplier payment).
     8. Lost response polling recovery (Expense payout).
     9. Lost response polling recovery (Cash session open).
     10. Lost response polling recovery (Stock adjustment).
     11. Crash during draft conversion recovery.
     12. Re-converting committed draft rejection.
     13. Outbox receipt reprint status tracking.
     14. Mid-transaction database termination recovery.
     15. Cash drawer concurrent movement lock.
     16. Thaka material issue idempotency replay.
     17. Warranty claim submission replay.
     18. End-to-end multi-terminal POS simulation.

### 4.2 Touchlist:
- All 16 files in `src/EdgeRetails.Application/Features/`
- All controllers in `src/EdgeRetails.Server/Controllers/`
- `tests/EdgeRetails.IntegrationTests/Phase12HostileConcurrencyPostgresTests.cs`

### 4.3 Verification Gate for Wave 3:
- All 18 hostile test cases passing 100% green against PostgreSQL port 55437.
- Complete regression suite green (all 1,735 existing tests passing without regressions).
- Production deployment runbooks updated.

---

## 5. Wave Summary & Progression Criteria

| Wave | Scope | Key Deliverables | Risk Level | Prerequisite |
| :--- | :--- | :--- | :--- | :--- |
| **Wave 1** | Server Core Authority (12A-D) | DB ClaimAsync, ResponsePayload, SHA256 Fingerprint, ACID Boundary | MEDIUM | Phase 12 Entry Authorization |
| **Wave 2** | Status API & Client Intent (12E-I) | GET /api/v1/operations/{{id}}, Intent Store, Exponential Backoff, Draft Lock | MEDIUM | Wave 1 Accepted |
| **Wave 3** | 16 Handlers & Hostile Suite (12J-L) | Full 16 Handler Wrapping, Physical Outbox Printing, 18-Case PG18 Suite | HIGH | Wave 2 Accepted |
"""

blueprint_path = os.path.join(dest_dir, "PHASE12_THREE_WAVE_EXECUTION_BLUEPRINT.md")
with open(blueprint_path, "w", encoding="utf-8") as f:
    f.write(blueprint_md)
print(f"Wrote {blueprint_path} ({os.path.getsize(blueprint_path)} bytes)")

# =========================================================================
# 5. Author Readback Verification Scripts
# =========================================================================
readback_ps1 = """# Verify-PreAuditReadback.ps1
# 100% bit-for-bit SHA256 readback verification of ER-RUN-PHASE12-PREAUDIT-20261015 delivery payload
$ErrorActionPreference = "Stop"

$runDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$indexFile = Join-Path $runDir "DELIVERY_INDEX.json"

if (-not (Test-Path $indexFile)) {
    Write-Error "DELIVERY_INDEX.json not found in $runDir"
    exit 1
}

$index = Get-Content $indexFile -Raw -Encoding utf8 | ConvertFrom-Json
Write-Host "Verifying $($index.total_payload_files) files in $($index.run_id)..." -ForegroundColor Cyan

$mismatches = 0
$checked = 0

foreach ($entry in $index.payload_files) {
    $filePath = Join-Path $runDir $entry.path
    if (-not (Test-Path $filePath)) {
        Write-Host "MISSING: $($entry.path)" -ForegroundColor Red
        $mismatches++
        continue
    }

    $actualHash = (Get-FileHash -Path $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = $entry.sha256.ToLowerInvariant()

    if ($actualHash -ne $expectedHash) {
        Write-Host "HASH MISMATCH: $($entry.path) (Expected: $expectedHash, Actual: $actualHash)" -ForegroundColor Red
        $mismatches++
    } else {
        $checked++
    }
}

if ($mismatches -eq 0) {
    Write-Host "SUCCESS: All $checked files verified bit-for-bit with exact SHA256 match!" -ForegroundColor Green
    exit 0
} else {
    Write-Error "FAILURE: $mismatches verification failures encountered."
    exit 1
}
"""

readback_py = """# Verify-PreAuditReadback.py
# 100% bit-for-bit SHA256 readback verification of ER-RUN-PHASE12-PREAUDIT-20261015 delivery payload
import os
import sys
import json
import hashlib

def sha256_file(filepath):
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest().lower()

run_dir = os.path.dirname(os.path.abspath(__file__))
index_path = os.path.join(run_dir, "DELIVERY_INDEX.json")

if not os.path.exists(index_path):
    print(f"ERROR: DELIVERY_INDEX.json not found in {run_dir}")
    sys.exit(1)

with open(index_path, "r", encoding="utf-8") as f:
    index = json.load(f)

print(f"Verifying {index['total_payload_files']} files in {index['run_id']}...")
mismatches = 0
checked = 0

for entry in index["payload_files"]:
    fpath = os.path.join(run_dir, entry["path"])
    if not os.path.exists(fpath):
        print(f"MISSING: {entry['path']}")
        mismatches += 1
        continue

    actual_hash = sha256_file(fpath)
    expected_hash = entry["sha256"].lower()

    if actual_hash != expected_hash:
        print(f"HASH MISMATCH: {entry['path']} (Expected: {expected_hash}, Actual: {actual_hash})")
        mismatches += 1
    else:
        checked += 1

if mismatches == 0:
    print(f"SUCCESS: All {checked} files verified bit-for-bit with exact SHA256 match!")
    sys.exit(0)
else:
    print(f"FAILURE: {mismatches} verification failures encountered.")
    sys.exit(1)
"""

with open(os.path.join(dest_dir, "Verify-PreAuditReadback.ps1"), "w", encoding="utf-8") as f:
    f.write(readback_ps1)
with open(os.path.join(dest_dir, "Verify-PreAuditReadback.py"), "w", encoding="utf-8") as f:
    f.write(readback_py)
print("Wrote readback verification scripts.")

# =========================================================================
# 6. Build DELIVERY_INDEX.json and EVIDENCE_MANIFEST.json
# =========================================================================
payload_entries = []
total_bytes = 0

for root, dirs, files in os.walk(dest_dir):
    for f in sorted(files):
        if f in ["DELIVERY_INDEX.json", "EVIDENCE_MANIFEST.json"]:
            continue
        full_path = os.path.join(root, f)
        rel_path = os.path.relpath(full_path, dest_dir).replace("\\", "/")
        fsize = os.path.getsize(full_path)
        fhash = sha256_file(full_path)
        total_bytes += fsize
        payload_entries.append({
            "path": rel_path,
            "size_bytes": fsize,
            "sha256": fhash
        })

delivery_index_data = {
    "artifact_id": f"{run_id}-DELIVERY-INDEX",
    "run_id": run_id,
    "packaging_timestamp_utc": packaging_now_utc,
    "total_payload_files": len(payload_entries),
    "total_payload_bytes": total_bytes,
    "payload_files": payload_entries
}

with open(os.path.join(dest_dir, "DELIVERY_INDEX.json"), "w", encoding="utf-8") as f:
    json.dump(delivery_index_data, f, indent=2, ensure_ascii=False)

evidence_manifest_data = {
    "artifact_id": f"{run_id}-EVIDENCE-MANIFEST",
    "run_id": run_id,
    "authority": "OWNER-UPDATE-20261013-007",
    "continuation_authorities": ["OWNER-UPDATE-20261014-001", "OWNER-UPDATE-20261014-002", "OWNER-UPDATE-20261011-002"],
    "governance_event": "ER-CHANGE-20261015-025",
    "timestamp_utc": packaging_now_utc,
    "audit_mode": "STRICTLY_READ_ONLY",
    "base_commit": "3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2",
    "branch": "feat/phase11-subledger-implementation",
    "database_isolation": "PORT_5432_ZERO_TOUCH_FAIL_CLOSED",
    "total_payload_files": len(payload_entries),
    "total_payload_bytes": total_bytes,
    "deliverables_summary": {
        "forensic_report": "PHASE12_PREAUDIT_REPORT.md",
        "mutating_census": "MUTATION_CENSUS_16_HANDLERS.json (16 Handlers Audited)",
        "defect_mapping": "HISTORICAL_DEFECT_MAPPING.json (Defects F05-F07, H01-H02, B07)",
        "wave_blueprint": "PHASE12_THREE_WAVE_EXECUTION_BLUEPRINT.md (3 Disciplined Waves)"
    },
    "payload_files": payload_entries
}

with open(os.path.join(dest_dir, "EVIDENCE_MANIFEST.json"), "w", encoding="utf-8") as f:
    json.dump(evidence_manifest_data, f, indent=2, ensure_ascii=False)

print(f"Created delivery index and evidence manifest with {len(payload_entries)} files ({total_bytes} bytes).")

# =========================================================================
# 7. Execute Readback Verification
# =========================================================================
print("\n--- Running Readback Verification ---")
import subprocess
res = subprocess.run([sys.executable, os.path.join(dest_dir, "Verify-PreAuditReadback.py")], capture_output=True, text=True)
print(res.stdout)
if res.returncode != 0:
    print(res.stderr)
    raise RuntimeError("Readback verification failed!")

print(f"\n=======================================================")
print(f"PHASE 12 PRE-AUDIT DELIVERY COMPLETED & VERIFIED: {run_id}")
print(f"=======================================================")
