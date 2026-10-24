# Manifest / Fingerprint Finding — Correction of a Stale Blocker Claim

**Run:** `ER-RUN-CATALOG-CORRECTION-20261021-107`  
**Date:** 2026-10-22  
**Subject:** CAT-107 open item (1), "complete deterministic manifest"

This document **corrects** an inherited claim and records two genuine defects that the audit
actually found. Read the "Correction" section first — the previously recorded blocker was partly
wrong, and repeating it would be its own kind of false evidence.

## Correction: the aliases/ambiguous-terms omission no longer exists

`CURRENT_BLOCKERS.md` and `CORRECTION_NOTICE.md` both stated:

> "`ComputeStoreHash()` still omits canonical fields/entities including aliases and ambiguous terms."

**That is not true of the current source.** Verified directly at
`src/EdgeRetails.Infrastructure/Services/TaxonomyV3Importer.cs:1111-1187`, `ComputeStoreHash()`
hashes **ten** entity groups:

| # | Group | Count | Line |
| ---: | :--- | ---: | ---: |
| 1 | Divisions | 9 | 1116 |
| 2 | Departments | 65 | 1122 |
| 3 | Product classes | 732 | 1128 |
| 4 | Class redirects | 42 | 1134 |
| 5 | Unit definitions | 51 | 1140 |
| 6 | Facet rules | — | 1151 |
| 7 | Attribute profiles | — | 1157 |
| 8 | **Search aliases** | 304 | 1163 |
| 9 | **Ambiguous terms** | 16 | 1169 |
| 10 | Collision policies | 7 | 1176-1182 |

Aliases and ambiguous terms are present, at lines 1163-1173. The blocker text was stale — it
described an earlier revision of the file. The claim is withdrawn. Recording it accurately matters
more than preserving the appearance of a consistent narrative.

**Field-level completeness also checks out.** Every stateful member of every canonical record
(`Division`, `Department`, `ProductClass`, `ClassRedirect`, `SearchAlias`, `AmbiguousSearchTerm`,
`UnitDefinition`, `AttributeProfile`, `ClassFacetRule`) appears in the hash. The only omissions are
get-only computed properties (`IsActiveLeaf`, `IsRedirect`, `IsNavigationOnly`, `IsUnreviewedLanguage`,
`IsEmptyEnum`, `IsNumericUnbounded`) — all of which are pure functions of already-hashed fields, so
omitting them is correct, not a gap.

## Genuine defect 1: `AppliedManifestSha256` ignores `ExtraClasses` / `ExtraRedirects`

`ApplySeedAsync` accepts `TaxonomySeedOptions` and genuinely writes `options.ExtraClasses` (lines
531-538) and `options.ExtraRedirects` (lines 554-561) into the target.

But the result it returns is built from:

```csharp
// TaxonomyV3Importer.cs:591
AppliedManifestSha256: ComputeStoreHash(),
```

`ComputeStoreHash()` takes **no parameters** and hashes only `TaxonomyV3Store.*`. So when extras are
applied, the reported manifest digest describes the static store, **not what was actually written**.

That is exactly the class of defect this run exists to catch: a field that looks like evidence but
is not bound to the thing it claims to describe. A reviewer comparing two runs' `AppliedManifestSha256`
would see the same digest even though one run seeded extra classes.

**Status: RECORDED, NOT FIXED.** Fixing it means either hash-stamping the applied payload or making
`ComputeStoreHash` take the payload as input — a contract change with its own parity implications.
It is not a one-line correction and does not belong in the tail of this wave. It stays open.

## Genuine defect 2: preview and apply fingerprints are structurally incomparable

Both call sites use `ComputeTargetFingerprint(...)`, but pass **different dictionary keys**:

| | Dry run (line 413) | Apply (lines 817-823) |
| :--- | :--- | :--- |
| Survey keys | `catalog_v3.divisions`, `catalog_v3.departments`, `catalog_v3.product_classes`, `catalog_v3.class_redirects` | `divisions`, `departments`, `product_classes`, `class_redirects`, `foreign_key_present` |
| Operation keys | 23 diff keys (`divisions_to_insert`, `total_to_update`, …) | `source_divisions`, `source_departments`, `source_product_classes`, `source_class_redirects` |

`ComputeTargetFingerprint` concatenates `key=value` pairs and SHA-256s them, so different key names
produce different digests over identical data. Preview and apply therefore emit two hash strings that
**cannot be compared**, even though both are named `TargetFingerprint`.

`VerifyAppliedRowsAsync` does not depend on this comparison to decide correctness — it independently
verifies row-by-row (`REDIRECT_ROW_MISMATCH`, etc.) and `IsExact` drives `PreviewApplyParityAchieved`.
So this is **not** a correctness hole in the apply path. It is a reporting hole: the paired field a
reviewer would naturally use to assert "the target is exactly what preview predicted" will never
match, and its current shape invites a false negative.

**Status: RECORDED, NOT FIXED.** Same reasoning as defect 1 — reconciling the two key sets is a
contract decision, and the real parity guarantee currently rests on row-level verification, which
does work.

## And: apply does not verify the target is unchanged since preview

Confirmed by direct inspection. `ApplySeedAsync` takes no expected fingerprint, and
`VerifyAppliedRowsAsync` computes a *fresh* target fingerprint after writing rather than comparing
against one captured at preview time. The documented protection is instead the SERIALIZABLE
transaction plus the transaction-scoped advisory lock (lines 466-478), which makes the apply path
atomic and serialized.

That is a legitimate design — serializable + advisory lock genuinely prevents two importers from
interleaving — but it is not the same property as "the target has not drifted since the human
reviewed the preview". The recorded blocker language ("checks target drift") described a check that
does not exist. Two different claims were being conflated:

- *No concurrent writer can interleave.* — **Holds**, via serializable + `pg_advisory_xact_lock`.
- *The target still matches what preview computed.* — **Does not hold**; nothing compares them.

## Net effect on the blocker

CAT-107 open item (1) **remains OPEN**, but for narrower and better-stated reasons than before:

1. ~~Aliases and ambiguous terms are omitted.~~ — **withdrawn, they are hashed.**
2. `AppliedManifestSha256` does not digest extras that were actually applied. — **open**
3. Preview and apply `TargetFingerprint` values are incomparable by construction. — **open**
4. Apply has no preview-time drift check; it relies on serializable isolation instead. — **open**

Items 2-4 are contract and reporting defects around the manifest. None of them corrupts data, and
none is closed by the integration suite going green. They are recorded here so the next reviewer
starts from an accurate map rather than inheriting a claim that was already repaired.
