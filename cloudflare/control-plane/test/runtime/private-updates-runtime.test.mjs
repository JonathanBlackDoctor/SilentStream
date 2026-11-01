import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import * as runtime from "miniflare";

test("native SQLite stores a device hold atomically and releases only the current audited revision", async () => {
  const source = await readFile(new URL("../../src/private-updates.js", import.meta.url), "utf8");
  const script = source + `
    export class PolicyTest {
      constructor(state) { this.state = state; }
      async fetch(request) {
        const body = await request.json(), storage = this.state.storage;
        const device = {deviceId:'test-device',roomId:'m112'};
        if(body.seed) { await storage.put('device:test-device',device); await registerUpdateIdentity(storage,device,body.seed); return new Response(null,{status:200}); }
        if(body.change) return changeUpdatePolicy(storage,device,body.change,'test-admin','m112');
        return authorizeUpdate(storage,body.proof,Date.now(),body.scope||'updates',body.defaults?'m112':'');
      }
    }
    export default {fetch(request,env) {return env.POLICY.get(env.POLICY.idFromName('global')).fetch(request);}};
  `;
  const options={modules:true,script,compatibilityDate:"2026-09-01",durableObjects:{POLICY:{className:"PolicyTest",useSQLite:true}}};
  const mf=new runtime.Miniflare(runtime.convertV4MiniflareOptions?.(options)||options);
  try {
    const keys=await crypto.subtle.generateKey({name:"ECDSA",namedCurve:"P-256"},true,["sign","verify"]);
    const publicKey=await crypto.subtle.exportKey("spki",keys.publicKey);
    const identity={keyId:Buffer.from(await crypto.subtle.digest("SHA-256",publicKey)).toString("hex"),publicKey:Buffer.from(publicKey).toString("base64")};
    const send=body=>mf.dispatchFetch("https://test/",{method:"POST",body:JSON.stringify(body)});
    const proof=async()=>{const p={method:"GET",target:"/api/updates/releases.win.json",keyId:identity.keyId,
      timestamp:String(Math.floor(Date.now()/1000)),nonce:crypto.randomUUID().replaceAll("-","")};
      p.signature=Buffer.from(await crypto.subtle.sign({name:"ECDSA",hash:"SHA-256"},keys.privateKey,
        new TextEncoder().encode([p.method,p.target,p.timestamp,p.nonce].join("\n")))).toString("base64url");return p;};
    assert.equal((await send({seed:identity})).status,200);
    assert.equal((await send({proof:await proof(),defaults:true})).status,503);
    assert.equal((await send({proof:await proof(),defaults:false})).status,503);
    const change={held:false,expectedRevision:1,reason:"verified original copy",preservationManifestSha256:"e".repeat(64)};
    const results=await Promise.all([send({change}),send({change})]);
    assert.deepEqual(results.map(x=>x.status).sort(),[200,409]);
    assert.equal((await send({proof:await proof(),defaults:true})).status,200);
  } finally {await mf.dispose();}
});

test("native Workers fetch supports private release lookup and credential-free asset redirects", async () => {
  const source = await readFile(new URL("../../src/private-updates.js", import.meta.url), "utf8");
  const options = { workers: [{ name: "subject", modules: true, compatibilityDate: "2026-09-01", outboundService: "mock",
    script: source + `\nexport default { async fetch() {
      try { return await privateUpdateResponse(new Request('https://agent.silentstream.win/api/updates/releases.win.json'),
        {GITHUB_RELEASE_READ_TOKEN:'test-only-token',DEVICE_BASE_URI:'https://agent.silentstream.win'}); }
      catch(e) { return Response.json({error:e.message},{status:500}); }
    }};` }, { name: "mock", modules: true, compatibilityDate: "2026-09-01", script: `
      export default {fetch(request) {
        const url=new URL(request.url);
        if(url.hostname==='api.github.com') {
          if(request.headers.get('authorization')!=='Bearer test-only-token') return new Response(null,{status:401});
          if(url.pathname.endsWith('/releases/latest')) return Response.json({tag_name:'v0.9.8',draft:false,prerelease:false,
            assets:[{id:18,name:'releases.win.json',size:13,digest:'sha256:'+'a'.repeat(64)}]});
          return new Response(null,{status:302,headers:{location:'https://release-assets.githubusercontent.com/test'}});
        }
        if(request.headers.has('authorization')) return new Response('credential leaked',{status:500});
        return Response.json({Assets:[]});
      }};` }] };
  const mf = new runtime.Miniflare(runtime.convertV4MiniflareOptions?.(options) || options);
  try { const response = await mf.dispatchFetch("https://runtime.test/");
    assert.equal(response.status, 200, await response.clone().text());
    assert.deepEqual(await response.json(), { Assets: [] });
  } finally { await mf.dispose(); }
});
