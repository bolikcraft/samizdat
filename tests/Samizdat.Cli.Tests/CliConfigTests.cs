using Samizdat.Cli;

namespace Samizdat.Cli.Tests;

public class CliConfigTests : IDisposable
{
    const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    readonly string root = Directory.CreateTempSubdirectory("samizdat-config").FullName;

    [Fact]
    public void Config_file_and_its_folder_are_closed_to_other_users()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(root, "samizdat", "config.json");
        new CliConfig { ServerUrl = "https://samizdat.test", Token = "secret-token" }.Save(path);

        Assert.Equal(OwnerFile, File.GetUnixFileMode(path));
        Assert.Equal(OwnerFile | UnixFileMode.UserExecute, new DirectoryInfo(Path.GetDirectoryName(path)!).UnixFileMode);
    }

    [Fact]
    public void Open_config_file_from_an_old_version_is_closed_on_save()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(root, "config.json");
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(path, OwnerFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        new CliConfig { Token = "secret-token" }.Save(path);

        Assert.Equal(OwnerFile, File.GetUnixFileMode(path));
        Assert.Contains("secret-token", File.ReadAllText(path));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
