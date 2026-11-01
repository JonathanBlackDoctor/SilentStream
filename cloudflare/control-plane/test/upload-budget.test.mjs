import test from "node:test";
import assert from "node:assert/strict";
import { registerUpdateIdentity, authorizeUpdate } from "../src/private-updates.js";
import { handleUploadBudget, nextPacificReset } from "../src/upload-budget.js";
import worker from "../src/index.js";

async function setup() {
  const values = new Map();
  let pending = Promise.resolve();
  const storage = {
    get: async key => structuredClone(values.get(key)),
    put: async (key, value) => values.set(key, structuredClone(value)),
    delete: async key => values.delete(key),
    transaction: action => { const next = pending.then(() => action(storage)); pending = next.catch(() => {}); return next; }
  };
  const signers = [];
  for (let i = 0; i < 4; i++) {
    const key = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
    const publicKey = await crypto.subtle.exportKey("spki", key.publicKey);
    const identity = { keyId: Buffer.from(await crypto.subtle.digest("SHA-256", publicKey)).toString("hex"), publicKey: Buffer.from(publicKey).toString("base64") };
    const device = { deviceId: "test-" + i, roomId: ["m112", "m113", "m111", "baek202"][i] };
    await storage.put("device:" + device.deviceId, device);
    await registerUpdateIdentity(storage, device, identity);
    signers.push(async (query, now, method = "POST") => {
      const proof = { method, target: "/api/upload-budget?" + query, keyId: identity.keyId,
        timestamp: String(Math.floor(now / 1000)), nonce: crypto.randomUUID().replaceAll("-", "") };
      proof.signature = Buffer.from(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key.privateKey,
        new TextEncoder().encode([proof.method, proof.target, proof.timestamp, proof.nonce].join("\n")))).toString("base64url");
      return proof;
    });
  }
  const warmup = Date.parse("2026-10-30T05:00:00Z");
  const request = async (query, now, device = 0) => handleUploadBudget(storage, await signers[device](query, now), now);
  const reserve = (date, now, device = 0, attempt = crypto.randomUUID().replaceAll("-", "")) =>
    request("op=reserve&date=" + date + "&attempt=" + attempt, now, device);
  const warm = await reserve("2026-10-30", warmup);
  assert.equal((await warm.json()).reason, "shared-pause");
  return { values, storage, signers, request, reserve, now: Date.parse("2026-10-30T08:00:00Z") };
}

test("four devices concurrently share 80 starts; retries consume capacity and state survives a handler restart", async () => {
  const x = await setup();
  const results = await Promise.all(Array.from({ length: 100 }, (_, i) => x.reserve("2026-10-30", x.now, i % 4).then(r => r.json())));
  assert.equal(results.filter(r => r.allowed).length, 80);
  assert.equal(x.values.get("upload-budget:v1").used, 80);
  assert.equal((await (await x.reserve("2026-10-30", x.now + 1000)).json()).allowed, false);
  assert.equal((await (await x.reserve("2026-10-31", nextPacificReset(x.now) + 1000)).json()).allowed, true);
});
test("backlog cannot consume today's 40 reserved starts", async () => {
  const x = await setup();
  const old = await Promise.all(Array.from({ length: 48 }, (_, i) => x.reserve("2026-10-29", x.now, i % 4).then(r => r.json())));
  assert.equal(old.filter(r => r.allowed).length, 40);
  const current = await Promise.all(Array.from({ length: 44 }, (_, i) => x.reserve("2026-10-30", x.now, i % 4).then(r => r.json())));
  assert.equal(current.filter(r => r.allowed).length, 40);
});
test("channel pause propagates across PCs and Pacific reset, including repeated delivery", async () => {
  const x = await setup(), until = x.now + 24 * 3600000;
  await x.request("op=pause&kind=channel&until=" + until / 1000, x.now);
  await x.request("op=pause&kind=channel&until=" + (until - 1000) / 1000, x.now, 1);
  const denied = await (await x.reserve("2026-10-31", nextPacificReset(x.now) + 1000, 2)).json();
  assert.equal(denied.allowed, false);
  assert.equal(Date.parse(denied.retryAtUtc), until);
  assert.equal((await (await x.reserve("2026-10-31", until + 1000, 3)).json()).allowed, true);
});
test("API pause is shared and expires at the project reset", async () => {
  const x = await setup(), until = nextPacificReset(x.now) + 1000;
  await x.request("op=pause&kind=api&until=" + until / 1000, x.now);
  assert.equal((await (await x.reserve("2026-10-30", x.now, 1)).json()).allowed, false);
  assert.equal((await (await x.reserve("2026-10-31", until + 1000, 1)).json()).allowed, true);
});
test("lost reservation acknowledgement cannot allocate twice; expired permits cannot upload", async () => {
  const x = await setup(), attempt = "a".repeat(32);
  await x.reserve("2026-10-30", x.now, 0, attempt);
  const retry = await (await x.reserve("2026-10-30", x.now + 1000, 0, attempt)).json();
  assert.equal(retry.allowed, true);
  assert.equal(x.values.get("upload-budget:v1").used, 1);
  assert.equal((await (await x.reserve("2026-10-30", x.now + 61000, 0, attempt)).json()).allowed, false);
});
test("invalid dates, future dates, extra parameters, excessive pauses and signatures fail closed", async () => {
  const x = await setup();
  for (const date of ["2026-99-99", "2026-02-30", "2026-11-01"])
    assert.equal((await x.reserve(date, x.now)).status, 400);
  assert.equal((await x.request("op=pause&kind=channel&until=9999999999999", x.now)).status, 400);
  assert.equal((await x.request("op=pause&kind=channel&until=1&extra=1", x.now)).status, 400);
  const proof = await x.signers[0]("op=reserve&date=2026-10-30&attempt=" + "b".repeat(32), x.now);
  assert.equal((await authorizeUpdate(x.storage, proof, x.now)).status, 401);
  assert.equal((await handleUploadBudget(x.storage, { ...proof, target: proof.target.replace("10-30", "10-29") }, x.now)).status, 401);
  await x.storage.put("device:test-0", { deviceId: "test-0", revokedAtUtc: "revoked" });
  assert.equal((await handleUploadBudget(x.storage, proof, x.now)).status, 401);
});
test("Pacific DST reset is 07Z before and 08Z after fallback; transition and warmup do not issue permits", async () => {
  assert.equal(new Date(nextPacificReset(Date.parse("2026-11-01T06:00:00Z"))).toISOString(), "2026-11-01T07:00:00.000Z");
  assert.equal(new Date(nextPacificReset(Date.parse("2026-11-01T08:30:00Z"))).toISOString(), "2026-11-02T08:00:00.000Z");
  assert.equal(new Date(nextPacificReset(Date.parse("2026-03-08T09:00:00Z"))).toISOString(), "2026-03-09T07:00:00.000Z");
  const x = await setup();
  assert.equal((await (await x.reserve("2026-10-31", nextPacificReset(x.now) - 60000)).json()).reason, "reset-boundary");
});
test("public route is agent-only, forwards only signed proof, and never reaches the release proxy", async () => {
  const calls = [];
  const env = { PUBLIC_BASE_URI: "https://admin.silentstream.win", DEVICE_BASE_URI: "https://agent.silentstream.win",
    CONTROL: { idFromName: () => "global", get: () => ({ fetch: async request => {
      calls.push({ path: new URL(request.url).pathname, body: await request.json() });
      return new Response(null, { status: 401 });
    } }) } };
  const path = "/api/upload-budget?op=reserve&date=2026-10-30&attempt=" + "a".repeat(32);
  assert.equal((await worker.fetch(new Request("https://admin.silentstream.win" + path, { method: "POST" }), env)).status, 404);
  assert.equal((await worker.fetch(new Request("https://agent.silentstream.win" + path, { method: "POST", body: '{"bypass":true}' }), env)).status, 401);
  assert.equal(calls[0].path, "/upload-budget");
  assert.equal(calls[0].body.target, path);
  assert.equal(calls[0].body.bypass, undefined);
});
