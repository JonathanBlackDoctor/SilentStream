const test=require('node:test'),assert=require('node:assert/strict'),vm=require('node:vm'),fs=require('node:fs'),path=require('node:path');
const html=fs.readFileSync(path.join(__dirname,'../src/SilentStream.App/Remote/index.html'),'utf8');
const code=html.slice(html.indexOf('    let immediateShutdownInFlight='),html.indexOf('    $("systemShutdownCancelBtn").onclick'));
function setup(){
 const nodes={},calls=[],questions=[];
 const context=vm.createContext({cur:{label:'M112'},Date,setTimeout,timeoutSignal:()=>undefined,
  $:id=>nodes[id]??={},toast(){},confirmAsk:async(...args)=>{questions.push(args);return true},
  api:async(room,url,options)=>{calls.push({room,url,options});return {ok:true,json:async()=>({ok:true,confirmationId:'nonce',notBeforeUtc:'2026-10-01T00:00:00Z'})}}});
 vm.runInContext(code,context);return {context,nodes,calls,questions};
}
test('click prepares safely, asks exactly once then submits the same confirmation',async()=>{
 const app=setup();await app.nodes.systemShutdownBtn.onclick();
 assert.equal(app.questions.length,1);assert.match(app.questions[0][1],/저장/);
 assert.deepEqual(app.calls.map(c=>c.url),['/api/system/shutdown/prepare','/api/system/shutdown']);
 assert.equal(JSON.parse(app.calls[1].options.body).confirmationId,'nonce');
});
test('declining or switching rooms releases preparation without powering off',async()=>{
 for(const switchRoom of [false,true]){
  const app=setup();app.context.confirmAsk=async()=>{if(switchRoom)app.context.cur={label:'M113'};return switchRoom};
  await app.nodes.systemShutdownBtn.onclick();
  assert.deepEqual(app.calls.map(c=>c.url),['/api/system/shutdown/prepare','/api/system/shutdown/cancel']);
  assert.equal(app.calls[0].room,app.calls[1].room);
 }
});
test('repeated clicks while preparation is pending never create duplicate requests',async()=>{
 const app=setup();let ready;const blocked=new Promise(resolve=>ready=resolve),api=app.context.api;
 app.context.api=async(...args)=>{await blocked;return api(...args)};
 const first=app.nodes.systemShutdownBtn.onclick();await app.nodes.systemShutdownBtn.onclick();ready();await first;
 assert.equal(app.calls.length,2);assert.equal(app.questions.length,1);
});
