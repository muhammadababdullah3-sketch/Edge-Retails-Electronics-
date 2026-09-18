# Edge Retails

Windows desktop POS for electronics and electrical retail shops.

## Stack
- .NET 10 LTS
- WPF + MVVM
- PostgreSQL
- EF Core / Npgsql
- Dapper for report-heavy queries
- xUnit tests
- .NET Worker Service for background jobs

## Architecture
`Desktop -> Application -> Domain`

`Infrastructure -> Application + Domain`

The Desktop and Worker projects are composition roots and may reference Infrastructure.

## Build
`dotnet build EdgeRetails.sln`

## Test
`dotnet test EdgeRetails.sln`
