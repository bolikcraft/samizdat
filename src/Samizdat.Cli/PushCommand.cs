namespace Samizdat.Cli;

public sealed record PushPlan(IReadOnlyList<VaultNote> Upload, IReadOnlyList<string> Delete)
{
    public static PushPlan Build(IReadOnlyList<VaultNote> notes,
                                 IReadOnlyDictionary<string, string> serverState,
                                 bool prune)
    {
        var upload = notes
            .Where(note => !serverState.TryGetValue(note.Slug, out var hash) || hash != note.Hash)
            .ToList();

        var delete = prune
            ? serverState.Keys.Where(slug => notes.All(note => note.Slug != slug)).Order().ToList()
            : [];

        return new PushPlan(upload, delete);
    }
}

public static class PushCommand
{
    public static async Task<int> RunAsync(ParsedArgs args, CliConfig config)
    {
        var vault = args.Value("vault") ?? config.VaultPath;
        if (string.IsNullOrWhiteSpace(vault))
            throw new CliException("Не задан путь к вольту: samizdat login или --vault <путь>");

        var scanner = new VaultScanner(vault);
        var notes = scanner.Scan().ToList();
        foreach (var warning in scanner.Warnings) Console.Error.WriteLine(warning);
        var client = SamizdatClient.FromConfig(config);
        var state = await client.GetStateAsync();
        var plan = PushPlan.Build(notes, state, args.Has("prune"));

        if (args.Has("dry-run"))
        {
            foreach (var note in plan.Upload) Console.WriteLine($"выложить  {note.Slug}");
            foreach (var slug in plan.Delete) Console.WriteLine($"удалить   {slug}");
            Console.WriteLine($"Всего: выложить {plan.Upload.Count}, удалить {plan.Delete.Count}");
            return 0;
        }

        var failed = 0;
        foreach (var note in plan.Upload)
        {
            try
            {
                await client.PutArticleAsync(note.Slug, note.Markdown, note.Attachments, note.Folder, note.Name);
                Console.WriteLine($"выложено  {note.Slug}");
            }
            catch (CliException error)
            {
                failed++;
                Console.Error.WriteLine(error.Message);
            }
        }

        foreach (var slug in plan.Delete)
        {
            await client.DeleteArticleAsync(slug);
            Console.WriteLine($"удалено   {slug}");
        }

        return failed == 0 ? 0 : 1;
    }
}
