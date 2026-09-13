using Samizdat.Cli;

namespace Samizdat.Cli.Tests;

public class PushPlanTests
{
    static VaultNote Note(string slug, string text)
        => new(slug, $"{slug}.md", System.Text.Encoding.UTF8.GetBytes(text), [], "");

    [Fact]
    public void New_note_is_uploaded()
    {
        var plan = PushPlan.Build([Note("a", "x")], new Dictionary<string, string>(), prune: false);

        Assert.Equal(["a"], plan.Upload.Select(note => note.Slug));
        Assert.Empty(plan.Delete);
    }

    [Fact]
    public void Unchanged_note_is_skipped()
    {
        var note = Note("a", "x");
        var plan = PushPlan.Build([note], new Dictionary<string, string> { ["a"] = note.Hash }, prune: false);

        Assert.Empty(plan.Upload);
    }

    [Fact]
    public void Changed_note_is_uploaded()
    {
        var plan = PushPlan.Build([Note("a", "новый текст")],
                                  new Dictionary<string, string> { ["a"] = "старый-хэш" }, prune: false);

        Assert.Single(plan.Upload);
    }

    [Fact]
    public void Prune_deletes_articles_that_left_the_vault()
    {
        var plan = PushPlan.Build([Note("a", "x")],
                                  new Dictionary<string, string> { ["a"] = "h", ["b"] = "h" }, prune: true);

        Assert.Equal(["b"], plan.Delete);
    }

    [Fact]
    public void Without_prune_nothing_is_deleted()
    {
        var plan = PushPlan.Build([Note("a", "x")],
                                  new Dictionary<string, string> { ["b"] = "h" }, prune: false);

        Assert.Empty(plan.Delete);
    }
}
