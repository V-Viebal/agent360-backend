namespace Agent360.API.FunctionalTests.Fixtures;

// Test-only identity: production seeders deliberately do not create an ADMIN user.
public static class TestActors
{
    public const string AdminSubject = "functional-admin";
    public const string AdminEmail = "functional-admin@example.test";
    public const string CustomerSubject = "functional-customer";
    public const string CustomerEmail = "functional-customer@example.test";
}