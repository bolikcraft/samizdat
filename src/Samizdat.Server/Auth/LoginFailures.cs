using Samizdat.Server.Endpoints;

namespace Samizdat.Server.Auth;

/// Неудачные входы по каждому логину за скользящее окно. Предел по адресу не спасает от перебора
/// с тысяч адресов, этот считает попытки на саму учётку.
// Счётчики в памяти процесса: после перезапуска окно начинается заново, это допустимо.
public sealed class LoginFailures(int limit, TimeProvider clock, int maxLogins = 10_000)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    sealed class Entry(string login)
    {
        public readonly LinkedListNode<string> Node = new(login);
        public readonly Queue<DateTimeOffset> Times = new();
    }

    readonly Lock sync = new();
    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    // Логины по времени последней неудачи. Полная таблица вытесняет первый: перебор случайных
    // логинов не съест память.
    readonly LinkedList<string> order = new();

    public bool IsBlocked(string login)
    {
        if (limit <= 0) return false;
        lock (sync)
        {
            if (!entries.TryGetValue(login, out var entry)) return false;
            DropExpired(entry);
            return entry.Times.Count >= limit;
        }
    }

    public void Add(string login)
    {
        // Логин длиннее столбца в базе не найдёт учётку, хранить его незачем.
        if (limit <= 0 || login.Length > SettingsPage.MaxLoginLength) return;
        lock (sync)
        {
            if (entries.TryGetValue(login, out var entry))
                order.Remove(entry.Node);
            else
                entries[login] = entry = new Entry(login);
            order.AddLast(entry.Node);

            entry.Times.Enqueue(clock.GetUtcNow());
            if (entry.Times.Count > limit) entry.Times.Dequeue();

            if (entries.Count > maxLogins)
            {
                entries.Remove(order.First!.Value);
                order.RemoveFirst();
            }
        }
    }

    void DropExpired(Entry entry)
    {
        var since = clock.GetUtcNow() - Window;
        while (entry.Times.TryPeek(out var time) && time <= since) entry.Times.Dequeue();
        if (entry.Times.Count > 0) return;

        order.Remove(entry.Node);
        entries.Remove(entry.Node.Value);
    }
}
