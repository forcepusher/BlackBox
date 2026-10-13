'use strict';
// Tiny Arena: the multiplayer server for our browser game. Players join, walk around a
// 20x20 arena and see each other move, all over one WebSocket per player.
const http = require('http');
const { WebSocketServer } = require('ws');

const major = Number(process.versions.node.split('.')[0]);
if (major < 20) {
  console.error(`tiny-arena needs Node.js 20 or newer, this is ${process.version}`);
  process.exit(1);
}

const PORT = Number(process.env.PORT || 3000);
const HOST = process.env.HOST || '0.0.0.0';
const SIZE = 20;

const players = new Map(); // id -> { name, x, y, ws }
let nextId = 1;

const server = http.createServer((req, res) => {
  if (req.url === '/health') {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ status: 'ok', players: players.size }));
    return;
  }
  res.writeHead(404, { 'content-type': 'text/plain' });
  res.end('Tiny Arena speaks WebSocket only\n');
});

// WebSocket connections are accepted on any path, so a proxy can mount it wherever it likes.
const wss = new WebSocketServer({ server });

const roster = () => [...players.entries()].map(([id, p]) => ({ id, name: p.name, x: p.x, y: p.y }));

function broadcast(msg) {
  const text = JSON.stringify(msg);
  for (const p of players.values()) if (p.ws.readyState === 1) p.ws.send(text);
}

wss.on('connection', (ws) => {
  let id = null;
  ws.on('message', (data) => {
    let msg;
    try { msg = JSON.parse(data); } catch { ws.send(JSON.stringify({ type: 'error', error: 'bad json' })); return; }
    if (msg.type === 'join' && id === null) {
      id = 'p' + nextId++;
      players.set(id, {
        name: String(msg.name || id).slice(0, 20),
        x: Math.floor(Math.random() * SIZE),
        y: Math.floor(Math.random() * SIZE),
        ws,
      });
      ws.send(JSON.stringify({ type: 'welcome', id, size: SIZE, players: roster() }));
      broadcast({ type: 'players', players: roster() });
    } else if (msg.type === 'move' && id !== null) {
      const p = players.get(id);
      p.x = Math.max(0, Math.min(SIZE - 1, p.x + Math.sign(Number(msg.dx) || 0)));
      p.y = Math.max(0, Math.min(SIZE - 1, p.y + Math.sign(Number(msg.dy) || 0)));
      broadcast({ type: 'moved', id, x: p.x, y: p.y });
    } else if (msg.type === 'ping') {
      ws.send(JSON.stringify({ type: 'pong', t: msg.t }));
    }
  });
  ws.on('close', () => {
    if (id !== null && players.delete(id)) broadcast({ type: 'players', players: roster() });
  });
});

server.listen(PORT, HOST, () => console.log(`tiny-arena listening on ${HOST}:${PORT}`));
