using Corridor.Portal.Services.TraceLink;

namespace Corridor.Portal.Tests;

public class ProblemDetailsMapperTests
{
    [Theory]
    [InlineData(TraceLinkFaults.InvalidIdentityMode, 502)]
    [InlineData(TraceLinkFaults.IllegalStatusTransition, 409)]
    [InlineData(TraceLinkFaults.CaseNotFound, 404)]
    [InlineData(TraceLinkFaults.ValidationError, 400)]
    [InlineData(TraceLinkFaults.Unavailable, 502)]
    [InlineData("cor:SomethingNew", 502)]
    public void Map_TranslatesFaultSubcodesToProblemStatuses(string subcode, int expectedStatus)
    {
        var (status, title) = TraceLinkProblemMapper.Map(subcode);

        Assert.Equal(expectedStatus, status);
        Assert.False(string.IsNullOrWhiteSpace(title));
    }
}
