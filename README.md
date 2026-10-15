# Agent360 API

A platform-neutral .NET 10 modular-monolith starter built with ASP.NET Core Minimal APIs, EF Core/PostgreSQL, Logto JWT authentication, Scalar/OpenAPI, and Aspire for local development.

## Scope

The generated project provides application architecture and local-development capabilities. It intentionally does **not** choose an organization-specific deployment platform or generate infrastructure automation.

After the business implementation and runtime topology are understood, add deployment assets in the generated application's own repository for its actual target, for example Kubernetes, Azure Container Apps, AWS ECS, a VM, or another platform. Do not copy Hmz-, Antares-, VPS-, Coolify-, runner-, domain-, service-UUID-, or secret-layout assumptions into a new project.

The template still includes a portable API `Dockerfile` as an optional OCI packaging boundary. It is not a deployment definition, pipeline, or infrastructure commitment and may be replaced or removed by the generated application.

## Local development with Aspire

Run the AppHost from the generated project root:

```powershell
dotnet run --project src/Agent360.AppHost
```

The AppHost provisions local-only resources:

- one project-isolated PostgreSQL instance;
- an application database named `application`;
- a separate Logto database named `logto` on the same local PostgreSQL instance;
- Logto Core and Admin endpoints;
- pgAdmin;
- project-specific persistent volumes;
- Aspire service discovery and connection-string injection.

The application database and Logto database must remain separate. These local resources are not staging or production resources. Use the Aspire dashboard to discover the assigned endpoints; Logto host ports are dynamic so multiple generated projects can run concurrently.

To reset local state, stop the AppHost and remove only the generated project's own volume. Never remove a shared or unrelated volume.

## Local configuration and secrets

Committed `appsettings*.json` files contain only non-secret defaults. Supply real values through Aspire, environment variables, .NET user secrets, or the secret mechanism selected by the generated application.

Example local user-secret setup:

```powershell
dotnet user-secrets --project .\src\<GeneratedProject>.API init
dotnet user-secrets --project .\src\<GeneratedProject>.API set "Logto:Authority" "https://your-logto-authority"
dotnet user-secrets --project .\src\<GeneratedProject>.API set "Logto:Audience" "https://your-api-resource"
dotnet user-secrets --project .\src\<GeneratedProject>.API set "Logto:OpenAPIClientId" "<scalar-spa-client-id>"
dotnet user-secrets --project .\src\<GeneratedProject>.API set "Logto:Webhooks:SignUpSigningKey" "<local-signing-key>"

dotnet user-secrets --project .\src\<GeneratedProject>.AppHost init
dotnet user-secrets --project .\src\<GeneratedProject>.AppHost set "Parameters:postgres-username" "<local-username>"
dotnet user-secrets --project .\src\<GeneratedProject>.AppHost set "Parameters:postgres-password" "<local-password>"
```

Aspire normally injects `ConnectionStrings:DefaultConnection` for the API. Never commit actual database passwords, tokens, webhook keys, client secrets, or production connection strings.

## Logto and Scalar

Development and staging-style application environments may expose Scalar/OpenAPI when the generated application elects to do so. Production does not expose them in the current host behavior.

The generated API uses:

```text
Logto__Authority
Logto__Audience
Logto__OpenAPIClientId
Logto__Webhooks__SignUpSigningKey
```

`Logto__OpenAPIClientId` is a public SPA client identifier used by Scalar's authorization-code flow with PKCE. It is not an M2M secret. `Logto__ClientId` and `Logto__ClientSecret` are reserved for a future outbound machine-to-machine integration and are not required for normal JWT validation or Scalar authorization.

The signup webhook route is:

```text
/api/v{version}/users/webhooks/logto/signup
```

It is anonymous at the bearer-authentication layer but protected by the webhook HMAC signature filter.
It is an idempotent external-event ingestion endpoint and returns
`204 No Content`; clients must not attempt to deserialize a resource from it.

## REST resource semantics

- `POST /api/v1/roles` creates a resource and returns `201 Created`, a
  `Location` header, the created role representation, and a strong ETag.
- `POST /api/v1/roles/search` is a complex read operation and returns
  `200 OK` with a paged representation; it does not create a resource.
- `POST /api/v1/users/search` follows the same allow-listed advanced-search
  contract and returns users with role metadata.
- `PUT` replacement/update endpoints return `200 OK` with the resulting
  representation and strong ETag when applicable.
- Successful deletes and idempotent external webhook acknowledgements return
  `204 No Content`.
- Functional clients call `ReadRequiredJsonAsync<T>` only for endpoints whose
  contracts include a response representation.

## User and role administration

`GET /api/v1/users/me` returns a strong representation ETag and supports
`If-None-Match`/`304`. The validator covers the complete serialized profile,
including assigned roles and effective permissions, so profile, membership,
and role-permission changes invalidate a previously issued tag.

`POST /api/v1/users/search` accepts the shared `AdvancedSearchRequest` and
returns a bounded `PagedResponse` of users and assigned role metadata. Its
allow list supports `Id`, `ExternalUserId`, `Email`, `FirstName`, `LastName`,
and `DisplayName`; role collections are intentionally not exposed to Dynamic
LINQ. OpenAPI publishes a concrete request example and
`x-hmz-advanced-search` metadata. It requires
`USER-MANAGEMENT.USERS.READ`.

`PUT /api/v1/users/{id}/roles` replaces another user's role assignments. It
requires a strong `If-Match` ETag and the
`USER-MANAGEMENT.USERS.MANAGE-ROLES` permission. This permission is reserved
for the seeded ADMIN role; SYSTEM and other roles do not receive it. An
administrator cannot change their own assignments, and SYSTEM roles cannot be
assigned through this API. Successful replacement invalidates that user's
permission snapshot before subsequent requests.

### Logto bootstrap

Run Logto initialization once after generating the project and deciding its
local and staging origins. A reusable bootstrap script or MCP integration
should reconcile, idempotently:

- a local Scalar SPA client for the local callback/origin;
- a staging Scalar SPA client when staging documentation is enabled;
- the API resource whose indicator is the configured audience; and
- the `User.Created` webhook pointing to the generated backend.

The bootstrap operator needs the Logto issuer/management endpoint, an existing
Management API M2M client with the required management scopes, the API resource
indicator, local and staging origins, the webhook URL, and the selected
project-scoped secret-manager destination. The exact variable names and
secret-manager commands are deployment-specific and must be documented in the
generated application's own runbook.

The Management API M2M credential is used only while running this bootstrap.
It is not `Logto__OpenAPIClientId`, is not an API runtime credential, and must
not be stored in `appsettings*.json`, containers, CI deployment variables, or
source control. Persist only the runtime values required by the API and Scalar
client IDs/signing key through the chosen secret manager. Do not silently run
this operation as part of `dotnet run`, application startup, or every deploy.

The generic template remains infrastructure-neutral: it does not create a
Logto tenant, choose a VPS/Coolify layout, or generate a provider-specific
bootstrap script. If a project uses a reusable organization skill or MCP
operation, copy/adapt its inputs and verification steps into the project
runbook without copying organization-specific domains, IDs, or secrets.

For least-privilege automation, keep the Management API bootstrap M2M separate
from any future runtime/application M2M client. The bootstrap client is used
only by the explicit initialization operation, receives only the approved
Management API role, and is stored in the secret manager's auth/bootstrap
scope. It is never injected into the API container, `appsettings`, CI deploy
variables, or application startup. If a later service needs to call Logto or
another protected resource, create a separate runtime M2M client with only
that resource's permissions and store it under a separate runtime integration
scope. Token exchange is opt-in and should remain disabled until that use case
is approved.

### Google social sign-in

Google sign-in is enabled in the Logto tenant, not in the API. After creating
the Google OAuth web client for the exact Logto origin and callback, configure
the `google-universal` connector and add `google` to
`socialSignInConnectorTargets`. The hosted Logto sign-in page then displays
Google as an available sign-in method. Do not add Google credentials to
`appsettings*.json`, the API container, or Scalar runtime settings.

For a newly initialized tenant, complete the Admin Console wizard as well:
**Sign-in & account → Get started → Sign-up and sign-in → confirm Google under
Social sign-in → Save**. Verify that the initial setup card is gone and the
hosted preview shows **Continue with Google**.

Email/password and email verification-code methods are separate from Google.
If the generated project enables those methods, configure a working SMTP
connector first; Logto rejects email sign-in-experience changes when no
enabled SMTP connector exists.

## Optional application permission matrix

The current generated Logto profile includes the application permission
matrix described below. This is not a requirement of every backend generated
from the template. If a product selects API-key authentication, another
identity provider, or authentication without application authorization, keep
the authentication profile and remove the unused UserManagement permission
catalog, role-matrix endpoints, permission snapshot resolver, and related
authorization policies as one coherent bundle. Do not add a fake permission
layer merely because the template documentation contains this optional
section.

Logto authenticates external identities. The UserManagement module owns the
application role and permission matrix; it does not call the Logto Management
API to resolve authorization and does not place the matrix in access-token
claims.

Permission codes are constants and follow this hierarchy:

```text
[app-module].[entity].[operation]
USER-MANAGEMENT.ROLES.MANAGE
```

The domain may add hyphenated sub-module, sub-entity, or sub-permission
segments when finer granularity is needed. Endpoints must use constants such
as `UserManagementPermissions.Roles.Manage`, never inline permission strings.

An authenticated request uses this pipeline:

```text
JWT authentication
  → external-subject/local-user resolution
  → application permission snapshot resolution
  → ASP.NET Core permission authorization
  → endpoint
```

The permission snapshot is the union of permissions from all roles assigned
to the local user. `ICurrentUser` remains identity-focused; permissions are
read through `IPermissionSnapshotAccessor` and checked through
`IPermissionChecker`. `/api/v1/users/me` includes the resolved permission-code
list in addition to the existing profile and role fields.

Users and roles carry permission timestamps. The timestamp tuple participates
in the cache key, so a changed user-role assignment or role-permission
assignment produces a new snapshot even before the prior entry expires.
HybridCache entries use only targeted tags:

```text
um:permissions:user:{userId}
um:permissions:role:{roleId}
```

The administration UI can use either matrix orientation:

- `GET /api/v1/permissions` returns the complete valid permission catalog.
  Permissions without roles are still returned, so they can be displayed as
  unchecked cells.
- `GET /api/v1/roles/{id}/permissions` returns only the selected role's
  permission metadata, not the list of roles assigned to each permission. It
  writes a strong ETag; clients can send `If-None-Match` to receive
  `304 Not Modified`.
- `PUT /api/v1/roles/{id}/permissions` replaces one role's assignments. It
  requires the current strong `If-Match` ETag returned by the GET endpoint and
  accepts
  `{ "permissionIds": [1, 2, 3] }`.

A role update invalidates only its role tag; it must not invalidate a global
permission tag. Each cached user snapshot is tagged with its user tag and all
of its assigned role tags, allowing a role invalidation to evict exactly the
affected users. The `ADMIN` and `SYSTEM` roles receive the complete seeded
permission catalog.

## CORS and runtime validation

The API uses `Cors:AllowedOrigins`. The development configuration includes common frontend origins on ports 4200 and 3000. Add a different frontend origin through local configuration when needed.

The Logto authority is not an API CORS origin. The browser communicates with Logto directly during OAuth; the API only needs origins of browser applications that call the API.

Deployed applications should set:

```text
RuntimeConfiguration__Validate=true
Cors__AllowedOrigins__0=https://frontend.example.com
```

Strongly typed `LogtoOptions` and `CorsOptions` use `ValidateOnStart`. With runtime validation enabled, the host requires HTTPS Logto authority/audience, the webhook signing key, explicit HTTP(S) CORS origins, and a Scalar client ID in Staging. Development permits incomplete external configuration so local orchestration and design-time tooling can start safely.

## API documentation and health

The current host behavior is:

- Development: Scalar/OpenAPI enabled and `/` redirects to `/scalar`;
- Staging: Scalar/OpenAPI enabled and `/` redirects to `/scalar`;
- Production: Scalar/OpenAPI and the documentation redirect are not mapped.

Anonymous health endpoints are provided by ServiceDefaults:

```text
GET /alive
GET /health
```

A deployment may use or restrict these endpoints according to its chosen ingress and monitoring platform.

## Database migrations

EF migrations stay source-controlled in each module's Infrastructure project.
The generated API never migrates an application database or exposes a
migrations endpoint. Each consuming project owns generation, review, backup,
application, and verification of idempotent SQL for **every** module before
starting its API in Development, Staging, or Production.

For a manual local project, after supplying that project's connection string
through its chosen secret manager/user secrets, generate and apply an
idempotent script per module. Use the API as the EF design-time startup project
and select the module's context explicitly:

```powershell
dotnet tool restore
# Repeat for each module Infrastructure project and its DbContext:
dotnet ef migrations script --idempotent `
  --project src/Modules/<Module>/<GeneratedProject>.<Module>.Infrastructure `
  --startup-project src/<GeneratedProject>.API `
  --context <Fully.Qualified.Module.AppDbContext> `
  --output <project-owned-output>/<Module>.sql
# Review/approve the output, back up the database, then apply with your
# chosen project-specific PostgreSQL migration identity and psql/job runner.
```

The runtime seeders are idempotent but are **not** a migration mechanism. The
API host seeds core data after schema preparation, adding Development-only
demo data when a product chooses that profile. The functional-test fixture
alone calls `MigrateAsync`, against a disposable PostgreSQL container.
Never make each API replica migrate at startup; do not copy a project-specific
migration or deployment script into the reusable template.

For modular monoliths, verify migration history, constraints, and indexes for
each owning `DbContext`. Define backup, review, ordering, destructive-change
handling, rollback/restore and least-privilege execution in the generated
application's deployment contract.

## Optional OCI image

The API project includes a non-AOT multi-stage `Dockerfile` and listens on port `8080`. To evaluate it locally:

```powershell
docker build `
  --file src/<GeneratedProject>.API/Dockerfile `
  --tag <generated-project>-api:local `
  .
```

A previous AOT-oriented example remains as `Dockerfile.aot`. Do not enable AOT until EF Core, reflection, JSON serialization, trimming, and all dependencies pass native-publish validation.

## Build and test

```powershell
dotnet restore <GeneratedProject>.slnx
dotnet build <GeneratedProject>.slnx --configuration Release --no-restore

Get-ChildItem tests -Recurse -Filter *.csproj | ForEach-Object {
    dotnet run --project $_.FullName --configuration Release --no-build -- `
        --minimum-expected-tests 1
}
```

Tests use Microsoft.Testing.Platform executable test hosts. Treat zero discovered tests as a failure.

This template intentionally does not generate an `eng/ci` wrapper or
provider-specific workflow. Once a consuming application selects its CI
platform and infrastructure, add a project-owned command wrapper that restores
and builds once, runs every test executable with a positive minimum count, and
provides a Docker-compatible daemon for the functional suite.

The API functional suite boots the complete in-process host against a disposable
PostgreSQL Testcontainer, applies every module migration, seeds the core
catalog, and verifies requests through the authentication, user-resolution,
permission, endpoint, and database pipeline. It uses the reusable
`Hmz.Core.Testing` `TestBearer` handler instead of calling Logto, while retaining
the real local-user and permission lookup behavior.

It references the stable `Hmz.Core.Testing` `0.1.0` package. Ensure that
version is available from a configured package source before restoring a
generated project.

All generated `[Fact]` and `[Theory]` methods use the exact `// Arrange`,
`// Action`, and `// Assertion` headings. Keep scenario setup, the behavior
under test, and observations in those sections so generated applications begin
with one readable test convention.

Successful JSON responses use the provider-neutral
`Hmz.Core.Testing.Http.ReadRequiredJsonAsync<T>` extension, which checks the
exact expected status, requires a non-empty/non-null payload, and reports
bounded response context when deserialization fails. Required ETags use the
same package's `RequireEntityTag()` extension. Endpoint methods and DTOs remain
in the generated partial `FunctionalApiClient`; expected error responses keep
direct status and Problem Details assertions.

The application client derives from `FunctionalApiClientBase`, so Core owns
shared-client validation/access and fluent test authentication. Routes,
requests, DTOs and endpoint partials remain generated application code.
The concrete API factory derives from
`ApiWebApplicationFactory<Program>`, and the local test base closes Core's
generic fixture/factory/client type parameters. PostgreSQL, migrations,
Respawn policies, settings and seeding remain generated application code;
Core supplies the generic PostgreSQL container/connection/reset plumbing.
The normal host uses Core's `"TestEnvironment"` default; fixture-created
alternate hosts can override it, for example with `Development`.

The same Testing package exposes its PostgreSQL fixture from the
`Hmz.Core.Testing.PostgreSql` subnamespace. The base owns PostgreSQL
Testcontainer, connection, Respawner, reset and disposal plumbing. The
generated fixture supplies module schemas/migrations, core seeding, test
actors and cache invalidation through overridable hooks.

### Functional-test lifecycle

One xUnit assembly fixture starts one PostgreSQL container and one in-process
API host, applies migrations, seeds core/test data, opens the Respawn
connection, and creates one shared `HttpClient` plus typed API client. Those
resources are reused serially by every functional test.

Before each test, `FunctionalTestBase.InitializeAsync()` clears authentication
and conditional-request headers. After each test,
`FunctionalTestBase.DisposeAsync()` invokes the fixture reset: Respawn clears
mutable application tables while preserving migration history, core data and
test actors are reseeded, and identity/permission caches are invalidated.
After the last test, xUnit disposes the shared client, connection, host, and
container once.

The complete call order, shared-versus-refreshed state table, hook frequency,
ownership rules, and alternate `Development` host behavior are documented in
`docs/TESTING_AND_SHARED_KERNEL_GUIDE.md` under **Lifecycle: what runs once,
what is shared, and what runs per test**.

Respawn resets PostgreSQL only. Because the application host is shared,
in-memory/HybridCache entries, singleton state, hosted services, queues,
timers, and background work can otherwise leak across cases or race the
database reset. The current template has no application-owned background
worker and explicitly invalidates seeded identity/permission cache entries.
When adding stateful services, define a deterministic fixture reset or
drain/wait contract, replace/disable unrelated hosted services, namespace and
purge disposable external stores, or stop sharing the host. See **Shared host:
cache, singleton, and background-work isolation** in the testing guide.
Use the fixture's `BeforeResetAsync` hook to quiesce tracked workers before
Respawn and `AfterResetAsync` to clear/rebuild host state after reseeding.

The fixture sets `Testing:IsTestHost` in `ConfigureWebHost` with `UseSetting`
to skip only optional Development demo seeding; Development test hosts still
seed core data after their schema has been prepared. Every non-test environment seeds
core data after separately applied per-module SQL migrations. The fixture
migrates only its disposable database, calls the public `SeedCoreAsync` method,
and creates an ADMIN-assigned test user that production seeders intentionally
omit. The former EF InMemory infrastructure tests are replaced by real
PostgreSQL-backed API and persistence scenarios. Endpoint tests live under `Roles/`, `Permissions/`,
`ReferenceData/`, `Users/`, and `Services/`; the common lifecycle is in
`Fixtures/`, with one partial typed HTTP client under `Services/ApiClients`.
Its root file
owns construction/authentication and endpoint methods use the
`FunctionalApiClient.<Area>.cs` naming convention. Small host-lifecycle
regressions stay at the test-project root. Assembly-wide xUnit
attributes live in `Properties/AssemblyInfo.cs`, separate from SDK-generated
version metadata.

Run the functional suite directly when iterating:

```powershell
dotnet run `
  --project tests/<GeneratedProject>.API.FunctionalTests `
  --configuration Release `
  -- --minimum-expected-tests 1
```

Docker or another Docker-compatible API is required. Testcontainers uses the
standard Docker endpoint or the endpoint exposed through `DOCKER_HOST`.
For Podman, discover the actual API endpoint for the selected machine/user
(`podman system connection list` or `podman machine inspect`) and set
`DOCKER_HOST` only when Testcontainers cannot reach the default socket. For
example, on a Windows Podman machine with the documented named pipe:

```powershell
podman info
$env:DOCKER_HOST = "npipe://./pipe/podman-machine-default" # replace with your real endpoint
# Optional only if rootless/remote Podman cannot run the Ryuk reaper:
# $env:TESTCONTAINERS_RYUK_DISABLED = "true"
```

On Linux/WSL, use the active Unix socket instead of the Windows named pipe.
Do not assume a specific UID or socket path. Keep Ryuk enabled when supported;
if it must be disabled, arrange cleanup for aborted CI jobs. The same tests
ran against a local Podman-compatible API with Ryuk enabled. Database cleanup
uses Respawn and execution is serial because the assembly shares one database.
When safe parallelism is needed, partition by independent databases/hosts per
worker, cap workers by PostgreSQL connections and host memory, and verify
migration/seeding/cache isolation before enabling concurrency. See
`docs/TESTING_AND_SHARED_KERNEL_GUIDE.md` for the staged scaling plan.

## Before adding deployment automation

Complete these decisions in the generated application first:

- actual modules, endpoints, authorization, and business invariants;
- database ownership and migration contexts;
- local, staging, and production identity topology;
- frontend origins and public domains;
- target runtime and container/orchestrator contract;
- registry, secret manager, ingress, health, observability, and backup policy;
- staging verification, production approval, rollback, RPO, and RTO.

Only then add the selected infrastructure's scripts and CI/CD workflow. Keep those files project-owned and replaceable rather than treating one Hmz infrastructure layout as part of the backend architecture.
