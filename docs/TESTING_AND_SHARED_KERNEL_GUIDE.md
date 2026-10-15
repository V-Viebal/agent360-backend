# Testing and shared-kernel guide

## Test taxonomy

Classify a test by the boundary it exercises, not by how many classes it touches.

- **Unit tests** run without real infrastructure. Domain tests and use-case handler tests that replace repositories, clocks, caches, senders, or other collaborators with mocks/fakes belong in a project ending in ".UnitTests". Heavy mocking does not make a test an integration test.
- **Integration tests** prove integration with real infrastructure or a real framework boundary. Examples include EF Core mappings, interceptors, PostgreSQL constraints, repository queries, migrations, and Redis behavior. Prefer disposable PostgreSQL/Redis instances instead of mocking these boundaries.
- **Functional API tests** boot the API and issue HTTP requests against the in-process host. They may use disposable real infrastructure. Call them functional tests unless they exercise the fully deployed system.
- **End-to-end tests** exercise a deployed application across its real network and operational boundaries. Keep this suite small and focused on critical journeys.

Current User Management projects follow this naming:

- "Agent360.UserManagement.Domain.UnitTests" for aggregate/value-object rules.
- "Agent360.UserManagement.UseCase.UnitTests" for result conventions, handlers, and other application orchestration that use mocks/fakes instead of a database.
- The former module infrastructure projects were EF InMemory tests, not integration tests. Their HTTP-visible scenarios now run against PostgreSQL in "Agent360.API.FunctionalTests". The template does not separately test its empty `EfRepository<T>` and `UserManagementUnitOfWork` wrappers because they add no behavior beyond the shared Core EF bases already exercised by API persistence. Add a module-local PostgreSQL integration project only when a module introduces custom repository queries, interceptors, transactions, constraint translation, concurrency, or other provider-specific behavior.
- In-process HTTP coverage belongs in "Agent360.API.FunctionalTests".

Do not create an integration or E2E project whose implementation only mocks repositories or the database.

## Test method structure

Every `[Fact]` and `[Theory]` method follows explicit Arrange–Action–Assertion
sections:

```csharp
// Arrange
var role = Role.Create("Original", "ORIGINAL");

// Action
role.Update("Updated", "UPDATED");

// Assertion
role.Code.Should().Be("UPDATED");
```

Use these exact headings: `// Arrange`, `// Action`, and `// Assertion`.
Keep fixture setup and shared lifecycle code outside individual test methods;
inside a test, put scenario-specific state and inputs under Arrange, the
behavior being exercised under Action, and all observations under Assertion.
For a theory whose input is entirely supplied by attributes, retain the
Arrange heading and state that theory data supplies the input. A multi-step
workflow may perform intermediate checks needed to continue safely, but still
ends with an explicit Assertion section for the scenario's final outcome.

## xUnit v3 configuration

The test projects use xUnit v3 through the following packages:

- "xunit.v3" 4.0.0 provides the test framework and executable test host.
- "xunit.runner.visualstudio" and "Microsoft.NET.Test.Sdk" retain compatibility with older Visual Studio/VSTest tooling.
- "AwesomeAssertions" 9.6.0 is the assertion library for test assertions.
- "FakeItEasy" 9.0.1 is referenced only by unit-test projects that fake
  collaborators.
- Add a data-generation package only when a test project needs it and use a
  fixed seed when generated values affect reproducibility. Do not add unused
  test dependencies to every generated project.

Each test project sets:

~~~xml
<OutputType>Exe</OutputType>
<IsTestProject>true</IsTestProject>
<IsPackable>false</IsPackable>
<IsPublishable>false</IsPublishable>
<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
~~~

The solution's `global.json` selects Microsoft.Testing.Platform for .NET 10.
Run each executable test host directly for deterministic discovery:

~~~powershell
dotnet run --project tests/Modules/UserManagement/Agent360.UserManagement.Domain.UnitTests/Agent360.UserManagement.Domain.UnitTests.csproj -- --no-ansi --progress off
~~~

No application runtime or publish setting is required for xUnit. Building the solution
compiles the tests, while publishing the API project publishes only the application.

## Functional API test architecture

The functional suite uses one xUnit v3 assembly fixture (under `Fixtures/`), assembly attributes under `Properties/AssemblyInfo.cs`, a shared `HttpClient` plus a typed request helper under `Services/`, and endpoint-oriented `Roles/`, `Permissions/`, `Users/`, `ReferenceData/`, and health cases:

1. start one PostgreSQL Testcontainer;
2. boot one `WebApplicationFactory`;
3. run all module migrations and core seeders;
4. send HTTP requests through the real ASP.NET Core middleware pipeline; and
5. reset mutable data with Respawn after each test.

### Lifecycle: what runs once, what is shared, and what runs per test

`Properties/AssemblyInfo.cs` registers `FunctionalTestFixture` as an xUnit v3
assembly fixture and disables assembly parallelization:

```csharp
[assembly: AssemblyFixture(typeof(FunctionalTestFixture))]
[assembly: Parallelization(Mode = ParallelMode.None)]
```

The resulting lifecycle is:

```text
Test executable starts
└─ Construct one FunctionalTestFixture
   └─ CreateContainer() defines the container (does not start it)
└─ Assembly fixture InitializeAsync() — once
   ├─ Start PostgreSQL Testcontainer
   ├─ Create ApiWebApplicationFactory and force host/service creation
   ├─ InitializeApplicationServicesAsync()
   │  └─ Apply ReferenceData and UserManagement migrations
   ├─ SeedApplicationAsync()
   │  └─ Run idempotent core seeding
   ├─ SeedTestDataAsync()
   │  └─ Create test ADMIN/CUSTOMER actors and invalidate their caches
   ├─ Open one Npgsql connection and create one Respawner
   └─ Create one shared HttpClient and FunctionalApiClient

For each test case, serially
├─ xUnit creates a new test-class instance
├─ FunctionalTestBase.InitializeAsync()
│  └─ Clear authentication and conditional-request headers
├─ Run the test method
└─ FunctionalTestBase.DisposeAsync()
   └─ FunctionalTestFixture.ResetAsync()
      ├─ Clear scenario-sensitive headers again
      ├─ Run BeforeResetAsync() to quiesce workers when overridden
      ├─ Respawn all configured application tables
      │  └─ Preserve each module's __EFMigrationsHistory
      ├─ Rerun idempotent core seeding
      ├─ Recreate test actors and invalidate their caches
      └─ Run AfterResetAsync() when an application overrides it

After the final test
└─ Assembly fixture DisposeAsync() — once
   ├─ Dispose shared HttpClient
   ├─ Dispose open Npgsql connection
   ├─ Dispose WebApplicationFactory/application host
   └─ Stop and dispose PostgreSQL Testcontainer
```

The following objects are deliberately **shared across all test cases**:

- PostgreSQL container and database;
- application host, dependency-injection service provider, and concrete
  `ApiWebApplicationFactory`;
- one `HttpClient` and one typed `FunctionalApiClient`;
- one open Npgsql connection and one configured Respawner.

The following state is **refreshed for every test case**:

- authentication, `If-Match`, and `If-None-Match` headers on the shared client;
- mutable rows in the configured application schemas;
- idempotent core/reference seed data;
- test-only users/role assignments and their identity/permission cache state;
- any extra state implemented by `AfterResetAsync()`.

The reset occurs after a test, including the normal failed-assertion path where
xUnit still disposes the test instance. Therefore, the first test starts from
the assembly bootstrap baseline, and each subsequent test starts from the
baseline recreated after the previous test. A killed process or crashed
runner cannot guarantee per-test cleanup; the disposable container still
prevents that database from becoming a shared development or production
database.

Hook frequency and ownership:

| Hook/member | Called | Template responsibility |
| --- | --- | --- |
| `CreateContainer()` | Once during fixture construction | Core provides PostgreSQL defaults; override only when the application needs another container definition. |
| `CreateFactory(connectionString)` | Once during assembly startup | Construct the concrete API factory against the disposable database. |
| `InitializeApplicationServicesAsync(...)` | Once during assembly startup | Apply every module migration before any seed/test runs. |
| `SeedApplicationAsync(...)` | Once at startup and after every test | Restore application-owned idempotent core data. |
| `SeedTestDataAsync(...)` | Once at startup and after every test | Restore test-only actors and invalidate relevant caches. |
| `CreateRespawnerOptions()` | Once during assembly startup | Configure reset schemas and preserve only migration history. |
| `CreateApiClient(client)` | Once during assembly startup | Build the typed wrapper around the shared client. |
| `FunctionalTestBase.InitializeAsync()` | Before every test | Remove scenario headers so identity and ETags cannot leak. |
| `FunctionalTestBase.DisposeAsync()` / `ResetAsync()` | After every test | Reset database and application baseline for the next case. |
| `BeforeResetAsync(...)` | Before every database reset | Optional application-owned quiesce/drain barrier so workers cannot race Respawn. |
| `AfterResetAsync(...)` | After every reset | Optional application-owned cleanup/reinitialization outside normal seed hooks. |
| Fixture `DisposeAsync()` | Once after the assembly | Dispose shared resources in client → connection → host → container order. |

Tests must not dispose the shared `_client`, `Api`, fixture factory, connection,
or container. Individual `HttpResponseMessage` values and any additional
factory/client created by a test remain test-owned and should be disposed by
that test.

`DevelopmentSeedingTests` demonstrates the alternate-host exception. It asks
the assembly fixture for another factory using the same disposable database
connection string but the `Development` environment. That secondary host is
created and disposed inside the test because an already-built
`TestEnvironment` host cannot change environment or replay startup. The common
base still resets the shared database after the case, so this pattern also
requires serial execution.

### Shared host: cache, singleton, and background-work isolation

Sharing the database and `WebApplicationFactory` is fast, but Respawn only
resets PostgreSQL. The application host and its singleton service graph remain
alive for the complete assembly. State in these components can leak from one
test to the next:

- `IMemoryCache` and the local/L1 portion of `HybridCache`;
- a real distributed/L2 cache such as Redis;
- singleton dictionaries, registries, channels, queues, timers, and counters;
- rate limiters, circuit breakers, or other resilience state;
- `IHostedService`, `BackgroundService`, schedulers, outbox dispatchers, and
  message consumers;
- fire-and-forget tasks started by an endpoint;
- external brokers, object storage, search indexes, and fake servers whose
  state is not stored in PostgreSQL.

This template currently registers in-process `HybridCache` and no
application-owned hosted/background service. The fixture explicitly
invalidates the seeded ADMIN and CUSTOMER identity/permission entries every
time it recreates those actors. Production mutation handlers also invalidate
their affected cache keys. That covers the current deterministic actors and
flows, but it is not a generic cache flush: a new test-created subject that
has been resolved and cached must be invalidated by that test or by an
application reset hook before the next case.

Use this decision table when adding stateful infrastructure:

| State/resource | Preferred functional-test policy |
| --- | --- |
| Local memory/HybridCache L1 | Invalidate every key/tag affected by reset, or replace with a fixture-owned resettable implementation. A fresh host is the safe fallback. |
| Redis/distributed cache | Use a unique suite/worker key prefix and delete only that namespace in `AfterResetAsync()`; never flush a shared cache. |
| Singleton mutable service | Add an explicit async reset contract and call it from `AfterResetAsync()`, or replace it in `ConfigureTestServices`. |
| Hosted service unrelated to the API scenario | Remove/disable its `IHostedService` registration in the test factory. |
| Worker/background job being tested | Use a controlled test queue/clock, await a completion or idle barrier, assert results, and drain it before the test returns. |
| Outbox/message broker | Give the suite its own namespace/queue, await dispatch/consumption, then purge it. Respawn must not race the dispatcher. |
| Untrackable fire-and-forget work | Refactor to a durable queue with an awaitable test seam, or isolate with a fresh host/database; do not accept timing sleeps. |

The critical ordering rule is:

1. stop accepting new scenario work;
2. await or cancel and drain tracked background work;
3. reset PostgreSQL and other external stores;
4. reseed application/test data;
5. clear or rebuild host-held caches/singletons; and
6. start the next test.

`BeforeResetAsync()` is the reusable pre-Respawn quiesce/drain hook.
`AfterResetAsync()` is suitable for cache/singleton cleanup that must happen
after reseeding. If a worker cannot provide a deterministic idle barrier, do
not share that host. Do not use arbitrary delays: they hide races and make CI
flaky. Do not run these tests in parallel merely because PostgreSQL reset is
fast; shared host state is another isolation boundary.

Both reset hooks run with `CancellationToken.None` because xUnit's test
disposal callback does not supply a token and cleanup must survive cancellation
of the request token used by the test. A consuming worker hook should apply
its own bounded timeout and fail with a useful diagnostic rather than wait
indefinitely.

`FunctionalApiClient` is one sealed partial type under
`Services/ApiClients` and the
`Agent360.API.FunctionalTests.Services.ApiClients` namespace. Keep
the `FunctionalApiClientBase` inheritance in `FunctionalApiClient.cs`; Core
then owns shared-client construction and test authentication. Place routes,
request DTO creation, response composition, and endpoint methods in files named
`FunctionalApiClient.<Area>.cs`, for example `FunctionalApiClient.Roles.cs`,
`FunctionalApiClient.Permissions.cs`, `FunctionalApiClient.ReferenceData.cs`,
and `FunctionalApiClient.Users.cs`. Prefer this dotted type-and-area convention
over names such as `FunctionalApiClient_ForRoles.cs` or
`FunctionalApiClientForRoles.cs`: it sorts all parts beside the root type,
matches common .NET partial-type conventions, and avoids artificial `#region`
blocks. Add one partial file when a new endpoint area appears; split an
unusually large area further with
`FunctionalApiClient.<Area>.<Capability>.cs`.

Use the `Hmz.Core.Testing.Http` response extensions for successful JSON
responses and ETag assertions. `ReadRequiredJsonAsync<T>` verifies the exact
expected status code before deserialization, rejects empty/null payloads,
wraps JSON/unsupported-type failures with the target type, and includes a
bounded response-body diagnostic. `RequireEntityTag()` fails clearly when a
required response validator is missing. Keep explicit status assertions for
expected failure responses; do not deserialize an error as a success DTO.

Keep small assembly-wide host-lifecycle regressions, such as
`DevelopmentSeedingTests`, at the functional-test project root beside
`FunctionalTestBase`. The shared fixture owns the container/database and
provides factory creation for alternate environments. Such a test still needs
a separate `WebApplicationFactory`: changing services on the already-built
Testing host cannot change its environment or replay the Development startup
pipeline.

The initial template serializes this assembly. A shared database plus a reset
after each test is fast and deterministic only while tests do not execute in
parallel. When parallel throughput becomes necessary, allocate a separate
database or schema per xUnit collection/worker; do not add locks around every
request as a substitute for isolation.

`Hmz.Core.Testing` owns provider-neutral authentication, HTTP assertion/reset
primitives, `FunctionalApiClientBase`,
`ApiWebApplicationFactory<TProgram>`, `IApiWebApplicationFactory`,
`IFunctionalTestFixture<TWebApplicationFactory, TApiClient>`, and the generic
xUnit `FunctionalTestBase` lifecycle shell. The application's concrete
factory still owns setting keys, webhook configuration, module DbContexts,
Npgsql migrations and schema names. Its concrete fixture owns the container
configuration overrides, migrations, Respawn schema/table policy, test actors,
and seed data. Core's PostgreSQL fixture base owns the generic
Testcontainer/Npgsql/Respawn lifecycle; the concrete fixture supplies the
application choices through its hooks. Endpoint DTOs and endpoint-specific
partial API-client methods also remain local.

The generic base intentionally standardizes only two per-test lifecycle
operations: clear scenario-sensitive shared-client headers before a case and
call the fixture's `ResetAsync()` afterward. A different architecture can
implement the same fixture interface with another provider/reset mechanism.

`ApiWebApplicationFactory<TProgram>` uses `"TestEnvironment"` by default.
The concrete factory accepts an optional environment and overrides the
protected `TestEnvironment` property, allowing startup regressions to select
`Development` or another explicit environment without changing the shared
default.

PostgreSQL lifecycle reuse is isolated in the
`Hmz.Core.Testing.PostgreSql` folder/subnamespace of the Testing package. Its
generic fixture base owns the
container, connection string, application factory/client creation sequence,
Respawner initialization/reset, and disposal. The generated fixture overrides
schemas, ignored migration-history tables, module migration, core seeding,
test-only actor creation and cache invalidation. Do not move those
application-specific hooks into Core.

Only the external authentication validation boundary is replaced; the webhook signature is still verified with a local test key. `Hmz.Core.Testing`
registers `TestBearer` and creates claims from
`TestBearer <subject>|<comma-separated-roles>`. The application still performs
real local-user resolution and loads application permissions from PostgreSQL,
which protects middleware ordering and permission hydration from regressions.

Respawn ignores migration history, clears every included application table,
and then reruns the idempotent core seeders. This is safer than maintaining a
growing ignore-list of master tables and their join-table dependencies. Every
application that adds another schema must include it in the reset scope and add
a test that proves data written by one case is absent in the next.

### Startup and migration ownership

`Testing:IsTestHost` is set using `IWebHostBuilder.UseSetting` by the `WebApplicationFactory` and suppresses only optional Development demo data, not core seeding. A Development test host still calls `SeedCoreAsync` after its schema has been prepared; a PostgreSQL regression test verifies this behavior. It is **not** an EF InMemory switch. The fixture selects the `Testing` environment, applies every module's migration against its disposable PostgreSQL database, calls the public idempotent `SeedCoreAsync` method, and adds an ADMIN-assigned user **only in test data**. Production `SeedCoreAsync` does not create a default ADMIN account. Development, Staging, and Production do **not** use this test flag to bypass core seeding: each consuming application must generate/review/apply idempotent SQL per module before API startup, then run core seeders in the host (with optional Development demo seeding). No generated API host calls `MigrateAsync`. Keep migration automation and deployment scripts in the consuming application, never the generic template.

The assembly-wide attributes belong in `Properties/AssemblyInfo.cs` by .NET convention (they could compile anywhere). SDK-generated assembly metadata remains enabled; this source file contains only xUnit fixture/parallelization attributes, not duplicate generated product/version attributes.

### Parallelism roadmap and container limits

The shared Respawn database must remain serial. As the suite grows, first measure cold container startup and per-test timings; split unit/contract tests into independent jobs, then partition functional tests by independently seeded database **per worker/collection** within one PostgreSQL container when resources permit. Each worker needs its own connection string, host/client, Respawner, migration history and seed lifecycle; reset only its own database. If schema migrations, bootstrap logging/static state or service-level caches cannot be safely isolated, use separate processes/containers instead. Bound worker count to CPU/memory/connection limits, monitor container startup and total runtime in CI, and keep a serial baseline for flaky-test diagnosis. Do not simply remove `[assembly: Parallelization(Mode = ParallelMode.None)]` while sharing a database.

### Container runtime (Docker and Podman)

Preflight the selected Docker-compatible API endpoint before running. Docker Desktop/daemon generally works with the default named pipe/socket and Testcontainers' Ryuk cleanup. Podman exposes a compatible API but its socket URI varies across native Windows, WSL and rootless Linux; explicitly set `DOCKER_HOST` to the actual reachable socket (for example, the active Podman machine's Windows named pipe) and validate with the chosen runtime CLI. `TESTCONTAINERS_RYUK_DISABLED=true` is an **opt-in last resort** for a rootless/remote runtime that cannot run Ryuk; when disabled, arrange cleanup for crashed jobs. Never bake a workstation socket or disabled Ryuk into the template or CI script. These tests exercise an in-process host, so Docker/Podman hosts the database only, not the API container.

These are functional API tests, not deployed E2E tests. Keep a separate, small
post-deployment smoke suite for TLS, ingress, real Logto/OIDC, deployed
configuration, and other operational boundaries.

The template provides the functional-test source but no operational CI wrapper.
Each consuming application should own its verification command and workflow
after selecting its CI platform and infrastructure. That project-owned gate
should restore/build once, run every Microsoft.Testing.Platform executable
with a positive minimum expected count, and provide a Docker-compatible daemon.

## Shared-kernel extraction candidates

The Role feature should use shared conventions without turning Role-specific concepts into global abstractions.

### Standardized in Hmz.Core.SharedKernel 0.1.11

- "Hmz.Core.SharedKernel.Results.Result", "Result<T>", and "IResult": the Hmz-owned success/failure contract used by commands and fallible queries.
- "Hmz.Core.SharedKernel.Results.Error" and "ErrorType": stable machine code, safe message, transport-neutral category, and optional validation target.
- "Hmz.Core.SharedKernel.Results.ErrorCode": generates readonly catalog values in the form "{APP}.{TYPE-PREFIX}.{APP-ERROR-CODE}" (for example, "COM.VAL.FAILED"). Use short stable module codes such as "UM" or "CM", hyphen-separated semantic suffixes, and do not repeat the error type (for example, "CM.NOTF.PRODUCT-ITEMS", not "CATALOGMANAGEMENT.NOTF.PRODUCT.NOT-FOUND").
- Failed results may carry multiple errors of one category; use "Match", "Map", and "Bind" for composition.
- "Hmz.Core.SharedKernel.Results.CommonErrorCodes" and "CommonErrors": cross-domain failures such as validation summaries, sanitized internal failures, and optimistic-concurrency conflicts.
- "Hmz.Core.SharedKernel.Contracts.Pagination.PagedResponse": the common pagination response contract.

Modules and services should reference these package namespaces directly. They must not reference another module's Common folder to reuse contracts.

### Current ASP.NET Core boundary

- "Hmz.Core.SharedKernel/Http/ProblemResults.cs": Problem Details creation with a stable "code" extension.
- "Hmz.Core.SharedKernel/Http/ResultHttpExtensions.cs": Hmz Result error-type-to-HTTP mapping.
- "Hmz.Core.SharedKernel/Http/EntityTagHttp.cs": strong ETag parsing,
  formatting, and reusable `HandleIfMatch`/`HandleIfNoneMatch` flows.
- "Hmz.Core.SharedKernel/Http/ConditionalRequestOpenApiExtensions.cs": reusable If-Match and ETag OpenAPI metadata.
- "Hmz.Core.SharedKernel.AdvancedSearch.OpenApi": reusable advanced-search OpenAPI metadata and schema transformer; keep endpoint-specific examples in the API project.
- "Hmz.Core.SharedKernel.Http.RequestBodyExceptionHandler": converts every malformed request-binding failure to the stable `errors.request` key. Ordinary failures use "The request is invalid."; explicitly safe advanced-search converters may expose bounded actionable messages such as "Unknown search/filter logic 'sideways'. Accepted values: AND or OR." or "Unknown sort direction 'sideways'. Accepted values: asc or desc.".
- "Hmz.Core.SharedKernel/Http/PreconditionResults.cs" and "HttpErrors.cs": HTTP conditional-request failures.

For a manual missing-query-parameter check, call either Reference Data endpoint
without `activeOnly`:

```http
GET /api/v1/reference-data/languages
GET /api/v1/reference-data/currencies
```

Calling with `?activeOnly=true` or `?activeOnly=false` exercises the successful path.

These types currently ship from `Hmz.Core.SharedKernel`. Keep their use at the
API boundary; HTTP status codes and headers must not leak into domain projects.
They may be extracted into a dedicated ASP.NET Core package in a future
breaking release, but documentation must describe the package that exists
today.

### Keep in persistence/provider infrastructure

- "UserManagement.Infrastructure/Persistence/PostgresRolePersistenceErrorClassifier.cs": provider-specific constraint classification belongs in infrastructure or a package such as "Hmz.Core.Persistence.PostgreSql", not the domain shared kernel.

### Keep feature-specific

- "RoleErrors" and all "role.*" error codes.
- "IRolePersistenceErrorClassifier", the role-code uniqueness rule, and the "IX_Roles_Code" constraint name.
- Role commands, queries, requests, responses, specifications, and authorization rules.

For soft-deletable entities, feature-defined unique indexes must include a
provider-correct partial filter such as `.HasFilter("\"IsDeleted\" = false")`.
Do not make composite-key join rows soft-deletable; use hard-delete audit
entities for relationships unless a surrogate key and filtered relationship
index are deliberately introduced.

## Extraction rules

1. Reuse the existing result convention; do not add another RoleOperationResult-style wrapper.
2. Keep application failures transport-neutral. Convert results to Problem Details only at the API boundary.
3. Move an incubating helper before another module copies it. Cross-module references to another module's Common folder are not a substitute for a shared package.
4. Keep provider-specific logic out of the generic shared kernel.
5. Treat error codes and response shapes as contracts and cover them with unit or functional tests.

## Package build and publication

The backend consumes the stable `Hmz.Core.SharedKernel` `0.1.11`. A developer may use any
configured NuGet source, including a local source for unpublished package
testing.

For CI or another developer machine, publish this package version to the
configured NuGet feed or provide it through an authenticated artifact/feed
source. Do not depend on one workstation path in shared automation.

Recommended package-consumer order, independent of CI provider:

1. Restore, build, and test "Hmz.Core.SharedKernel".
2. Pack and publish the SharedKernel package.
3. Restore, build, and test the backend against that package version.
4. Publish the API project, not the entire solution.
