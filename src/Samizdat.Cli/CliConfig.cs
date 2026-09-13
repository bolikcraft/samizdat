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

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
