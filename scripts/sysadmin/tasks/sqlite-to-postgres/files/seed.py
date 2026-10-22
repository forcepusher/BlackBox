#!/usr/bin/env python3
"""The game's data for the sqlite-to-postgres ticket, made the same way every time from the run's
token: setup.sh fills the SQLite file with it ("seed.py create DB TOKEN", after the app made its
tables), check.sh makes it again ("import seed; seed.rows(TOKEN)") to compare with PostgreSQL.

What a real game database of two and a half years holds: deleted accounts (gaps in the ids, and
AUTOINCREMENT counters past the last id), names in every script with emoji, quotes, backslashes and
"NULL", NULL next to empty strings, avatars as BLOBs (one empty), 0/1 booleans, text timestamps,
scores past 32 bits, and cloud saves up to about 30 KB of JSON text written in three styles
(compact, ASCII-escaped, indented with CRLF), which the API hands back byte for byte.
Players 1 to 20 are kept as they are: the live traffic leaves them alone, the checks read them.
"""
import base64
import json
import random
import sqlite3
import sys
from datetime import datetime, timedelta

PLAYERS, PLAYER_SEQ = 3000, 3010   # ids 1..3010 were handed out; 3001..3010 deleted again
SCORES, SCORE_SEQ = 42000, 42025
FRIENDSHIPS = 6000
START = datetime(2024, 3, 1)
SPAN = (datetime(2026, 10, 1) - START).total_seconds()

FIRST = ["Ada", "Linus", "Grace", "Zo\u00eb", "\u00d1and\u00fa", "S\u00f8ren", "M\u00e1ria", "\u0418\u0432\u0430\u043d",
         "\u041c\u0430\u0440\u0438\u044f", "\u674e\u5c0f\u9f99", "\u3055\u304f\u3089", "\uae40\ubbfc\uc900",
         "\u0645\u062d\u0645\u062f", "\u05d3\u05e0\u05d4", "\u0391\u03bb\u03ad\u03be\u03b7\u03c2", "\u0110\u1ee9c",
         "\u0141ukasz", "Jos\u00e9", "Fran\u00e7ois", "Nguy\u1ec5n", "O'Brien", "D'Angelo", "Smith, John",
         'The "Boss"', "back\\slash", "\\o/ yay", "NULL", "null", "  spaced  ", "Robert'); DROP TABLE players;--",
         "x\u0301y\u0327", "\u202eevil", "zero\u200bwidth", "xX_sniper_Xx", "kevin", "Mo", "a" * 40]
EMOJI = ["\U0001f409", "\U0001f525", "\U0001f47e", "\U0001f3ae", "\U0001f480", "\U0001f984", "\u2694\ufe0f",
         "\U0001f3c6", "\U0001f469\u200d\U0001f4bb", "\U0001f1ef\U0001f1f5", "\u2764\ufe0f", "\U0001f600"]
COUNTRIES = ["US", "DE", "FR", "JP", "KR", "BR", "PL", "UA", "IN", "CN", "GB", "SE", "NO", "ES", "IT", "IL", "EG", "VN", "GR", "MX"]
ITEMS = ["sword", "shield", "potion", "Schwert", "\u00e9p\u00e9e", "\u0449\u0438\u0442", "\u9b54\u6cd5\u306e\u6756",
         "\U0001f5e1\ufe0f dagger", "\U0001f344 mushroom", 'key "gold"', "map\\old", "rope; 10m", "it's a trap"]
NOTES = ["", "gg", 'don\'t forget the "secret" door', "C:\\Users\\me\\Desktop\\run.txt", "line1\nline2\r\nline3",
         "tab\tseparated", "\U0001f642\U0001f643 emoji ok", "\u041f\u0440\u0438\u0432\u0435\u0442, \u043c\u0438\u0440",
         "\u0645\u0631\u062d\u0628\u0627", "e\u0301 combining", "nul \x00 byte", "\\N", "NULL", "50% done", "a,b,c"]


def deleted(pid):
    """Accounts deleted over the years, with their scores, saves and friendships."""
    return pid > PLAYERS or (pid > 20 and (pid * 7) % 13 == 5)


def stamp(t):
    return t.strftime("%Y-%m-%d %H:%M:%S")


def avatar_bytes(r, size):
    return b"\x89PNG\r\n\x1a\n" + r.randbytes(size)


def save_text(r, name, big=False, style=None):
    doc = {
        "version": 3,
        "player": name,
        "level": "%d-%d" % (r.randint(1, 8), r.randint(1, 4)),
        "coins": r.randint(0, 10 ** 6),
        "inventory": [{"item": r.choice(ITEMS), "qty": r.randint(1, 99)} for _ in range(r.randint(0, 12))],
        "settings": {"music": r.random() < 0.5, "volume": round(r.random(), 2), "lang": r.choice(["en", "de", "ja", "ar"]),
                     "keys": {"jump": "space", "path": "C:\\Games\\Lab\\save.dat"}},
        "notes": r.choice(NOTES),
        "last_seen": None,
    }
    if big or r.random() < 0.06:
        doc["map"] = base64.b64encode(r.randbytes(21000 if big else r.randint(2000, 15000))).decode()
        doc["journal"] = [r.choice(NOTES) + " " + r.choice(EMOJI) for _ in range(120 if big else 20)]
    style = style or r.choices(["compact", "ascii", "pretty"], [5, 3, 2])[0]
    if style == "compact":
        return json.dumps(doc, ensure_ascii=False, separators=(",", ":"))
    if style == "ascii":
        return json.dumps(doc)
    return json.dumps(doc, ensure_ascii=False, indent=2).replace("\n", "\r\n")


def rows(token):
    """Every row, as SQLite holds it: {table: [tuple, ...]} in the app's column order."""
    r = random.Random("game-api-seed-" + token)
    players, saves = [], []
    created = {}
    special = {
        1: ("Ada \U0001f409 " + token, "GB", None, 0),
        2: ('Zo\u00eb "Zed" O\'Brien \\ ' + token, "FR", avatar_bytes(r, 3000), 0),
        3: ("\u674e\u5c0f\u9f99 \u2694\ufe0f " + token, None, avatar_bytes(r, 700), 0),
        4: ("NULL", "", b"", 0),
        5: ("  spaced " + token + "  ", None, None, 1),
    }
    for pid in range(1, PLAYER_SEQ + 1):
        t = START + timedelta(seconds=SPAN * (pid - 1) / PLAYER_SEQ + r.randint(0, 3000))
        if pid in special:
            name, country, avatar, banned = special[pid]
        else:
            name = r.choice(FIRST)
            if r.random() < 0.3:
                name += " " + r.choice(EMOJI)
            if r.random() < 0.6:
                name += str(r.randint(1, 9999))
            x = r.random()
            country = None if x < 0.25 else "" if x < 0.28 else r.choice(COUNTRIES)
            avatar = avatar_bytes(r, r.randint(64, 4096)) if r.random() < 0.15 else None
            banned = 1 if r.random() < 0.03 else 0
        if deleted(pid):
            continue
        created[pid] = t
        players.append((pid, name, country, avatar, banned, stamp(t)))
        if pid in (1, 3, 5) or (pid > 20 and r.random() < 0.55):
            big = pid == 3
            style = {1: "compact", 3: "ascii", 5: "pretty"}.get(pid)
            text = save_text(r, name, big=big, style=style)
            saves.append((pid, text, stamp(t + timedelta(seconds=r.randint(60, 86400 * 30)))))
    alive = sorted(created)

    scores = []
    for sid in range(1, SCORE_SEQ + 1):
        t = START + timedelta(seconds=SPAN * (sid - 1) / SCORE_SEQ + r.randint(0, 600))
        bound = max(30, int(PLAYER_SEQ * (sid / SCORE_SEQ)))
        pid = r.randint(1, bound)
        level = None if r.random() < 0.1 else "endless" if r.random() < 0.01 else "%d-%d" % (r.randint(1, 8), r.randint(1, 4))
        score = r.randint(3 * 10 ** 9, 9 * 10 ** 9) if level == "endless" else r.randint(0, 250000)
        if sid > SCORES or deleted(pid) or r.random() < 0.005:
            continue
        scores.append((sid, pid, score, level, stamp(max(t, created[pid] + timedelta(seconds=30)))))

    pairs = set()
    friendships = []
    while len(friendships) < FRIENDSHIPS:
        a, b = r.choice(alive), r.choice(alive)
        if a == b or (a, b) in pairs:
            continue
        pairs.add((a, b))
        t = max(created[a], created[b]) + timedelta(seconds=r.randint(60, 86400 * 60))
        friendships.append((a, b, stamp(t)))
    friendships.sort()
    return {"players": players, "scores": scores, "saves": saves, "friendships": friendships}


COLUMNS = {
    "players": ("id", "name", "country", "avatar", "is_banned", "created_at"),
    "scores": ("id", "player_id", "score", "level", "created_at"),
    "saves": ("player_id", "data", "updated_at"),
    "friendships": ("player_id", "friend_id", "created_at"),
}


def create(path, token):
    data = rows(token)
    conn = sqlite3.connect(path)
    conn.execute("PRAGMA journal_mode = WAL")
    with conn:
        for table, cols in COLUMNS.items():
            conn.executemany("INSERT INTO %s (%s) VALUES (%s)" % (table, ", ".join(cols), ", ".join("?" * len(cols))), data[table])
        conn.execute("UPDATE sqlite_sequence SET seq = ? WHERE name = 'players'", (PLAYER_SEQ,))
        conn.execute("UPDATE sqlite_sequence SET seq = ? WHERE name = 'scores'", (SCORE_SEQ,))
    conn.close()
    return {t: len(v) for t, v in data.items()}


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "create":
        print(create(sys.argv[2], sys.argv[3]))
    else:
        sys.exit("usage: seed.py create DB TOKEN")
