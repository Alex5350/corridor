namespace Corridor.Ops.Tool.Tests;

/// <summary>
/// Color behavior: ANSI when allowed, plain text under NO_COLOR. These tests
/// live in one class on purpose: xUnit runs a class sequentially, so the
/// environment variable flips cannot race each other.
/// </summary>
public class OutputTests
{
    [Fact]
    public void Colorize_WrapsTextInAnsiSequencesWhenColorAllowed()
    {
        Environment.SetEnvironmentVariable("NO_COLOR", null);

        var colored = Output.Colorize("plain", Output.AnsiColor.Green);

        Assert.False(Output.NoColorRequested);
        Assert.StartsWith("\u001b[32m", colored, StringComparison.Ordinal);
        Assert.EndsWith("\u001b[0m", colored, StringComparison.Ordinal);
        Assert.Contains("plain", colored);
    }

    [Fact]
    public void Colorize_ReturnsPlainTextWhenNoColorIsSet()
    {
        Environment.SetEnvironmentVariable("NO_COLOR", "1");

        try
        {
            Assert.True(Output.NoColorRequested);
            Assert.Equal("plain", Output.Colorize("plain", Output.AnsiColor.Red));
            Assert.Equal("plain", Output.Colorize("plain", Output.AnsiColor.Bold));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", null);
        }
    }

    [Fact]
    public void NoColorRequested_IgnoresAnEmptyValue()
    {
        Environment.SetEnvironmentVariable("NO_COLOR", "");

        try
        {
            // The convention disables color only for non-empty values.
            Assert.False(Output.NoColorRequested);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", null);
        }
    }

    [Fact]
    public void PlainColorNeverEmitsEscapes()
    {
        Assert.Equal("plain", Output.Colorize("plain", Output.AnsiColor.Plain));
    }
}
