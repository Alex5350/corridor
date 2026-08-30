using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Corridor.Portal.Services;

public sealed record ChecklistItem(
    [property: JsonPropertyName("item")] string Item,
    [property: JsonPropertyName("done")] bool Done);

/// <summary>
/// Server side updates for idn.Assignments.ChecklistJson. The SPA only reports
/// (itemIndex, done); the portal owns the list itself.
/// </summary>
public sealed class ChecklistService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(UnicodeRanges.BasicLatin)
    };

    public IReadOnlyList<ChecklistItem> Parse(string checklistJson)
    {
        return JsonSerializer.Deserialize<List<ChecklistItem>>(checklistJson, Options) ?? [];
    }

    public string Serialize(IReadOnlyList<ChecklistItem> items)
    {
        return JsonSerializer.Serialize(items, Options);
    }

    /// <summary>Toggles one checklist item by index. Returns false when the index is out of range.</summary>
    public bool TryToggle(string checklistJson, int itemIndex, bool done, out string updatedJson)
    {
        var items = Parse(checklistJson);
        if (itemIndex < 0 || itemIndex >= items.Count)
        {
            updatedJson = checklistJson;
            return false;
        }
        var updated = items.ToArray();
        updated[itemIndex] = updated[itemIndex] with { Done = done };
        updatedJson = Serialize(updated);
        return true;
    }
}
