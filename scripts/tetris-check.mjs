// Plays a Tetris page in headless Edge and records what happens, so a run of
// tetris-test.ps1 can be judged without a person: does it load without script errors,
// do pieces fall on their own, does it react to the keys, does it reach game over.
// Screenshots of each stage go next to the report for a human (or Claude) to look at.
//
// Usage: node scripts/tetris-check.mjs <path to tetris.html> <output folder>
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync, rmSync } from "node:fs";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

const [page, outDir] = process.argv.slice(2).map(p => p && resolve(p));
if (!page || !outDir) { console.error("Usage: node tetris-check.mjs <tetris.html> <output folder>"); process.exit(2); }
mkdirSync(outDir, { recursive: true });

const report = { page, exists: existsSync(page), errors: [], stages: {}, checks: {} };
const finish = code => {
  writeFileSync(join(outDir, "check.json"), JSON.stringify(report, null, 2));
  for (const [name, ok] of Object.entries(report.checks)) console.log(`${ok ? "PASS" : "FAIL"} ${name}`);
  for (const e of report.errors.slice(0, 10)) console.log("  error: " + e);
  process.exit(code);
};
if (!report.exists) { report.checks["game file exists"] = false; finish(1); }
report.checks["game file exists"] = true;

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"]
  .find(existsSync);
if (!edge) { console.error("Microsoft Edge not found"); process.exit(2); }

const profile = join(outDir, "edge-profile");
const browser = spawn(edge, ["--headless=new", "--remote-debugging-port=0", `--user-data-dir=${profile}`,
  "--window-size=1100,900", "--no-first-run", "--disable-extensions", "about:blank"], { stdio: "ignore" });
const sleep = ms => new Promise(r => setTimeout(r, ms));

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

  const keys = {
    ArrowLeft: 37, ArrowUp: 38, ArrowRight: 39, ArrowDown: 40, " ": 32, Enter: 13, r: 82, p: 80,
  };
  const press = async (key, times = 1, gap = 80) => {
    for (let i = 0; i < times; i++) {
      const code = key === " " ? "Space" : key.length === 1 ? "Key" + key.toUpperCase() : key;
      const base = { key, code, windowsVirtualKeyCode: keys[key], nativeVirtualKeyCode: keys[key] };
      await send("Input.dispatchKeyEvent", { type: "keyDown", ...base, text: key.length === 1 ? key : undefined });
      await send("Input.dispatchKeyEvent", { type: "keyUp", ...base });
      await sleep(gap);
    }
  };
  const shot = async name => {
    const { data } = await send("Page.captureScreenshot", { format: "png" });
    writeFileSync(join(outDir, name + ".png"), Buffer.from(data, "base64"));
    const text = (await send("Runtime.evaluate", { expression: "document.body ? document.body.innerText : ''", returnByValue: true })).result?.value ?? "";
    report.stages[name] = { text: text.slice(0, 400) };
    return data;
  };

  await send("Runtime.enable");
  await send("Page.enable");
  await send("Page.navigate", { url: pathToFileURL(page).href });
  await sleep(1500);
  await shot("1-loaded");

  // Games often wait for a key or a click before the first piece.
  await press("Enter");
  await send("Input.dispatchMouseEvent", { type: "mousePressed", x: 550, y: 450, button: "left", clickCount: 1 });
  await send("Input.dispatchMouseEvent", { type: "mouseReleased", x: 550, y: 450, button: "left", clickCount: 1 });
  await sleep(500);
  const a = await shot("2-started");
  await sleep(3000);
  const b = await shot("3-after-3s-idle");
  report.checks["pieces fall on their own"] = a !== b;

  await press("ArrowLeft", 4);
  const c = await shot("4-moved-left");
  await press("ArrowRight", 8);
  await shot("5-moved-right");
  await press("ArrowUp");
  await shot("6-rotated");
  await press(" ");
  const d = await shot("7-hard-dropped");
  report.checks["reacts to the keys"] = b !== c && c !== d;

  // Drop a lot of pieces straight down: the stack reaches the top and the game should end.
  await press(" ", 60, 120);
  await sleep(1000);
  await shot("8-after-60-drops");
  const over = report.stages["8-after-60-drops"].text;
  report.checks["shows game over when the stack is full"] = /game\s*over|you lost|try again|restart/i.test(over);

  await press("r");
  await press("Enter");
  await sleep(800);
  await shot("9-restarted");

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
finish(Object.values(report.checks).every(Boolean) ? 0 : 1);
