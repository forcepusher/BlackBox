// Plays a 2048 page in headless Edge and checks it against the rules, so a run of
// game2048-test.ps1 can be judged without a person. The board is read from the page's HTML
// (the task asks for HTML elements, not a canvas): every move is replayed with the real rules
// and the page must show the same board plus exactly one new 2 or 4, nothing new when the move
// changes nothing, and a score that grows by the merged values. It also checks the game ends
// and R starts a new one, and that the tiles sit inside the board. Screenshots of each stage go
// next to the report.
//
// Usage: node scripts/game2048-check.mjs <path to 2048.html> <output folder>
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync, rmSync } from "node:fs";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

const [page, outDir] = process.argv.slice(2).map(p => p && resolve(p));
if (!page || !outDir) { console.error("Usage: node game2048-check.mjs <2048.html> <output folder>"); process.exit(2); }
mkdirSync(outDir, { recursive: true });

const report = { page, errors: [], problems: {}, moves: [], checks: {} };
const problem = (kind, text) => (report.problems[kind] ??= []).length < 5 && report.problems[kind].push(text);
const finish = () => {
  writeFileSync(join(outDir, "check.json"), JSON.stringify(report, null, 2));
  for (const [name, ok] of Object.entries(report.checks)) console.log(`${ok ? "PASS" : "FAIL"} ${name}`);
  for (const p of Object.values(report.problems).flat()) console.log("  problem: " + p);
  for (const e of report.errors.slice(0, 10)) console.log("  error: " + e);
  process.exit(Object.values(report.checks).every(Boolean) ? 0 : 1);
};
report.checks["game file exists"] = existsSync(page);
if (!report.checks["game file exists"]) finish();

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"]
  .find(existsSync);
if (!edge) { console.error("Microsoft Edge not found"); process.exit(2); }

const profile = join(outDir, "edge-profile");
const browser = spawn(edge, ["--headless=new", "--remote-debugging-port=0", `--user-data-dir=${profile}`,
  "--window-size=1000,900", "--no-first-run", "--disable-extensions", "about:blank"], { stdio: "ignore" });
const sleep = ms => new Promise(r => setTimeout(r, ms));

// Tiles are leaf elements showing a power of two, outside anything named like a score. The board
// is the first ancestor of a tile that is at least 2.5 tiles wide and tall; a tile's cell is
// where its center falls on a 4x4 split of the board.
const readState = `(() => {
  // Not tiles: the score, and titles like the big "2048" heading.
  const named = el => { for (; el; el = el.parentElement) if (/^H[1-6]$|^HEADER$|^TITLE$/.test(el.tagName) || /score|best|title|header|logo|heading/i.test((el.id || "") + " " + (el.className || ""))) return true; return false; };
  const tiles = [];
  for (const el of document.body.querySelectorAll("*")) {
    if (el.children.length || named(el)) continue;
    const t = el.textContent.trim();
    if (/^\\d+$/.test(t) && +t >= 2 && (+t & (+t - 1)) === 0 && el.getClientRects().length) tiles.push(el);
  }
  let board = null;
  if (tiles.length) {
    let el = tiles[0], unit = null;
    for (; el; el = el.parentElement) {
      const r = el.getBoundingClientRect();
      if (!unit) { if (r.width >= 16 && r.height >= 16) unit = r; continue; }
      if (r.width >= unit.width * 2.5 && r.height >= unit.height * 2.5) {
        // A block element can be wider than the grid it holds; its children (cells, rows or
        // tiles) cover exactly the grid.
        const kids = [...el.children].map(k => k.getBoundingClientRect()).filter(k => k.width && k.height);
        if (kids.length < 4) { board = r; break; }
        const x = Math.min(...kids.map(k => k.x)), y = Math.min(...kids.map(k => k.y));
        board = { x, y, width: Math.max(...kids.map(k => k.right)) - x, height: Math.max(...kids.map(k => k.bottom)) - y };
        break;
      }
    }
  }
  const grid = Array(16).fill(0);
  let clash = false;
  if (board) for (const t of tiles) {
    const r = t.getBoundingClientRect();
    const col = Math.min(3, Math.max(0, Math.floor((r.x + r.width / 2 - board.x) / (board.width / 4))));
    const row = Math.min(3, Math.max(0, Math.floor((r.y + r.height / 2 - board.y) / (board.height / 4))));
    if (grid[row * 4 + col]) clash = true;
    grid[row * 4 + col] = +t.textContent.trim();
  }
  let score = null;
  for (const el of document.body.querySelectorAll("[id*=score i],[class*=score i]")) {
    if (/best/i.test((el.id || "") + " " + (el.className || ""))) continue;
    const m = el.textContent.match(/\\d+/);
    if (m) { score = +m[0]; break; }
  }
  // Tiles must sit inside the board's own box (a common bug: the last column sticks out).
  let outside = 0;
  const boardBox = tiles.length && board ? (() => { let el = tiles[0]; for (; el; el = el.parentElement) { const r = el.getBoundingClientRect(); if (r.width >= board.width - 1 && r.height >= board.height - 1) return r; } return null; })() : null;
  if (boardBox) for (const t of tiles) {
    let el = t;
    while (el.parentElement && el.parentElement.getBoundingClientRect().width < boardBox.width * 0.5) el = el.parentElement;
    const r = el.getBoundingClientRect();
    if (r.left < boardBox.left - 1 || r.right > boardBox.right + 1 || r.top < boardBox.top - 1 || r.bottom > boardBox.bottom + 1) outside++;
  }
  return { grid, tiles: tiles.length, clash, outside, score, text: document.body.innerText.slice(0, 300) };
})()`;

// The real rules: slide toward the side, merge equal neighbours once per move, score the merges.
const directions = { ArrowLeft: [0, 1], ArrowRight: [0, -1], ArrowUp: [1, 1], ArrowDown: [1, -1] };
function slide(grid, key) {
  const [vertical, step] = directions[key];
  const out = Array(16).fill(0);
  let gained = 0;
  for (let k = 0; k < 4; k++) {
    const cells = [0, 1, 2, 3].map(j => (step > 0 ? j : 3 - j)).map(j => (vertical ? j * 4 + k : k * 4 + j));
    const values = cells.map(i => grid[i]).filter(Boolean);
    const merged = [];
    for (let i = 0; i < values.length; i++) {
      if (values[i] === values[i + 1]) { merged.push(values[i] * 2); gained += values[i] * 2; i++; }
      else merged.push(values[i]);
    }
    cells.forEach((c, j) => (out[c] = merged[j] ?? 0));
  }
  return { grid: out, gained };
}
const show = g => [0, 4, 8, 12].map(i => g.slice(i, i + 4).join(" ")).join(" / ");
const canMove = g => Object.keys(directions).some(k => slide(g, k).grid.join() !== g.join());

try {
  let port;
  for (let i = 0; i < 100 && !port; i++) {
    await sleep(100);
    try { port = readFileSync(join(profile, "DevToolsActivePort"), "utf8").split("\n")[0].trim(); } catch { }
  }
  const target = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  const ws = new WebSocket(target.find(t => t.type === "page").webSocketDebuggerUrl);
  await new Promise((ok, fail) => { ws.onopen = ok; ws.onerror = fail; });

  let nextId = 0;
  const pending = new Map();
  ws.onmessage = ev => {
    const msg = JSON.parse(ev.data);
    if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg.result ?? {}); pending.delete(msg.id); }
    if (msg.method === "Runtime.exceptionThrown")
      report.errors.push(msg.params.exceptionDetails.exception?.description ?? msg.params.exceptionDetails.text);
    if (msg.method === "Runtime.consoleAPICalled" && msg.params.type === "error")
      report.errors.push(msg.params.args.map(a => a.value ?? a.description).join(" "));
  };
  const send = (method, params = {}) => new Promise(ok => { const id = ++nextId; pending.set(id, ok); ws.send(JSON.stringify({ id, method, params })); });
  const codes = { ArrowLeft: 37, ArrowUp: 38, ArrowRight: 39, ArrowDown: 40, r: 82 };
  const press = async key => {
    const base = { key, code: key === "r" ? "KeyR" : key, windowsVirtualKeyCode: codes[key], nativeVirtualKeyCode: codes[key] };
    await send("Input.dispatchKeyEvent", { type: "keyDown", ...base, text: key === "r" ? "r" : undefined });
    await send("Input.dispatchKeyEvent", { type: "keyUp", ...base });
    await sleep(300); // let slide and spawn animations finish
  };
  const state = async () => (await send("Runtime.evaluate", { expression: readState, returnByValue: true })).result?.value;
  const shot = async name => {
    const { data } = await send("Page.captureScreenshot", { format: "png" });
    writeFileSync(join(outDir, name + ".png"), Buffer.from(data, "base64"));
  };

  await send("Runtime.enable");
  await send("Page.enable");
  await send("Page.navigate", { url: pathToFileURL(page).href });
  await sleep(1500);
  await shot("1-start");
  let s = await state();
  report.start = s;
  const startTiles = s.grid.filter(Boolean);
  report.checks["starts with two tiles of 2 or 4"] = startTiles.length === 2 && startTiles.every(t => t === 2 || t === 4);
  report.checks["board can be read from the page"] = s.tiles > 0 && !s.clash;

  // Play until the game ends, mostly left/down/right like a person would.
  const order = ["ArrowLeft", "ArrowDown", "ArrowRight", "ArrowDown", "ArrowLeft", "ArrowUp"];
  let over = false, merges = 0;
  for (let i = 0; i < 800 && !over; i++) {
    const key = order[i % order.length];
    const expected = slide(s.grid, key);
    await press(key);
    const n = await state();
    if (n.outside) problem("layout", `after move ${i + 1} (${key}): ${n.outside} tile(s) stick out of the board`);
    const moved = expected.grid.join() !== s.grid.join();
    const where = `move ${i + 1} (${key}) from ${show(s.grid)}`;
    report.moves.push({ key, grid: show(n.grid), score: n.score });
    if (!moved) {
      if (n.grid.join() !== s.grid.join()) problem("nothing-moved", `${where}: nothing can move that way, but the board became ${show(n.grid)}`);
    } else {
      const diff = n.grid.map((v, c) => [v, c]).filter(([v, c]) => v !== expected.grid[c]);
      const ok = diff.length === 1 && expected.grid[diff[0][1]] === 0 && (diff[0][0] === 2 || diff[0][0] === 4);
      if (!ok) problem("rules", `${where}: expected ${show(expected.grid)} plus one new 2 or 4, got ${show(n.grid)}`);
      if (expected.gained) merges++;
    }
    if (n.score !== null && s.score !== null && n.score - s.score !== (moved ? expected.gained : 0))
      problem("score", `${where}: score went from ${s.score} to ${n.score}, expected +${moved ? expected.gained : 0}`);
    if (i === 9) await shot("2-after-10-moves");
    over = /game\s*over|no (more )?moves|you lose|you lost|try again/i.test(n.text);
    if (!over && !canMove(n.grid) && n.grid.every(Boolean)) {
      problem("game-over", `no move is left on ${show(n.grid)} but the page does not say the game is over`);
      break;
    }
    if (over && canMove(n.grid)) problem("game-over", `the page says game over but ${show(n.grid)} can still move`);
    s = n;
  }
  await shot("3-end");
  report.end = s;
  report.checks["moves follow the 2048 rules"] = !report.problems["rules"] && !report.problems["nothing-moved"];
  report.checks["tiles merge"] = merges > 0;
  report.checks["score shown and counts the merges"] = s.score !== null && s.score > 0 && !report.problems["score"];
  report.checks["shows game over exactly when no move is left"] = over && !report.problems["game-over"];

  await press("r");
  await sleep(500);
  await shot("4-restarted");
  const r = await state();
  report.restart = r;
  report.checks["R starts a new game"] = r.grid.filter(Boolean).length === 2 && (r.score ?? 0) === 0;
  report.checks["tiles sit inside the board"] = !s.outside && !report.problems["layout"];
  report.checks["no script errors"] = report.errors.length === 0;
  ws.close();
} catch (e) {
  report.errors.push("checker: " + e.message);
  report.checks["checker ran"] = false;
} finally {
  browser.kill();
  await sleep(500);
  try { rmSync(profile, { recursive: true, force: true }); } catch { }
}
finish();
