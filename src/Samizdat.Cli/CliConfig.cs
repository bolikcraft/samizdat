using System.Text.Json;

namespace Samizdat.Cli;

public sealed class CliConfig
{
    public string ServerUrl { get; set; } = "";
    public string Token { get; set; } = "";
    public string VaultPath { get; set; } = "";

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "samizdat", "config.json");

    public static CliConfig Load()
        => File.Exists(Path)
            ? JsonSerializer.Deserialize<CliConfig>(File.ReadAllText(Path)) ?? new CliConfig()
            : new CliConfig();

    const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode OwnerFolder = OwnerFile | UnixFileMode.UserExecute;

    public void Save() => Save(Path);

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var folder = System.IO.Path.GetDirectoryName(path)!;

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, json);
            return;
        }

        // Режим задаётся при создании. Иначе токен какое-то время лежит в файле с правами по umask.
        Directory.CreateDirectory(folder, OwnerFolder);
        // UnixCreateMode не меняет уже существующий файл, а старые версии CLI создавали его с 0644.
        if (File.Exists(path)) File.SetUnixFileMode(path, OwnerFile);

        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = OwnerFile,
        });
        using var writer = new StreamWriter(stream);
        writer.Write(json);
    }
}
