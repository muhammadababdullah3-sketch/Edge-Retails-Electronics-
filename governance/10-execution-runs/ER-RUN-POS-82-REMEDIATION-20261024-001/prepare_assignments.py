from pathlib import Path
import json, hashlib, shutil
from datetime import datetime, timezone

run = Path(__file__).resolve().parent
root = run.parents[2]
relrun = run.relative_to(root).as_posix()
integrator = [
    'tests/EdgeRetails.IntegrationTests/Phase2PostgresTestHarness.cs',
    'tests/EdgeRetails.IntegrationTests/PostgresFixtureProfile.cs',
    'tests/EdgeRetails.IntegrationTests/MasterSupplierProductSequencePostgresTests.cs',
    'tests/EdgeRetails.IntegrationTests/MasterCashierCreationPostgresTests.cs',
    'tests/EdgeRetails.IntegrationTests/Phase1FrontendRecoveryContractPostgresTests.cs',
    'tests/EdgeRetails.IntegrationTests/TrackingMigrationRehearsalPostgresTests.cs',
    'tests/EdgeRetails.IntegrationTests/Phase7Pass5WarrantyMigrationPostgresTests.cs',
    'tests/EdgeRetails.IntegrationTests/Phase7Pass5R1MultilotExactWarrantyPostgresTests.cs',
    'scripts/Invoke-IntegrationFailureRemediation.ps1', relrun]
environment = [
    'tests/EdgeRetails.IntegrationTests/IntegrationChildHost.cs',
    'tests/EdgeRetails.IntegrationTests/Phase12Wave3PostgresTests.cs',
    'tests/EdgeRetails.IntegrationTests/Phase3CrashRestartIntegrationTests.cs']
behavior = [
    'src/EdgeRetails.Application/Features/Finance/SupplierAccountHandlers.cs',
    'tests/EdgeRetails.IntegrationTests/Phase7Pass5CommercialPostgresTests.cs',
    'tests/EdgeRetails.IntegrationTests/Phase7Pass5WarrantyReportingPostgresTests.cs',
    'tests/EdgeRetails.IntegrationTests/Phase7BackendFinancialSafetyPostgresTests.cs',
    'tests/EdgeRetails.IntegrationTests/Phase7Pass3PostgresTests.cs',
    'tests/EdgeRetails.IntegrationTests/Phase234KhataTimestampPostgresTests.cs',
    'tests/EdgeRetails.IntegrationTests/MasterLabelSourcePostgresTests.cs']
before = []
for name in integrator[:-1] + environment + behavior:
    p = root / name
    dst = run / 'before' / name
    if p.is_file():
        if not dst.exists():
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(p, dst)
        before.append({'path':name,'exists':True,'sha256':hashlib.sha256(dst.read_bytes()).hexdigest()})
    else: before.append({'path':name,'exists':False})
(run/'ASSIGNED_BEFORE.json').write_text(json.dumps(before,indent=2)+'\n',encoding='utf-8')
attachment = Path(r'C:\Users\muham\.codex\attachments\7367354d-c7da-4a06-a6a9-b20d7448a891\pasted-text-1.txt')
authority = {'observed_utc':datetime.now(timezone.utc).isoformat(),'user_instruction_verbatim':'ye corrected goal ha start implementation',
             'attachment':str(attachment),'attachment_sha256':hashlib.sha256(attachment.read_bytes()).hexdigest(),
             'thread_id':'01a16647-b3d8-7a30-b1cd-c42f1da47ef8','writer':'current root integrator',
             'assignment_basis':'Current explicit owner instruction authorizes this task to implement the corrected handoff. It supersedes the previous planning-only scope; no historical handoff or expired lease is claimed verified.',
             'capacity':{'limit':4,'includes_coordinator':True,'observed_active':['/root'],'proof':'collaboration.list_agents returned only root before this assignment'},
             'resource_owner':'root only; workers cannot run builds/tests/DB operations','publication':'not authorized'}
(run/'CAPACITY_AND_AUTHORITY.json').write_text(json.dumps(authority,indent=2)+'\n',encoding='utf-8')
shutil.copyfile(attachment,run/'OWNER_HANDOFF.txt')
workers=[]
for wid, paths, criteria in [('environment',environment,['E3 absolute SDK/current Release child binary','E4 real 40P01 deadlock without privileged timing override']),('behavior',behavior,['B1 supplier claimed-outcome ownership','B3-B9 original behavioral invariants and fixture repair'])]:
    workers.append({'id':wid,'runtime_id':None,'role':'bounded implementation','status':'PLANNED','required':True,'completion_outcome':None,
                    'original_task_ids':['R82_ORIGINAL_82'],'criterion_bindings':criteria,'completion_criteria':criteria,
                    'deliverable':'Implement assigned paths and return exact causal explanation/source locations; do not build/test/access DB',
                    'depends_on':[],'read_only':False,'read_paths':['src','tests','scripts',relrun+'/FAILURE_REGISTER.json'],
                    'write_paths':paths,'resources':[],'can_run_tests':False,'proof_paths':[]})
plan={'schema_version':1,'status':'PLANNED','plan_id':'R82-REMEDIATION-001','authority':authority,'phase_wave':'82 historical failures; Phase11 formal closure separate',
      'workspace':str(root),'baseline_proof':relrun+'/BASELINE.json','capacity_proof':relrun+'/CAPACITY_AND_AUTHORITY.json',
      'capacity_limit':4,'capacity_scope':'all_actors','coordinator_slots':1,'other_active_slots':0,'requested_parallelism':2,
      'integrator_id':'root','integrator_runtime_id':'01a16647-b3d8-7a30-b1cd-c42f1da47ef8','integrator_write_paths':integrator,
      'test_coordinator_id':'root','resource_owners':{'builds':'root','test_outputs':'root','PostgreSQL':'root','backup_restore':'root'},'workers':workers,
      'independent_review':'Assign a separate native read-only reviewer after integration and candidate/result freeze; no D05 acceptance inferred',
      'efficiency':{'started_utc':datetime.now(timezone.utc).isoformat(),'speedup':'not_measured'}}
(run/'PARALLEL_PLAN.json').write_text(json.dumps(plan,indent=2)+'\n',encoding='utf-8')
print('Preserved assigned before bytes; saved two-worker plan.')
