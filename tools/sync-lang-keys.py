#!/usr/bin/env python3
"""Переносит набор ключей lang/en.json во все пакеты.

Новый ключ получает английский текст, лишний ключ удаляется, порядок как в en.json.
Настоящий перевод делается отдельно.
"""
import json
import pathlib

root = pathlib.Path(__file__).resolve().parent.parent / "lang"
english = json.loads((root / "en.json").read_text(encoding="utf-8"))

for path in sorted(root.glob("*.json")):
    if path.name == "en.json":
        continue
    pack = json.loads(path.read_text(encoding="utf-8"))
    synced = {key: pack.get(key, text) for key, text in english.items()}
    path.write_text(json.dumps(synced, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
