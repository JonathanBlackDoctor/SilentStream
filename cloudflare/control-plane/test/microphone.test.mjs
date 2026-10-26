import test from "node:test";
import assert from "node:assert/strict";
import vm from "node:vm";
import { readFile } from "node:fs/promises";
import worker from "../src/index.js";

const source = await readFile(new URL("../public/room/app.js", import.meta.url), "utf8");
const panel = source.slice(source.indexOf("function microphonePanel("));
const escapeHelper = source.split("\n").find(s => s.startsWith("function esc("));
function render(device) {
  const context = vm.createContext({ device });
  vm.runInContext(escapeHelper + "\n" + panel, context);
  return vm.runInContext("microphonePanel(device)", context);
}

test("all-input option is only offered by apps supporting automatic mode", () => {
  assert.doesNotMatch(render({ online: true, audio: { microphoneControl: true } }), /value="\*"/);
  assert.match(render({ online: true, audio: { microphoneControl: true, autoIncludeMicrophones: true } }), /value="\*" selected/);
  assert.match(render({ online: true, audio: { microphoneControl: true, autoIncludeMicrophones: false } }), /value="\*" /);
});

test("old app advertises upgrade instead of unusable microphone actions", () => {
  const html = render({ online: true, audio: {} });
  assert.match(html, /업데이트/);
  assert.doesNotMatch(html, /data-action/);
});

test("offline microphones disable controls and escape device names and IDs", () => {
  const html = render({ online: false, audio: { microphoneControl: true,
    devices: [{ id: '"><script>', name: "<script>bad</script>" }], sources: [] } });
  assert.match(html, /disabled data-mic-select/);
  assert.doesNotMatch(html, /<script>/);
  assert.match(html, /&lt;script&gt;/);
});

test("connected capture does not claim audible signal; muted and failed input remain distinct", () => {
  const html = render({ online: true, audio: { microphoneControl: true, sources: [
    { kind: "mic", name: "A", available: true, peak: 0 },
    { kind: "mic", name: "B", available: true, muted: true },
    { kind: "mic", name: "C", available: false, captureError: "unplugged" }
  ] } });
  assert.match(html, /실제 소리 확인 필요/);
  assert.match(html, /앱 음소거/);
  assert.match(html, /unplugged/);
});

test("healthy quiet input and endpoint or data failure have separate escaped labels", () => {
  const html = render({ online: true, audio: { microphoneControl: true, micWarning: false, sources: [
    { kind: "mic", name: "Quiet", available: true, inputState: "quiet", inputMessage: "연결됨 · 입력 데이터 정상 · 현재 조용함" },
    { kind: "mic", name: "USB", available: false, inputState: "deviceUnavailable", inputMessage: "장치 인식 안 됨" },
    { kind: "mic", name: "Driver", available: false, inputState: "dataStopped", inputMessage: "입력 데이터 중단 · 원인 미확정" },
    { kind: "mic", name: "Escape", inputMessage: "<script>unsafe</script>" }
  ] } });
  assert.match(html, /입력 데이터 정상 · 현재 조용함/);
  assert.match(html, /장치 인식 안 됨/);
  assert.match(html, /입력 데이터 중단 · 원인 미확정/);
  assert.doesNotMatch(html, /연결 안 됨|<script>/);
  assert.match(html, /&lt;script&gt;/);
});

test("microphone commands preserve selected ID and reject cross-room and foreign-origin requests", async () => {
  const calls = [];
  const control = { async fetch(request) {
    const path = new URL(request.url).pathname;
    const body = await request.json();
    if (path === "/authorize-room") return Response.json({ allowed: true });
    calls.push(body);
    return Response.json({ ok: true });
  }};
  const env = { PUBLIC_BASE_URI: "https://admin.silentstream.win",
    DEVICE_BASE_URI: "https://agent.silentstream.win", ENABLE_DEVELOPMENT_IDENTITY: "true",
    CONTROL: { idFromName: () => "global", get: () => control } };
  const send = (action, room="m112", origin="https://m112.silentstream.win", deviceId="{USB-ENDPOINT}") => worker.fetch(new Request(
    "https://m112.silentstream.win/api/control/rooms/" + room + "/actions/" + action,
    { method: "POST", headers: { origin, "content-type": "application/json", "x-silentstream-dev-email": "manager@example.com" },
      body: JSON.stringify({ deviceId }) }), env);
  for (const action of ["audio.select", "audio.refresh", "audio.restore"]) {
    assert.equal((await send(action)).status, 200);
    assert.equal(calls.at(-1).action, action);
    assert.equal(calls.at(-1).body.deviceId, "{USB-ENDPOINT}");
  }
  assert.equal((await send("audio.select", "m113")).status, 404);
  assert.equal((await send("audio.select", "m112", "https://evil.example")).status, 403);
  assert.equal(calls.length, 3);
  assert.equal((await send("audio.select", "m112", "https://m112.silentstream.win", "*")).status, 200);
  assert.equal(calls.at(-1).body.deviceId, "*");
});

test("old coordinator disables commands even when the PC app is new", () => {
  const html=render({online:true,audio:{microphoneControl:true,sources:[]}});
  assert.match(html,/disabled data-mic-select/);
  assert.match(html,/연결 도우미 업데이트 후/);
  const supported=render({online:true,audio:{microphoneControl:true,remoteMicrophoneCommands:true,sources:[]}});
  assert.doesNotMatch(supported,/disabled data-mic-select/);
});
