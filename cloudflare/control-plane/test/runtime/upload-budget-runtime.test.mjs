import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import * as runtime from "miniflare";

test("native SQLite Durable Object atomically caps simultaneous device reservations", async () => {
  const auth = await readFile(new URL("../../src/private-updates.js", import.meta.url), "utf8");
  const budget = (await readFile(new URL("../../src/upload-budget.js", import.meta.url), "utf8"))
    .replace('import { authorizeUpdate } from "./private-updates.js";', "");
  const script = auth + "\n" + budget + `
    export class BudgetTest {
      constructor(state) { this.state = state; }
      async fetch(request) {
        const body = await request.json();
        if (body.seed) {
          const device = {deviceId:"test-device",roomId:"m112"};
          await this.state.storage.put("device:test-device",device);
          await registerUpdateIdentity(this.state.storage,device,body.seed);
          return new Response(null,{status:200});
        }
        return handleUploadBudget(this.state.storage,body.proof,body.now);
      }
    }
    export default {fetch(request,env) {return env.BUDGET.get(env.BUDGET.idFromName("global")).fetch(request);}};
  `;
  const options = { modules: true, script, compatibilityDate: "2026-09-01",
    durableObjects: { BUDGET: { className: "BudgetTest", useSQLite: true } } };
  const mf = new runtime.Miniflare(runtime.convertV4MiniflareOptions?.(options) || options);
  try {
    const keys = await crypto.subtle.generateKey({name:"ECDSA",namedCurve:"P-256"}, true, ["sign","verify"]);
    const publicKey = await crypto.subtle.exportKey("spki",keys.publicKey);
    const identity = {keyId:Buffer.from(await crypto.subtle.digest("SHA-256",publicKey)).toString("hex"),publicKey:Buffer.from(publicKey).toString("base64")};
    const send = body => mf.dispatchFetch("https://test/",{method:"POST",body:JSON.stringify(body)});
    assert.equal((await send({seed:identity})).status,200);
    async function reserve(now) {
      const proof = {method:"POST",target:"/api/upload-budget?op=reserve&date=2026-10-30&attempt="+crypto.randomUUID().replaceAll("-",""),
        timestamp:String(now/1000),nonce:crypto.randomUUID().replaceAll("-",""),keyId:identity.keyId};
      proof.signature = Buffer.from(await crypto.subtle.sign({name:"ECDSA",hash:"SHA-256"},keys.privateKey,
        new TextEncoder().encode([proof.method,proof.target,proof.timestamp,proof.nonce].join("\n")))).toString("base64url");
      const result = await send({proof,now});
      assert.equal(result.status,200,await result.clone().text());
      return result.json();
    }
    assert.equal((await reserve(Date.parse("2026-10-30T05:00:00Z"))).allowed,false);
    const results = await Promise.all(Array.from({length:100},()=>reserve(Date.parse("2026-10-30T08:00:00Z"))));
    assert.equal(results.filter(x=>x.allowed).length,80);
    assert.equal((await reserve(Date.parse("2026-10-30T09:00:00Z"))).allowed,false);
  } finally { await mf.dispose(); }
});
