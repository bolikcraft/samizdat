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
    public void Folder_path_and_anchor_are_dropped_together()
        => Assert.Equal(["Заметка", "zametka"], WikiLinkTarget.Candidates("Техника/Серверы/Заметка#Диски"));

    [Fact]
    public void Empty_target_gives_nothing()
        => Assert.Empty(WikiLinkTarget.Candidates("#раздел"));

    [Fact]
    public void Target_without_letters_does_not_lead_to_the_fallback_slug()
        => Assert.Equal(["!!!"], WikiLinkTarget.Candidates("!!!"));

    [Fact]
    public void Literal_fallback_slug_still_works()
        => Assert.Equal(["bez-nazvaniya"], WikiLinkTarget.Candidates("bez-nazvaniya"));

    [Fact]
    public void Title_that_really_gives_the_fallback_word_still_resolves()
        => Assert.Equal(["Без названия", "bez-nazvaniya"], WikiLinkTarget.Candidates("Без названия"));
}

public class WikiLinksTests
{
    [Fact]
    public void Targets_are_collected_in_order()
    {
        var targets = WikiLinks.Targets("см. [[proxmox]] и [[Заметка про диски|диски]]");

        Assert.Equal(["proxmox", "Заметка про диски"], targets);
    }

    [Fact]
    public void Picture_embed_is_not_a_link()
        => Assert.Empty(WikiLinks.Targets("![[shema.png]]"));

    [Fact]
    public void The_same_target_twice_comes_twice()
        => Assert.Equal(["proxmox", "proxmox"], WikiLinks.Targets("[[proxmox]] и ещё [[proxmox]]"));

    [Fact]
    public void Link_inside_a_table_is_found()
        => Assert.Equal(["proxmox"], WikiLinks.Targets("| a | b |\n|---|---|\n| [[proxmox]] | x |"));

    [Fact]
    public void Link_inside_a_callout_is_found()
        => Assert.Equal(["proxmox"], WikiLinks.Targets("> [!note] Заголовок\n> см. [[proxmox]]\n"));

    [Fact]
    public void Text_without_links_gives_nothing()
        => Assert.Empty(WikiLinks.Targets("обычный текст без ссылок"));
}
