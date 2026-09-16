using System.Text;
using System.Text.RegularExpressions;
using Samizdat.Core;

namespace Samizdat.Cli;

/// Name — имя файла без .md: по нему Obsidian разрешает [[ссылку]].
public sealed record VaultNote(
    string Slug,
    string SourcePath,
    byte[] Markdown,
    IReadOnlyList<(string Name, byte[] Bytes)> Attachments,
    string Folder,
    string Name)
{
    public string Hash => ArticleHash.Compute(Markdown, Attachments, Folder, Name);
}

public sealed partial class VaultScanner(string vaultPath)
{
    // После имени в эмбеде Obsidian бывает "|300", "|подпись" или "#якорь" — имя файла это не меняет.
    [GeneratedRegex(@"!\[\[(?<name>[^\]|#]+)(?:[|#][^\]]*)?\]\]|!\[[^\]]*\]\((?<path>[^)]+)\)")]
    private static partial Regex ImageReference();

    [GeneratedRegex(@"\A﻿?---[ \t]*\r?\n(?<yaml>.*?)^---[ \t]*\r?$", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex FrontMatterBlock();

    [GeneratedRegex(@"^(?<quote>[""']?)publish\k<quote>[ \t]*:(?<value>[^\r\n]*)", RegexOptions.Multiline)]
    private static partial Regex PublishLine();

    // Те же истинные значения, что понимает YamlDotNet; хвост "# ..." — комментарий YAML.
    [GeneratedRegex(@"\A[""']?(true|yes|on|y)[""']?[ \t]*(#.*)?\z", RegexOptions.IgnoreCase)]
    private static partial Regex TrueValue();

    readonly List<string> warnings = [];

    /// Заметки, пропущенные из-за битой шапки. Заполняется по ходу перечисления Scan.
    public IReadOnlyList<string> Warnings => warnings;

    public IEnumerable<VaultNote> Scan()
    {
        warnings.Clear();
        filesByName = null;
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
                // Шаблоны Obsidian с {{title}} не разбираются как YAML, из-за них push стоять не должен.
                // Заметку, которую явно просят выложить, молча не теряем.
                var publish = RawPublishValue(text);
                if (publish is not null && TrueValue().IsMatch(publish))
                    throw new CliException($"{file}: {error.Message}");
                if (publish is not null)
                    warnings.Add($"{file}: заметка пропущена. {error.Message}");
                continue;
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

            yield return new VaultNote(slug, file, Encoding.UTF8.GetBytes(text), FindAttachments(text, file), folder,
                                       Path.GetFileNameWithoutExtension(file));
        }
    }

    /// slug идёт прямо в URL и в имя каталога на диске. Правило общее с сервером: заметку, которую
    /// сервер отвергнет, CLI должен остановить до выкладки.
    static string ValidateSlug(string slug, string file)
        => SafeName.SlugProblem(slug) is { } problem ? throw new CliException($"{file}: {problem}") : slug;

    /// Значение publish из сырой шапки, когда YAML целиком не разбирается. null — ключа нет.
    static string? RawPublishValue(string text)
    {
        var block = FrontMatterBlock().Match(text);
        if (!block.Success) return null;

        var line = PublishLine().Match(block.Groups["yaml"].Value);
        return line.Success ? line.Groups["value"].Value.Trim() : null;
    }

    bool IsHidden(string file)
        => Path.GetRelativePath(vaultPath, file)
               .Split(Path.DirectorySeparatorChar)
               .Any(part => part.StartsWith('.'));

    ILookup<string, string>? filesByName;

    /// Ищем только те файлы, на которые есть ссылка в тексте.
    IReadOnlyList<(string Name, byte[] Bytes)> FindAttachments(string text, string noteFile)
    {
        var found = new Dictionary<string, byte[]>();
        var noteFolder = Path.GetDirectoryName(noteFile)!;

        foreach (Match match in ImageReference().Matches(text))
        {
            var reference = (match.Groups["name"].Success
                ? match.Groups["name"].Value
                : Uri.UnescapeDataString(match.Groups["path"].Value)).Trim();

            if (reference.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;

            // На сервере вложения лежат плоско по имени, поэтому второй файл с тем же именем не нужен.
            var name = Path.GetFileName(reference);
            // Сервер отвергнет всю статью из-за такого имени, а index.md затёр бы её текст.
            if (!SafeName.IsAttachment(name) || found.ContainsKey(name)) continue;

            if (ResolveAttachment(reference, name, noteFolder) is { } file)
                found[name] = File.ReadAllBytes(file);
        }

        return found.Select(item => (item.Key, item.Value)).ToList();
    }

    /// Порядок как в Obsidian: путь от папки заметки, путь от корня вольта, потом файл с тем же именем.
    string? ResolveAttachment(string reference, string name, string noteFolder)
    {
        var relative = reference.TrimStart('/');
        foreach (var start in new[] { noteFolder, vaultPath })
        {
            var candidate = Path.GetFullPath(Path.Combine(start, relative));
            if (IsVaultFile(candidate)) return candidate;
        }

        return FilesByName()[name]
            .OrderBy(candidate => Distance(noteFolder, candidate))
            .ThenBy(candidate => candidate, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    bool IsVaultFile(string path)
    {
        // Сегмент ".." начинается с точки, поэтому IsHidden отсекает и выход за пределы вольта.
        // Корневой путь в ответе GetRelativePath значит другой диск в Windows.
        return !Path.IsPathRooted(Path.GetRelativePath(vaultPath, path))
               && File.Exists(path)
               && !IsHidden(path);
    }

    // Маска "*" нужна только для обхода; имя из ссылки сравнивается с ключом точно.
    ILookup<string, string> FilesByName()
        => filesByName ??= Directory.EnumerateFiles(vaultPath, "*", SearchOption.AllDirectories)
                                    .Where(file => !IsHidden(file))
                                    .ToLookup(file => Path.GetFileName(file), StringComparer.Ordinal);

    /// Шаги по дереву папок: вверх до общего предка и вниз до папки файла.
    static int Distance(string fromFolder, string file)
    {
        var steps = Path.GetRelativePath(fromFolder, Path.GetDirectoryName(file)!)
                        .Split(Path.DirectorySeparatorChar);
        return steps is ["."] ? 0 : steps.Length;
    }
}
