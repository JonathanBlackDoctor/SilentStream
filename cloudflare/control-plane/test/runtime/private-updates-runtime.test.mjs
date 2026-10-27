import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import * as runtime from "miniflare";

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
