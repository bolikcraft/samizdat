using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class RegistrationTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public RegistrationTests(DatabaseFixture database) => this.database = database;

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    [Fact]
    public void Invite_is_alive_until_it_is_used_revoked_or_expired()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(new InviteRow { Token = "t" }.IsAlive(now));
        Assert.True(new InviteRow { Token = "t", ExpiresAt = now.AddDays(1) }.IsAlive(now));
        Assert.False(new InviteRow { Token = "t", ExpiresAt = now.AddDays(-1) }.IsAlive(now));
        Assert.False(new InviteRow { Token = "t", RevokedAt = now }.IsAlive(now));
        Assert.False(new InviteRow { Token = "t", UsedAt = now }.IsAlive(now));
    }

    [Fact]
    public void A_person_made_by_hand_is_approved_at_once()
    {
        Assert.NotNull(new UserRow { Login = "ivan", PasswordHash = "x" }.ApprovedAt);
    }

    [Fact]
    public async Task A_person_waiting_for_approval_cannot_log_in()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPending(factory, "gost", "parol-gostya");

        var answer = await TryLogin(factory, "gost", "parol-gostya");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("must approve your request", await answer.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_live_invite_shows_the_form_and_stays_out_of_caches()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var token = AddInvite(factory, note: "для Ивана");
        var client = factory.CreateClient();

        var answer = await client.GetAsync($"/i/{token}");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("для Ивана", await answer.Content.ReadAsStringAsync());
        Assert.True(answer.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task An_invite_makes_a_reader_who_is_let_in_at_once()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var token = AddInvite(factory, note: "для Ивана");

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var answer = await client.PostAsync($"/i/{token}", await InviteFields(client, token, "ivan", "parol-ivana"));

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal("/", answer.Headers.Location?.ToString());
        // Учётка не просто заведена: этой же cookie человек уже ходит по сайту.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);

        var person = Users(factory).Single(row => row.Login == "ivan");
        Assert.Equal(UserRole.Reader, person.Role);
        Assert.NotNull(person.ApprovedAt);

        var invite = Invites(factory).Single();
        Assert.NotNull(invite.UsedAt);
        Assert.Equal("ivan", invite.UsedByLogin);
    }

    [Fact]
    public async Task The_same_invite_does_not_work_twice()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var token = AddInvite(factory, note: null);
        var first = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // Второй клиент, а не первый: первый после входа ушёл бы на главную, а не на страницу
        // «ссылка не работает». Форму он берёт заранее — потом её уже не дадут.
        var second = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var fields = await InviteFields(second, token, "petr", "parol-petra");
        await first.PostAsync($"/i/{token}", await InviteFields(first, token, "ivan", "parol-ivana"));

        var again = await second.PostAsync($"/i/{token}", fields);

        Assert.Equal(HttpStatusCode.Gone, again.StatusCode);
        Assert.Single(Users(factory));
    }

    [Fact]
    public async Task A_used_invite_shows_a_dead_page()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var token = AddInvite(factory, note: null);
        var first = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await first.PostAsync($"/i/{token}", await InviteFields(first, token, "ivan", "parol-ivana"));

        var answer = await factory.CreateClient().GetAsync($"/i/{token}");

        Assert.Equal(HttpStatusCode.Gone, answer.StatusCode);
    }

    [Fact]
    public async Task An_expired_or_revoked_invite_shows_a_dead_page()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var expired = AddInvite(factory, note: null, expiresAt: DateTimeOffset.UtcNow.AddDays(-1));
        var revoked = AddInvite(factory, note: null, revokedAt: DateTimeOffset.UtcNow);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync($"/i/{expired}")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync($"/i/{revoked}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/i/takogo-net")).StatusCode);
    }

    // Чужая страница не должна сжечь приглашение на подставную учётку и выдать браузеру её cookie.
    [Fact]
    public async Task A_post_without_a_token_is_refused()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var token = AddInvite(factory, note: null);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var answer = await client.PostAsync($"/i/{token}", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["login"] = "ivan", ["password"] = "parol-ivana", ["repeat"] = "parol-ivana",
            }));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Empty(Users(factory));
        Assert.Null(Invites(factory).Single().UsedAt);
    }

    // Вошедший не заводит вторую учётку и не теряет свою сессию на чужой ссылке.
    [Fact]
    public async Task A_person_who_is_already_in_goes_to_the_main_page()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);
        var token = AddInvite(factory, note: null);
        var client = await Login(factory, "ivan", "parol-ivana");

        var page = await client.GetAsync($"/i/{token}");

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/", page.Headers.Location?.ToString());
        Assert.Null(Invites(factory).Single().UsedAt);
    }

    // Два человека открыли одну ссылку разом. Гашение с условием внутри UPDATE обязано пустить
    // только одного: без него оба проходили проверку IsAlive и заводили по учётке.
    [Fact]
    public async Task Two_people_racing_for_one_invite_give_one_reader()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var token = AddInvite(factory, note: null);
        var first = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var second = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Форму берут по очереди, пока ссылка жива, а отправляют разом: гонка в отправке.
        var firstFields = await InviteFields(first, token, "ivan", "parol-ivana");
        var secondFields = await InviteFields(second, token, "petr", "parol-petra");

        var answers = await Task.WhenAll(
            first.PostAsync($"/i/{token}", firstFields),
            second.PostAsync($"/i/{token}", secondFields));

        Assert.Single(Users(factory));
        Assert.Equal(1, answers.Count(answer => answer.StatusCode == HttpStatusCode.Redirect));
    }

    // Регистр логина роли не играет: столбец сверяется по case_insensitive.
    [Theory]
    [InlineData("ivan")]
    [InlineData("Ivan")]
    public async Task A_busy_login_keeps_the_invite_alive(string wanted)
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);
        var token = AddInvite(factory, note: null);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var answer = await client.PostAsync($"/i/{token}", await InviteFields(client, token, wanted, "drugoy-parol"));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("This login is in use.", await answer.Content.ReadAsStringAsync());
        Assert.Null(Invites(factory).Single().UsedAt);
    }

    [Theory]
    [InlineData("ivan", "korotko", "korotko", "needs 8 characters or more")]
    [InlineData("ivan", "parol-ivana", "drugoy-parol", "are not the same")]
    [InlineData("", "parol-ivana", "parol-ivana", "must not be empty")]
    [InlineData(LoginOverTheLimit, "parol-ivana", "parol-ivana", "more than 100 characters")]
    public async Task A_bad_form_shows_the_reason_and_keeps_the_invite(string login, string password,
                                                                      string repeat, string expected)
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var token = AddInvite(factory, note: null);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var (name, value) = AntiforgeryToken(await client.GetStringAsync($"/i/{token}"));

        var answer = await client.PostAsync($"/i/{token}", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["login"] = login, ["password"] = password, ["repeat"] = repeat, [name] = value,
            }));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains(expected, await answer.Content.ReadAsStringAsync());
        Assert.Empty(Users(factory));
        Assert.Null(Invites(factory).Single().UsedAt);
    }

    [Fact]
    public async Task Owner_makes_an_invite_and_sees_its_link_in_the_settings()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        var owner = await Login(factory, "hozyain", "parol-hozyaina");

        var answer = await Post(owner, "/settings/invites",
            new() { ["note"] = "для Ивана", ["days"] = "7" });

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal("/settings?ok=invite_created#users", answer.Headers.Location?.ToString());

        var invite = Invites(factory).Single();
        Assert.Equal("для Ивана", invite.Note);
        Assert.NotNull(invite.ExpiresAt);

        var html = await owner.GetStringAsync("/settings/");
        Assert.Contains($"/i/{invite.Token}", html);
    }

    [Fact]
    public async Task Owner_revokes_an_invite_and_it_stops_working()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        var token = AddInvite(factory, note: null);
        var id = Invites(factory).Single().Id;
        var owner = await Login(factory, "hozyain", "parol-hozyaina");

        var answer = await Post(owner, $"/settings/invites/{id}/revoke", new());

        Assert.Equal("/settings?ok=invite_revoked#users", answer.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.Gone,
            (await factory.CreateClient().GetAsync($"/i/{token}")).StatusCode);
    }

    [Fact]
    public async Task A_reader_cannot_make_an_invite()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);
        var reader = await Login(factory, "ivan", "parol-ivana");

        var answer = await Post(reader, "/settings/invites", new() { ["days"] = "7" });

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Empty(Invites(factory));
    }

    [Fact]
    public async Task An_unknown_term_is_refused()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        var owner = await Login(factory, "hozyain", "parol-hozyaina");

        var answer = await Post(owner, "/settings/invites", new() { ["days"] = "9999" });

        Assert.Equal("/settings?err=invite_term#users", answer.Headers.Location?.ToString());
        Assert.Empty(Invites(factory));
    }

    [Fact]
    public async Task A_second_revoke_of_an_invite_answers_404()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        var token = AddInvite(factory, note: null, revokedAt: DateTimeOffset.UtcNow);
        var id = Invites(factory).Single().Id;
        var owner = await Login(factory, "hozyain", "parol-hozyaina");

        var answer = await Post(owner, $"/settings/invites/{id}/revoke", new());

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }

    [Fact]
    public async Task A_reader_cannot_revoke_an_invite()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);
        var token = AddInvite(factory, note: null);
        var id = Invites(factory).Single().Id;
        var reader = await Login(factory, "ivan", "parol-ivana");

        var answer = await Post(reader, $"/settings/invites/{id}/revoke", new());

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.True(Invites(factory).Single().IsAlive(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_used_invite_cannot_be_revoked()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        var token = AddInvite(factory, note: null, usedAt: DateTimeOffset.UtcNow);
        var id = Invites(factory).Single().Id;
        var owner = await Login(factory, "hozyain", "parol-hozyaina");

        var answer = await Post(owner, $"/settings/invites/{id}/revoke", new());

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        Assert.Null(Invites(factory).Single().RevokedAt);
    }

    [Fact]
    public async Task Registration_is_closed_until_the_owner_opens_it()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/register")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync("/register", Fields("ivan", "parol-ivana"))).StatusCode);
        Assert.Empty(Users(factory));
    }

    [Fact]
    public async Task An_open_registration_puts_the_person_in_the_queue_without_a_session()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        OpenRegistration(factory);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var answer = await client.PostAsync("/register", await RegisterFields(client, "ivan", "parol-ivana"));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("Your request goes to the owner", await answer.Content.ReadAsStringAsync());

        var person = Users(factory).Single();
        Assert.Null(person.ApprovedAt);
        Assert.Equal(UserRole.Reader, person.Role);
        // Сессии нет: страница сайта по-прежнему уводит на вход.
        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync("/")).StatusCode);
    }

    // CSRF на /i/{token} была найденной дырой: /register из той же формы не должен стать
    // второй такой же.
    [Fact]
    public async Task A_post_to_register_without_a_token_is_refused()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        OpenRegistration(factory);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var answer = await client.PostAsync("/register", Fields("ivan", "parol-ivana"));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Empty(Users(factory));
    }

    [Fact]
    public async Task A_busy_login_is_refused_on_the_open_form()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        OpenRegistration(factory);
        AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);
        var client = factory.CreateClient();

        var answer = await client.PostAsync("/register", await RegisterFields(client, "ivan", "drugoy-parol"));

        Assert.Contains("This login is in use.", await answer.Content.ReadAsStringAsync());
        Assert.Single(Users(factory));
    }

    [Fact]
    public async Task A_full_queue_closes_the_open_registration()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        OpenRegistration(factory);
        FillTheQueue(factory, 50);
        var client = factory.CreateClient();

        var answer = await client.PostAsync("/register", await RegisterFields(client, "ivan", "parol-ivana"));

        Assert.Contains("too many requests", await answer.Content.ReadAsStringAsync());
        Assert.Equal(50, Users(factory).Count);
    }

    // Одно свободное место и заявки разом: pg_advisory_xact_lock обязан развести их по одной.
    // Без него часть заявок читает один и тот же счётчик и проходит предел все сразу — но не
    // каждый раз: пяти заявок для надёжной поимки мало, гонка ловится не всегда.
    // Двадцать ловят её стабильно.
    [Fact]
    public async Task A_burst_of_requests_does_not_break_the_queue_limit()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        OpenRegistration(factory);
        FillTheQueue(factory, 49);

        var clients = Enumerable.Range(0, 20)
            .Select(_ => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
            .ToList();
        var fields = await Task.WhenAll(clients.Select((client, i) =>
            RegisterFields(client, $"vorvalsya{i}", "parol-vorvavshegosya")));

        await Task.WhenAll(clients.Select((client, i) => client.PostAsync("/register", fields[i])));

        Assert.Equal(50, Users(factory).Count);
    }

    [Fact]
    public async Task Approved_people_do_not_fill_the_queue()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        OpenRegistration(factory);
        FillTheQueue(factory, 50, approved: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var answer = await client.PostAsync("/register", await RegisterFields(client, "ivan", "parol-ivana"));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("Your request goes to the owner", await answer.Content.ReadAsStringAsync());
        Assert.Equal(51, Users(factory).Count);
    }

    [Fact]
    public async Task A_person_who_is_already_in_does_not_get_the_open_form()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        OpenRegistration(factory);
        AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);
        var client = await Login(factory, "ivan", "parol-ivana");

        var answer = await client.GetAsync("/register");

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal("/", answer.Headers.Location?.ToString());
    }

    [Fact]
    public async Task The_login_page_offers_to_register_only_when_it_is_open()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();

        Assert.DoesNotContain("/register", await factory.CreateClient().GetStringAsync("/login"));

        OpenRegistration(factory);

        Assert.Contains("/register", await factory.CreateClient().GetStringAsync("/login"));
    }

    [Fact]
    public async Task Owner_switches_the_open_registration_on_and_off()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        var owner = await Login(factory, "hozyain", "parol-hozyaina");

        var on = await Post(owner, "/settings/signup/open", new() { ["open"] = "on" });
        Assert.Equal("/settings?ok=signup_open#users", on.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/register")).StatusCode);

        await Post(owner, "/settings/signup/open", new());
        Assert.Equal(HttpStatusCode.NotFound,
            (await factory.CreateClient().GetAsync("/register")).StatusCode);
    }

    [Fact]
    public async Task Owner_lets_a_waiting_person_in()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        AddPending(factory, "gost", "parol-gostya");
        var id = Users(factory).Single(row => row.Login == "gost").Id;
        var owner = await Login(factory, "hozyain", "parol-hozyaina");

        var answer = await Post(owner, $"/settings/signup/{id}/approve", new());

        Assert.Equal("/settings?ok=signup_approved#users", answer.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await TryLogin(factory, "gost", "parol-gostya")).StatusCode);
    }

    [Fact]
    public async Task Owner_rejects_a_waiting_person_and_the_row_is_gone()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        AddPending(factory, "gost", "parol-gostya");
        var id = Users(factory).Single(row => row.Login == "gost").Id;
        var owner = await Login(factory, "hozyain", "parol-hozyaina");

        var answer = await Post(owner, $"/settings/signup/{id}/reject", new());

        Assert.Equal("/settings?ok=signup_rejected#users", answer.Headers.Location?.ToString());
        Assert.DoesNotContain(Users(factory), row => row.Login == "gost");
    }

    // Этими кнопками нельзя тронуть живого человека: иначе «отказать» стало бы вторым способом
    // удалить кого угодно, минуя защиту раздела «Пользователи».
    [Fact]
    public async Task The_queue_buttons_do_not_touch_a_person_who_is_already_in()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);
        var id = Users(factory).Single(row => row.Login == "ivan").Id;
        var owner = await Login(factory, "hozyain", "parol-hozyaina");

        var approved = await Post(owner, $"/settings/signup/{id}/approve", new());
        var rejected = await Post(owner, $"/settings/signup/{id}/reject", new());

        Assert.Equal("/settings?err=not_pending#users", approved.Headers.Location?.ToString());
        Assert.Equal("/settings?err=not_pending#users", rejected.Headers.Location?.ToString());
        Assert.Contains(Users(factory), row => row.Login == "ivan");
    }

    [Fact]
    public async Task The_queue_buttons_answer_404_for_a_person_who_is_not_there()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        var owner = await Login(factory, "hozyain", "parol-hozyaina");

        Assert.Equal(HttpStatusCode.NotFound,
            (await Post(owner, "/settings/signup/4242/approve", new())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Post(owner, "/settings/signup/4242/reject", new())).StatusCode);
    }

    // Гашение с условием внутри UPDATE/DELETE обязано пустить только одного: без него оба запроса
    // проходили проверку в C# и один пускал уже снесённого, а другой сносил уже пущенного.
    [Fact]
    public async Task Racing_approve_and_reject_settle_on_one_outcome()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        AddPending(factory, "gost", "parol-gostya");
        var id = Users(factory).Single(row => row.Login == "gost").Id;
        var owner = await Login(factory, "hozyain", "parol-hozyaina");
        var (name, value) = AntiforgeryToken(await owner.GetStringAsync("/settings/"));

        var answers = await Task.WhenAll(
            owner.PostAsync($"/settings/signup/{id}/approve",
                new FormUrlEncodedContent(new Dictionary<string, string> { [name] = value })),
            owner.PostAsync($"/settings/signup/{id}/reject",
                new FormUrlEncodedContent(new Dictionary<string, string> { [name] = value })));

        var person = Users(factory).SingleOrDefault(row => row.Login == "gost");
        var wonByApprove = answers[0].Headers.Location?.ToString() == "/settings?ok=signup_approved#users";
        var wonByReject = answers[1].Headers.Location?.ToString() == "/settings?ok=signup_rejected#users";

        // Ровно один из запросов победил, и база согласна с тем, кто именно: человек либо
        // одобрен и цел, либо снесён — не оба сразу и не ни одного.
        Assert.True(wonByApprove ^ wonByReject);
        if (wonByApprove) Assert.NotNull(person?.ApprovedAt);
        else Assert.Null(person);
    }

    [Fact]
    public async Task A_reader_cannot_touch_the_queue()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);
        AddPending(factory, "gost", "parol-gostya");
        var id = Users(factory).Single(row => row.Login == "gost").Id;
        var reader = await Login(factory, "ivan", "parol-ivana");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await Post(reader, "/settings/signup/open", new() { ["open"] = "on" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Post(reader, $"/settings/signup/{id}/approve", new())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Post(reader, $"/settings/signup/{id}/reject", new())).StatusCode);
        Assert.Contains(Users(factory), row => row.Login == "gost");
    }

    [Fact]
    public async Task The_queue_is_shown_to_the_owner_and_hidden_from_a_reader()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);
        AddPending(factory, "gost", "parol-gostya");
        var pendingId = Users(factory).Single(row => row.Login == "gost").Id;

        var owner = await Login(factory, "hozyain", "parol-hozyaina");
        var ownerHtml = await owner.GetStringAsync("/settings/");
        var reader = await Login(factory, "ivan", "parol-ivana");
        var readerHtml = await reader.GetStringAsync("/settings/");

        Assert.Contains($"/settings/signup/{pendingId}/approve", ownerHtml);
        Assert.Contains("/settings/signup/open", ownerHtml);
        Assert.DoesNotContain("/settings/signup", readerHtml);
        // Ждущий не человек сайта: в списке раздела «Пользователи» ему места нет, а кнопку
        // удаления рисуют только строкам того списка.
        Assert.DoesNotContain($"/settings/people/{pendingId}/delete", ownerHtml);
    }

    WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

    static void AddPerson(WebApplicationFactory<Program> factory, string login, string password,
                          UserRole role)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Users.Add(new UserRow
        {
            Login = login,
            PasswordHash = PasswordHasher.Hash(password),
            Role = role,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    static void AddPending(WebApplicationFactory<Program> factory, string login, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Users.Add(new UserRow
        {
            Login = login,
            PasswordHash = PasswordHasher.Hash(password),
            Role = UserRole.Reader,
            CreatedAt = DateTimeOffset.UtcNow,
            ApprovedAt = null,
        });
        db.SaveChanges();
    }

    /// Очередь для проверки предела: пароль тут не проверяют, поэтому хэш подставной — считать
    /// полсотни настоящих Argon2id дорого и незачем.
    static void FillTheQueue(WebApplicationFactory<Program> factory, int count, bool approved = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var now = DateTimeOffset.UtcNow;
        for (var number = 0; number < count; number++)
            db.Users.Add(new UserRow
            {
                Login = $"gost{number}",
                PasswordHash = "x",
                Role = UserRole.Reader,
                CreatedAt = now,
                ApprovedAt = approved ? now : null,
            });
        db.SaveChanges();
    }

    static List<UserRow> Users(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SamizdatDbContext>()
            .Users.AsNoTracking().ToList();
    }

    static List<InviteRow> Invites(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SamizdatDbContext>()
            .Invites.AsNoTracking().ToList();
    }

    /// Вход, который может и не удаться: неверный пароль возвращает форму с 200, верный — редирект.
    static async Task<HttpResponseMessage> TryLogin(WebApplicationFactory<Program> factory,
                                                    string login, string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return await TestLogin.PostLogin(client, login, password);
    }

    // Без автоперехода: иначе клиент сам сходит по редиректу и тест не увидит его кода.
    static async Task<HttpClient> Login(WebApplicationFactory<Program> factory, string login, string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var answer = await TestLogin.PostLogin(client, login, password);
        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        return client;
    }

    // Форма достаёт свой antiforgery-токен со страницы — сервер требует его на каждом небезопасном POST.
    static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string, string> fields)
    {
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings/"));
        fields[name] = value;
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    static (string Name, string Value) AntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]+)\">");
        if (!match.Success) throw new InvalidOperationException("Antiforgery-поле не найдено на странице");
        return (match.Groups[1].Value, match.Groups[2].Value);
    }

    /// Форма приглашения: сперва её берут, потом отправляют — antiforgery-поле и кука едут вместе.
    static async Task<FormUrlEncodedContent> InviteFields(HttpClient client, string token,
                                                          string login, string password)
    {
        var (name, value) = AntiforgeryToken(await client.GetStringAsync($"/i/{token}"));
        return new(new Dictionary<string, string>
        {
            ["login"] = login, ["password"] = password, ["repeat"] = password, [name] = value,
        });
    }

    // Столбец логина держит сто знаков; тут на один больше. Собран из частей: в InlineData
    // нужна константа, а строка в сто один знак не влезает в строку файла.
    const string TenLetters = "iiiiiiiiii";
    const string LoginOverTheLimit = TenLetters + TenLetters + TenLetters + TenLetters + TenLetters
                                     + TenLetters + TenLetters + TenLetters + TenLetters + TenLetters + "i";

    static string AddInvite(WebApplicationFactory<Program> factory, string? note,
                            DateTimeOffset? expiresAt = null, DateTimeOffset? revokedAt = null,
                            DateTimeOffset? usedAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var invite = new InviteRow
        {
            Token = ShareToken.Create(),
            Note = note,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt,
            UsedAt = usedAt,
        };
        db.Invites.Add(invite);
        db.SaveChanges();
        return invite.Token;
    }

    // Поля без antiforgery-поля — нужны и закрытой регистрации, и проверке отказа без токена.
    static FormUrlEncodedContent Fields(string login, string password)
        => new(new Dictionary<string, string>
        {
            ["login"] = login, ["password"] = password, ["repeat"] = password,
        });

    static void OpenRegistration(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteSettings>().Set("auth.open_registration", "true");
    }

    /// Открытая форма: сперва её берут, потом отправляют — antiforgery-поле и кука едут вместе.
    static async Task<FormUrlEncodedContent> RegisterFields(HttpClient client, string login, string password)
    {
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/register"));
        return new(new Dictionary<string, string>
        {
            ["login"] = login, ["password"] = password, ["repeat"] = password, [name] = value,
        });
    }
}
