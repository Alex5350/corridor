using System.Text.Json;
using Corridor.Portal.Services;

namespace Corridor.Portal.Tests;

public class ChecklistServiceTests
{
    private const string ThreeItems =
        "[{\"item\":\"Review acquisition log\",\"done\":false},{\"item\":\"Sample serials\",\"done\":false},{\"item\":\"Verify permits\",\"done\":true}]";

    private readonly ChecklistService _service = new();

    [Fact]
    public void TryToggle_FlipsTheItemAtTheIndex()
    {
        var success = _service.TryToggle(ThreeItems, 1, true, out var updatedJson);

        Assert.True(success);
        var items = _service.Parse(updatedJson);
        Assert.False(items[0].Done);
        Assert.True(items[1].Done);
        Assert.True(items[2].Done);
        Assert.Equal("Sample serials", items[1].Item);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(100)]
    public void TryToggle_RejectsOutOfRangeIndexes(int index)
    {
        var success = _service.TryToggle(ThreeItems, index, true, out var updatedJson);

        Assert.False(success);
        Assert.Equal(ThreeItems, updatedJson);
    }

    [Fact]
    public void Parse_RoundTripsThroughSerialize()
    {
        var items = _service.Parse(ThreeItems);
        var json = _service.Serialize(items);

        using var original = JsonDocument.Parse(ThreeItems);
        using var roundTripped = JsonDocument.Parse(json);
        Assert.Equal(original.RootElement.GetArrayLength(), roundTripped.RootElement.GetArrayLength());
        Assert.Equal("Verify permits", roundTripped.RootElement[2].GetProperty("item").GetString());
        Assert.True(roundTripped.RootElement[2].GetProperty("done").GetBoolean());
    }
}
