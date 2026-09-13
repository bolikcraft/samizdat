using Samizdat.Cli;

namespace Samizdat.Cli.Tests;

public class ArgParserTests
{
    [Fact]
    public void Reads_command_name()
        => Assert.Equal("push", ArgParser.Parse(["push"]).Command);

    [Fact]
    public void Reads_flags()
    {
        var parsed = ArgParser.Parse(["push", "--dry-run", "--prune"]);

        Assert.True(parsed.Has("dry-run"));
        Assert.True(parsed.Has("prune"));
        Assert.False(parsed.Has("force"));
    }

    [Fact]
    public void Reads_positional_argument()
        => Assert.Equal("privet", ArgParser.Parse(["pull", "privet"]).Argument(0));

    [Fact]
    public void Empty_args_give_empty_command()
        => Assert.Equal("", ArgParser.Parse([]).Command);

    [Fact]
    public void Option_with_value_is_read()
        => Assert.Equal("/home/aleks/vault", ArgParser.Parse(["push", "--vault", "/home/aleks/vault"]).Value("vault"));
}
