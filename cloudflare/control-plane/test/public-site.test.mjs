import test from "node:test";
import assert from "node:assert/strict";
import worker from "../src/index.js";

const env = {
  PUBLIC_BASE_URI: "https://admin.silentstream.win",
  DEVICE_BASE_URI: "https://agent.silentstream.win",
  GOOGLE_CLIENT_ID: "test-client-id", GOOGLE_CLIENT_SECRET: "test-client-secret",
  ASSETS: { fetch: async request => new Response(new URL(request.url).pathname, { headers: { "content-type": "text/html" } }) }
};

test("public download and guide assets open only on apex and www", async () => {
  for (const host of ["silentstream.win", "www.silentstream.win"]) {
    for (const [url, asset] of [["/", "/site/index.html"], ["/index.html", "/site/index.html"], ["/guide.html", "/site/guide.html"], ["/assets/site.js", "/site/assets/site.js"], ["/assets/site.css", "/site/assets/site.css"]]) {
      const result = await worker.fetch(new Request(`https://${host}${url}`), env);
      assert.equal(result.status, 200);
      assert.equal(await result.text(), asset);
      assert.match(result.headers.get("content-security-policy"), /frame-ancestors 'none'/);
      assert.equal(result.headers.get("cache-control"), "public, max-age=0, must-revalidate");
    }
  }
});

test("public route does not expose authenticated pages, APIs or non-public source files", async () => {
  for (const host of ["admin.silentstream.win", "m111.silentstream.win", "m112.silentstream.win", "m113.silentstream.win", "baek202.silentstream.win"]) {
    for (const path of ["/", "/guide.html", "/assets/site.js"]) {
      const result = await worker.fetch(new Request(`https://${host}${path}`), env);
      assert.equal(result.status, 302);
      assert.equal(await result.text(), "");
      assert.equal(result.headers.get("cache-control"), "no-store");
    }
  }
  for (const path of ["/manager-install-email.txt", "/site/guide.html", "/USER_GUIDE.md", "/api/control/session"]) {
    const result = await worker.fetch(new Request(`https://silentstream.win${path}`), env);
    assert.equal(result.status, 302);
    assert.equal(new URL(result.headers.get("location")).hostname, "admin.silentstream.win");
  }
  const result = await worker.fetch(new Request("https://agent.silentstream.win/guide.html"), env);
  assert.equal(result.status, 404);
});
