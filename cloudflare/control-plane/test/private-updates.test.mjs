import test from "node:test";
import assert from "node:assert/strict";
import { registerUpdateIdentity, authorizeUpdate, releaseAsset, downloadReleaseAsset, privateUpdateResponse } from "../src/private-updates.js";
const hex = data => Buffer.from(data).toString("hex");
async function setup() {
  const keys = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
  const publicKey = await crypto.subtle.exportKey("spki", keys.publicKey);
  const identity = { keyId: hex(await crypto.subtle.digest("SHA-256", publicKey)), publicKey: Buffer.from(publicKey).toString("base64") };
  const device = { deviceId: "device-one", roomId: "m112" };
  const values = new Map([["device:device-one", device]]);
  let pending = Promise.resolve();
  const storage = { get: async k => values.get(k), put: async (k,v) => values.set(k,v), delete: async k => values.delete(k),
    transaction: action => { const next = pending.then(() => action(storage)); pending = next.catch(() => {}); return next; } };
  const proof = async (overrides = {}) => {
    const value = { method: "GET", target: "/api/updates/releases.win.json", timestamp: String(Math.floor(Date.now()/1000)), nonce: crypto.randomUUID().replaceAll("-", ""), keyId: identity.keyId, ...overrides };
    value.signature = Buffer.from(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, keys.privateKey,
      new TextEncoder().encode([value.method,value.target,value.timestamp,value.nonce].join("\n")))).toString("base64url");
    return value;
  };
  return { keys, identity, device, values, storage, proof };
}
test("only a device-registered public key can authenticate update requests", async () => {
  const x=await setup(); const proof=await x.proof();
  assert.equal((await authorizeUpdate(x.storage,proof)).status,401);
  assert.equal(await registerUpdateIdentity(x.storage,x.device,x.identity),true);
  assert.equal((await authorizeUpdate(x.storage,proof)).status,200);
  assert.equal((await authorizeUpdate(x.storage,proof)).status,401);
  assert.equal(await registerUpdateIdentity(x.storage,x.device,{...x.identity,keyId:"a".repeat(64)}),false);
});
test("expired, altered, revoked and concurrent duplicate proofs fail closed", async () => {
  const x=await setup(); await registerUpdateIdentity(x.storage,x.device,x.identity);
  const valid=await x.proof();
  assert.equal((await authorizeUpdate(x.storage,{...valid,target:"/api/updates/other-file"})).status,401);
  assert.equal((await authorizeUpdate(x.storage,await x.proof({timestamp:String(Math.floor(Date.now()/1000)-600)}))).status,401);
  assert.deepEqual((await Promise.all([authorizeUpdate(x.storage,valid),authorizeUpdate(x.storage,valid)])).map(x=>x.status).sort(),[200,401]);
  x.values.set("device:device-one",{...x.device,revokedAtUtc:new Date().toISOString()});
  assert.equal((await authorizeUpdate(x.storage,await x.proof())).status,401);
});
test("key rotation removes the previous key, while unrelated keys cannot replace the binding", async () => {
  const x=await setup(), other=await setup(); await registerUpdateIdentity(x.storage,x.device,x.identity);
  await registerUpdateIdentity(x.storage,x.device,other.identity);
  assert.equal((await authorizeUpdate(x.storage,await x.proof())).status,401);
  assert.equal((await authorizeUpdate(x.storage,await other.proof())).status,200);
});
test("private release proxy strips the GitHub credential before following a signed asset redirect", async () => {
  const original=globalThis.fetch, env={GITHUB_RELEASE_READ_TOKEN:"server-only-test-token",DEVICE_BASE_URI:"https://agent.silentstream.win"};
  const assets=[{id:2,name:"SilentStream-Central-Installer-v0.9.9.exe",size:3,digest:"sha256:"+"a".repeat(64)},
    {id:3,name:"SilentStream-prepared-package-v0.9.9.zip",size:3,digest:"sha256:"+"b".repeat(64)}];
  const observed=[];
  globalThis.fetch=async(input,init)=>{
    const url=String(input); observed.push({url,init});
    if(url.endsWith("/releases/tags/v0.9.9"))return Response.json({tag_name:"v0.9.9",draft:false,prerelease:false,assets});
    if(url.endsWith("/releases/assets/2"))return new Response(null,{status:302,headers:{location:"https://release-assets.githubusercontent.com/test?signature=temporary"}});
    assert.equal(new URL(url).hostname,"release-assets.githubusercontent.com");
    assert.equal(init.headers.authorization,undefined); return new Response("abc",{headers:{"content-length":"3"}});
  };
  try {
    const asset=await releaseAsset(env,"https://github.com/JonathanBlackDoctor/SilentStream/releases/download/v0.9.9/SilentStream-Central-Installer-v0.9.9.exe");
    assert.equal(await (await downloadReleaseAsset(env,asset)).text(),"abc");
    assert.equal(observed[0].init.headers.authorization,"Bearer server-only-test-token");
    const manifest=await (await privateUpdateResponse(new Request("https://agent.silentstream.win/api/updates/v0.9.9/manifest.json"),env)).json();
    assert.equal(manifest.assets.length,2); assert.equal(JSON.stringify(manifest).includes("server-only"),false);
    assert.equal(manifest.assets[0].url,"https://agent.silentstream.win/api/updates/SilentStream-Central-Installer-v0.9.9.exe");
    await assert.rejects(()=>releaseAsset(env,"https://other.invalid/file.exe"));
    assert.equal((await privateUpdateResponse(new Request("https://agent.silentstream.win/api/updates/unrelated.txt"),env)).status,404);
  } finally {globalThis.fetch=original;}
});
