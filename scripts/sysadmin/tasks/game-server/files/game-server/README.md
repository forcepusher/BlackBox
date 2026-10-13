# Tiny Arena server

The multiplayer server for Tiny Arena. Every player's browser keeps one WebSocket open to it.

Needs Node.js 20 or newer.

    npm ci --omit=dev
    node server.js

Settings, as environment variables:

- `PORT`: the port to listen on (default 3000)
- `HOST`: the address to listen on (default 0.0.0.0, all of them)

`GET /health` answers `{"status":"ok"}` while the server is up. WebSocket connections are
accepted on any path. The server keeps no files; restarting it just empties the arena.
