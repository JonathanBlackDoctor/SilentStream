import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import worker from "../src/index.js";

const env = {
  PUBLIC_BASE_URI: "https://admin.silentstream.win",
  DEVICE_BASE_URI: "https://agent.silentstream.win",
  GOOGLE_CLIENT_ID: "test-client-id", GOOGLE_CLIENT_SECRET: "test-client-secret",
  ASSETS: { fetch: async request => new Response(new URL(request.url).pathname, { headers: { "content-type": "text/html" } }) }
};
const siteRoutes = [["/index.html", "/site/index.html"], ["/guide.html", "/site/guide.html"],
  ["/assets/site.js", "/site/assets/site.js"], ["/assets/site.css", "/site/assets/site.css"]];
const sessionToken = "valid-session-token-for-site-tests";
function sessionEnv(session = { email: "manager@example.com", roomId: "" }) {
  return { ...env, CONTROL: {
    idFromName: name => name,
    get: () => ({ fetch: async request => {
      assert.equal(new URL(request.url).pathname, "/oauth/session");
      assert.deepEqual(await request.json(), { token: sessionToken });
      return session ? Response.json(session) : new Response(null, { status: 401 });
    } })
  } };
}

test("bundled public assets match canonical docs for direct and archived deployments", async () => {
  for (const name of ["index.html", "guide.html", "assets/site.css", "assets/site.js"]) {
    const canonical = await readFile(new URL(`../../../docs/${name}`, import.meta.url), "utf8");
    const bundled = await readFile(new URL(`../public/site/${name}`, import.meta.url), "utf8");
    assert.equal(bundled.replace(/\r\n/g, "\n"), canonical.replace(/\r\n/g, "\n"), name);
  }
});

test("apex and www redirect downloads and guidance to the session host without exposing assets", async () => {
  for (const host of ["silentstream.win", "www.silentstream.win"]) {
    for (const method of ["GET", "HEAD"]) {
      for (const path of ["/", ...siteRoutes.map(([path]) => path)]) {
        const result = await worker.fetch(new Request(`https://${host}${path}?source=test`, { method }), env);
        assert.equal(result.status, 302);
        assert.equal(result.headers.get("location"), `${env.PUBLIC_BASE_URI}${path === "/" ? "/index.html" : path}?source=test`);
        assert.equal(await result.text(), "");
        assert.equal(result.headers.get("cache-control"), "no-store");
      }
    }
  }
});

test("missing, malformed, expired and room-bound sessions cannot read admin guidance or assets", async () => {
  for (const [cookie, configuredEnv] of [
    ["", env], ["__Host-SilentStreamSession=invalid", env],
    [`__Host-SilentStreamSession=${sessionToken}`, sessionEnv(null)],
    [`__Host-SilentStreamSession=${sessionToken}`, sessionEnv({ email: "manager@example.com", roomId: "m112" })]
  ]) {
    for (const method of ["GET", "HEAD"]) {
      for (const [path] of siteRoutes) {
        const result = await worker.fetch(new Request(`${env.PUBLIC_BASE_URI}${path}?source=test`, {
          method, headers: { cookie }
        }), configuredEnv);
        assert.equal(result.status, 302);
        const login = new URL(result.headers.get("location"));
        assert.equal(login.origin, env.PUBLIC_BASE_URI);
        assert.equal(login.pathname, "/auth/login");
        assert.equal(login.searchParams.get("returnTo"), `${path}?source=test`);
        assert.equal(await result.text(), "");
        assert.equal(result.headers.get("cache-control"), "no-store");
      }
    }
  }
});

test("authenticated admin sessions read guidance and its assets without public caching", async () => {
  for (const method of ["GET", "HEAD"]) {
    for (const [path, asset] of siteRoutes) {
      const result = await worker.fetch(new Request(`${env.PUBLIC_BASE_URI}${path}`, {
        method, headers: { cookie: `__Host-SilentStreamSession=${sessionToken}` }
      }), sessionEnv());
      assert.equal(result.status, 200);
      assert.equal(await result.text(), method === "HEAD" ? "" : asset);
      assert.equal(result.headers.get("cache-control"), "no-store");
      assert.match(result.headers.get("content-security-policy"), /frame-ancestors 'none'/);
    }
  }
});

test("site asset mapping leaves room, API and non-public source boundaries intact", async () => {
  for (const host of ["m111.silentstream.win", "m112.silentstream.win", "m113.silentstream.win", "baek202.silentstream.win"]) {
    for (const path of ["/", ...siteRoutes.map(([path]) => path)]) {
      const result = await worker.fetch(new Request(`https://${host}${path}`), env);
      assert.equal(result.status, 302);
      assert.equal(await result.text(), "");
    }
  }
  for (const path of ["/manager-install-email.txt", "/site/guide.html", "/USER_GUIDE.md", "/api/control/session"]) {
    const result = await worker.fetch(new Request(`https://silentstream.win${path}`), env);
    assert.equal(result.status, 302);
    assert.equal(new URL(result.headers.get("location")).hostname, "admin.silentstream.win");
  }
  const result = await worker.fetch(new Request("https://agent.silentstream.win/guide.html"), env);
  assert.equal(result.status, 404);
  for (const path of ["/site/index.html", "/site/guide.html", "/api/control/session"]) {
    const result = await worker.fetch(new Request(`${env.PUBLIC_BASE_URI}${path}`), env);
    assert.equal(result.status, path.startsWith("/api/") ? 401 : 302);
    assert.equal(await result.text(), "");
  }
});

test("room scripts, manifests and icons never bypass authentication or room permission", async () => {
  const paths = ["/manifest.webmanifest", "/service-worker.js", "/app.js", "/upload-view.js",
    "/icons/apple-touch-icon.png", "/icons/icon-192.png", "/icons/icon-512.png", "/icons/icon-maskable.png"];
  for (const room of ["m111", "m112", "m113", "baek202"]) {
    for (const method of ["GET", "HEAD"]) {
      for (const path of paths) {
        const url = `https://${room}.silentstream.win${path}?v=test`;
        const denied = await worker.fetch(new Request(url, { method }), env);
        assert.equal(denied.status, 302);
        assert.equal(await denied.text(), "");
        assert.equal(denied.headers.get("cache-control"), "no-store");
        const login = new URL(denied.headers.get("location"));
        assert.equal(login.pathname, "/auth/room");
        assert.equal(login.searchParams.get("room"), room);
        assert.equal(login.searchParams.get("returnTo"), `${path}?v=test`);

        for (const permitted of [false, true]) {
          const roomEnv = { ...env, CONTROL: { idFromName: name => name, get: () => ({ fetch: async request => {
            const route = new URL(request.url).pathname;
            if (route === "/oauth/session") return Response.json({ email: "manager@example.com", roomId: room });
            assert.equal(route, "/authorize-room");
            assert.deepEqual(await request.json(), { email: "manager@example.com", roomId: room });
            return new Response(null, { status: permitted ? 200 : 403 });
          } }) } };
          const result = await worker.fetch(new Request(url, { method,
            headers: { cookie: `__Host-SilentStreamSession=${sessionToken}` } }), roomEnv);
          assert.equal(result.status, permitted ? 200 : 403);
          assert.equal(result.headers.get("cache-control"), "no-store");
          if (!permitted || method === "HEAD") assert.equal(await result.text(), "");
        }
      }
    }
  }
});
