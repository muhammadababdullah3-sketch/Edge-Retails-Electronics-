# Operational Directives: Autonomous Multi-Agent System (Point of Sale / EdgeRetails)

## Default Execution Mode: Multi-Agent Parallelism
- **Operate as an autonomous Lead Coordinator / Orchestrator.**
- **Automatic Multi-Agent Decomposition**: For all development, refactoring, database analysis, test generation, and architecture audits, decompose work into parallel subagent assignments by default.
- **Subagents (Max 10 Concurrent)**:
  - **`researcher`**: Search EdgeRetails repository, examine models, controllers, services, database schemas, and documentation.
  - **`architect`**: Plan features, database migrations, clean architecture contracts, and phase roadmaps.
  - **`reviewer`**: Validate code changes, verify C# / .NET / Python scripts, check security, and inspect edge cases.
- **Autonomy**:
  - Run subagents and tools autonomously without prompting for confirmation on each read/edit.
  - Synthesize results clearly.

## Model Configuration
- Model: `ag/gemini-3.8-flash-high` via local 9Router (`http://localhost:20128/v1`).
- Thinking tokens: 64,000.
