# Edge Retails — Phase7 Pass3 candidate drift reconciliation and refreeze

## 1. Verdict

**PASS3_REFREEZE_DENIED_UNEXPLAINED_DRIFT**

**New candidate manifest: NOT CREATED.** The old manifest and both previous reports remain preserved. No Pass3 reimplementation, Desktop reversal, database access, migration, test execution or Git mutation was performed.

The seven Pass3 production/test files remain byte-identical to the frozen manifest. However, the current monitored candidate has **six Desktop mismatches**, not the five named in the reconciliation request. **SearchBox.xaml is additional drift.** CompleteSaleDialog.xaml and SalesReturnDialog.xaml also changed between this review's opening and closing hashes. Exact post-freeze authorization and the frozen Desktop file contents were not recovered from available evidence.

Fresh Debug and Release solution builds both succeeded with **0 warnings and 0 errors**. Compile success does not waive provenance, additional-drift or candidate-stability gates.

## 2. Authority, scope and execution ledger

Current user authority: `C:\Users\muham\.codex\attachments\a18cebce-995a-4fe3-8402-f9707ff097ad\Pasted text.txt`, especially §§3–11. Previous authority and canonical repository instructions were established during the immediately preceding challenge; no applicable AGENTS.md was found. The new prompt authorizes read-only reconciliation, two fresh builds, this report, and a new manifest **only if every refreeze condition passes**.

| Gate | Scope | Evidence required | Command/inspection | Status |
|---|---|---|---|---|
| B/C preservation | Five production/two tests | Exact old-manifest hashes | Opening and closing full manifest recomputation | **PASS 5/5 + 2/2** |
| Desktop purpose | Five named XAML files | Diff, attributes, commands, events, styles, timestamps | Git HEAD diff + current XML/source inspection | Current purpose mapped; exact freeze-to-live delta **NOT_PROVEN** |
| Frontend provenance | Separate work authority | Reports/evidence tied to current bytes | Frontend Pass1/2 reports/manifests and scratch artifacts | Scope association supported; current authorization **UNKNOWN** |
| Dependency | Backend gates and UI callers | Project graph and unchanged payload/command sources | ProjectReference + binding/event comparisons | Backend compiled dependency absent; exact old UI equivalence not established |
| Debug build | Current solution | Terminal compile result | dotnet build EdgeRetails.sln -c Debug | **PASS 0 warnings / 0 errors** |
| Release build | Current solution | Terminal compile result | dotnet build EdgeRetails.sln -c Release | **PASS 0 warnings / 0 errors** |
| Additional drift | Beyond named five | Zero unexpected monitored changes | Full813 recomputation | **FAIL: SearchBox.xaml** |
| Stability | Single eligible candidate | Stable reviewed bytes | Opening/closing hashes | **FAIL: two dialogs changed during review** |
| Manifest coverage | Source/test inventory | No silently omitted candidate inputs | rg src/tests/scripts vs manifest paths | Two frontend test files absent from old manifest |
| Safe refreeze | All conditions | All required gates PASS | Gate reconciliation | **DENIED** |

All started builds reached terminal results before this report was created. No Pass3 PostgreSQL execution was launched.

## 3. Old manifest and candidate identity

Old manifest: [EDGE_RETAILS_PHASE7_PASS3_FINAL_SOURCE_MANIFEST.sha256](<C:/Users/muham/OneDrive/Desktop/Point of Sale/EDGE_RETAILS_PHASE7_PASS3_FINAL_SOURCE_MANIFEST.sha256>).  
SHA256: `E20CB2A543F9801E5AD4705E2F06532EFB51B3BF00C18B9E52E4BE3D6CE747B2`.  
Branch: `tracking-remediation-20261002`.  
HEAD: `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`.

| Observation | Opening 2026-10-04T09:24:00.0621789Z | Closing 2026-10-04T09:32:44.1791605Z |
|---|---:|---:|
| Listed paths | 813 | 813 |
| Matching hashes | 807 | 807 |
| Desktop mismatches | 6 | 6 |
| Class B matches | 5/5 | 5/5 |
| Class C matches | 2/2 | 2/2 |

The previous Sol challenge recorded808/813 with five Desktop mismatches at2026-10-04T09:15:26.2870906Z. Its historical statement was correct for that audit; it is not the current six-file state.

## 4. Exact Pass3 byte-preservation proof

All seven expected/current hashes below match at both audit boundaries.

| File | Class | Frozen/current SHA256 | Match |
|---|---|---|---|
| [src\EdgeRetails.Application\Features\Purchasing\VoidPurchaseHandler.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/VoidPurchaseHandler.cs>) | B | `DE9DB990BDFB447F0F69A64948FE8E0AD049DA702106E4DDB60DFDE9D972D37A` | YES |
| [src\EdgeRetails.Application\Features\Sales\CommercialExchangeHandler.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Sales/CommercialExchangeHandler.cs>) | B | `DBE549C5BAE0D51FE92D51A55270B15DC86681BBB03BC9F9590DEA5721987C9C` | YES |
| [src\EdgeRetails.Application\Features\Sales\PosDraftHandlers.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Sales/PosDraftHandlers.cs>) | B | `F4D940C9824F33AAD04A141EC338970E2404826AD608DBF876E5C6657D2FD4CE` | YES |
| [src\EdgeRetails.Infrastructure\Persistence\EdgeRetailsDbContext.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs>) | B | `D325385DDC631EE62563502BCB8158549DDA69EED8568E2D39BC0DB247FAD4CA` | YES |
| [src\EdgeRetails.Infrastructure\Services\BusinessOperationsReadServices.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/BusinessOperationsReadServices.cs>) | B | `BF7ACB493BEE90FCDF569ECE7079869AAC79E2F4B34E151854303BD7C5624E1E` | YES |
| [tests\EdgeRetails.IntegrationTests\Phase7Pass3PostgresTests.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass3PostgresTests.cs>) | C | `42173B48D22187EA7486B0E4D7D7D45D1E6A4275241F4D14645CEC58CACDC568` | YES |
| [tests\EdgeRetails.UnitTests\Phase7Pass3IntegrityTests.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.UnitTests/Phase7Pass3IntegrityTests.cs>) | C | `8727EDF26FB965612017D53B0A843972A4A75154B74AF15883CB3A3890AEA08B` | YES |

This confirms preservation of Pass3 bytes, not their correctness or final certification. No fix to these files was authorized or attempted.

## 5. Desktop drift classification and provenance

| File | Semantic classification | Observed purpose | Provenance/authorization verdict |
|---|---|---|---|
| [src\EdgeRetails.Desktop\Controls\SearchBox.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Controls/SearchBox.xaml>) | UI_VISUAL_ONLY (additional file) | Embedded input alignment/pixel snapping/focus brush and placeholder alignment. Text TwoWay/PropertyChanged and IsReadOnly bindings unchanged versus HEAD; code-behind listed hash matches. | PROVENANCE_UNKNOWN / D |
| [src\EdgeRetails.Desktop\Resources\Inputs.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Resources/Inputs.xaml>) | UI_VISUAL_ONLY (observed HEAD delta) | Embedded TextBox/compact styles, pixel snapping, focus brushes/rings and a read-only background trigger. Existing IsDropDownOpen template binding retained; no business command or service payload added. | PROVENANCE_UNKNOWN / D |
| [src\EdgeRetails.Desktop\Views\Dialogs\CompleteSaleDialog.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/CompleteSaleDialog.xaml>) | UI_VISUAL_ONLY (observed HEAD delta) | AmountReceivedHost focus border, embedded amount input, checkbox/chip/button focus visuals. AmountReceivedText TwoWay/PropertyChanged binding, payment commands and completion command unchanged versus HEAD. | PROVENANCE_UNKNOWN / D |
| [src\EdgeRetails.Desktop\Views\Dialogs\SalesReturnDialog.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/SalesReturnDialog.xaml>) | UI_VISUAL_ONLY (observed HEAD delta) | Named embedded quantity input and focus border; disposition radio focus styles. ReturnQuantityText, Increment/Decrement, disposition parameters, validation and ProcessReturnCommand unchanged versus HEAD. | PROVENANCE_UNKNOWN / D |
| [src\EdgeRetails.Desktop\Views\PosView.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/PosView.xaml>) | UI_VISUAL_ONLY (observed HEAD delta) | DiscountAmountHost/input focus border, embedded input and alignment. DiscountAmountText, SaveDraft/Hold/CompleteSale/Scan commands and catalog event handler unchanged versus HEAD. | PROVENANCE_UNKNOWN / D |
| [src\EdgeRetails.Desktop\Views\ShellView.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/ShellView.xaml>) | UI_BEHAVIORAL | ContentControl KeyboardNavigation.TabNavigation changes Cycle to Continue; changes keyboard traversal across page/shell. CurrentPage binding and modal/drawer services remain unchanged. | PROVENANCE_UNKNOWN / D |

The primary A/B/C/D classification is **D: unknown / insufficient evidence of exact authorization** for the post-freeze changes. Their current content has a frontend focus/geometry purpose; that alone cannot prove category A (authorized changes). No author identity is inferred from timestamps, filenames, tool scripts or certification prose. No evidence established that these are Pass3-backend changes (B), and no evidence established accidental edits (C).

**Scope association:** `UNRELATED_CHANGE_PROVEN_BY_SCOPE_ONLY` for the observed presentation changes relative to Git HEAD. **Exact post-freeze provenance:** `PROVENANCE_UNKNOWN`.

### Full hash/timestamp table

| File | Old manifest hash | Current closing hash | Last-write UTC | Git status |
|---|---|---|---|---|
| [src\EdgeRetails.Desktop\Controls\SearchBox.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Controls/SearchBox.xaml>) | `9DD9AE3A70FD9D0FC982FBD3CA963C5312E559F4D5ED8FD100719A3B81B84828` | `2E4BEA99F8745050FD5408EC5AD4CBB305F9EA9D08E437B4DECC79E74CEAD1D5` | 2026-10-04T09:17:14.4865949Z | M |
| [src\EdgeRetails.Desktop\Resources\Inputs.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Resources/Inputs.xaml>) | `A6D2D27DF7EA8EB028E1C2BA1D6BB88F383ACB032782FC9C53BE98DF95BB3376` | `872D874461826F797DFD4E423E822150A7C0E302DF991F60A57A801E79DC0AA0` | 2026-10-04T09:17:14.4843909Z | M |
| [src\EdgeRetails.Desktop\Views\Dialogs\CompleteSaleDialog.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/CompleteSaleDialog.xaml>) | `E32BC887194A80E8D65C649EA0ED2AE8FC367ACF050282B126C663E6157D523E` | `C57D2A1ECF32575EA8AF07ACE58204C36A6DEF2659E0836E7D6783418F422D75` | 2026-10-04T09:24:34.3000878Z | M |
| [src\EdgeRetails.Desktop\Views\Dialogs\SalesReturnDialog.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/SalesReturnDialog.xaml>) | `53E9BD3CA4BE0CB8262CD4942F8E2DD27AB0D830A5E158949E3CA2B95C07ABB0` | `D53AC50BE14A494E8FE0B28F2C472BC64F8BAF186C3DC7959E797E78C5349EEA` | 2026-10-04T09:24:34.3102861Z | M |
| [src\EdgeRetails.Desktop\Views\PosView.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/PosView.xaml>) | `F7A17AD1FEE56213DB79AB4002F3A802D6EDFCE470D1186032C51595B4386A12` | `2937E955E56DAE699D5B2F7403817543915EAF79548B69EB84BB5C3CBA8FAFAB` | 2026-10-04T09:17:14.4921313Z | M |
| [src\EdgeRetails.Desktop\Views\ShellView.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/ShellView.xaml>) | `6D1972174AC120A3B517318DA4D01440694514C872C009976F1CFC51EAE8AB18` | `C1E59EE291A6DA44E9D834A0F00021BDE568525943C52C1341B5B22B516B6B68` | 2026-10-04T09:03:21.4793197Z | M |

### Changes during this review

| File | Opening hash | Closing hash |
|---|---|---|
| [src\EdgeRetails.Desktop\Views\Dialogs\CompleteSaleDialog.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/CompleteSaleDialog.xaml>) | `21960FBC0265A7D922DCB3C48324D68EBE85BE594B3E472895C8F63B3CF2425C` | `C57D2A1ECF32575EA8AF07ACE58204C36A6DEF2659E0836E7D6783418F422D75` |
| [src\EdgeRetails.Desktop\Views\Dialogs\SalesReturnDialog.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/SalesReturnDialog.xaml>) | `AEEC7AD79B30C454D0D5D52F832916516F7193FB4D7D0CCA2469E9EF949D21A9` | `D53AC50BE14A494E8FE0B28F2C472BC64F8BAF186C3DC7959E797E78C5349EEA` |

These changes were not made by this reconciliation. The last-write times lie after the opening hash timestamp. Existing/concurrent workspace changes were preserved.

## 6. Diff baseline limitation

The old manifest preserves hashes, **not old source contents**. The bounded artifact/scratch search found no exact frozen copies of the five Desktop files. Available Frontend Pass1/2 manifests repeat the old hashes but do not supply their exact bytes.

Git HEAD is `97956831...`; its Desktop source predates the frontend working-tree changes already present at the old Pass3 freeze. Therefore **git diff HEAD is not the exact frozen-working-tree-to-current diff**. This report keeps the distinction explicit. The complete HEAD comparison is retained in Appendix A; it is not mislabeled an exact post-freeze reconstruction.

Current XML comparisons against HEAD found:

- Command/CommandParameter, event/handler, converter/validation and IsEnabled/IsReadOnly/Visibility/IsChecked attribute inventories unchanged for the five inspected views/control.
- Existing data bindings unchanged. Three added bindings observe IsKeyboardFocused on amount/quantity/discount inputs and set only BorderBrush.
- Input resource bindings retained; added styles/template visual setters and triggers were inspected.
- Shell's TabNavigation changed from Cycle to Continue, which is UI behavior, not harmless color styling.
- Resource references changed to embedded input/focus styles. No new service contract, request field, operation ID, actor binding or business condition was found in the HEAD diff.

Attribute inventory comparisons are supporting static evidence, not universal rendered/runtime behavior proof. No UI acceptance or authentication was performed.

## 7. Frontend workstream evidence

| Evidence | What it establishes | What it does not establish |
|---|---|---|
| [docs/EDGE_RETAILS_FRONTEND_PASS1_VERIFICATION.md](<C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/EDGE_RETAILS_FRONTEND_PASS1_VERIFICATION.md:20>) | Separate frontend geometry scope names Inputs, SearchBox, CompleteSale, Pos and SalesReturn | Authorization/currentness of every later edit |
| [docs/Frontend_Pass1_Candidate_Hash_Manifest.md](<C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Frontend_Pass1_Candidate_Hash_Manifest.md:18>) | Old hashes for SearchBox/CompleteSale/Pos/SalesReturn match old Pass3 manifest values | Current modified byte approval |
| [docs/EDGE_RETAILS_FRONTEND_PASS2_VERIFICATION.md](<C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/EDGE_RETAILS_FRONTEND_PASS2_VERIFICATION.md:21>) | Focus/accessibility scope, shared styles and shell traversal surface | Exact later Shell Cycle→Continue authorization |
| [docs/Frontend_Pass2_Candidate_Hash_Manifest.md](<C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Frontend_Pass2_Candidate_Hash_Manifest.md:21>) | Inputs old hash A6D2... matches Pass3 frozen hash | Current Inputs872D... provenance |
| [scratch/apply_pass2_focus.py](<C:/Users/muham/OneDrive/Desktop/Point of Sale/scratch/apply_pass2_focus.py:126>) | An available frontend focus script targets input style changes | Evidence that this script produced all later bytes, or who ran it |
| [scratch/FrontendPass2RuntimeProbe/Program.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/scratch/FrontendPass2RuntimeProbe/Program.cs:44>) | Separate frontend runtime probe instantiates POS/Shell and checks focus geometry | Business correctness or current source authorization |

Frontend reports cite a master roadmap that was not located in the bounded workspace filename search. Current frontend reports/manifests do not hash-bind the six current Desktop files as one approved candidate.

The latest current FrontendPass2FocusAccessibilityTests file also differs from the old frontend report hash. Its last-write time is2026-10-04T09:25:23.0999518Z, during this review. No certification/reuse of that frontend test suite is made here.

## 8. Pass3 dependency analysis

### Actual UI adjacency

| Pass3 subject | Current relationship | Drift impact conclusion |
|---|---|---|
| VoidPurchase | Purchasing adapters submit VoidPurchaseCommand; no changed purchasing view/adapter in drift set | No changed request/handler path found |
| CommercialExchange | No new exchange command in these XAML differences; backend handler bytes preserved | No changed backend intent/custody calculation found |
| POS Draft completion | PosView → CompleteSaleViewModel → BackendTransactionService can route CompletePosDraftCommand | UI adjacency exists; command/data bindings unchanged vs HEAD; frozen-to-live UI equality not proven |
| Warranty return/exchange | SalesReturnDialog edits ReturnQuantityText/disposition and invokes ProcessReturn; backend adapter uses CreateSaleReturnCommand | Existing return inputs unchanged vs HEAD; visual/focus changes do not rewrite warranty authority |
| Reporting calculations | Shell hosts pages but calculation implementation and backend projects unchanged | No numeric formula dependency on XAML |
| EF append-only behavior | Infrastructure DbContext has no Desktop project dependency | No XAML dependency |

Relevant source: [src/EdgeRetails.Desktop/Services/BackendTransactionService.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/BackendTransactionService.cs:155>), [src/EdgeRetails.Desktop/Services/BackendTransactionService.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/BackendTransactionService.cs:467>), [src/EdgeRetails.Desktop/ViewModels/CompleteSaleViewModel.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/CompleteSaleViewModel.cs:420>), [src/EdgeRetails.Desktop/Views/PosView.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/PosView.xaml:277>), [src/EdgeRetails.Desktop/Views/Dialogs/SalesReturnDialog.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/SalesReturnDialog.xaml:279>).

**PASS3_BEHAVIORAL_DEPENDENCY = NONE_PROVEN for compiled backend test/calculation execution.** This statement does not claim zero UI adjacency, equivalence to unrecovered frozen XAML bytes, or certified keyboard interaction. Shell traversal is explicitly UI_BEHAVIORAL.

### Compiled dependency graph

UnitTests references Domain, Application, Infrastructure, Worker and Server; IntegrationTests references Domain, Application, Infrastructure and Server. Neither references Desktop. Domain/Application/Infrastructure/Server/Worker project references do not lead back to Desktop.

Pass3 unit/PG test files contain no Desktop/XAML reads in the targeted dependency search. All monitored backend source, project/test files and migration/snapshot bytes remain unchanged; the six mismatches are exclusively Desktop XAML. Therefore those XAML differences cannot change compiled backend handler/test execution through a project dependency.

This is bounded source/dependency eligibility. It does not validate every previous test discovery inventory, runtime assumption, concurrency assertion or hash omission.

## 9. Fresh build results

| Command | Exit code | Passed | Failed | Skipped | Database provider | Completion status |
|---|---:|---:|---:|---:|---|---|
| dotnet build EdgeRetails.sln -c Debug | 0 | 1 build gate | 0 | 0 | N/A — no database execution | PASS |
| dotnet build EdgeRetails.sln -c Release | 0 | 1 build gate | 0 | 0 | N/A — no database execution | PASS |

Debug:0 warnings /0 errors; elapsed00:01:59.77.  
Release:0 warnings /0 errors; elapsed00:00:09.56.

Both reached “Build succeeded.” terminal output; all solution projects, including Desktop, compiled. Builds generated normal bin/obj output under the explicitly authorized commands. No source/test edits were used to obtain green builds.

The source was not globally frozen throughout the whole review. Build success is evidence of compilation, not proof that every opening Desktop hash was the sole input. No refrozen candidate is inferred from the build result.

## 10. Test-impact and old-evidence decision

The old command records and focused TRX counters were read from [artifacts/phase7-pass3-final-20261004/final/final-command-records.json](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass3-final-20261004/final/final-command-records.json>). Focused units record Pass1=22, Pass2=16, Pass3=19, Tracking=156, sequence=56, all terminal exit0/no failures/no skipped. These are **prior results**, not fresh executions.

The prior PG terminal artifact [artifacts/phase7-pass3-final-20261004/final/postgresql/terminal-result.json](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass3-final-20261004/final/postgresql/terminal-result.json>) records provider PostgreSQL18 / Npgsql, exit0,196 passed/no failures/no skipped, and cleanup=true. It uses a filtered test command. No complete protected inventory reconciliation was performed in this refreeze task.

| Prior gate | Change-impact classification | Old-evidence label | Meaning / next challenge restriction |
|---|---|---|---|
| Focused Pass3 units | REUSABLE_BY_HASH_AND_DEPENDENCY | VALID_FOR_UNCHANGED_BACKEND_BY_HASH | The19-method artifact is not invalidated by Desktop-only changes; upcoming Sol challenge still requires fresh focused units |
| Focused Pass3 PG methods within prior run | REUSABLE_BY_HASH_AND_DEPENDENCY | VALID_FOR_UNCHANGED_BACKEND_BY_HASH | Backend test/schema bytes preserved; no fresh PG certification here; upcoming Sol fresh PG requirement remains |
| Pass1 backend focused gates | REUSABLE_BY_HASH_AND_DEPENDENCY | VALID_FOR_UNCHANGED_BACKEND_BY_HASH | Desktop drift does not invalidate backend evidence; full protected coverage remains to reconcile |
| Pass2 backend focused gates | REUSABLE_BY_HASH_AND_DEPENDENCY | VALID_FOR_UNCHANGED_BACKEND_BY_HASH | Same; numeric/race/rollback/Thaka exact inventories remain next challenge work |
| Tracking/sequence backend gates | REUSABLE_BY_HASH_AND_DEPENDENCY | VALID_FOR_UNCHANGED_BACKEND_BY_HASH | No changed backend authority;70/5 versus protected larger sets still not reconciled |
| Full945-unit current-candidate claim | MUST_RERUN | INVALIDATED_BY_CURRENT_CANDIDATE | Frontend tests read mutable XAML and are not all bound by old813 manifest; do not carry full aggregate result forward |
| Old Desktop Debug/Release builds | MUST_RERUN — completed here | INVALIDATED_BY_CURRENT_CANDIDATE | New Debug/Release terminal results replace only compile evidence |
| Frontend visual/focus/UI acceptance | MUST_RERUN | INVALIDATED_BY_CURRENT_CANDIDATE | Current styles/traversal/controls differ |
| Old EF model/migration evidence | REUSABLE_BY_HASH_AND_DEPENDENCY for drift impact | VALID_FOR_UNCHANGED_BACKEND_BY_HASH | XAML does not alter EF model; upcoming fresh Sol EF requirement remains |

“REUSABLE” above is an explicit **backend evidence eligibility decision**, not formal approval of previous certification. Individual artifacts/test names, dependency/schema/provider conditions and completeness must still pass the upcoming Sol challenge. No old result is relabeled fresh, and no test was run here.

## 11. Manifest coverage gap

The current src/tests/scripts inventory also contains these two files absent from the old813 manifest:

| File | Current SHA256 | Evidence |
|---|---|---|
| [tests/EdgeRetails.UnitTests/FrontendPass1GeometryBaselineTests.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.UnitTests/FrontendPass1GeometryBaselineTests.cs>) | `36EBDA668B3DEAA0BA89C7E5A8CA64BE7A5DCC6EFCABA5327B79109CE7F177A9` | Separate frontend Pass1 manifest matches; file predates old Pass3 freeze |
| [tests/EdgeRetails.UnitTests/FrontendPass2FocusAccessibilityTests.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.UnitTests/FrontendPass2FocusAccessibilityTests.cs>) | `2D0AB44C195861F15DF2AB243F340CC308FDC217F212A33C82A93717D9030E72` | Current hash differs from old frontend Pass2 reportE8E8...; modified during review |

These are **coverage omissions in the old manifest**, not proof that both were created after its freeze. They are compiled by the UnitTests project and frontend tests read source XAML. Thus the old manifest cannot alone bind the full945-unit claim to all current test inputs. No tests were modified by this reconciliation.

## 12. Refreeze decision matrix

| Required condition | Result |
|---|---|
| Class B5/5 preserved | PASS |
| Class C2/2 preserved | PASS |
| Five Desktop changes sufficiently understood, including exact post-freeze provenance | NOT_PROVEN: current purpose understood; archived-byte/authorization gap |
| No unauthorized Pass3-related behavior found | No such backend change found; not a blanket authorization claim |
| Current Debug build | PASS |
| Current Release build | PASS |
| Unexpected additional drift=0 | **FAIL: SearchBox.xaml** |
| Candidate remains stable across reconciliation | **FAIL: two dialogs changed during review** |

Accordingly **REFREEZE DENIED**. No old manifest overwrite, current-manifest rewrite or new manifest creation was performed.

## 13. Next permitted action and controlled stop

**STOP — reconcile the additional SearchBox drift, exact current Desktop provenance and candidate stability before any refreeze.** The current user scope does not authorize silently expanding the five-file exception or treating unknown edits as approved.

New manifest path: N/A — not created.  
New manifest digest: N/A.  
Old digest preserved: `E20CB2A543F9801E5AD4705E2F06532EFB51B3BF00C18B9E52E4BE3D6CE747B2`.

No final Sol hostile certification, Pass4, Phase8 or Pass3 remediation was started. Both owned build tasks have terminal results. No PG task/process was created.

SOURCE MODIFIED: NO  
TESTS MODIFIED: NO  
DESKTOP MODIFIED: NO  
DATABASE MODIFIED: NO  
MIGRATIONS CREATED: NO  
GIT HISTORY MODIFIED: NO  
PASS4 STARTED: NO  
OLD MANIFEST MODIFIED: NO  
NEW MANIFEST CREATED: NO  
ONLY NEW PERSISTENT DELIVERABLE: this requested reconciliation report

## 14. Final delivery checkpoint — 2026-10-04T09:49:02.6540916Z

Read-only verification on continuation again found 813 listed files, 807 matching, the same six Desktop mismatch paths, and all five Class B plus both Class C hashes preserved. Branch, HEAD and the old manifest digest remained unchanged. No refrozen manifest exists.

CompleteSaleDialog.xaml changed again after the section 3 closing audit: its current observed SHA256 is `19BDF77352212D4673AB1908BCA4B7DFF8D2E980B84AFF4348F4452A3A036403`, versus `C57D2A1ECF32575EA8AF07ACE58204C36A6DEF2659E0836E7D6783418F422D75` in the earlier closing table. The table and Appendix A remain historical evidence of their inspected bytes, not an assertion that subsequent content stayed identical. The continuing change was not made by this reconciliation.

The completed Debug/Release builds retain their terminal results, but they are **not compile certification of the subsequently changed current Desktop bytes**. They were not rerun because the unexplained additional-drift/provenance/stability gates already deny refreeze and the prompt requires stopping at that outcome. No build/test/database task was launched in this continuation.

Final manifest verification command completed with exit code 0; 807 comparisons matched and 6 failed. Database provider: NONE. Completion status: COMPLETED, refreeze gate DENIED. The old manifest was preserved; only this authorized report was updated with the delivery checkpoint.

Final verdict remains **PASS3_REFREEZE_DENIED_UNEXPLAINED_DRIFT**. New manifest: **NOT CREATED**.

## Appendix A. Complete bounded Git HEAD diff

**Baseline is Git HEAD, not the unrecovered old frozen working-tree contents.** This is the exact diff command output retained for the inspected six files. Git line-ending notices are diagnostic only; Git checkout/reset/rewrite was not run.

```diff
warning: in the working copy of 'src/EdgeRetails.Desktop/Views/Dialogs/CompleteSaleDialog.xaml', LF will be replaced by CRLF the next time Git touches it
warning: in the working copy of 'src/EdgeRetails.Desktop/Views/Dialogs/SalesReturnDialog.xaml', LF will be replaced by CRLF the next time Git touches it
diff --git a/src/EdgeRetails.Desktop/Controls/SearchBox.xaml b/src/EdgeRetails.Desktop/Controls/SearchBox.xaml
index 2380316..594491d 100644
--- a/src/EdgeRetails.Desktop/Controls/SearchBox.xaml
+++ b/src/EdgeRetails.Desktop/Controls/SearchBox.xaml
@@ -17,5 +17,6 @@
     </UserControl.Style>
     <Border BorderThickness="1"
-            CornerRadius="{StaticResource Radius.Control}">
+            CornerRadius="{StaticResource Radius.Control}"
+            SnapsToDevicePixels="True">
         <Border.Style>
             <Style TargetType="Border">
@@ -24,10 +25,10 @@
                 <Style.Triggers>
                     <DataTrigger Binding="{Binding IsKeyboardFocused, ElementName=Input}" Value="True">
-                        <Setter Property="BorderBrush" Value="{StaticResource Brush.Brand.Primary}" />
+                        <Setter Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
                     </DataTrigger>
                 </Style.Triggers>
             </Style>
         </Border.Style>
-        <Grid Margin="14,0">
+        <Grid Margin="14,0" SnapsToDevicePixels="True">
             <Grid.ColumnDefinitions>
                 <ColumnDefinition Width="18" />
@@ -48,16 +49,14 @@
                   VerticalAlignment="Center" />
 
-            <Grid Grid.Column="2">
+            <Grid Grid.Column="2" VerticalAlignment="Center">
                 <TextBox x:Name="Input"
+                         Style="{StaticResource Input.TextBox.Embedded}"
                          Text="{Binding Text, ElementName=Root, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
-                         Background="Transparent"
-                         Foreground="{DynamicResource Brush.Text.Primary}"
-                         BorderThickness="0"
                          FontFamily="{StaticResource Font.Primary}"
                          FontSize="14"
+                         VerticalAlignment="Center"
                          VerticalContentAlignment="Center"
                          CaretBrush="{StaticResource Brush.Brand.Primary}"
-                         IsReadOnly="{Binding IsReadOnly, ElementName=Root}"
-                         FocusVisualStyle="{x:Null}" />
+                         IsReadOnly="{Binding IsReadOnly, ElementName=Root}" />
 
                 <TextBlock IsHitTestVisible="False"
@@ -66,5 +65,7 @@
                            FontFamily="{StaticResource Font.Primary}"
                            FontSize="14"
-                           VerticalAlignment="Center">
+                           VerticalAlignment="Center"
+                           Margin="0"
+                           Padding="0">
                     <TextBlock.Style>
                         <Style TargetType="TextBlock">
diff --git a/src/EdgeRetails.Desktop/Resources/Inputs.xaml b/src/EdgeRetails.Desktop/Resources/Inputs.xaml
index 77dee72..ae01fa0 100644
--- a/src/EdgeRetails.Desktop/Resources/Inputs.xaml
+++ b/src/EdgeRetails.Desktop/Resources/Inputs.xaml
@@ -1,7 +1,5 @@
 <ResourceDictionary
     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
-    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
-
-    <Style x:Key="Input.TextBox" TargetType="TextBox">
+    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">    <Style x:Key="Input.TextBox" TargetType="TextBox">
         <Setter Property="Height" Value="{StaticResource Dimension.Control.Standard}" />
         <Setter Property="Padding" Value="{StaticResource Padding.Input.Standard}" />
@@ -16,4 +14,5 @@
         <Setter Property="SelectionBrush" Value="{StaticResource Brush.Brand.Tint}" />
         <Setter Property="FocusVisualStyle" Value="{x:Null}" />
+        <Setter Property="SnapsToDevicePixels" Value="True" />
         <Setter Property="Template">
             <Setter.Value>
@@ -23,5 +22,6 @@
                             BorderBrush="{TemplateBinding BorderBrush}"
                             BorderThickness="{TemplateBinding BorderThickness}"
-                            CornerRadius="{StaticResource Radius.Control}">
+                            CornerRadius="{StaticResource Radius.Control}"
+                            SnapsToDevicePixels="True">
                         <ScrollViewer x:Name="PART_ContentHost"
                                       Margin="{TemplateBinding Padding}"
@@ -29,9 +29,13 @@
                                       Focusable="False"
                                       HorizontalScrollBarVisibility="Hidden"
-                                      VerticalScrollBarVisibility="Hidden" />
+                                      VerticalScrollBarVisibility="Hidden"
+                                      SnapsToDevicePixels="True" />
                     </Border>
                     <ControlTemplate.Triggers>
                         <Trigger Property="IsKeyboardFocused" Value="True">
-                            <Setter TargetName="InputBorder" Property="BorderBrush" Value="{StaticResource Brush.Brand.Primary}" />
+                            <Setter TargetName="InputBorder" Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
+                        </Trigger>
+                        <Trigger Property="IsReadOnly" Value="True">
+                            <Setter TargetName="InputBorder" Property="Background" Value="{DynamicResource Brush.Surface.Subtle}" />
                         </Trigger>
                         <Trigger Property="IsEnabled" Value="False">
@@ -48,6 +52,38 @@
     <Style x:Key="Input.TextBox.Compact" TargetType="TextBox" BasedOn="{StaticResource Input.TextBox}">
         <Setter Property="Height" Value="{StaticResource Dimension.Control.Compact}" />
-        <Setter Property="FontSize" Value="13.5" />
-        <Setter Property="Padding" Value="7,0" />
+        <Setter Property="FontSize" Value="13" />
+        <Setter Property="Padding" Value="{StaticResource Padding.Input.Compact}" />
+    </Style>
+
+    <!-- Embedded / Borderless TextBox for composite controls (SearchBox, Currency inputs, steppers) -->
+    <Style x:Key="Input.TextBox.Embedded" TargetType="TextBox">
+        <Setter Property="Height" Value="Auto" />
+        <Setter Property="MinHeight" Value="0" />
+        <Setter Property="Padding" Value="0" />
+        <Setter Property="Margin" Value="0" />
+        <Setter Property="Background" Value="Transparent" />
+        <Setter Property="Foreground" Value="{DynamicResource Brush.Text.Primary}" />
+        <Setter Property="BorderThickness" Value="0" />
+        <Setter Property="FontFamily" Value="{StaticResource Font.Primary}" />
+        <Setter Property="FontSize" Value="14" />
+        <Setter Property="VerticalAlignment" Value="Center" />
+        <Setter Property="VerticalContentAlignment" Value="Center" />
+        <Setter Property="CaretBrush" Value="{StaticResource Brush.Brand.Primary}" />
+        <Setter Property="SelectionBrush" Value="{StaticResource Brush.Brand.Tint}" />
+        <Setter Property="FocusVisualStyle" Value="{x:Null}" />
+        <Setter Property="SnapsToDevicePixels" Value="True" />
+        <Setter Property="Template">
+            <Setter.Value>
+                <ControlTemplate TargetType="TextBox">
+                    <ScrollViewer x:Name="PART_ContentHost"
+                                  Focusable="False"
+                                  HorizontalScrollBarVisibility="Hidden"
+                                  VerticalScrollBarVisibility="Hidden"
+                                  VerticalAlignment="Center"
+                                  Margin="{TemplateBinding Padding}"
+                                  SnapsToDevicePixels="True" />
+                </ControlTemplate>
+            </Setter.Value>
+        </Setter>
     </Style>
 
@@ -70,5 +106,5 @@
             </Trigger>
             <Trigger Property="IsKeyboardFocusWithin" Value="True">
-                <Setter Property="BorderBrush" Value="{StaticResource Brush.Brand.Primary}" />
+                <Setter Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
             </Trigger>
         </Style.Triggers>
@@ -140,5 +176,5 @@
                     <ControlTemplate.Triggers>
                         <Trigger Property="IsKeyboardFocusWithin" Value="True">
-                            <Setter TargetName="InputBorder" Property="BorderBrush" Value="{StaticResource Brush.Brand.Primary}" />
+                            <Setter TargetName="InputBorder" Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
                         </Trigger>
                         <Trigger Property="IsEnabled" Value="False">
@@ -154,7 +190,7 @@
 
     <Style x:Key="Input.ComboBox.Compact" TargetType="ComboBox" BasedOn="{StaticResource Input.ComboBox}">
-        <Setter Property="Height" Value="32" />
+        <Setter Property="Height" Value="{StaticResource Dimension.Control.Compact}" />
         <Setter Property="FontSize" Value="13" />
-        <Setter Property="Padding" Value="8,0" />
+        <Setter Property="Padding" Value="{StaticResource Padding.Input.Compact}" />
     </Style>
 
@@ -172,4 +208,5 @@
         <Setter Property="SelectionBrush" Value="{StaticResource Brush.Brand.Tint}" />
         <Setter Property="FocusVisualStyle" Value="{x:Null}" />
+        <Setter Property="SnapsToDevicePixels" Value="True" />
         <Setter Property="Template">
             <Setter.Value>
@@ -179,5 +216,6 @@
                             BorderBrush="{TemplateBinding BorderBrush}"
                             BorderThickness="{TemplateBinding BorderThickness}"
-                            CornerRadius="{StaticResource Radius.Control}">
+                            CornerRadius="{StaticResource Radius.Control}"
+                            SnapsToDevicePixels="True">
                         <ScrollViewer x:Name="PART_ContentHost"
                                       Margin="{TemplateBinding Padding}"
@@ -185,9 +223,10 @@
                                       Focusable="False"
                                       HorizontalScrollBarVisibility="Hidden"
-                                      VerticalScrollBarVisibility="Hidden" />
+                                      VerticalScrollBarVisibility="Hidden"
+                                      SnapsToDevicePixels="True" />
                     </Border>
                     <ControlTemplate.Triggers>
                         <Trigger Property="IsKeyboardFocused" Value="True">
-                            <Setter TargetName="InputBorder" Property="BorderBrush" Value="{StaticResource Brush.Brand.Primary}" />
+                            <Setter TargetName="InputBorder" Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
                         </Trigger>
                         <Trigger Property="IsEnabled" Value="False">
@@ -202,4 +241,10 @@
     </Style>
 
+    <Style x:Key="Input.PasswordBox.Compact" TargetType="PasswordBox" BasedOn="{StaticResource Input.PasswordBox}">
+        <Setter Property="Height" Value="{StaticResource Dimension.Control.Compact}" />
+        <Setter Property="FontSize" Value="13" />
+        <Setter Property="Padding" Value="{StaticResource Padding.Input.Compact}" />
+    </Style>
+
     <!-- CheckBox -->
     <Style x:Key="Input.CheckBox" TargetType="CheckBox">
@@ -218,21 +263,33 @@
                             <ColumnDefinition Width="*" />
                         </Grid.ColumnDefinitions>
-                        <Border x:Name="Box"
-                                Width="18" Height="18"
-                                Background="{DynamicResource Brush.Input.Background}"
-                                BorderBrush="{DynamicResource Brush.Border.Default}"
-                                BorderThickness="1"
-                                CornerRadius="4"
-                                VerticalAlignment="Center">
-                            <Path x:Name="CheckMark"
-                                  Data="M 3 9 L 7 13 L 15 5"
-                                  Stroke="{StaticResource Brush.White}"
-                                  StrokeThickness="2"
-                                  StrokeStartLineCap="Round"
-                                  StrokeEndLineCap="Round"
-                                  Opacity="0"
-                                  HorizontalAlignment="Center"
-                                  VerticalAlignment="Center" />
-                        </Border>
+                        <Grid Grid.Column="0" Width="18" Height="18" VerticalAlignment="Center">
+                            <Border x:Name="FocusRing"
+                                    Width="24" Height="24"
+                                    BorderThickness="2"
+                                    BorderBrush="Transparent"
+                                    CornerRadius="6"
+                                    HorizontalAlignment="Center"
+                                    VerticalAlignment="Center"
+                                    IsHitTestVisible="False"
+                                    SnapsToDevicePixels="True" />
+                            <Border x:Name="Box"
+                                    Width="18" Height="18"
+                                    Background="{DynamicResource Brush.Input.Background}"
+                                    BorderBrush="{DynamicResource Brush.Border.Default}"
+                                    BorderThickness="1"
+                                    CornerRadius="4"
+                                    HorizontalAlignment="Center"
+                                    VerticalAlignment="Center">
+                                <Path x:Name="CheckMark"
+                                      Data="M 3 9 L 7 13 L 15 5"
+                                      Stroke="{StaticResource Brush.White}"
+                                      StrokeThickness="2"
+                                      StrokeStartLineCap="Round"
+                                      StrokeEndLineCap="Round"
+                                      Opacity="0"
+                                      HorizontalAlignment="Center"
+                                      VerticalAlignment="Center" />
+                            </Border>
+                        </Grid>
                         <ContentPresenter Grid.Column="1"
                                           Margin="8,0,0,0"
@@ -247,9 +304,18 @@
                         </Trigger>
                         <Trigger Property="IsMouseOver" Value="True">
-                            <Setter TargetName="Box" Property="BorderBrush" Value="{StaticResource Brush.Brand.Primary}" />
+                            <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
                         </Trigger>
                         <Trigger Property="IsKeyboardFocused" Value="True">
-                            <Setter TargetName="Box" Property="BorderBrush" Value="{StaticResource Brush.Brand.Primary}" />
+                            <Setter TargetName="FocusRing" Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
+                            <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
                         </Trigger>
+                        <MultiTrigger>
+                            <MultiTrigger.Conditions>
+                                <Condition Property="IsChecked" Value="True" />
+                                <Condition Property="IsKeyboardFocused" Value="True" />
+                            </MultiTrigger.Conditions>
+                            <Setter TargetName="FocusRing" Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
+                            <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Brush.Surface.Default}" />
+                        </MultiTrigger>
                         <Trigger Property="IsEnabled" Value="False">
                             <Setter Property="Opacity" Value="0.55" />
@@ -277,18 +343,30 @@
                             <ColumnDefinition Width="*" />
                         </Grid.ColumnDefinitions>
-                        <Border x:Name="Circle"
-                                Width="18" Height="18"
-                                Background="{DynamicResource Brush.Input.Background}"
-                                BorderBrush="{DynamicResource Brush.Border.Default}"
-                                BorderThickness="1"
-                                CornerRadius="9"
-                                VerticalAlignment="Center">
-                            <Ellipse x:Name="Dot"
-                                     Width="8" Height="8"
-                                     Fill="{StaticResource Brush.White}"
-                                     Opacity="0"
-                                     HorizontalAlignment="Center"
-                                     VerticalAlignment="Center" />
-                        </Border>
+                        <Grid Grid.Column="0" Width="18" Height="18" VerticalAlignment="Center">
+                            <Border x:Name="FocusRing"
+                                    Width="24" Height="24"
+                                    BorderThickness="2"
+                                    BorderBrush="Transparent"
+                                    CornerRadius="12"
+                                    HorizontalAlignment="Center"
+                                    VerticalAlignment="Center"
+                                    IsHitTestVisible="False"
+                                    SnapsToDevicePixels="True" />
+                            <Border x:Name="Circle"
+                                    Width="18" Height="18"
+                                    Background="{DynamicResource Brush.Input.Background}"
+                                    BorderBrush="{DynamicResource Brush.Border.Default}"
+                                    BorderThickness="1"
+                                    CornerRadius="9"
+                                    HorizontalAlignment="Center"
+                                    VerticalAlignment="Center">
+                                <Ellipse x:Name="Dot"
+                                         Width="8" Height="8"
+                                         Fill="{StaticResource Brush.White}"
+                                         Opacity="0"
+                                         HorizontalAlignment="Center"
+                                         VerticalAlignment="Center" />
+                            </Border>
+                        </Grid>
                         <ContentPresenter Grid.Column="1"
                                           Margin="8,0,0,0"
@@ -303,9 +381,18 @@
                         </Trigger>
                         <Trigger Property="IsMouseOver" Value="True">
-                            <Setter TargetName="Circle" Property="BorderBrush" Value="{StaticResource Brush.Brand.Primary}" />
+                            <Setter TargetName="Circle" Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
                         </Trigger>
                         <Trigger Property="IsKeyboardFocused" Value="True">
-                            <Setter TargetName="Circle" Property="BorderBrush" Value="{StaticResource Brush.Brand.Primary}" />
+                            <Setter TargetName="FocusRing" Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
+                            <Setter TargetName="Circle" Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
                         </Trigger>
+                        <MultiTrigger>
+                            <MultiTrigger.Conditions>
+                                <Condition Property="IsChecked" Value="True" />
+                                <Condition Property="IsKeyboardFocused" Value="True" />
+                            </MultiTrigger.Conditions>
+                            <Setter TargetName="FocusRing" Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
+                            <Setter TargetName="Circle" Property="BorderBrush" Value="{DynamicResource Brush.Surface.Default}" />
+                        </MultiTrigger>
                         <Trigger Property="IsEnabled" Value="False">
                             <Setter Property="Opacity" Value="0.55" />
@@ -320,4 +407,5 @@
     <Style x:Key="Input.DatePicker" TargetType="DatePicker">
         <Setter Property="Height" Value="{StaticResource Dimension.Control.Standard}" />
+        <Setter Property="Padding" Value="{StaticResource Padding.Input.Standard}" />
         <Setter Property="Foreground" Value="{DynamicResource Brush.Text.Primary}" />
         <Setter Property="Background" Value="{DynamicResource Brush.Input.Background}" />
@@ -325,7 +413,9 @@
         <Setter Property="BorderThickness" Value="1" />
         <Setter Property="FontFamily" Value="{StaticResource Font.Primary}" />
-        <Setter Property="FontSize" Value="13.5" />
+        <Setter Property="FontSize" Value="14" />
+        <Setter Property="VerticalContentAlignment" Value="Center" />
         <Setter Property="IsTodayHighlighted" Value="True" />
         <Setter Property="FocusVisualStyle" Value="{x:Null}" />
+        <Setter Property="SnapsToDevicePixels" Value="True" />
         <Setter Property="CalendarStyle">
             <Setter.Value>
@@ -338,9 +428,18 @@
         <Style.Triggers>
             <Trigger Property="IsKeyboardFocusWithin" Value="True">
-                <Setter Property="BorderBrush" Value="{StaticResource Brush.Brand.Primary}" />
+                <Setter Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
+            </Trigger>
+            <Trigger Property="IsEnabled" Value="False">
+                <Setter Property="Opacity" Value="0.75" />
             </Trigger>
         </Style.Triggers>
     </Style>
 
+    <Style x:Key="Input.DatePicker.Compact" TargetType="DatePicker" BasedOn="{StaticResource Input.DatePicker}">
+        <Setter Property="Height" Value="{StaticResource Dimension.Control.Compact}" />
+        <Setter Property="FontSize" Value="13" />
+        <Setter Property="Padding" Value="{StaticResource Padding.Input.Compact}" />
+    </Style>
+
     <!-- Implicit Styles for Automatic Theming -->
     <Style TargetType="TextBox" BasedOn="{StaticResource Input.TextBox}" />
diff --git a/src/EdgeRetails.Desktop/Views/Dialogs/CompleteSaleDialog.xaml b/src/EdgeRetails.Desktop/Views/Dialogs/CompleteSaleDialog.xaml
index 88f69cc..d8e13ab 100644
--- a/src/EdgeRetails.Desktop/Views/Dialogs/CompleteSaleDialog.xaml
+++ b/src/EdgeRetails.Desktop/Views/Dialogs/CompleteSaleDialog.xaml
@@ -26,24 +26,27 @@
                     <ControlTemplate TargetType="CheckBox">
                         <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
-                            <Border x:Name="Box"
-                                    Width="18"
-                                    Height="18"
-                                    CornerRadius="4"
-                                    BorderBrush="{DynamicResource Brush.Border.Default}"
-                                    BorderThickness="1.5"
-                                    Background="{DynamicResource Brush.Input.Background}"
-                                    Margin="0,0,10,0"
-                                    VerticalAlignment="Center">
-                                <Path x:Name="CheckMark"
-                                      Data="M 3 9 L 7 13 L 15 5"
-                                      Stroke="{StaticResource Brush.White}"
-                                      StrokeThickness="2"
-                                      StrokeStartLineCap="Round"
-                                      StrokeEndLineCap="Round"
-                                      StrokeLineJoin="Round"
-                                      Visibility="Collapsed"
-                                      HorizontalAlignment="Center"
-                                      VerticalAlignment="Center" />
-                            </Border>
+                            <Grid Width="18" Height="18" Margin="0,0,10,0" VerticalAlignment="Center">
+                                <Border x:Name="Box"
+                                        CornerRadius="4"
+                                        BorderBrush="{DynamicResource Brush.Border.Default}"
+                                        BorderThickness="1.5"
+                                        Background="{DynamicResource Brush.Input.Background}">
+                                    <Path x:Name="CheckMark"
+                                          Data="M 3 9 L 7 13 L 15 5"
+                                          Stroke="{StaticResource Brush.White}"
+                                          StrokeThickness="2"
+                                          StrokeStartLineCap="Round"
+                                          StrokeEndLineCap="Round"
+                                          StrokeLineJoin="Round"
+                                          Visibility="Collapsed"
+                                          HorizontalAlignment="Center"
+                                          VerticalAlignment="Center" />
+                                </Border>
+                                <Border x:Name="FocusRing"
+                                        BorderBrush="Transparent"
+                                        BorderThickness="1.5"
+                                        CornerRadius="4"
+                                        IsHitTestVisible="False" />
+                            </Grid>
                             <ContentPresenter VerticalAlignment="Center" />
                         </StackPanel>
@@ -58,7 +61,15 @@
                             </Trigger>
                             <Trigger Property="IsKeyboardFocused" Value="True">
-                                <Setter TargetName="Box" Property="BorderBrush" Value="{StaticResource Brush.Brand.Primary}" />
-                                <Setter TargetName="Box" Property="BorderThickness" Value="2" />
+                                <Setter TargetName="FocusRing" Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
+                                <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
                             </Trigger>
+                            <MultiTrigger>
+                                <MultiTrigger.Conditions>
+                                    <Condition Property="IsChecked" Value="True" />
+                                    <Condition Property="IsKeyboardFocused" Value="True" />
+                                </MultiTrigger.Conditions>
+                                <Setter TargetName="FocusRing" Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
+                                <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Brush.Surface.Default}" />
+                            </MultiTrigger>
                             <Trigger Property="IsEnabled" Value="False">
                                 <Setter TargetName="Box" Property="Opacity" Value="0.5" />
@@ -83,5 +94,5 @@
             <Setter Property="Margin" Value="0,0,6,0" />
             <Setter Property="Cursor" Value="Hand" />
-            <Setter Property="FocusVisualStyle" Value="{x:Null}" />
+            <Setter Property="FocusVisualStyle" Value="{StaticResource FocusVisual.Button.Text}" />
             <Setter Property="Template">
                 <Setter.Value>
@@ -104,4 +115,8 @@
                                 <Setter TargetName="ChipBorder" Property="Background" Value="{DynamicResource Brush.Brand.Tint}" />
                             </Trigger>
+                            <Trigger Property="IsKeyboardFocused" Value="True">
+                                <Setter TargetName="ChipBorder" Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
+                                <Setter TargetName="ChipBorder" Property="Background" Value="{DynamicResource Brush.Hover.Subtle}" />
+                            </Trigger>
                         </ControlTemplate.Triggers>
                     </ControlTemplate>
@@ -167,5 +182,5 @@
                         Height="32"
                         Cursor="Hand"
-                        FocusVisualStyle="{x:Null}">
+                        FocusVisualStyle="{StaticResource FocusVisual.Button.PrimaryAction}">
                     <Button.Template>
                         <ControlTemplate TargetType="Button">
@@ -507,10 +522,21 @@
 
                 <!-- Input Box Container with "Rs." prefix -->
-                <Border Height="46"
+                <Border x:Name="AmountReceivedHost"
+                        Height="46"
                         CornerRadius="8"
                         Background="{DynamicResource Brush.Input.Background}"
-                        BorderBrush="{DynamicResource Brush.Border.Default}"
-                        BorderThickness="1">
-                    <Grid>
+                        BorderThickness="1"
+                        SnapsToDevicePixels="True">
+                    <Border.Style>
+                        <Style TargetType="Border">
+                            <Setter Property="BorderBrush" Value="{DynamicResource Brush.Border.Default}" />
+                            <Style.Triggers>
+                                <DataTrigger Binding="{Binding IsKeyboardFocused, ElementName=AmountReceivedTextBox}" Value="True">
+                                    <Setter Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
+                                </DataTrigger>
+                            </Style.Triggers>
+                        </Style>
+                    </Border.Style>
+                    <Grid VerticalAlignment="Center">
                         <Grid.ColumnDefinitions>
                             <ColumnDefinition Width="Auto" />
@@ -522,8 +548,8 @@
                                    Text="Rs."
                                    FontFamily="{StaticResource Font.Numeric}"
-                                   FontSize="16"
+                                   FontSize="18"
                                    FontWeight="Bold"
                                    Foreground="{DynamicResource Brush.Text.Muted}"
-                                   Padding="14,0,4,0"
+                                   Margin="14,0,6,0"
                                    VerticalAlignment="Center" />
 
@@ -531,4 +557,5 @@
                         <TextBox x:Name="AmountReceivedTextBox"
                                  Grid.Column="1"
+                                 Style="{StaticResource Input.TextBox.Embedded}"
                                  Text="{Binding AmountReceivedText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                                  FontFamily="{StaticResource Font.Numeric}"
@@ -536,10 +563,7 @@
                                  FontWeight="Bold"
                                  Foreground="{DynamicResource Brush.Text.Primary}"
-                                 Background="Transparent"
-                                 BorderThickness="0"
-                                 VerticalContentAlignment="Center"
-                                 Padding="2,0,14,0"
-                                 CaretBrush="{DynamicResource Brush.Brand.Primary}"
-                                 FocusVisualStyle="{x:Null}" />
+                                 Padding="0,0,14,0"
+                                 VerticalAlignment="Center"
+                                 VerticalContentAlignment="Center" />
                     </Grid>
                 </Border>
@@ -876,8 +900,4 @@
                                     <Setter TargetName="RootBorder" Property="Background" Value="{StaticResource Gradient.Payment.Pressed}" />
                                 </Trigger>
-                                <Trigger Property="IsKeyboardFocused" Value="True">
-                                    <Setter TargetName="RootBorder" Property="BorderBrush" Value="{StaticResource Brush.White}" />
-                                    <Setter TargetName="RootBorder" Property="BorderThickness" Value="2" />
-                                </Trigger>
                                 <Trigger Property="IsEnabled" Value="False">
                                     <Setter TargetName="RootBorder" Property="Opacity" Value="0.45" />
diff --git a/src/EdgeRetails.Desktop/Views/Dialogs/SalesReturnDialog.xaml b/src/EdgeRetails.Desktop/Views/Dialogs/SalesReturnDialog.xaml
index 6274bdb..b96e98c 100644
--- a/src/EdgeRetails.Desktop/Views/Dialogs/SalesReturnDialog.xaml
+++ b/src/EdgeRetails.Desktop/Views/Dialogs/SalesReturnDialog.xaml
@@ -257,14 +257,25 @@
                                                     Style="{StaticResource Stepper.Button}" />
 
-                                            <Border Width="42"
+                                            <Border x:Name="ReturnQuantityHost"
+                                                    Width="42"
                                                     Height="28"
                                                     Margin="4,0"
                                                     BorderThickness="1"
-                                                    BorderBrush="{DynamicResource Brush.Border.Default}"
                                                     CornerRadius="{StaticResource Radius.Control}"
-                                                    Background="{DynamicResource Brush.Surface.Raised}">
-                                                <TextBox Text="{Binding ReturnQuantityText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
-                                                         BorderThickness="0"
-                                                         Background="Transparent"
+                                                    Background="{DynamicResource Brush.Surface.Raised}"
+                                                    SnapsToDevicePixels="True">
+                                                <Border.Style>
+                                                    <Style TargetType="Border">
+                                                        <Setter Property="BorderBrush" Value="{DynamicResource Brush.Border.Default}" />
+                                                        <Style.Triggers>
+                                                            <DataTrigger Binding="{Binding IsKeyboardFocused, ElementName=ReturnQuantityTextBox}" Value="True">
+                                                                <Setter Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
+                                                            </DataTrigger>
+                                                        </Style.Triggers>
+                                                    </Style>
+                                                </Border.Style>
+                                                <TextBox x:Name="ReturnQuantityTextBox"
+                                                         Style="{StaticResource Input.TextBox.Embedded}"
+                                                         Text="{Binding ReturnQuantityText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                                                          Foreground="{DynamicResource Brush.Text.Primary}"
                                                          FontFamily="{StaticResource Font.Numeric}"
@@ -272,6 +283,6 @@
                                                          FontWeight="Bold"
                                                          TextAlignment="Center"
-                                                         VerticalContentAlignment="Center"
-                                                         FocusVisualStyle="{x:Null}" />
+                                                         VerticalAlignment="Center"
+                                                         VerticalContentAlignment="Center" />
                                             </Border>
 
@@ -334,4 +345,5 @@
                     <!-- 1. Customer Changed Mind / Restock -->
                     <RadioButton Grid.Row="0" Grid.Column="0"
+                                 FocusVisualStyle="{StaticResource FocusVisual.Button.PrimaryAction}"
                                  GroupName="ReturnReasonGroup"
                                  IsChecked="{Binding IsDispositionRestock, Mode=OneWay}"
@@ -372,4 +384,5 @@
                     <!-- 2. Defective -->
                     <RadioButton Grid.Row="0" Grid.Column="2"
+                                 FocusVisualStyle="{StaticResource FocusVisual.Button.PrimaryAction}"
                                  GroupName="ReturnReasonGroup"
                                  IsChecked="{Binding IsDispositionDefective, Mode=OneWay}"
@@ -410,4 +423,5 @@
                     <!-- 3. Damaged -->
                     <RadioButton Grid.Row="1" Grid.Column="0"
+                                 FocusVisualStyle="{StaticResource FocusVisual.Button.PrimaryAction}"
                                  GroupName="ReturnReasonGroup"
                                  IsChecked="{Binding IsDispositionDamaged, Mode=OneWay}"
@@ -447,4 +461,5 @@
                     <!-- 4. Other -->
                     <RadioButton Grid.Row="1" Grid.Column="2"
+                                 FocusVisualStyle="{StaticResource FocusVisual.Button.PrimaryAction}"
                                  GroupName="ReturnReasonGroup"
                                  IsChecked="{Binding IsDispositionOther, Mode=OneWay}"
diff --git a/src/EdgeRetails.Desktop/Views/PosView.xaml b/src/EdgeRetails.Desktop/Views/PosView.xaml
index 312a0cb..dd1cf28 100644
--- a/src/EdgeRetails.Desktop/Views/PosView.xaml
+++ b/src/EdgeRetails.Desktop/Views/PosView.xaml
@@ -684,13 +684,24 @@
 
                                     <!-- Discount Input Box -->
-                                    <Border Grid.Column="1"
+                                    <Border x:Name="DiscountAmountHost"
+                                            Grid.Column="1"
                                             Width="100"
                                             Height="28"
                                             Background="{DynamicResource Brush.Input.Background}"
-                                            BorderBrush="{DynamicResource Brush.Border.Default}"
                                             BorderThickness="1"
                                             CornerRadius="7"
-                                            Padding="8,0">
-                                        <Grid>
+                                            Padding="8,0"
+                                            SnapsToDevicePixels="True">
+                                        <Border.Style>
+                                            <Style TargetType="Border">
+                                                <Setter Property="BorderBrush" Value="{DynamicResource Brush.Border.Default}" />
+                                                <Style.Triggers>
+                                                    <DataTrigger Binding="{Binding IsKeyboardFocused, ElementName=DiscountAmountTextBox}" Value="True">
+                                                        <Setter Property="BorderBrush" Value="{DynamicResource Brush.Focus.Ring}" />
+                                                    </DataTrigger>
+                                                </Style.Triggers>
+                                            </Style>
+                                        </Border.Style>
+                                        <Grid VerticalAlignment="Center">
                                             <Grid.ColumnDefinitions>
                                                 <ColumnDefinition Width="Auto" />
@@ -703,14 +714,13 @@
                                                        Foreground="{DynamicResource Brush.Text.Muted}"
                                                        VerticalAlignment="Center" />
-                                            <TextBox Grid.Column="1"
+                                            <TextBox x:Name="DiscountAmountTextBox"
+                                                     Grid.Column="1"
+                                                     Style="{StaticResource Input.TextBox.Embedded}"
                                                      Text="{Binding DiscountAmountText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
-                                                     BorderThickness="0"
-                                                     Background="Transparent"
-                                                     Foreground="{DynamicResource Brush.Text.Primary}"
                                                      FontFamily="{StaticResource Font.Primary}"
                                                      FontSize="13"
                                                      TextAlignment="Right"
-                                                     VerticalContentAlignment="Center"
-                                                     FocusVisualStyle="{x:Null}" />
+                                                     VerticalAlignment="Center"
+                                                     VerticalContentAlignment="Center" />
                                         </Grid>
                                     </Border>
diff --git a/src/EdgeRetails.Desktop/Views/ShellView.xaml b/src/EdgeRetails.Desktop/Views/ShellView.xaml
index c8fca81..abb3689 100644
--- a/src/EdgeRetails.Desktop/Views/ShellView.xaml
+++ b/src/EdgeRetails.Desktop/Views/ShellView.xaml
@@ -26,5 +26,5 @@
                                 HorizontalContentAlignment="Stretch"
                                 VerticalContentAlignment="Stretch"
-                                KeyboardNavigation.TabNavigation="Cycle" />
+                                KeyboardNavigation.TabNavigation="Continue" />
             </Grid>
         </Grid>

```

