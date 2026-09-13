# Samizdat

Publish your Obsidian notes as a private web site. You keep the password, and you give each
article its own link that expires.

**Status: design stage. There is no code yet.**

## What it does

You write notes in Obsidian. You mark a note with `publish: true`. One command sends the note to
your own server. The server keeps the Markdown, makes the HTML page, and shows the page only to
the people you allow.

- The whole site is behind a login form.
- Each article can get a public link with a time limit. You can revoke the link.
- Your notes stay yours. The server runs on your machine, in Docker or in a container.
- The server keeps the Markdown source, so you can change the theme or get the note back.

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

## Planned design

- **Source of truth is your vault.** The server keeps a copy of the published notes. The `push`
  command replaces that copy. The server does not edit your notes, so there is no merge conflict.
- **Markdown on disk** (`data/articles/<slug>/index.md` and the attachments next to it).
  PostgreSQL keeps the metadata, the users, the rights, the links and the search index.
- **HTML is made on request** and cached. A new theme needs no rebuild.
- **Themes are files.** The package has some themes. You can add your own directory.
- **Roles:** owner, authors, readers. Each article has a visibility level, and you can give access
  to a person or to a group.
- **Search** uses PostgreSQL full text search with the Russian and English dictionaries.

## Planned stack

.NET 10 (ASP.NET Core Minimal API), EF Core with Npgsql, PostgreSQL, Markdig for Markdown,
Scriban for the themes, a command line tool for `push` and `pull`. Docker Compose for the
installation, or one self-contained binary.

## License

MIT
