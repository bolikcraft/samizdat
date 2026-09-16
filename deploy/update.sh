#!/usr/bin/env bash
# Обновляет Samizdat на стенде: дамп базы, версия из git, сборка, ожидание старта.
# Запуск из каталога репозитория: deploy/update.sh [git-ref], по умолчанию origin/main.
set -euo pipefail
# Дампы содержат хэши паролей и токенов.
umask 077

cd "$(dirname "$0")/.."

ref="${1:-origin/main}"
keep_dumps=10
wait_seconds=300

for tool in curl git docker; do
    if ! command -v "$tool" > /dev/null; then
        echo "Не найдена программа $tool. Обновление остановлено." >&2
        exit 1
    fi
done

if [[ ! -f .env ]]; then
    echo "Нет файла .env. Скопируйте .env.example и задайте POSTGRES_PASSWORD." >&2
    exit 1
fi

# Значение из .env без кавычек, пробелов и \r (файл могли править в Windows).
env_value() {
    grep -E "^[[:space:]]*$1=" .env | tail -n 1 | cut -d= -f2- | tr -d "\"' \t\r" || true
}
port="$(env_value SAMIZDAT_PORT)"
port="${port:-8080}"
host="$(env_value SAMIZDAT_BIND)"
if [[ -z "$host" || "$host" == "0.0.0.0" ]]; then
    host="127.0.0.1"
fi

if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
    echo "В каталоге есть незакоммиченные правки. Обновление остановлено." >&2
    exit 1
fi

old="$(git rev-parse --short HEAD)"
db_image="$(docker compose config --images db)"
part=""
checked_out=""

# shellcheck disable=SC2329 # вызывается из trap
on_exit() {
    local status=$?
    if [[ -n "$part" ]]; then
        rm -f -- "$part"
    fi
    if [[ $status -ne 0 && -n "$checked_out" ]]; then
        echo "Обновление не удалось. Вернуть прежнюю версию: deploy/update.sh $old" >&2
    fi
}
trap on_exit EXIT

git fetch --tags origin

mkdir -p data backups

dump=""
# pgdata принадлежит пользователю postgres из контейнера, с хоста его не прочитать.
if [[ -d pgdata ]] && docker run --rm -v "$PWD/pgdata:/pgdata:ro" "$db_image" test -s /pgdata/PG_VERSION; then
    if [[ -z "$(docker compose ps --status running -q db)" ]]; then
        docker compose up -d --wait db
    fi
    dump="backups/samizdat-$(date +%Y%m%d-%H%M%S)-$old.dump"
    part="$dump.part"
    # pg_dump внутри контейнера базы: версия клиента совпадает с сервером.
    docker compose exec -T db pg_dump -U samizdat -Fc samizdat > "$part"
    # Архив читается до конца. pg_restore -l читает только оглавление и обрезанный файл пропускает.
    docker compose exec -T db pg_restore -f /dev/null < "$part"
    mv -- "$part" "$dump"
    part=""
    # Время в имени файла: обратная сортировка по имени ставит новые дампы первыми.
    printf '%s\n' backups/samizdat-*.dump | sort -r | tail -n +$((keep_dumps + 1)) | xargs -r rm --
else
    echo "База ещё не создана, дамп не нужен."
fi

checked_out=1
git checkout --detach "$ref"
new="$(git rev-parse --short HEAD)"

# Сервер в образе работает от uid 1654 и сам создаёт папки статей и ключей.
# Образ базы compose скачивает всё равно, лишний образ не нужен. chown только там, где владелец другой.
docker run --rm -v "$PWD/data:/data" "$db_image" find /data ! -user 1654 -exec chown -h 1654:1654 {} +

docker compose build app
docker compose up -d

# Kestrel слушает только после миграций и достройки поискового индекса, поэтому 200 значит «сервер готов».
deadline=$((SECONDS + wait_seconds))
while ((SECONDS < deadline)); do
    if [[ "$(curl -s -m 5 -o /dev/null -w '%{http_code}' "http://$host:$port/login" || true)" == "200" ]]; then
        echo "Samizdat $new работает (было $old)."
        if [[ -n "$dump" ]]; then
            echo "Дамп базы до обновления: $dump"
        fi
        exit 0
    fi
    sleep 2
done

echo "Сервер не ответил за $wait_seconds секунд." >&2
docker compose logs --tail 50 app >&2
exit 1
