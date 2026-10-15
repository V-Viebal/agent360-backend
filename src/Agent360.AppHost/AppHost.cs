using Agent360.AppHost.Extensions;

var builder = DistributedApplication.CreateBuilder(args);
var applicationId = "Agent360".Replace('.', '-').ToLowerInvariant();
var apiResourceName = $"{applicationId}-api";

// Define a parameter for the password (it will prompt you if not found in configuration)
var userName = builder.AddParameter("postgres-username");
var password = builder.AddParameter("postgres-password", secret: true);

// One project-isolated PostgreSQL instance for local development. The API and
// Logto use separate databases on this instance; neither local database is a
// staging or production database.
var postgres = builder.AddPostgres("postgres")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithUserName(userName)
    .WithPassword(password)
    .WithEnvironment("POSTGRES_DB", "postgres")
    .WithBindMount("./_scripts/postgres", "/docker-entrypoint-initdb.d")
    .WithVolume($"{applicationId}-postgres-data", "/var/lib/postgresql/18/docker")
    .WithPgAdmin(pd => pd.WithLifetime(ContainerLifetime.Persistent));

var applicationDatabase = postgres.AddDatabase("application");

var logtoContainer = builder.AddContainer("logto", "svhd/logto")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEntrypoint("sh")
    .WithArgs("-c", "npm run cli db seed -- --swe && npm start")
    .WithEndpoint(port: 3001, targetPort: 3001, name: "core")
    .WithEndpoint(port: 3002, targetPort: 3002, name: "admin")
    .WithEnvironment("DB_URL", $"postgresql://{userName}:{password}@postgres:5432/logto")
    .WithEnvironment("ENDPOINT", "http://localhost:3001")
    .WithEnvironment("ADMIN_ENDPOINT", "http://localhost:3002")
    .WithEnvironment("TRUST_PROXY_HEADER", "1")
    .WaitFor(postgres);

var api = builder.AddProject<Projects.Agent360_API>(apiResourceName)
    .WithReference(applicationDatabase, "DefaultConnection")
    .WaitFor(applicationDatabase);

logtoContainer
    .WithReference(api)
    .WithPodmanHostFix();

builder.Build().Run();