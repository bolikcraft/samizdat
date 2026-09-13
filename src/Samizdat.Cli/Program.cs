using Samizdat.Cli;

var parsed = ArgParser.Parse(args);

try
{
    switch (parsed.Command)
    {
        case "login":
        {
            var config = CliConfig.Load();
            Console.Write("Адрес сервера: ");
            config.ServerUrl = Console.ReadLine()?.Trim() ?? "";
            Console.Write("Токен: ");
            config.Token = Console.ReadLine()?.Trim() ?? "";
            Console.Write("Путь к вольту: ");
            config.VaultPath = Console.ReadLine()?.Trim() ?? "";
            config.Save();
            Console.WriteLine($"Записано в {CliConfig.Path}");
            return 0;
        }

        case "pull":
        {
            var slug = parsed.Argument(0) ?? throw new CliException("Нужен slug: samizdat pull <slug>");
            var client = SamizdatClient.FromConfig(CliConfig.Load());
            Console.WriteLine(await client.GetMarkdownAsync(slug));
            return 0;
        }

        case "push":
            return await PushCommand.RunAsync(parsed, CliConfig.Load());

        default:
            Console.Error.WriteLine("samizdat push [--dry-run] [--prune] | pull <slug> | login");
            return 1;
    }
}
catch (CliException error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
catch (HttpRequestException error)
{
    Console.Error.WriteLine($"Сервер недоступен: {error.Message}");
    return 1;
}
