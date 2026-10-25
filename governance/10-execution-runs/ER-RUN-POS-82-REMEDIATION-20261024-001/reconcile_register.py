"""Join causal repair dispositions without changing original case identities or errors."""
import argparse
import csv
import hashlib
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

RUN = Path(__file__).resolve().parent
ROOT = RUN.parents[2]
path = RUN / 'FAILURE_REGISTER.json'
original = RUN / 'FAILURE_REGISTER.original.json'
if not original.exists(): shutil.copyfile(path, original)
data = json.loads(original.read_text(encoding='utf-8-sig'))
specific = {
 11: ('B9', 'INVALID_TEST_PRECONDITION', 'Fixture renames a unique unit to existing Piece; safely reuse canonical unit and preserve provenance/label assertions.'),
 35: ('B8', 'INVALID_TEST_PRECONDITION', 'Fixed purchase date was outside live posting window; inject the timestamp fixture clock without changing date restrictions.'),
 40: ('B1', 'CONFIRMED_PRODUCT_DEFECT', 'Changed-actor replay rejection was followed by failure recording against the original succeeded operation; record failure only for this attempt\'s claimed pending outcome under the operation lock.'),
 41: ('B7', 'INVALID_TEST_ORACLE', 'Actor context has a typed context_mismatch contract; retain entire original succeeded outcome and one domain effect.'),
 42: ('B7', 'INVALID_TEST_ORACLE', 'Supplier operation-type collision has typed operation_type_mismatch; preserve the separate expense handler contract and immutable succeeded outcome.'),
 43: ('B8', 'INVALID_TEST_PRECONDITION', '2037 report clock conflicted with today-based purchase/session seeds; use injected clock consistently.'),
 52: ('B5', 'EXISTING_REPAIR_AWAITING_VERIFICATION', 'Exact complete-pack carrying amount must survive commercial exchange and replay; preserve assertions, verify production amount-based cost paths.'),
 53: ('B3', 'INVALID_TEST_PRECONDITION', 'Missing immutable operation ID prevented reaching the post-SQL-flush fault; supply stable ID and assert fault reached plus fresh-scope rollback.'),
 54: ('B3', 'INVALID_TEST_PRECONDITION', 'Manual cash requires a stable operation ID; preserve missing-ID rejection and prove exact replay/changed-intent rejection with one effect.'),
 55: ('B3', 'INVALID_TEST_PRECONDITION', 'Manual cash requires a stable operation ID; preserve missing-ID rejection and prove exact replay/changed-intent rejection with one effect.'),
 56: ('B4', 'INVALID_TEST_PRECONDITION', 'Unsupported refund method preempts missing-unit eligibility; use approved Bank precondition and retain a separate unsupported-method no-mutation assertion.'),
 70: ('B6', 'INVALID_TEST_ORACLE', 'Per-project non-netting yields 100-10-5-40+20=65; an overpaid second project contributes zero debt, not negative ten.')
}
for n in range(44, 52):
 specific[n] = ('B2' if n < 47 else 'B5', 'EXISTING_REPAIR_AWAITING_VERIFICATION',
                 'Existing G13/H1 source repair has a matching later pass, but final candidate binding and consolidated regression remain required.')
for row in data['rows']:
 n = int(row['row_id'].split('-')[1])
 if n in specific:
  task, disposition, cause = specific[n]
 elif row['symptom_group'] == 'E1_FIXTURE_GUARDS':
  task, disposition = ('E4' if 'Cutover' in row['test_name_and_parameters'] else 'E1'), 'FIXTURE_RUNNER_INCOMPATIBILITY'
  cause = 'The generic Catalog fixture violates this family\'s explicit database/root/schema contract. Use separate immutable migrated Phase11, migrated Master and model-only Phase1 profiles; retain all endpoint/custody guards.'
 elif row['symptom_group'] == 'E2_PERMISSIONS':
  task, disposition = ('E4' if 'deadlock_timeout' in row['original_error'] else 'E2'), 'FIXTURE_RUNNER_INCOMPATIBILITY'
  cause = ('A real deadlock does not require the application role to override a privileged timing parameter; use the observed server interval and retain SQLSTATE40P01/recovery.' if task == 'E4' else
           'Database/role lifecycle used the restricted application connection. Route only generated owned resources through the separately attested maintenance connection; do not grant administrator membership to write tests.')
 elif row['symptom_group'] == 'E3_CHILD_SDK':
  task, disposition, cause = 'E3', 'FIXTURE_RUNNER_INCOMPATIBILITY', 'Child process launches relied on PATH and stale/copied/Debug host fallback. Use the absolute global.json SDK and a complete source/current Release host output manifest.'
 else: raise RuntimeError('Unmapped original case: '+row['row_id'])
 row['repair_task'] = task
 row['disposition'] = disposition
 row['causal_trace'] = cause
 row['repair_or_no_change'] = 'Implemented; source and runtime proof must be joined before acceptance.'
 row['approved_invariant'] = 'Original business/safety assertion retained; exact replay has one effect, rejected intent preserves the original outcome, operational endpoints and unowned resources fail closed.'
 for ref in row['source_references']:
  p = ROOT / ref['path']
  if p.exists(): ref['after_sha256'] = hashlib.sha256(p.read_bytes()).hexdigest()
 row['final_result'] = 'NOT_VERIFIED_ON_FINAL_CANDIDATE'
 row['final_evidence'] = None

parser = argparse.ArgumentParser()
parser.add_argument('--final-trx', type=Path)
args = parser.parse_args()
if args.final_trx:
 ns = {'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
 results = ET.parse(args.final_trx).findall('.//t:UnitTestResult', ns)
 indexed = {}
 for result in results: indexed.setdefault(result.get('testName'), []).append(result)
 for row in data['rows']:
  matches = indexed.get(row['test_name_and_parameters'], [])
  row['final_result'] = matches[0].get('outcome') if len(matches) == 1 else 'MISSING_OR_AMBIGUOUS'
  row['final_evidence'] = {'path':str(args.final_trx.resolve()), 'sha256':hashlib.sha256(args.final_trx.read_bytes()).hexdigest(),
                           'matches':len(matches),'candidate_binding':'Requires matching passing runner terminal, source/binary manifest and readback; not inferred from TRX alone.'}
 assert len(data['rows']) == 82 and len({r['test_name_and_parameters'] for r in data['rows']}) == 82
data['root_cause_note'] = 'Symptom counts remain 28+26+8+11+4+2+2+1=82. E3 original failures are five Phase12 cases and three Phase3 cases; ten launch sites were repaired (seven Phase12, three Phase3).'
path.write_text(json.dumps(data, indent=2, ensure_ascii=False)+'\n', encoding='utf-8')
with (RUN/'FAILURE_REGISTER.csv').open('w', encoding='utf-8-sig', newline='') as f:
 fields=['row_id','test_name_and_parameters','symptom_group','repair_task','disposition','causal_trace','affected_selector','final_result']
 writer=csv.DictWriter(f, fields, extrasaction='ignore');writer.writeheader();writer.writerows(data['rows'])
print('Reconciled exactly 82 original cases; runtime acceptance remains explicit.')
