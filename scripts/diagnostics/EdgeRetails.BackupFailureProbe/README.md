# Bounded installed backup exception probe

Read-only runtime EventPipe observer; never takes a dump, reads process environment, writes a raw trace, or changes services/configuration. Only three exact static source messages from the installed backup provider/job lock are emitted. Other exception payloads are discarded in memory. Does not use network credentials or Desktop tokens. Production target restricted to the installed Server path/version 1.0.11; PID validated at execution. Normal administrator elevation may be required because installed service is LocalSystem. Never bypass UAC.

Build this independent tool and execute `--self-test` first. The canary throws all allowed cases and a deliberately unknown private sentinel. Self-test must show three categories and no private sentinel. A real capture remains a separate gate: authenticate installed Desktop, attach for at most 120 seconds, invoke Backup Now once, match runtime timestamp/exception with UI failure and safe audit correlation. NO_MATCH is not PASS and does not justify a missing-key root-cause assertion.

Implementation follows the official [Diagnostics client live EventPipe example](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/diagnostics-client-library). No product dependency or source-run Server is introduced.
