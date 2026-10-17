const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const html=fs.readFileSync(path.join(__dirname,'../src/SilentStream.App/Remote/index.html'),'utf8');
for(const match of html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/g))new vm.Script(match[1]);
const code=html.slice(html.indexOf('    let currentShutdownSchedule='),html.indexOf('    function renderStatus(s){'));
function setup(){
 const nodes={},calls=[],messages=[];
 const $=id=>nodes[id]??={value:'2026-10-17T18:30',classList:{toggle(){}},style:{}};
 const context=vm.createContext({$,Date,cur:{label:'M112'},confirmAsk:async()=>true,
  api:async(room,path,request)=>{calls.push({room,path,body:JSON.parse(request.body)});return {json:async()=>({ok:true,message:'PC 예약됨'})}},
  toast:message=>messages.push(message),timeoutSignal:()=>undefined});
 vm.runInContext(code,context);
 vm.runInContext('Date = class extends Date { static now(){return new Date("2026-10-17T00:00:00Z").getTime()} }',context);
 return {context,nodes,calls,messages,$};
}
test('direct UI sends KST target and reports actual server acknowledgement',async()=>{
 const app=setup();
 await app.nodes.shutdownScheduleBtn.onclick();
 assert.equal(app.calls[0].path,'/api/system/shutdown/schedule');
 assert.equal(app.calls[0].body.targetUtc,'2026-10-17T09:30:00.000Z');
 assert.equal(app.messages.at(-1),'PC 예약됨');
});
test('direct cancellation uses displayed reservation ID and room change clears it',async()=>{
 const app=setup();
 vm.runInContext('renderShutdownSchedule({shutdownScheduleControl:true,shutdownSchedule:{id:"exact-id",targetUtc:"2026-10-17T09:30:00Z",status:"pending",message:"예약됨"}})',app.context);
 assert.equal(app.nodes.shutdownScheduleBtn.disabled,true);
 await app.nodes.shutdownScheduleCancelBtn.onclick();
 assert.equal(app.calls[0].body.id,'exact-id');
 vm.runInContext('renderShutdownSchedule({})',app.context);
 await app.nodes.shutdownScheduleCancelBtn.onclick();
 assert.equal(app.calls.length,1);
});
test('direct UI blocks invalid time and changing rooms during confirmation',async()=>{
 const app=setup();
 app.$('shutdownDateTime').value='2026-10-17T09:01';
 await app.nodes.shutdownScheduleBtn.onclick();
 assert.equal(app.calls.length,0);
 app.$('shutdownDateTime').value='2026-10-17T18:30';
 app.context.confirmAsk=async()=>{app.context.cur={label:'M113'};return true};
 await app.nodes.shutdownScheduleBtn.onclick();
 assert.equal(app.calls.length,0);
});
