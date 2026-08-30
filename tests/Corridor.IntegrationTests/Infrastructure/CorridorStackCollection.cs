namespace Corridor.IntegrationTests.Infrastructure;

/// <summary>
/// One collection for every integration test class: the stack boots exactly once and
/// the classes run sequentially against it (trust mode flips are order sensitive).
/// </summary>
[CollectionDefinition(Name)]
public sealed class CorridorStackCollection : ICollectionFixture<CorridorStackFixture>
{
    public const string Name = "Corridor stack";
}
