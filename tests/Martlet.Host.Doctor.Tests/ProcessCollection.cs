namespace Martlet.Host.Doctor.Tests;

// Real pipe/deadline fixtures should not compete with unrelated CPU/cancellation fixtures in this test process.
[CollectionDefinition("Owned process fixtures", DisableParallelization = true)]
public sealed class ProcessCollection;
