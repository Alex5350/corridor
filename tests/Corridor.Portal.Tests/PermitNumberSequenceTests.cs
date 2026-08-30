using Corridor.Portal.Services;

namespace Corridor.Portal.Tests;

public class PermitNumberSequenceTests
{
    [Fact]
    public void Next_ContinuesTheHighestSequenceForTheYear()
    {
        var existing = new[]
        {
            "IP-2026-0301", "IP-2026-0308", "IP-2026-0007", "IP-2025-9999", "TRC-100101"
        };

        Assert.Equal("IP-2026-0309", PermitNumberSequence.Next(existing, 2026));
    }

    [Fact]
    public void Next_StartsAtOneWhenTheYearHasNoPermits()
    {
        var existing = new[] { "IP-2025-9999" };

        Assert.Equal("IP-2026-0001", PermitNumberSequence.Next(existing, 2026));
        Assert.Equal("IP-2026-0001", PermitNumberSequence.Next(Array.Empty<string>(), 2026));
    }
}
