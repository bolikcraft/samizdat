# Samizdat

Publish your Obsidian notes as a private web site. You keep the password, and you give each
article its own link that expires.

**Status: it works. You build it from the source. There is no package yet.**

<p>
  <img src="docs/media/obsidian.gif" alt="Publish a note from Obsidian" width="49%"><img src="docs/media/demo.gif" alt="Read an article on the site and share it" width="49%">
</p>

## What it does

You write notes in Obsidian. You mark a note with `publish: true`. One command sends the note to
your own server. The server keeps the Markdown, makes the HTML page, and shows the page only to
the people you allow.

- The whole site is behind a login form.
- Each article can get a link with a time limit. You can revoke the link.
- Your notes stay yours. The server runs on your own machine.
- The server keeps the Markdown source, so you can change the theme or get the note back.

## What works now

- **Publish from the vault.** The `push` command sends the notes that have `publish: true`.
  It can remove from the server the notes that you deleted (`--prune`), and it can show the plan
  first (`--dry-run`). The `pull` command reads one note back.
- **Pages.** The server makes the HTML on request and keeps it in a cache. It shows wiki links,
  image embeds, callouts and code with colors.
- **Navigation.** The side bar shows the folder tree of your vault, with a filter by title.
- **Users.** The owner makes the accounts. An article is private, or it is open to all the users
  of the site. A reader sees the titles of the private articles, but not their descriptions or text.
- **Links for other people.** An article can get a link with a term of one day, one week, one
  month, one year, or with no term. The link opens one article to a person who has no account.
  The owner sees how many times the link was opened, and can revoke it.
- **Search.** Full text search with Russian morphology: the query `серверы` finds the word
  `сервер`. You can search for a phrase in quotes, and you can remove a word with a minus sign.
  Each result shows the words you looked for. If the search finds nothing, it looks for similar
  words, because you can make a mistake when you type.
- **Backlinks.** Each article shows the articles that mention it.
- **Look.** Two color schemes, a gallery of backgrounds, and your own picture as the background.
  The themes are files, so you can add your own directory.
- **Language.** The interface speaks English or Russian. The owner sets the language of the site,
  and each user can set a different language for their own pages. A language is a file, so you can
  add one more.

Not there yet: a ready image in a registry and the packages. See "How to run it".

## Why

Each part of this is common. The set is not:

| Tool | Static output | Editor | Login for the site | Link to one article |
|---|---|---|---|---|
| Obsidian Publish | yes | Obsidian | site only | no |
| Quartz, Perlite, Hugo | yes | Obsidian | no | no |
| Outline, Docmost, BookStack | no, app and database | their own | yes | yes |
| **Samizdat** | server-rendered | Obsidian | yes | **yes, with a time limit** |

Obsidian Publish gives one password for the full site. Users asked for per-note links since 2021.
Static site generators have no login at all. Wiki applications have the links, but you must write
in their editor.

## Design

- **Source of truth is your vault.** The server keeps a copy of the published notes. The `push`
  command replaces that copy. The server does not edit your notes, so there is no merge conflict.
- **Markdown on disk** (`data/articles/<slug>/index.md` and the attachments next to it).
  PostgreSQL keeps the metadata, the users, the links and the search index.
- **HTML is made on request** and cached. A new theme needs no rebuild.
- **The slug is the address.** The folder of the note is a separate field, so you can move a note
  between folders and the links still work.
- **Search is in the database.** PostgreSQL makes the search vectors from the title, the
  description and the text. The rights are part of the query, so a reader finds a private article
  only by its title.
- **A language is a file.** `en` and `ru` are in the program. Put a file `<code>.json` into
  `<data root>/lang/` and that language comes into the list in the settings. A file with a code
  that the program knows replaces only the lines that it contains; the other lines stay. A line
  that no file gives comes from `en`.

## Themes

A theme is a folder `<data root>/themes/<name>/`. You select it in the settings. If the theme does
not have a file, the server uses the file from the built-in theme `default`.

The theme sets how the site looks: the templates of the reader pages, the CSS, the images and the
set of backgrounds. The program gives the scripts and the settings page. A theme cannot replace them.

`layout.html` must have:

- `{{ app_head }}` in `<head>`, after the styles of the theme. It adds the script of the layout buttons.
  On the settings page, it also adds `assets/settings.css`.
- `{{ app_foot }}` before `</body>`. It adds the scripts.
- `{{ include side_nav }}` in an element with the class `sidebar`, when the page sets `side_nav`.
  The settings page puts the list of its sections there.

The scripts find these elements in the templates of the theme:

- `<main>`: the text of the page. The scripts replace its content when you open an article or send a form.
- `#nav-tree`: the article tree. A folder is `details.nav-folder` with `data-path`. An article is
  `li` in `.nav-articles`. The link to the current article has `aria-current="page"`.
- `#nav-filter`: the filter field of the tree.
- `.nav-switch`: the `<details>` that opens the side column on a narrow screen.
- `.search input[name=q]`: the search field in the header.
- `form.visibility-form`, `details.share` and `form.share-form` in it: the forms of an article.
  Put `form.visibility-form` and `details.share` directly in `<main>`. The scripts do not look for
  them deeper, because the text of an article can have elements with the same classes.
- `button.copy-link` with `data-url` and `data-copied`: the button that copies a link.
- `button#sidebar-toggle`: the button that collapses the side column. The script sets `aria-expanded` on it.
- `button#reading-toggle`: the button of the reading mode. Put it outside `<main>`, because the scripts replace
  the content of `<main>`. The script sets `aria-pressed` on it.
- `pre.mermaid` in an article: a Mermaid diagram. The server writes the text of the diagram in it. The diagram script
  draws an `<svg>` in it and sets `data-diagram="done"`, or `data-diagram="error"` if the text has an error. The theme
  gives the look of the block.

The diagram script uses Mermaid 11.17.2: the file `dist/mermaid.min.js` from the npm package `mermaid`. It is in
`app/assets/vendor/mermaid.min.js`, and its MIT license is in `app/assets/vendor/mermaid.LICENSE`. The script loads
Mermaid only on a page with a diagram.

The script of the layout buttons sets attributes on `<html>`. The theme uses them in its CSS:

- `data-controls`: the script runs. Show the layout buttons only with this attribute.
- `data-sidebar="collapsed"`: the reader collapsed the side column. The browser keeps this choice.
- `data-reading`: the reading mode is on. The article uses all the window, without the header and the side column.

The image viewer script adds `dialog.lightbox` to `<body>`. The dialog contains an `<img>`. When you click
an image in `main article`, the dialog shows this image. An image in a link does not open the dialog.
The theme gives the styles of `dialog.lightbox`.

The markup of the settings page is in `app/settings.html`. The theme gives its styles in
`assets/settings.css`.

The markup of the page `/setup` is in `app/setup.html`. It uses the classes of the login form:
`login`, `invite-note`, `error` and `login-register`.

## How to run it

### With Docker

You need Docker with the Compose plugin and git. The image is built on your server from the source code.

```bash
git clone https://github.com/bolikcraft/samizdat.git /opt/samizdat
cd /opt/samizdat
cp .env.example .env        # then set POSTGRES_PASSWORD in .env
deploy/update.sh            # builds the image and starts the site and the database

docker compose run --rm app owner set <login>   # asks for the password
docker compose run --rm app token new laptop
```

The site is at `http://<server>:8080`. To change the port or the address, set `SAMIZDAT_PORT` and
`SAMIZDAT_BIND` in `.env`. The folders `data/` (articles, keys, background) and `pgdata/`
(the database) are in the same folder as `compose.yaml`. `docker compose down -v` does not remove them.

A new database has the demo account `admin` with the password `admin`. When a person signs in with a password
of less than 8 characters, the site opens only the page `/setup`. On this page, the person sets a new password
and can change the login. Then the other pages open.

The command `owner set` asks for the password and does not show it on the screen. It also reads the password
from a pipe or a file: `docker compose run -T --rm app owner set <login> < password.txt`.
A password in the arguments also works, but the shell keeps it in its history.
The password must have 8 to 200 characters.
The command `owner set` with a different login removes the account `admin` if its password is still `admin`.
If a person with the login exists, `owner set` gives this person the new password and makes this person an owner.

The database uses `POSTGRES_PASSWORD` only when it starts for the first time with an empty `pgdata/`.
After that, do not change the password in `.env` only. First change it in the database, then in `.env`.

To update, run `deploy/update.sh` again. Before the update, the script writes a dump of the database
to `backups/` and keeps the last 10 dumps. If the database is stopped, the script starts it for the dump.
Then the script gets the new version, builds the image and waits until the site starts (at most 5 minutes).
Without an argument, the script updates to `origin/main`. To go to another version, give the script
a git ref — a commit or a tag: `deploy/update.sh <ref>`. If the update fails, the script shows
the command that goes back to the previous version.
The database changes of each version only add data, so an earlier version works with the new database.

The dump contains only the database. It does not contain `data/` (articles, keys, background).
For a full copy, also copy the folder `data/`, or make a snapshot of the machine.

To restore the database from a dump:

```bash
docker compose stop app
# pg_restore --clean does not remove tables that are not in the dump, so remove all tables first.
docker compose exec -T db psql -U samizdat -d samizdat -c 'DROP SCHEMA public CASCADE; CREATE SCHEMA public;'
docker compose exec -T db pg_restore -U samizdat -d samizdat < backups/<file>.dump
docker compose start app
```

Behind a reverse proxy with HTTPS, the proxy must send the header `X-Forwarded-Proto: https`.
Without it, the login cookie does not get the `Secure` flag.

The server limits the attempts to sign in and to register. The default limit is 10 attempts in one minute
from one IP address. When a client sends more attempts, the server sends the status 429.
To change the limit, set `SAMIZDAT_LOGIN_ATTEMPTS` in `.env`. The value `0` removes the limit.
The server does not use the header `X-Forwarded-For`. Behind a reverse proxy, all clients have the address
of the proxy and share one limit.
Each password hash uses 64 MB of memory. The server calculates at most 2 hashes at the same time
(`Samizdat__Auth__ParallelHashes`) and keeps at most 32 more requests in a queue (`Samizdat__Auth__HashQueue`).
When the queue is full, the server sends the status 429.

### From the source code

You need the .NET 10 SDK and a PostgreSQL server (version 14 or later).

```bash
# 1. Give the server the address of the database (or use the Postgres connection string
#    in the configuration file appsettings.xml).
dotnet user-secrets --project src/Samizdat.Server \
  set "ConnectionStrings:Postgres" "Host=localhost;Database=samizdat;Username=samizdat;Password=..."

# 2. Start the server. It makes the tables itself.
dotnet run --project src/Samizdat.Server --no-launch-profile -- \
  --urls http://127.0.0.1:5080 --Samizdat:DataRoot=/path/to/data

# 3. Set the owner and make a token for the command line tool.
dotnet run --project src/Samizdat.Server --no-launch-profile -- owner set <login>
dotnet run --project src/Samizdat.Server --no-launch-profile -- token new laptop

# 4. Tell the tool where the server and the vault are, then publish.
dotnet run --project src/Samizdat.Cli -- login
dotnet run --project src/Samizdat.Cli -- push --dry-run
dotnet run --project src/Samizdat.Cli -- push
```

The command `reindex` makes the search index again for all the articles. You need it only after
an update that changes how the text is read.

You can also publish from Obsidian itself: the plugin is in the repository
[samizdat-obsidian](https://github.com/bolikcraft/samizdat-obsidian). It works on a phone, and the
command line tool does not.

## Stack

.NET 10 (ASP.NET Core Minimal API), EF Core with Npgsql, PostgreSQL, Markdig for Markdown,
Scriban for the themes, a command line tool for `push` and `pull`.

## License

MIT
