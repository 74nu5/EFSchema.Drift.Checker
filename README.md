# ef-schema-diff

[![CI](https://github.com/74nu5/EFSchema.Drift.Checker/actions/workflows/ci.yml/badge.svg)](https://github.com/74nu5/EFSchema.Drift.Checker/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/ef-schema-diff?logo=nuget&label=nuget)](https://www.nuget.org/packages/ef-schema-diff)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ef-schema-diff?logo=nuget&label=downloads)](https://www.nuget.org/packages/ef-schema-diff)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> **Detect schema drift between your EF Core model and your real SQL Server database — before it hits production.**

`ef-schema-diff` is a .NET global CLI tool that loads your `DbContext` directly from a compiled assembly, reads the live database schema, and produces a precise diff of every table, column, index, foreign key, and constraint that is out of sync. It integrates naturally into CI/CD pipelines with structured output formats and deterministic exit codes.

```
──────────────────── ef-schema-diff ────────────────────
 Assembly   MyApp.Data.dll
 Context    AppDbContext
 Database   localhost / mydb

 ✔ EF Core model loaded    (12 entities)
 ✔ Database schema read    (14 tables)
 ✔ Comparison complete     (3 differences)

 ─────────────────── Differences ────────────────────
 ┌──────────────────────────┬──────────────┬────────────────────────────────────┐
 │ Type                     │ Object       │ Details                            │
 ├──────────────────────────┼──────────────┼────────────────────────────────────┤
 │ ColumnTypeMismatch       │ Orders.Total │ EF: decimal(18,2) → DB: float      │
 │ IndexMissingInDatabase   │ Orders       │ IX_Orders_CustomerId               │
 │ TableMissingInDatabase   │ AuditLogs    │                                    │
 └──────────────────────────┴──────────────┴────────────────────────────────────┘

 ✘ 3 differences  ·  2 errors  ·  1 warning
```

---

## Table of Contents

- [Requirements](#requirements)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Usage Reference](#usage-reference)
  - [Options](#options)
  - [Exit Codes](#exit-codes)
  - [Environment Variables](#environment-variables)
- [Output Formats](#output-formats)
- [Filtering](#filtering)
- [CI/CD Integration](#cicd-integration)
- [How It Works](#how-it-works)
- [Detected Differences](#detected-differences)
- [Troubleshooting](#troubleshooting)
- [Contributing](#contributing)
- [License](#license)

---

## Requirements

| Requirement | Version |
|---|---|
| .NET SDK | 10.0+ |
| EF Core (target project) | 10.x |
| SQL Server | 2016+ / Azure SQL |

---

## Installation

```bash
dotnet tool install --global ef-schema-diff
```

Update to the latest version:

```bash
dotnet tool update --global ef-schema-diff
```

Verify:

```bash
ef-schema-diff --version
```

---

## Quick Start

Point the tool at your compiled assembly and your database:

```bash
ef-schema-diff compare \
  --assembly ./src/MyApp.Data/bin/Release/net10.0/MyApp.Data.dll \
  --connection "Server=localhost;Database=mydb;Integrated Security=True;Encrypt=false"
```

When a single `DbContext` exists in the assembly, `--context` can be omitted. The tool prints a live progress header to **stderr** and the diff table to **stdout**, so piping works cleanly:

```bash
ef-schema-diff compare -a MyApp.Data.dll -s "..." --output json > drift.json
```

---

## Usage Reference

```
ef-schema-diff compare [options]
```

### Options

| Option | Alias | Required | Default | Description |
|---|---|---|---|---|
| `--assembly` | `-a` | ✅ | — | Path to the `.dll` containing the `DbContext`. |
| `--connection` | `-s` | * | — | SQL Server connection string. Falls back to `EFSD_CONNECTION` env var. |
| `--context` | `-c` | — | auto | `DbContext` type name (simple or fully qualified). Required when the assembly contains more than one `DbContext`. |
| `--startup-assembly` | | — | — | Path to the startup assembly (used to resolve `IHostBuilder` / generic host). |
| `--schema` | | — | all | Restrict comparison to a single database schema, e.g. `dbo`. |
| `--ignore-tables` | | — | — | Glob patterns of tables to exclude. Repeatable. |
| `--ignore-columns` | | — | — | Glob patterns of columns to exclude. Repeatable. |
| `--exclude-navigation-tables` | | — | `false` | Skip auto-generated many-to-many join tables. |
| `--output` | `-o` | — | `table` | Output format: `table`, `json`, `markdown`, `sarif`. |
| `--output-file` | | — | — | Write formatted output to a file instead of stdout. |
| `--min-severity` | | — | `Warning` | Minimum severity to report: `Error`, `Warning`, `Info`. |
| `--no-color` | | — | `false` | Disable ANSI colour output (useful in terminals without colour support). |

### Exit Codes

| Code | Meaning |
|---|---|
| `0` | No differences at or above the minimum severity. |
| `1` | Differences found. |
| `2` | Invalid configuration (missing assembly, unknown severity, …). |
| `3` | Database connection or query error. |
| `4` | Could not load or instantiate the `DbContext`. |
| `5` | Unexpected runtime error. |

These codes make it straightforward to gate a CI step:

```bash
ef-schema-diff compare -a MyApp.Data.dll -s "$CONN" || exit $?
```

### Environment Variables

| Variable | Description |
|---|---|
| `EFSD_CONNECTION` | Fallback connection string when `--connection` is not provided. Useful for CI secrets. |

---

## Output Formats

### `table` (default)

Rich Spectre.Console table rendered to **stdout**, with coloured severity indicators and an inline summary. Progress messages go to **stderr** and are never mixed into the table output.

### `json`

Machine-readable JSON — ideal for downstream tooling, dashboards, or custom reporters.

```bash
ef-schema-diff compare -a MyApp.Data.dll -s "$CONN" --output json > drift.json
```

```json
{
  "differenceCount": 2,
  "differences": [
    {
      "type": "ColumnTypeMismatch",
      "severity": "Error",
      "objectName": "Orders.Total",
      "details": "Type mismatch",
      "expectedValue": "decimal(18,2)",
      "actualValue": "float"
    }
  ]
}
```

### `markdown`

Generates a Markdown table — ready to paste into a PR description or post as a pull-request comment.

```bash
ef-schema-diff compare -a MyApp.Data.dll -s "$CONN" --output markdown --output-file drift.md
```

### `sarif`

[SARIF 2.1.0](https://sarifweb.azurewebsites.net/) output — compatible with GitHub Code Scanning, Azure DevOps, and any SARIF-aware IDE.

```bash
ef-schema-diff compare -a MyApp.Data.dll -s "$CONN" --output sarif --output-file drift.sarif
```

Upload to GitHub Code Scanning:

```yaml
- uses: github/codeql-action/upload-sarif@v3
  with:
    sarif_file: drift.sarif
```

---

## Filtering

### Ignore specific tables

```bash
# Exclude EF migrations history and all Hangfire tables
ef-schema-diff compare -a MyApp.Data.dll -s "$CONN" \
  --ignore-tables "__EFMigrationsHistory" \
  --ignore-tables "Hangfire.*"
```

### Ignore specific columns

```bash
# Exclude audit timestamp columns everywhere
ef-schema-diff compare -a MyApp.Data.dll -s "$CONN" \
  --ignore-columns "*.CreatedAt" \
  --ignore-columns "*.UpdatedAt"
```

### Restrict to a schema

```bash
ef-schema-diff compare -a MyApp.Data.dll -s "$CONN" --schema dbo
```

### Skip navigation join tables

```bash
ef-schema-diff compare -a MyApp.Data.dll -s "$CONN" --exclude-navigation-tables
```

### Only fail on errors (ignore warnings and info)

```bash
ef-schema-diff compare -a MyApp.Data.dll -s "$CONN" --min-severity Error
```

---

## CI/CD Integration

### GitHub Actions

```yaml
name: Schema Drift Check

on:
  push:
    branches: [main]
  pull_request:

jobs:
  schema-drift:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.x'

      - name: Build
        run: dotnet build src/MyApp.Data -c Release

      - name: Install ef-schema-diff
        run: dotnet tool install --global ef-schema-diff

      - name: Check schema drift
        env:
          EFSD_CONNECTION: ${{ secrets.DB_CONNECTION }}
        run: |
          ef-schema-diff compare \
            --assembly src/MyApp.Data/bin/Release/net10.0/MyApp.Data.dll \
            --ignore-tables "__EFMigrationsHistory" \
            --output sarif \
            --output-file drift.sarif

      - name: Upload SARIF
        if: always()
        uses: github/codeql-action/upload-sarif@v3
        with:
          sarif_file: drift.sarif
```

### Azure DevOps

```yaml
- task: DotNetCoreCLI@2
  displayName: Install ef-schema-diff
  inputs:
    command: custom
    custom: tool
    arguments: install --global ef-schema-diff

- script: |
    ef-schema-diff compare \
      --assembly $(Build.BinariesDirectory)/MyApp.Data.dll \
      --connection "$(DbConnectionString)" \
      --ignore-tables "__EFMigrationsHistory" \
      --min-severity Error
  displayName: Check schema drift
```

---

## How It Works

### Assembly isolation

The tool loads your `DbContext` into a **collectible `AssemblyLoadContext` (ALC)**. This prevents version conflicts between your project's dependencies and the tool's own dependencies. The ALC is unloaded after extraction — no types or assemblies from your project leak into the tool's process.

EF Core types (`IModel`, `IEntityType`, …) are intentionally **shared** between the user ALC and the tool's context, so the extractor can walk the model without reflection bridging.

For `Microsoft.Extensions.*` assemblies, the tool uses a smart sharing strategy:
- Extensions **the tool bundles** → shared from the tool's context.
- Extensions **the tool does not bundle** (e.g. `HealthChecks.Abstractions`) → resolved from your project's own dependency tree.

This avoids `FileNotFoundException` spam for any non-standard `Microsoft.Extensions.*` package your project happens to depend on.

### DbContext instantiation strategies

The tool tries four strategies in order, stopping at the first success:

| # | Strategy | When it works |
|---|---|---|
| 1 | `IDesignTimeDbContextFactory<T>` | Recommended — explicit control over instantiation. |
| 2 | Constructor with `DbContextOptions<T>` | Works for most standard contexts. |
| 3 | `IHostBuilder` / Generic Host | Works when the startup project wires DI. Requires `--startup-assembly`. |
| 4 | Parameterless constructor | Fallback for simple contexts. |

> **Tip:** Adding an `IDesignTimeDbContextFactory<T>` in your data project is the most robust approach and is already required for EF Core migrations.

### EF Core version check

Before loading anything, the tool reads the EF Core version from your assembly's dependency manifest (without loading any types) and fails fast with a clear message if the major version does not match the tool's built-in EF Core.

---

## Detected Differences

| Diff Type | Severity | Description |
|---|---|---|
| `TableMissingInDatabase` | Error | Table exists in the EF model but not in the database. |
| `TableMissingInModel` | Info | Table exists in the database but not in the EF model. |
| `ColumnMissingInDatabase` | Error | Column exists in the EF model but not in the database. |
| `ColumnMissingInModel` | Warning | Column exists in the database but not in the EF model. |
| `ColumnTypeMismatch` | Error | Column store type differs between EF model and database. |
| `NullabilityMismatch` | Warning | Nullability differs between EF model and database. |
| `MaxLengthMismatch` | Warning | Max length differs between EF model and database. |
| `PrecisionMismatch` | Warning | Numeric precision differs. |
| `ScaleMismatch` | Warning | Numeric scale differs. |
| `PrimaryKeyMismatch` | Error | Primary key definition differs. |
| `ForeignKeyMismatch` | Warning | Foreign key definition differs. |
| `IndexMissingInDatabase` | Warning | Index defined in EF model is absent from the database. |
| `IndexMissingInModel` | Info | Index exists in the database but is not in the EF model. |
| `UniqueConstraintMismatch` | Warning | Unique constraint definition differs. |
| `SchemaMismatch` | Warning | Table schema differs between EF model and database. |

---

## Troubleshooting

### `Could not load DbContext` — missing assembly

```
Could not load all types from 'MyApp.Data.dll'. Missing: Some.ThirdParty.Package
```

**Cause:** The tool loaded your assembly but a transitive dependency was not present next to the `.dll`.

**Fix:** Point `--assembly` at the **build output directory** (e.g. `bin/Release/net10.0/`), not a copied or published single-file path. All dependent assemblies must be co-located.

---

### EF Core version mismatch

```
EF Core major version mismatch.
  Target assembly requires: EF Core 9.x
  ef-schema-diff is built with: EF Core 10.x
```

**Fix:** Upgrade your project to EF Core 10, or use the version of `ef-schema-diff` built for your EF Core version.

---

### Multiple `DbContext` types found

```
Multiple DbContext types found: AppDbContext, ReadOnlyDbContext. Use --context to specify one.
```

**Fix:** Pass `--context AppDbContext` (simple name or fully qualified).

---

### Windows Authentication connection refused

Use `Trusted_Connection=True` (or `Integrated Security=SSPI`) — **not** `Trusted_Connection=false`:

```
Server=localhost;Database=mydb;Trusted_Connection=True;Encrypt=false
```

---

### No colour output in CI

Pass `--no-color` or set the standard `NO_COLOR=1` / `TERM=dumb` environment variables.

---

## Contributing

Contributions are welcome! Please open an issue before submitting a large pull request so we can align on the approach.

```bash
git clone https://github.com/your-org/ef-schema-diff.git
cd ef-schema-diff
dotnet build
dotnet test
```

The solution is structured as follows:

```
src/
├── EfSchemaDiff.Core/           # Interfaces and neutral DTOs (no EF Core dependency)
├── EfSchemaDiff.Infrastructure/ # ALC loader, EF model extractor, SQL Server reader, comparer
├── EfSchemaDiff.Cli/            # System.CommandLine entry point, formatters, Spectre.Console UX
└── EfSchemaDiff.Tests/          # xUnit unit tests (73 tests)
```

---

## License

[MIT](LICENSE)
