namespace Samizdat.Cli;

public sealed record ParsedArgs(string Command, IReadOnlyList<string> Positional,
                                IReadOnlyDictionary<string, string?> Options)
{
    public bool Has(string name) => Options.ContainsKey(name);
    public string? Value(string name) => Options.GetValueOrDefault(name);
    public string? Argument(int index) => index < Positional.Count ? Positional[index] : null;
}

public static class ArgParser
{
    /// Флаг без значения даёт null: `--prune` есть, значения нет.
    public static ParsedArgs Parse(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string?>();

        for (var at = 0; at < args.Length; at++)
        {
            var item = args[at];
            if (!item.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(item);
                continue;
            }

            var name = item[2..];
            var next = at + 1 < args.Length ? args[at + 1] : null;
            if (next is not null && !next.StartsWith("--", StringComparison.Ordinal))
            {
                options[name] = next;
                at++;
            }
            else
            {
                options[name] = null;
            }
        }

        var command = positional.Count > 0 ? positional[0] : "";
        return new ParsedArgs(command, positional.Skip(1).ToList(), options);
    }
}
