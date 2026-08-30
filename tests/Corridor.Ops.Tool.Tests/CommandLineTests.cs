namespace Corridor.Ops.Tool.Tests;

public class CommandLineTests
{
    [Fact]
    public void Parse_SupportsSeparatedAndEqualsOptionForms()
    {
        var parsed = CommandLine.Parse(new[]
        {
            "--url", "http://localhost:8080",
            "--idp=okta",
            "positional-word",
        });

        Assert.Equal("http://localhost:8080", parsed.GetOption("url"));
        Assert.Equal("okta", parsed.GetOption("idp"));
        Assert.Equal(new[] { "positional-word" }, parsed.Positional);
        Assert.False(parsed.HasOption("help"));
    }

    [Fact]
    public void Parse_OptionNamesAreCaseInsensitive()
    {
        var parsed = CommandLine.Parse(new[] { "--IDP", "adfs" });

        Assert.Equal("adfs", parsed.GetOption("idp"));
    }

    [Fact]
    public void Parse_TreatsSingleDashHAsTheHelpOption()
    {
        var parsed = CommandLine.Parse(new[] { "-h" });

        Assert.True(parsed.HasOption("h"));
        Assert.Empty(parsed.Positional);
    }

    [Fact]
    public void Parse_BareDashedTokensOtherThanHStayPositional()
    {
        // A base64url token can legitimately start with a dash.
        var parsed = CommandLine.Parse(new[] { "-eyJhbGciOiJKV1QifQ.payload" });

        Assert.Equal(new[] { "-eyJhbGciOiJKV1QifQ.payload" }, parsed.Positional);
    }

    [Fact]
    public void GetOption_FallsBackToTheGivenDefault()
    {
        var parsed = CommandLine.Parse(Array.Empty<string>());

        Assert.Null(parsed.GetOption("url"));
        Assert.Equal("fallback", parsed.GetOption("url", "fallback"));
    }

    [Fact]
    public void Parse_RejectsAnOptionWithoutAName()
    {
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(new[] { "--" }));
    }
}
