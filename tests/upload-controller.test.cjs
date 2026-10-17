const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const root=path.resolve(__dirname,'..'),html=fs.readFileSync(path.join(root,'src/SilentStream.App/Remote/index.html'),'utf8');
const shared=fs.readFileSync(path.join(root,'cloudflare/control-plane/public/room/upload-view.js'),'utf8');
const source=html.slice(html.indexOf('    let lastUploadStatus='),html.indexOf('    function renderStatus(s)'));
function setup(){
  const fields={};const $=id=>fields[id]??={value:'',textContent:'',innerHTML:''};
  const context=vm.createContext({Date,Intl,$,cur:{label:'M112'},stateOf:()=>({online:true})});
  vm.runInContext(shared+'\n'+source,context);
  return {context,fields,run:s=>vm.runInContext(s,context)};
}
test('direct controller preserves date selection across polling and clears another room',()=>{
  const app=setup(),today=new Intl.DateTimeFormat('sv-SE',{timeZone:'Asia/Seoul'}).format(new Date());
  app.context.sample={updatedAtUtc:new Date().toISOString(),queue:{items:[{date:today,period:1,status:'completed',videoId:'room112'}]}};
  app.run('directUploadRoom=cur;lastUploadStatus=sample;renderUploads()');
  assert.match(app.fields.qList.innerHTML,/room112/);
  app.fields.directUploadDate.value='2026-09-01';app.run('renderUploads()');
  assert.doesNotMatch(app.fields.qList.innerHTML,/room112/);
  assert.equal(app.fields.directUploadDate.value,'2026-09-01');
  app.run('cur={label:"M111"};renderUploads()');
  assert.doesNotMatch(app.fields.qList.innerHTML,/room112/);
  assert.match(app.fields.qList.innerHTML,/이 호실의 업로드 정보를 기다리고/);
});
test('direct offline rendering suppresses estimated completion times',()=>{
  const app=setup(),today=new Intl.DateTimeFormat('sv-SE',{timeZone:'Asia/Seoul'}).format(new Date());
  app.context.sample={updatedAtUtc:new Date().toISOString(),queue:{items:[{date:today,period:1,status:'uploading',estimatedCompletedAtUtc:new Date(Date.now()+600000).toISOString()}]}};
  app.run('directUploadRoom=cur;lastUploadStatus=sample;stateOf=()=>({online:false});renderUploads()');
  assert.doesNotMatch(app.fields.qList.innerHTML,/업로드 완료 예상/);
  assert.match(app.fields.qList.innerHTML,/마지막 수신 상태/);
});
test('offline app shell includes the shared renderer and all scripts compile',()=>{
  for(const match of html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/g))new vm.Script(match[1]);
  const sw=fs.readFileSync(path.join(root,'src/SilentStream.App/Remote/service-worker.js'),'utf8');
  assert.match(sw,/"\/upload-view.js"/);
  assert.match(html,/<script src="\/upload-view.js\?v=1">/);
});
