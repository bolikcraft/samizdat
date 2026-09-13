namespace Samizdat.Cli;

/// Заглушка: реальная реализация — Task 17.
public static class PushCommand
{
    public static Task<int> RunAsync(ParsedArgs args, CliConfig config)
        => throw new CliException("push ещё не сделан");
}
