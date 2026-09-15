using Samizdat.Core.Rendering;

namespace Samizdat.Core.Tests;

public class WikiLinkTargetTests
{
    [Fact]
    public void Slug_target_stays_as_it_is()
        => Assert.Equal(["proxmox"], WikiLinkTarget.Candidates("proxmox"));

    [Fact]
    public void Note_name_gives_a_slug_too()
        => Assert.Equal(["Заметка про Proxmox", "zametka-pro-proxmox"],
                        WikiLinkTarget.Candidates("Заметка про Proxmox"));

    [Fact]
    public void Anchor_is_dropped()
        => Assert.Equal(["proxmox"], WikiLinkTarget.Candidates("proxmox#Хранилище"));

    [Fact]
    public void Folder_path_is_dropped()
        => Assert.Equal(["Заметка", "zametka"], WikiLinkTarget.Candidates("Техника/Серверы/Заметка"));

    [Fact]
    public void Empty_target_gives_nothing()
        => Assert.Empty(WikiLinkTarget.Candidates("#раздел"));
}
