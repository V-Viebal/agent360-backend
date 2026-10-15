# Unit of Work guidance

## What this change does

`Hmz.Core.SharedKernel.Data.EfRepositoryBase<T>` keeps Ardalis Specification queries, but overrides repository mutation methods so `AddAsync`, `UpdateAsync`, and `DeleteAsync` only change the shared `DbContext` change tracker. Persistence happens once through a module-owned `IUnitOfWork`.

A command should follow this shape:

```csharp
await repository.AddAsync(entity, cancellationToken);
await unitOfWork.SaveChangesAsync(cancellationToken);
```

For a command that changes multiple aggregate roots, call `SaveChangesAsync` once after all domain rules have passed. Read handlers should continue to use `IReadRepository<T>` and should not call SaveChangesAsync.

## Modular-monolith boundary

Each module owns its unit-of-work interface in its application/use-case project, for example `IUserManagementUnitOfWork`, and its EF implementation in Infrastructure. This avoids exposing `DbContext` to application code and avoids registering one global `IUnitOfWork` for multiple module databases.

The current User Management registration is:

```csharp
services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>))
    .AddScoped(typeof(IReadRepository<>), typeof(EfRepository<>))
    .AddScoped<IUserManagementUnitOfWork, UserManagementUnitOfWork>();
```

Keep one `DbContext`, repository set, and unit of work per module persistence boundary. If two modules share one database, they may still use separate schemas and contexts; do not assume that gives a transaction across both contexts.

## Error handling and concurrency

- Catch provider-specific `DbUpdateException`/concurrency failures around `SaveChangesAsync`, not around `AddAsync`/`UpdateAsync`/`DeleteAsync`.
- Keep constraint classification in Infrastructure. Return application-level errors from the use-case layer.
- For optimistic concurrency, load with the expected version/precondition, mutate the aggregate, and translate a failed commit to a stable concurrency error.
- Do not return an entity as successfully persisted until `SaveChangesAsync` succeeds.

## Transactions and scale-out

The unit of work is a consistency boundary, not a distributed transaction. For one module command, the default `DbContext.SaveChangesAsync` transaction is usually sufficient. Add an explicit database transaction only when the command requires multiple savepoints or multiple `SaveChanges` calls that must commit atomically.

When a command must publish an integration event, persist an outbox record in the same unit of work and publish it asynchronously. Avoid trying to coordinate two module databases with a distributed transaction. In a later microservice split, the module's use-case contract and outbox can remain while the repository/unit-of-work implementation moves behind the service boundary.

## Package-consumer workflow

The Core package is currently the stable `0.1.11`. For local unpublished-package testing:

```powershell
# From C:\Repos\Hmz.Core
dotnet pack Hmz.Core.SharedKernel/Hmz.Core.SharedKernel.csproj `
  -c Release -o <local-nuget-source>

# From C:\Repos\Agent360
dotnet restore Agent360.slnx --force-evaluate
dotnet build Agent360.slnx --no-restore
Get-ChildItem tests -Recurse -Filter *.csproj | ForEach-Object {
    dotnet run --project $_.FullName --configuration Release --no-build -- `
        --minimum-expected-tests 1
}
```

Do not hardcode a workstation path in shared automation. Publish the same
package version to the selected authenticated feed or provide it through that
system's package artifact mechanism.
