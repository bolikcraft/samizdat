using System.Text;
using System.Text.RegularExpressions;
using Samizdat.Core;

namespace Samizdat.Cli;

public sealed record VaultNote(
    string Slug,
    string SourcePath,
    byte[] Markdown,
    IReadOnlyList<(string Name, byte[] Bytes)> Attachments,
    string Folder)
{
    public string Hash => ArticleHash.Compute(Markdown, Attachments, Folder);
}

public sealed partial class VaultScanner(string vaultPath)
{
    [GeneratedRegex(@"!\[\[(?<name>[^\]|#]+)\]\]|!\[[^\]]*\]\((?<path>[^)]+)\)")]
    private static partial Regex ImageReference();

    public IEnumerable<VaultNote> Scan()
    {
        var taken = new Dictionary<string, string>();

        foreach (var file in Directory.EnumerateFiles(vaultPath, "*.md", SearchOption.AllDirectories))
        {
            if (IsHidden(file)) continue;

            var text = File.ReadAllText(file);
            ParsedDocument parsed;
            try
            {
                parsed = FrontMatterParser.Parse(text);
            }
            catch (FrontMatterException error)
            {
                throw new CliException($"{file}: {error.Message}");
            }

            if (!parsed.FrontMatter.Publish) continue;

            var slug = parsed.FrontMatter.Slug is { } explicitSlug
                ? ValidateSlug(explicitSlug, file)
                : Slugger.FromTitle(parsed.FrontMatter.Title ?? Path.GetFileNameWithoutExtension(file));

            if (taken.TryGetValue(slug, out var other))
                throw new CliException($"Один slug «{slug}» у двух заметок: {other} и {file}");
            taken[slug] = file;

            var relative = Path.GetRelativePath(vaultPath, Path.GetDirectoryName(file)!);
            var folder = relative == "." ? "" : relative.Replace(Path.DirectorySeparatorChar, '/');

            yield return new VaultNote(slug, file, Encoding.UTF8.GetBytes(text), FindAttachments(text), folder);
        }
    }

    /// slug идёт прямо в URL и в имя каталога на диске. Правило общее с сервером: заметку, которую
    /// сервер отвергнет, CLI должен остановить до выкладки.
    static string ValidateSlug(string slug, string file)
        => SafeName.SlugProblem(slug) is { } problem ? throw new CliException($"{file}: {problem}") : slug;

    bool IsHidden(string file)
        => Path.GetRelativePath(vaultPath, file)
               .Split(Path.DirectorySeparatorChar)
               .Any(part => part.StartsWith('.'));

    /// Ищем только те картинки, на которые есть ссылка в тексте; путь ищем по всему вольту.
    IReadOnlyList<(string Name, byte[] Bytes)> FindAttachments(string text)
    {
        var found = new Dictionary<string, byte[]>();

        foreach (Match match in ImageReference().Matches(text))
        {
            var reference = match.Groups["name"].Success
                ? match.Groups["name"].Value
                : Uri.UnescapeDataString(match.Groups["path"].Value);

            if (reference.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;

            var name = Path.GetFileName(reference.Trim());
            // Сервер отвергнет всю статью из-за такого имени, а index.md затёр бы её текст.
            if (!SafeName.IsAttachment(name) || found.ContainsKey(name)) continue;

            var file = Directory.EnumerateFiles(vaultPath, name, SearchOption.AllDirectories)
                                .FirstOrDefault(candidate => !IsHidden(candidate));
            if (file is not null) found[name] = File.ReadAllBytes(file);
        }

        return found.Select(item => (item.Key, item.Value)).ToList();
    }
}
