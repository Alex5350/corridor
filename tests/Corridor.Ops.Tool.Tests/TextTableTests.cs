namespace Corridor.Ops.Tool.Tests;

public class TextTableTests
{
    [Fact]
    public void Render_AlignsColumnsUnderAHeader()
    {
        var table = new TextTable(new[] { "claim", "value" }, new[] { 14, 64 });
        table.AddRow("alg", "RS256");
        table.AddRow("kid", "okta-sim-2026-08");

        var lines = table.Render().Split(Environment.NewLine);

        // Header, separator, then one line per row.
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("claim  value", lines[0]);
        Assert.All(lines[1], ch => Assert.Equal('-', ch));
        Assert.StartsWith("alg    RS256", lines[2]);
        Assert.StartsWith("kid    okta-sim-2026-08", lines[3]);
    }

    [Fact]
    public void Render_TruncatesWideCellsAndCollapsesNewlines()
    {
        var table = new TextTable(new[] { "userName", "note" }, new[] { 10, 10 });
        table.AddRow("abcdefghijklmnopqrstuvwxyz", "two" + Environment.NewLine + "lines");

        var rendered = table.Render();

        Assert.Contains("abcdefg...", rendered);
        Assert.DoesNotContain("jklmnop", rendered);
        // The embedded line break becomes a space, never a second row.
        Assert.Equal(3, rendered.Split(Environment.NewLine).Length);
        Assert.Contains("two lines", rendered);
    }

    [Fact]
    public void AddRow_TreatsNullCellsAsEmpty()
    {
        var table = new TextTable(new[] { "a", "b" }, new[] { 10, 10 });
        table.AddRow(null!, "kept");

        var lines = table.Render().Split(Environment.NewLine);

        // First cell renders empty, the following cell is intact.
        Assert.StartsWith("kept", lines[2].TrimStart());
    }
}
