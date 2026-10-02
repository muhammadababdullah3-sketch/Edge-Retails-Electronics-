# Phase 3A.1 Historical Migration Immutability Closure — 2026-09-29

Historical Migration Immutability: **PASS (PATH B)**  
Historical source-byte archive: **UNAVAILABLE**  
Semantic / released migration immutability: **PROVEN**

This bounded closure applies the user's final controlled closure contract, section 3A.1, in attachment `C:\Users\muham\.codex\attachments\2fa0ac88-2fff-4e9b-9fda-bd7214f4f495\Pasted text.txt`. It supersedes only the historical migration source-provenance HOLD in `docs/Phase3A_Production_Database_Certification_2026-09-29.md`. It does not certify or lock all of Phase 3A; InventoryUnit closure and fresh independent PHASE3A-CERT-2 remain separate gates.

## Policy and acceptance

`docs/Architecture_Authority_Manifest.json` identifies `docs/Edge_Retails_Final_Architecture_Report_v1.md` as canonical. Its freshly calculated SHA-256 matches the manifest. Section 79 (lines 2752–2782) freezes released migration history, requires append-only post-production migrations, and prohibits rewriting, renumbering or silently regenerating released migration identities. It does not impose mandatory historical source-byte archival as an independent closure gate. The user explicitly permits Path B and classifies unavailable historical source archival as an evidence limitation.

## Evidence and finding

| Required property | Evidence | Result |
|---|---|---|
| Migration ID unchanged | Current Designer line 15 retains `20260925142150_Phase1SemanticProductIdentity`; prior certification records matching released/current migration semantics and ordered migration inventory | PASS |
| Released/current compiled semantics match | Existing successful comparison of Release 1.0.3, Release 1.0.5 and current Release build: UpOperations, DownOperations and TargetModel identical | PASS |
| Migration operations match | 13 Up and 11 Down operations in each comparison; canonical hashes below | PASS |
| Historical migration not edited to solve category failure | Current source retains empty-string identity-symbol default and unique index that caused the legacy failure; source/Designer hashes equal prior successful comparison and PDB document-checksum evidence | PASS |
| Repair uses new append-only migration | New migration `20260929100000_Phase3LegacyCategoryUpgradeRecovery` performs restoration; prior production certification records it as migration 18 | PASS |
| Canonical policy requires immutability, not unavailable byte archival | Section 79 and user's explicit Path B | PASS |

The successful semantic comparison and current-source/PDB binding are reused from the prior certification, lines 99–103. They are not a claim that this closure reran reflection. Its canonical operation hashes are:

| Comparison | SHA-256 |
|---|---|
| Up (13 operations) | 2147CA02302244107FFD9322AB243513124E2613DF56A499635D4DCF7473EFF7 |
| Down (11 operations) | BAF89DF4C4FE076B971DB831ADC2FDEFB6A76AC494C1F4BC5C17E05568C65D67 |
| TargetModel | 1AF5C62E4A78FDCF33FABFB35D8E7B1BA86AB8A2F1EE9D36C8C08C7F86892BFB |

Fresh inspection of `scripts/Invoke-Phase3LegacyCategoryPreMigration.ps1` confirms transactional staging: the exact 13-migration baseline, two legacy rows, known category FKs and absence of dependent rows are guarded; ID/name/active-state hashes are compared before clearing the categories table. The script does not rewrite migration source or EF migration history. Inspection is not execution.

Fresh inspection of the new recovery migration confirms deterministic unique-symbol generation, exact ID/name/active-state set comparison, count/shape/uniqueness checks, hold-table removal only after restoration checks, and single-category repair. `Down()` throws `NotSupportedException`, explicitly preserving forward-only recovery. These changes reside in the new migration and staging script. The historical migration still contains its original empty default and index operations.

## Fresh artifact fingerprints

Whole-assembly hashes identify today's artifacts; differing assembly hashes across components/releases are not evidence of differing historical migration operations. The prior semantic comparison is the evidence for operation equivalence. This closure does not retroactively claim these fingerprints were recorded at publication or by the previous comparator.

| Workspace-relative artifact | SHA-256 |
|---|---|
| docs\Edge_Retails_Final_Architecture_Report_v1.md | 12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673 |
| src\EdgeRetails.Infrastructure\Persistence\Migrations\20260925142150_Phase1SemanticProductIdentity.cs | 307BA075A56E683457321C479BD15C646B8AD10727AF0287014F8B01B3247842 |
| src\EdgeRetails.Infrastructure\Persistence\Migrations\20260925142150_Phase1SemanticProductIdentity.Designer.cs | 068279861318B4CF92D2D5520930B79DEAD1B0CA0A3E01C7996E4AFA78D6BD45 |
| src\EdgeRetails.Infrastructure\Persistence\Migrations\20260929100000_Phase3LegacyCategoryUpgradeRecovery.cs | E17551FBDA6A77AC50CF93D4031C8E1D7C9E591794292549B2F47A9E3228A0D6 |
| scripts\Invoke-Phase3LegacyCategoryPreMigration.ps1 | 03D67D75A9F124F353A9DFE1886CA3FCCF438D3BEA9B52CE9FF0A232E9B4525D |
| src\EdgeRetails.Infrastructure\bin\Release\net10.0\EdgeRetails.Infrastructure.dll | 731BAB5E416A88A259E00B74FDEE4294A62B94121D429509CE3ABA949EDA31F4 |
| src\EdgeRetails.Infrastructure\bin\Release\net10.0\EdgeRetails.Infrastructure.pdb | 351504FE64B73D144A0019672865727B8B37FF27DB07A4C98A0E1493F530A468 |
| artifacts\release-1.0.3\publish\EdgeRetails.Infrastructure.dll | CEECBFABF24C23568F4BEF708A100B2ABA209ADA3028E7E9C17DEDE7524EC0C0 |
| artifacts\release-1.0.3\publish\server\EdgeRetails.Infrastructure.dll | 6B1D63EF3C3B80E118C9E30AE5ECF04ACA1051FF618A67B6964B3FAC0C06A913 |
| artifacts\release-1.0.3\publish\worker\EdgeRetails.Infrastructure.dll | 48C14CC9A8E6A1807E2640F42AE124FFFAE08494CA8A937A9FA9904EAA686426 |
| artifacts\release-1.0.5\publish\EdgeRetails.Infrastructure.dll | CDCDFE50DF4F51765BF5846D6D23D31487A63E0BBBEB64BB0CE49E662EBD90DF |
| artifacts\release-1.0.5\publish\server\EdgeRetails.Infrastructure.dll | 245BA1520AB59BE468B2B93B223CF9D3D77FDD8496F15EBCB0FE91092AAD055C |
| artifacts\release-1.0.5\publish\worker\EdgeRetails.Infrastructure.dll | 022B18AC6F537D4210434CC16273635A5DD618926B265C6B526257E3A56DE3BA |

## Bounded command ledger

| Inspection | Exit | Outcome |
|---|---:|---|
| `Get-Content -LiteralPath` of supplied attachment and prior certification | 0 | Read governing Path B and existing successful comparison |
| Targeted `rg -n` across docs, Infrastructure and scripts for migration/policy references | 0 | Located canonical section 79, repair source and continuation evidence |
| `Get-Content` of canonical section 79, recovery migration, staging transaction; scoped artifact directory listing | 0 | Policy and append-only repair independently inspected |
| Manifest and scoped directory reads followed by initial `rg` with a Windows wildcard in its path | 1 | Reads succeeded; rg rejected wildcard path (OS error 123); no audit claim depends on this failed search |
| Corrected `rg -n ... -g '20260925142150_Phase1SemanticProductIdentity*'` and `Get-FileHash -Algorithm SHA256` on bounded paths | 0 | Migration ID/default/index and all fingerprints above captured; source hashes match prior evidence |
| `Get-FileHash` on exact listed paths and `Set-Content` of this report | 0 | Reconfirmed fingerprints; wrote only this closure report |

No build, test, reflection traversal, source/migration edit, database operation, service operation or later-phase action was performed. Search ended after existing evidence plus targeted current verification; no historical-source archive hunt was repeated.

## Limitations and conclusion

Historical source bytes are unavailable: the previous bounded Git/history/tag inspection found the historical files untracked and absent from available tracked history. Published source-linked PDB/manifests tying exact historical source bytes to Release 1.0.3 are unavailable. Exact historical source-byte identity cannot be claimed. The existing successful semantic comparison is trusted prior certification evidence; it is reused rather than recomputed, and its record does not enumerate exact compared assembly paths or whole-file hashes. Today's artifact fingerprints provide a reproducible current inventory, not retroactive publication provenance.

Those are **EVIDENCE_LIMITATION**, not blockers under the user's clarified contract. There is no evidence that the released migration was rewritten. The unchanged migration ID, matching released/current operations and target model, unchanged current source fingerprints, and inspected new append-only repair satisfy Path B. Phase 3A.1 has zero Critical blockers, zero High blockers, zero blocked required gates and zero unexecuted required gates. Overall Phase 3A lock requires the separate remaining closure and independent review.
