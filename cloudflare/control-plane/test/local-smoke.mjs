import assert from "node:assert/strict";
import { once } from "node:events";
import WebSocket from "ws";

const base = process.env.SILENTSTREAM_SMOKE_BASE || "http://127.0.0.1:8799";
const ownerHeaders = { "x-silentstream-dev-email": "owner@example.com" };

async function json(response) {
  const body = await response.json();
  assert.equal(response.ok, true, JSON.stringify(body));
  return body;
}

const health = await json(await fetch(`${base}/health`));
assert.equal(health.runtime, "cloudflare-workers");
assert.equal((await json(await fetch(`${base}/ready`))).status, "ready");

const owner = await json(await fetch(`${base}/api/control/session`, { headers: ownerHeaders }));
assert.equal(owner.admin, true);
assert.equal(owner.owner, true);
assert.deepEqual(owner.rooms, ["m111", "m112", "m113", "baek202"]);

await json(await fetch(`${base}/api/control/members`, {
  method: "POST",
  headers: { ...ownerHeaders, origin: "http://admin.silentstream.win", "content-type": "application/json" },
  body: JSON.stringify({ email: "manager@example.com", role: "manager", rooms: ["m112"] })
}));
const manager = await json(await fetch(`${base}/api/control/session`, { headers: { "x-silentstream-dev-email": "manager@example.com" } }));
assert.equal(manager.admin, false);
assert.deepEqual(manager.rooms, ["m112"]);

const state = "state-" + "x".repeat(32);
const authorize = new URL(`${base}/api/install/authorize`);
authorize.searchParams.set("room", "m112");
authorize.searchParams.set("state", state);
authorize.searchParams.set("callback", "http://127.0.0.1:49152/callback/");
const approval = await fetch(authorize, { headers: ownerHeaders, redirect: "manual" });
assert.equal(approval.status, 302);
const callback = new URL(approval.headers.get("location"));
assert.equal(callback.searchParams.get("state"), state);
assert.equal(callback.searchParams.get("room"), "m112");

const enrollment = await json(await fetch(`${base}/api/device/enroll`, {
  method: "POST",
  headers: { "content-type": "application/json" },
  body: JSON.stringify({ roomId: "m112", installationId: "smoke-install", machineName: "SMOKE-PC", enrollmentCode: callback.searchParams.get("code") })
}));
assert.equal(enrollment.packageLength, 26);
assert.equal(enrollment.packageSha256, "b36b339adabf55d829c99c813a1986305b9e6de80623f92071f87eabec439880");
const deviceAuthorization = `Device ${enrollment.roomId}:${enrollment.deviceId}:${enrollment.deviceSecret}`;

const packageResponse = await fetch(`${base}/api/device/package`, { headers: { authorization: deviceAuthorization } });
assert.equal(packageResponse.status, 200);
assert.equal(await packageResponse.text(), "silentstream-package-test\n");

let self = await json(await fetch(`${base}/api/device/self`, { headers: { authorization: deviceAuthorization } }));
assert.equal(self.connected, false);
assert.equal(self.reporting, false);

const socketUrl = base.replace(/^http/, "ws") + "/api/device/connect";
const socket = new WebSocket(socketUrl, { headers: { authorization: deviceAuthorization } });
await once(socket, "open");
socket.send(JSON.stringify({ schemaVersion: 1, type: "hello", requestId: null, payload: { roomId: "m112", installationId: "smoke-install", machineName: "SMOKE-PC", agentVersion: "0.4.1", connectedAtUtc: new Date().toISOString() } }));
socket.send(JSON.stringify({ schemaVersion: 1, type: "snapshot", requestId: null, payload: { roomId: "m112", installationId: "smoke-install", machineName: "SMOKE-PC", agentVersion: "0.4.1", capturedAtUtc: new Date().toISOString(), app: { state: "ready" }, audio: { ok: true }, inspection: { startup: { state: "passed" } }, recovery: { available: true }, playlists: { room: "m112", items: [{ date: "2026-09-05", title: "m112 | 2026-09-05", playlistId: "PL_today", status: "ready" }, { date: "2026-09-04", title: "m112 | 2026-09-04", playlistId: "PL_previous", status: "ready" }] } } }));
await new Promise(resolve => setTimeout(resolve, 100));

self = await json(await fetch(`${base}/api/device/self`, { headers: { authorization: deviceAuthorization } }));
assert.equal(self.connected, true);
assert.equal(self.reporting, true);
const managerRooms = await json(await fetch(`${base}/api/control/rooms`, { headers: { "x-silentstream-dev-email": "manager@example.com" } }));
assert.equal(managerRooms.length, 1);
assert.deepEqual(managerRooms[0].playlists.items.map(item => item.date), ["2026-09-05", "2026-09-04"]);
assert.equal(managerRooms[0].playlists.items[1].url, "https://www.youtube.com/playlist?list=PL_previous");

const commandMessage = once(socket, "message");
const commandRequest = fetch(`${base}/api/control/rooms/m112/actions/inspection.rerun`, {
  method: "POST",
  headers: { ...ownerHeaders, origin: "http://admin.silentstream.win", "content-type": "application/json" },
  body: "{}"
});
const [rawCommand] = await commandMessage;
const envelope = JSON.parse(rawCommand.toString());
assert.equal(envelope.type, "command");
assert.equal(envelope.payload.action, "inspection.rerun");
socket.send(JSON.stringify({ schemaVersion: 1, type: "commandResult", requestId: envelope.requestId, payload: { commandId: envelope.requestId, ok: true, statusCode: 200, contentType: "application/json", body: "{}", error: null, completedAtUtc: new Date().toISOString() } }));
const commandResult = await json(await commandRequest);
assert.equal(commandResult.ok, true);
socket.close();

console.log(JSON.stringify({ health: "ok", ready: "ready", owner: true, managerRoom: "m112", playlistHistory: "ok", enrollment: "ok", websocket: "ok", command: "ok" }));
