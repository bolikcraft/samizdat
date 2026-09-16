#!/usr/bin/env bash
# Обновляет Samizdat на стенде: версия из git, дамп базы, сборка, ожидание старта.
# Запуск из каталога репозитория: deploy/update.sh [git-ref], по умолчанию origin/main.
set -euo pipefail

cd "$(dirname "$0")/.."

ref="${1:-origin/main}"
keep_dumps=10

if [[ ! -f .env ]]; then
    echo "Нет файла .env. Скопируйте .env.example и задайте POSTGRES_PASSWORD." >&2
    exit 1
fi
port="$(grep -E '^SAMIZDAT_PORT=' .env | cut -d= -f2 || true)"
port="${port:-8080}"

if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
    echo "В каталоге есть незакоммиченные правки. Обновление остановлено." >&2
    exit 1
fi

old="$(git rev-parse --short HEAD)"
git fetch --tags origin
git checkout --detach "$ref"
new="$(git rev-parse --short HEAD)"

mkdir -p data backups
# Сервер в образе работает от uid 1654 и сам создаёт папки статей и ключей.
docker run --rm -v "$PWD/data:/data" alpine:3 chown -R 1654:1654 /data

dump=""
if [[ -n "$(docker compose ps --status running -q db)" ]]; then
    dump="backups/samizdat-$(date +%Y%m%d-%H%M%S)-$old.dump"
    # pg_dump внутри контейнера базы: версия клиента совпадает с сервером.
    docker compose exec -T db pg_dump -U samizdat -Fc samizdat > "$dump"
    # Время в имени файла: обратная сортировка по имени ставит новые дампы первыми.
    printf '%s\n' backups/samizdat-*.dump | sort -r | tail -n +$((keep_dumps + 1)) | xargs -r rm --
fi

docker compose build app
docker compose up -d

# Kestrel начинает слушать только после миграций, поэтому 200 значит «схема обновлена».
for _ in $(seq 1 60); do
    if [[ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/login" || true)" == "200" ]]; then
        echo "Samizdat $new работает (было $old)."
        [[ -n "$dump" ]] && echo "Дамп базы до обновления: $dump"
        exit 0
    fi
    sleep 2
done

echo "Сервер не ответил за 120 секунд." >&2
docker compose logs --tail 50 app >&2
exit 1
