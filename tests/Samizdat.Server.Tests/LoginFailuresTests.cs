using Samizdat.Server.Auth;

namespace Samizdat.Server.Tests;

public class LoginFailuresTests
{
    sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    readonly ManualClock clock = new();

    static void Fail(LoginFailures failures, string login, int times)
    {
        for (var i = 0; i < times; i++) failures.Add(login);
    }

    [Fact]
    public void Login_is_blocked_at_the_limit_of_failures()
    {
        var failures = new LoginFailures(3, clock);

        Fail(failures, "aleks", 2);
        Assert.False(failures.IsBlocked("aleks"));
        failures.Add("aleks");

        Assert.True(failures.IsBlocked("aleks"));
    }

    [Fact]
    public void Failures_of_one_login_do_not_block_another()
    {
        var failures = new LoginFailures(3, clock);

        Fail(failures, "aleks", 3);

        Assert.False(failures.IsBlocked("ivan"));
    }

    [Fact]
    public void Failures_leave_the_window_after_15_minutes()
    {
        var failures = new LoginFailures(3, clock);
        Fail(failures, "aleks", 3);

        clock.Now += LoginFailures.Window;

        Assert.False(failures.IsBlocked("aleks"));
    }

    [Fact]
    public void Window_slides_with_each_failure()
    {
        var failures = new LoginFailures(3, clock);
        failures.Add("aleks");
        clock.Now += TimeSpan.FromMinutes(10);
        Fail(failures, "aleks", 2);
        Assert.True(failures.IsBlocked("aleks"));

        clock.Now += TimeSpan.FromMinutes(5);

        // Первая неудача вышла из окна, две другие ещё в нём.
        Assert.False(failures.IsBlocked("aleks"));
        failures.Add("aleks");
        Assert.True(failures.IsBlocked("aleks"));
    }

    // Регистр приводит к виду из базы сам маршрут входа; счётчик сравнивает ключи точно.
    [Fact]
    public void Login_is_compared_exactly()
    {
        var failures = new LoginFailures(3, clock);

        Fail(failures, "Aleks", 3);

        Assert.False(failures.IsBlocked("aleks"));
    }

    [Fact]
    public void Zero_limit_blocks_nobody()
    {
        var failures = new LoginFailures(0, clock);

        Fail(failures, "aleks", 100);

        Assert.False(failures.IsBlocked("aleks"));
    }

    [Fact]
    public void Login_that_failed_longest_ago_leaves_a_full_table()
    {
        var failures = new LoginFailures(1, clock, maxLogins: 2);

        failures.Add("first");
        failures.Add("second");
        failures.Add("first");
        failures.Add("third");

        Assert.True(failures.IsBlocked("first"));
        Assert.False(failures.IsBlocked("second"));
        Assert.True(failures.IsBlocked("third"));
    }

    [Fact]
    public void Login_longer_than_the_column_is_not_kept()
    {
        var failures = new LoginFailures(1, clock);
        var login = new string('a', 101);

        failures.Add(login);

        Assert.False(failures.IsBlocked(login));
    }
}
