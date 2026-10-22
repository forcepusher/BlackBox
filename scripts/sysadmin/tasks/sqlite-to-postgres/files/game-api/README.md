# game-api

The backend of our game: accounts, scores and the leaderboard, cloud saves and friends lists.
The game clients call it over HTTP; nginx passes `/api/` and `/health` on to it at 127.0.0.1:8080.

## Running

    python3 /opt/game-api/app.py            # the service game-api, as the user game
    python3 /opt/game-api/app.py init-db    # only creates the tables that are missing

Settings come from the environment (the service reads `/etc/game-api/env`):

- `DATABASE_URL`: which database to use.
  - `sqlite:////var/lib/game-api/game.db`: a SQLite file
  - `postgresql://user:password@host:5432/dbname`: PostgreSQL; needs `python3-psycopg2`
- `PORT`: 8080 unless set.

On start the API creates the tables that are missing in that database, with the column types of
the engine it talks to; the server code is the same for both.

## API

| Request | Body | Answer |
| --- | --- | --- |
| `GET /health` | | `{"ok": true}` once the database answers |
| `POST /api/players` | `{"name": "...", "country": "DE", "avatar": "<base64>"}` | `201 {"id": 123}` |
| `GET /api/players/{id}` | | the player, best score, number of games |
| `GET /api/players/{id}/scores` | | the last 10 scores |
| `POST /api/scores` | `{"player_id": 123, "score": 4200, "level": "3-1"}` | `201 {"id": 456}` |
| `GET /api/leaderboard` | | the 20 best players (banned ones left out) |
| `PUT /api/saves/{player_id}` | the save, as JSON | `200`; stored as sent |
| `GET /api/saves/{player_id}` | | the save exactly as the client sent it |
| `POST /api/friends` | `{"player_id": 123, "friend_id": 77}` | `201` |
| `GET /api/friends/{player_id}` | | the friends' ids |

Clients check their cloud save byte for byte against their local copy, so a save must come back
exactly as it was sent.
