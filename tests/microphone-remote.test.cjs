const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const html = fs.readFileSync(require("node:path").join(__dirname, "../src/SilentStream.App/Remote/index.html"), "utf8");
for (const match of html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/g))
  new vm.Script(match[1]);
const render = html.slice(html.indexOf("    function renderAudio("), html.indexOf("    function renderLevels("));
function setup(ok = true) {
  const nodes = {}, calls = [];
  const $ = id => nodes[id] ??= { value: "", textContent: "", querySelectorAll: () => [] };
  const context = vm.createContext({ $, esc: String, cur: "m112",
    updateMicWarn() {}, applySettingsLockUi() {}, handleSettingsLockError: async () => false,
    loadAudio: async () => {}, api: async (room, path, request) => {
      calls.push({ room, path, body: JSON.parse(request.body) });
      return { ok, json: async () => ok ? { message: "신호 확인 필요" } : { error: "캡처 중지" } };
    } });
  vm.runInContext(render, context);
  vm.runInContext('renderAudio({microphoneControl:true,devices:[{id:"usb",name:"USB"}],sources:[]})', context);
  $("remoteMicDevice");
  return { nodes, calls, context };
}
test("direct selection sends exact endpoint; refresh and restore use separate commands", async () => {
  const app = setup();
  app.nodes.remoteMicDevice.value = "usb";
  await app.nodes.remoteMicSelect.onclick();
  await app.nodes.remoteMicRefresh.onclick();
  await app.nodes.remoteMicRestore.onclick();
  assert.deepEqual(app.calls.map(c => c.path), ["/api/audio/select", "/api/audio/refresh", "/api/audio/restore"]);
  assert.equal(app.calls[0].body.deviceId, "usb");
  assert.equal(app.nodes.remoteMicResult.textContent, "신호 확인 필요");
});
test("stopped capture error is shown instead of a recovery success", async () => {
  const app = setup(false);
  await app.nodes.remoteMicRefresh.onclick();
  assert.equal(app.nodes.remoteMicResult.textContent, "캡처 중지");
});

test("all-input option is capability gated and sends the automatic-mode selection", async () => {
  const app = setup();
  assert.doesNotMatch(app.nodes.srcList.innerHTML, /value="\*"/);
  vm.runInContext('renderAudio({microphoneControl:true,autoIncludeMicrophones:true,devices:[],sources:[]})', app.context);
  assert.match(app.nodes.srcList.innerHTML, /value="\*" selected/);
  app.nodes.remoteMicDevice.value = "*";
  await app.nodes.remoteMicSelect.onclick();
  assert.equal(app.calls[0].body.deviceId, "*");
});
