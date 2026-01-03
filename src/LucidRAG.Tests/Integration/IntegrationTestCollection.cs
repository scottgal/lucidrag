namespace LucidRAG.Tests.Integration;

/// <summary>
/// Collection definition for integration tests.
/// Tests in this collection run sequentially to avoid race conditions.
/// </summary>
[CollectionDefinition("Integration")]
public class IntegrationTestCollection : ICollectionFixture<TestWebApplicationFactory>
{
    // This class has no code, and is never created.
    // Its purpose is simply to be the place to apply [CollectionDefinition]
    // and all the ICollectionFixture<> interfaces.
}
