using Xunit.Sdk;
using Xunit.v3;

[assembly: AssemblyFixture(typeof(FunctionalTestFixture))]
[assembly: Parallelization(Mode = ParallelMode.None)]