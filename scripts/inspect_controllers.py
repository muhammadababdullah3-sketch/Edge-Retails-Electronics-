import os
import re

controllers = [
    "src/EdgeRetails.Server/Controllers/SalesController.cs",
    "src/EdgeRetails.Server/Controllers/PurchasingController.cs",
    "src/EdgeRetails.Server/Controllers/InventoryController.cs",
    "src/EdgeRetails.Server/Controllers/FinanceController.cs",
    "src/EdgeRetails.Server/Controllers/ExpensesController.cs",
    "src/EdgeRetails.Server/Controllers/ThakaController.cs",
    "src/EdgeRetails.Server/Controllers/WarrantyController.cs",
    "src/EdgeRetails.Server/Controllers/OperationsController.cs",
]

for p in controllers:
    full_path = os.path.abspath(p)
    with open(full_path, "r", encoding="utf-8") as f:
        text = f.read()

    print(f"\n==========================================")
    print(f"Controller: {p}")
    # find HTTP attributes and actions
    actions = re.findall(r"\[Http(Post|Put|Get|Delete)\s*(?:\(\"([^\"]*)\"\))?\s*\]\s*(?:\[[^\]]+\]\s*)*public\s+(?:async\s+)?Task<IActionResult>\s+([A-Za-z0-9_]+)\s*\((.*?)\)", text, re.DOTALL)
    for method, route, name, params in actions:
        has_op_id = "clientOperationId" in params or "ClientOperationId" in params or "OperationId" in params
        first_param = params.split(",")[0].strip() if params else ""
        print(f"  {method} \"{route}\" -> {name} (opId: {has_op_id}) | {first_param}")
