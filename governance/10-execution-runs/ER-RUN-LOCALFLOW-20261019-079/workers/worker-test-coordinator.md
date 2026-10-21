# Native Worker Log: Test Coordinator

- **Worker ID**: `test-coordinator`
- **Runtime ID**: `actor-test-coordinator-079`
- **Role**: Test creation, test execution, compilation diagnostics, and raw TRX log collection
- **Assigned Tasks**: `OWNER019-LOCALPOS-LF07-TESTS`

## Actions Performed
1. Updated test handler factories in `tests/EdgeRetails.UnitTests/Phase2StocktakeThakaCashSessionBehavioralTests.cs` to accept optional `IApplicationPermissionAuthorizer`.
2. Created 5 dedicated named behavioral tests in `tests/EdgeRetails.UnitTests/Phase2StocktakeThakaCashSessionBehavioralTests.cs` (lines 370-450):
   - `LF07_OpenCashSession_WithoutPosPermission_RejectedWithUnauthorized`: Asserts that when authorizer denies POS permission, open cash session fails with exact code `"auth.forbidden"`.
   - `LF07_OpenCashSession_WithEmptyActor_WhenAuthorizerConfigured_RejectedWithActorRequired`: Asserts that when authorizer is present, empty `ActorId` cannot bypass authorization and fails with `"auth.actor_required"`.
   - `LF07_CloseCashSession_WithoutPosPermission_RejectedWithUnauthorized`: Asserts that when authorizer denies POS permission on close, closing an active session fails with exact code `"auth.forbidden"`.
   - `LF07_CloseCashSession_WithEmptyActor_WhenAuthorizerConfigured_RejectedWithActorRequired`: Asserts that empty `ActorId` on close fails with `"auth.actor_required"`.
   - `LF07_CloseCashSession_WithEmptySessionId_RejectedWithSessionIdRequired`: Asserts that empty `SessionId` on close fails immediately with `"cash.session_id_required"`.
3. Executed `dotnet test tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj --filter "FullyQualifiedName~LF07"`: 5 passed, 0 failed.
4. Executed full unit test suite: 1,387 passed / 1,387 total (0 failed, 0 skipped, duration 26 s).
5. Executed desktop performance safety test suite: 409 passed / 409 total (0 failed, duration 26 s).
6. Generated raw TRX test logs:
   - `run079_unit_tests.trx` (1,387 tests passed, 0 failed).
   - `run079_desktop_performance_safety_tests.trx` (409 tests passed, 0 failed).
   - `build.log` (12 projects built, 0 warnings, 0 errors).
