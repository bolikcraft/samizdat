-- Роль приложения samizdat_app: владелец базы без прав суперпользователя. Повторный запуск безопасен.
-- Пароль приходит в переменной окружения SAMIZDAT_DB_PASSWORD: аргументы psql видно в ps.
\set ON_ERROR_STOP on
\getenv app_password SAMIZDAT_DB_PASSWORD

-- psql не подставляет переменные внутри DO $$...$$, поэтому условие сделано через \gexec.
SELECT 'CREATE ROLE samizdat_app LOGIN'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'samizdat_app')
\gexec

-- Пароль ставится на каждом запуске: его могли сменить в .env.
ALTER ROLE samizdat_app WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD :'app_password';

SELECT format('ALTER DATABASE %I OWNER TO samizdat_app', current_database())
\gexec

-- После восстановления дампа по старой инструкции README схемой владеет суперпользователь,
-- и владелец базы не может создать в ней таблицу.
ALTER SCHEMA public OWNER TO pg_database_owner;

-- Последовательность колонки (serial, identity) переходит вместе с таблицей, свой ALTER для неё — ошибка.
-- Объекты расширений (pg_trgm) остаются у того, кто создал расширение.
SELECT format('ALTER %s %I.%I OWNER TO samizdat_app',
              CASE c.relkind WHEN 'v' THEN 'VIEW' WHEN 'm' THEN 'MATERIALIZED VIEW'
                             WHEN 'S' THEN 'SEQUENCE' ELSE 'TABLE' END,
              n.nspname, c.relname)
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public'
  AND c.relkind IN ('r', 'p', 'v', 'm', 'S')
  AND c.relowner <> 'samizdat_app'::regrole
  AND NOT EXISTS (SELECT FROM pg_depend d
                  WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid
                    AND (d.deptype = 'e' OR (c.relkind = 'S' AND d.deptype IN ('a', 'i'))))
\gexec

-- Collation case_insensitive из миграции CaseInsensitiveLogin.
SELECT format('ALTER COLLATION %I.%I OWNER TO samizdat_app', n.nspname, c.collname)
FROM pg_collation c
JOIN pg_namespace n ON n.oid = c.collnamespace
WHERE n.nspname = 'public'
  AND c.collowner <> 'samizdat_app'::regrole
  AND NOT EXISTS (SELECT FROM pg_depend d
                  WHERE d.classid = 'pg_collation'::regclass AND d.objid = c.oid AND d.deptype = 'e')
\gexec
