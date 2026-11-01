import test from "node:test";
import assert from "node:assert/strict";
import { registerUpdateIdentity, authorizeUpdate, releaseAsset, downloadReleaseAsset, privateUpdateResponse, readUpdatePolicy, changeUpdatePolicy } from "../src/private-updates.js";
import worker, { ControlPlane } from "../src/index.js";
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

test("a preservation hold follows the verified device across keys and ignores claimed room/version", async () => {
  const x = await setup(); await registerUpdateIdentity(x.storage, x.device, x.identity);
  const held = await authorizeUpdate(x.storage, await x.proof({roomId:"m111", appVersion:"0.4.99"}), Date.now(), "updates", "m112");
  assert.equal(held.status, 503); assert.equal(held.headers.get("retry-after"), "21600");
  assert.equal(x.values.get("update-policy:device-one").held, true);
  const other = await setup(); await registerUpdateIdentity(x.storage, x.device, other.identity);
  assert.equal((await authorizeUpdate(x.storage, await other.proof())).status, 503, "persisted without the default setting");
  assert.equal((await authorizeUpdate(x.storage, await x.proof())).status, 401, "rotation still revokes the old key");
  assert.equal((await authorizeUpdate(x.storage, await other.proof({ method:"POST", target:"/api/upload-budget?operation=reserve" }), Date.now(), "upload-budget")).status, 200);
  x.values.set("device:device-one", {...x.device, revokedAtUtc:new Date().toISOString()});
  assert.equal((await authorizeUpdate(x.storage, await other.proof())).status, 401);
});

test("all feed and artifact requests stop before release lookup even with HEAD/range/explicit version", async () => {
  const x = await setup(); await registerUpdateIdentity(x.storage, x.device, x.identity);
  const control = new ControlPlane({storage:x.storage}, {PRIVATE_UPDATE_HOLD_ROOMS:"m112"});
  const env = { DEVICE_BASE_URI:"https://agent.silentstream.win", PUBLIC_BASE_URI:"https://admin.silentstream.win",
    CONTROL:{idFromName:()=>"global", get:()=>({fetch:r=>control.fetch(r)})} };
  const original = globalThis.fetch; let fetched = 0;
  globalThis.fetch = async () => { fetched++; throw new Error("must not reach asset cache/upstream"); };
  try {
    for (const path of ["releases.win.json","assets.win.json","v0.4.31/manifest.json","MediaCaptureHelper-0.4.31-full.nupkg",
      "SilentStream-Central-Installer-v0.4.31.exe","SilentStream-prepared-package-v0.4.31.zip"]) {
      for (const method of ["GET","HEAD"]) {
        const proof = await x.proof({method,target:"/api/updates/"+path+"?room=m111&version=0.4.99"});
        const response = await worker.fetch(new Request("https://agent.silentstream.win"+proof.target, {method,headers:{
          "x-update-key":proof.keyId,"x-update-time":proof.timestamp,"x-update-nonce":proof.nonce,
          "x-update-signature":proof.signature,range:"bytes=0-1"}}),env);
        assert.equal(response.status,503,path); assert.equal(response.headers.get("cache-control"),"no-store");
        assert.equal(response.headers.get("x-update-policy"),"preservation-hold"); assert.equal(await response.text(),"");
      }
    }
    assert.equal(fetched,0);
  } finally { globalThis.fetch = original; }
});

test("policy release needs admin authentication, same origin, current revision and a preservation manifest", async () => {
  const x = await setup(), deviceId = "a".repeat(32), device = {...x.device,deviceId};
  x.values.set("device:"+deviceId,device); x.values.set("ownerEmail","owner@example.com");
  x.values.set("member:manager@example.com",{role:"manager",rooms:["m112"]});
  const control = new ControlPlane({storage:x.storage}, {PRIVATE_UPDATE_HOLD_ROOMS:"m112",BOOTSTRAP_OWNER_EMAIL:"owner@example.com"});
  const env = { DEVICE_BASE_URI:"https://agent.silentstream.win",PUBLIC_BASE_URI:"https://admin.silentstream.win",
    GOOGLE_CLIENT_ID:"test",GOOGLE_CLIENT_SECRET:"test",ENABLE_DEVELOPMENT_IDENTITY:"true",CONTROL:{idFromName:()=>"global",get:()=>({fetch:r=>control.fetch(r)})} };
  const url = "https://admin.silentstream.win/api/control/devices/"+deviceId+"/update-policy";
  const request = (email,body,origin="https://admin.silentstream.win") => worker.fetch(new Request(url,{method:body?"POST":"GET",
    headers:{"x-silentstream-dev-email":email,origin,"content-type":"application/json"},body:body?JSON.stringify(body):undefined}),env);
  const change={held:false,expectedRevision:1,reason:"full verified original copy"};
  assert.equal((await request("manager@example.com",change)).status,403);
  assert.equal((await request("owner@example.com",change,"https://other.invalid")).status,403);
  const first=await request("owner@example.com"); assert.equal(first.status,200);
  assert.equal((await first.json()).held,true);
  assert.equal((await request("owner@example.com",change)).status,400);
  const release={...change,preservationManifestSha256:"f".repeat(64)};
  assert.equal((await request("owner@example.com",{...release,expectedRevision:0})).status,409);
  assert.equal((await request("owner@example.com",release)).status,200);
  assert.equal((await request("owner@example.com",release)).status,409,"stale retry cannot overwrite later policy");
  assert.equal((await readUpdatePolicy(x.storage,device,"m112")).held,false);
  assert.equal(x.values.get("update-policy-audit:"+deviceId+":2").next.actor,"owner@example.com");
  assert.equal((await worker.fetch(new Request(url),{...env,ENABLE_DEVELOPMENT_IDENTITY:"false"})).status,401);
});

test("unknown or corrupt policy fails closed and an unrelated room remains eligible", async () => {
  const x=await setup(); await registerUpdateIdentity(x.storage,x.device,x.identity);
  x.values.set("device:device-one",{...x.device,roomId:"m111"});
  assert.equal((await authorizeUpdate(x.storage,await x.proof(),Date.now(),"updates","m112")).status,200);
  x.values.set("update-policy:device-one",{held:false,deviceId:"someone-else",revision:5});
  assert.equal((await authorizeUpdate(x.storage,await x.proof())).status,503);
  for (const revision of [-1,0]) {
    x.values.set("update-policy:device-one",{held:false,deviceId:x.device.deviceId,revision});
    assert.equal((await authorizeUpdate(x.storage,await x.proof())).status,503);
  }
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

test("readiness never certifies private distribution through the temporary public bridge", async () => {
  const original=globalThis.fetch; let publicHeads=0;
  globalThis.fetch=async(url,init)=>{
    if(init.method==='HEAD'){publicHeads++;return new Response(null,{headers:{'content-length':'3'}});}
    return new Response(null,{status:403});
  };
  const env={PUBLIC_BASE_URI:'https://admin.silentstream.win',DEVICE_BASE_URI:'https://agent.silentstream.win',
    GOOGLE_CLIENT_ID:'test',GOOGLE_CLIENT_SECRET:'test',GITHUB_RELEASE_READ_TOKEN:'test',ENABLE_DEVELOPMENT_IDENTITY:'true',
    PACKAGE_KEY:'p',PACKAGE_LENGTH:'3',PACKAGE_SHA256:'a'.repeat(64),PACKAGE_URL:'https://github.com/JonathanBlackDoctor/SilentStream/releases/download/v0.9.6/package.zip',
    INSTALLER_KEY:'i',INSTALLER_LENGTH:'3',INSTALLER_SHA256:'a'.repeat(64),INSTALLER_URL:'https://github.com/JonathanBlackDoctor/SilentStream/releases/download/v0.9.6/installer.exe',
    CONTROL:{idFromName:()=> 'global',get:()=>({fetch:async()=>new Response(null,{status:200})})}};
  try {const response=await worker.fetch(new Request('https://admin.silentstream.win/ready',{headers:{'x-silentstream-dev-email':'owner@example.com'}}),env);
    assert.equal(response.status,503);assert.deepEqual((await response.json()).missing,['prepared package','bootstrap installer']);assert.equal(publicHeads,0);
  } finally {globalThis.fetch=original;}
});
