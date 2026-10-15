# Agent360 — Generated Application Structure

This document describes the application tree generated from the `hmz-modular-monolith` template. It is intentionally scoped to the generated project; repository packaging guidance belongs in the parent repository `docs/` folder.

## Generated project tree

```text
<GeneratedProject>/
├── .dockerignore
├── .editorconfig
├── .gitignore
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
├── <GeneratedProject>.slnx
├── README.md
├── docs/
├── src/
│   ├── <GeneratedProject>.API/
│   │   ├── _openApis/
│   │   │   └── .gitkeep
│   │   ├── Abstractions/
│   │   ├── Configurations/
│   │   │   └── OpenApi/
│   │   ├── Constants/
│   │   ├── Extensions/
│   │   ├── Features/
│   │   ├── Filters/
│   │   ├── Identity/
│   │   ├── Middlewares/
│   │   ├── Options/
│   │   ├── Properties/
│   │   ├── Program.cs
│   │   ├── Dockerfile
│   │   ├── Dockerfile.aot
│   │   └── appsettings*.json
│   ├── <GeneratedProject>.AppHost/
│   ├── <GeneratedProject>.ServiceDefaults/
│   └── Modules/
│       ├── ReferenceData/
│       │   ├── <GeneratedProject>.ReferenceData.Domain/
│       │   ├── <GeneratedProject>.ReferenceData.UseCase/
│       │   └── <GeneratedProject>.ReferenceData.Infrastructure/
│       └── UserManagement/
│           ├── <GeneratedProject>.UserManagement.Domain/
│           ├── <GeneratedProject>.UserManagement.UseCase/
│           └── <GeneratedProject>.UserManagement.Infrastructure/
└── tests/
    ├── <GeneratedProject>.API.ContractTests/
    ├── <GeneratedProject>.API.FunctionalTests/
    └── Modules/UserManagement/
        ├── <GeneratedProject>.UserManagement.Domain.UnitTests/
        └── <GeneratedProject>.UserManagement.UseCase.UnitTests/
```

`<GeneratedProject>` is produced from the template source token `Agent360` by `dotnet new --name`.

## Root application files

| Path | Responsibility |
| --- | --- |
| `Directory.Build.props` | Shared `net10.0`, nullable, implicit usings, warning policy, documentation generation, and common MSBuild settings. |
| `Directory.Packages.props` | Central package versions. Project files use versionless `PackageReference` entries. |
| `global.json` | .NET 10 SDK feature-band and Microsoft.Testing.Platform settings. |
| `.gitignore` | Generated build, IDE, logs, local data, OpenAPI output, and test-result exclusions. |
| `.dockerignore` | Generated application Docker build-context exclusions. |
| `*.slnx` | Solution membership for API, Aspire, modules, and tests. |

No release workflow, infrastructure bootstrap script, provider-specific
deployment manifest, or automated migration runner is generated. Add those
later in the generated application's repository after selecting its target
infrastructure. The `.dockerignore` and API Dockerfiles are optional OCI
packaging assets, not a prescribed deployment platform.

Logto initialization is also a post-generation concern. A generated project
may use a reusable script or MCP integration to create/reconcile its local
and staging Scalar SPA clients, API resource, and `User.Created` webhook, but
the selected project must own the command, environment inputs, secret-manager
binding, and verification record. Management API M2M credentials are
bootstrap-only and must never be generated into runtime settings or committed.

## API host placement

`src/<GeneratedProject>.API` is the composition root and HTTP boundary.

- `Program.cs`: host composition, middleware, authentication, authorization, endpoint discovery, module registration, health endpoints, and OpenAPI setup.
- `Configurations/`: host-level registration and OpenAPI transformers.
- `Features/`: Minimal API endpoints organized by business area and operation.
- `Abstractions/IEndpoint.cs`: endpoint discovery contract.
- `Identity/`: request-scoped current-user adapter.
- `Middlewares/`: host middleware such as user resolution.
- `Filters/`: HTTP filters such as webhook signature verification.
- `Options/`: strongly typed configuration contracts.
- `_openApis/`: required output directory for generated OpenAPI documents. The tracked `.gitkeep` preserves the directory in a fresh generated project; generated JSON files are build output and are ignored.

## Module placement

Each business module is split by dependency direction:

```text
Modules/<ModuleName>/
├── <Project>.Domain/
├── <Project>.UseCase/
└── <Project>.Infrastructure/
```

- **Domain**: aggregates, value objects, specifications, domain events, and domain errors. No ASP.NET Core or EF Core references.
- **UseCase**: commands, queries, handlers, responses, validation, and module-owned application abstractions.
- **Infrastructure**: EF Core `DbContext`, mappings, migrations, seeders, repositories, provider-specific queries, and dependency injection.

The API host may reference module Infrastructure for composition. Infrastructure references UseCase and Domain. UseCase references Domain and SharedKernel. Domain remains provider and transport neutral.

## Tests

- API contract tests verify HTTP/result/OpenAPI boundary behavior.
- Domain unit tests verify aggregates and value objects without infrastructure.
- UseCase unit tests verify handlers and application behavior.
- Infrastructure integration tests verify EF Core mappings, PostgreSQL translation, repositories, migrations, and unit-of-work behavior.

## Configuration and secrets

`appsettings*.json` contains non-sensitive logging and host defaults. External
connection, Logto, webhook, and client values are empty by default; the files
must never contain a real password, token, client secret, signing key, or
production connection string.

Provide runtime configuration through:

- Aspire parameters for local orchestration;
- environment variables such as `ConnectionStrings__DefaultConnection`, `Logto__Authority`, `Logto__Audience`, and `Logto__Webhooks__SignUpSigningKey`;
- .NET user secrets for local development;
- the secret mechanism selected by the generated application's eventual
  deployment platform.

Never commit actual values to the generated project or template source.

## Generated and ignored output

The following are generated or machine-local and must not be treated as source architecture:

- `bin/`, `obj/`, `.vs/`;
- `_logs/`;
- generated JSON under `API/_openApis/`;
- `TestResults/` and coverage output;
- local PostgreSQL data and package artifacts.
