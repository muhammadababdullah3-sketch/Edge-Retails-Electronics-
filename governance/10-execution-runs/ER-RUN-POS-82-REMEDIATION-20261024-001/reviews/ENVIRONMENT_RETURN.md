# Native environment worker return

Platform-issued task identity: `/root/r82_environment`. Returned through `collaboration` to root; this is an implementation return, not test acceptance.

Implemented only `IntegrationChildHost.cs`, `Phase12Wave3PostgresTests.cs`, and `Phase3CrashRestartIntegrationTests.cs`. Ten child launches (seven Phase12, three Phase3) now use an absolute SDK resolver. The resolver checks global.json and a complete source/current Release output manifest; copied and Debug fallbacks are rejected. Deadlock recovery reads the real server deadlock interval, bounds the operation, and retains SQLSTATE 40P01, rollback, and replay assertions.

Required runner variables: `EDGE_RETAILS_TEST_DOTNET`, `EDGE_RETAILS_CRASH_HOST_MANIFEST`. Manifest fields: candidate_root, configuration=Release, dotnet_sdk_version, built_utc, source_inputs[path,sha256], output_files[path,sha256]. Source membership includes src, CrashTestHost, IntegrationTests and existing root/.config build inputs; generated bin/obj/artifacts/TestResults are excluded from source membership. All Release host output files are included. Timestamp must follow source modification times within one second and not exceed current time by five minutes.

Worker reported `git diff --check` passed. It ran no build, test or DB operation. Delivered files released to root for integration and coordinator verification.
