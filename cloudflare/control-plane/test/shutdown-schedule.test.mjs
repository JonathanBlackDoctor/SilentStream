import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';
import worker, { ControlPlane } from '../src/index.js';

const roomSource = await readFile(new URL('../public/room/app.js', import.meta.url), 'utf8');
const centralSource = await readFile(new URL('../public/control/app.js', import.meta.url), 'utf8');
function setup(source=roomSource) {
  const nodes={},calls=[],messages=[];
  const context=vm.createContext({Date,esc: value=>String(value).replaceAll('<','&lt;'),
    $: id=>nodes[id]??={value:'2026-10-17T18:30'},confirm:()=>true,actionPending:false,
    api:async(path,request)=>{calls.push({path,body:JSON.parse(request.body)});return {ok:true,body:'{"ok":true,"message":"예약됨"}'}},
    show:message=>messages.push(message),load:async()=>{}});
  vm.runInContext(source.slice(0,source.indexOf('const $=')),context);
  return {context,calls,messages,nodes};
}
for(const [label,source] of [['room',roomSource],['central',centralSource]]) {
  test(label+': KST conversion is independent of browser timezone; invalid dates and past times rejected',()=>{
    const {context}=setup(source);
    vm.runInContext('Date = class extends Date { static now(){return new Date("2026-10-17T00:00:00Z").getTime()} }',context);
    assert.equal(vm.runInContext('shutdownTarget("2026-10-17T18:30")',context),'2026-10-17T09:30:00.000Z');
    for(const value of ['','2026-02-30T12:00','2026-10-17T09:01','2026-11-18T09:00'])
      assert.throws(()=>vm.runInContext(`shutdownTarget(${JSON.stringify(value)})`,context));
  });
  test(label+': offline requests can be saved, while PC cancellation requires a capable online PC',()=>{
    const {context}=setup(source);
    const render=value=>{context.device=value;return vm.runInContext('shutdownPanel(device)',context)};
    const device={roomId:'m112',role:'admin',online:true,app:{shutdownScheduleControl:true,remoteShutdownScheduleCommands:true}};
    assert.equal(render({...device,role:'manager'}),'');
    assert.doesNotMatch(render({...device,online:false}),/disabled[^>]*data-shutdown-create/);
    assert.match(render({...device,online:false}),/연결 후 종료 예약 등록/);
    assert.match(render({...device,app:{shutdownScheduleControl:true}}),/업데이트/);
    vm.runInContext('shutdownDrafts.m112="2026-10-17T18:30"',context);
    assert.match(render(device),/value="2026-10-17T18:30"/);
    const html=render({...device,app:{...device.app,shutdownSchedule:{id:'reservation',status:'pending',targetUtc:'2026-10-17T09:30:00Z',message:'<script>'}}});
    assert.match(html,/data-shutdown-cancel="m112"/);
    assert.match(html,/disabled data-shutdown-create/);
    assert.doesNotMatch(html,/<script>/);
  });
  test(label+': create and cancel send room-bound body and show PC acknowledgement',async()=>{
    const app=setup(source);
    vm.runInContext('Date = class extends Date { static now(){return new Date("2026-10-17T00:00:00Z").getTime()} }',app.context);
    await vm.runInContext('changeShutdown("m112",null)',app.context);
    assert.equal(app.calls[0].body.targetUtc,'2026-10-17T09:30:00.000Z');
    await vm.runInContext('changeShutdown("m112","reservation")',app.context);
    assert.match(app.calls[1].path,/m112\/actions\/system.shutdown.schedule.cancel$/);
    assert.equal(app.calls[1].body.id,'reservation');
    assert.equal(app.messages.at(-1),'예약됨');
  });
  test(label+': immediate shutdown requires an admin, online updated app and no active schedule',()=>{
    const {context}=setup(source);
    const render=value=>{context.device=value;return vm.runInContext('shutdownPanel(device)',context)};
    const value={roomId:'m112',role:'admin',online:true,app:{quietShutdownControl:true}};
    assert.match(render(value),/class="danger"  data-action="system.shutdown"/);
    for(const blocked of [{...value,online:false},{...value,app:{}},
      {...value,app:{...value.app,windowsShutdownPending:true}},
      {...value,cloudShutdownRequest:{state:'queued',targetUtc:new Date().toISOString()}},
      {...value,app:{...value.app,shutdownSchedule:{status:'executing',targetUtc:new Date().toISOString()}}}])
      assert.match(render(blocked),/disabled data-action="system.shutdown"/);
    assert.equal(render({...value,role:'manager'}),'');
  });
}

test('server permission rejects managers and refuses scheduling without a registered PC',async()=>{
  const values=new Map([['ownerEmail','owner@example.com'],['member:manager@example.com',{role:'manager',rooms:['m112']}]]);
  const storage={get:async key=>values.get(key),put:async(key,value)=>values.set(key,value),
    list:async()=>new Map(),delete:async key=>values.delete(key)};
  storage.transaction=async callback=>callback(storage);
  const plane=new ControlPlane({storage,getWebSockets:()=>[]},{});
  for(const action of ['system.shutdown.schedule','system.shutdown.schedule.cancel']){
    assert.equal((await plane.command({roomId:'m112',email:'manager@example.com',action,body:{}})).status,403);
    assert.equal((await plane.command({roomId:'m112',email:'owner@example.com',action,body:{}})).status,action==='system.shutdown.schedule'?409:503);
  }
});

test('room routing rejects cross-room, foreign-origin and unauthenticated scheduling',async()=>{
  const control={fetch:async request=>new URL(request.url).pathname==='/authorize-room'?Response.json({allowed:true}):Response.json({ok:true})};
  const env={PUBLIC_BASE_URI:'https://admin.silentstream.win',DEVICE_BASE_URI:'https://agent.silentstream.win',
    ENABLE_DEVELOPMENT_IDENTITY:'true',GOOGLE_CLIENT_ID:'test-client-id',GOOGLE_CLIENT_SECRET:'test-secret',
    CONTROL:{idFromName:()=>'',get:()=>control}};
  const send=(room,origin,email)=>worker.fetch(new Request('https://m112.silentstream.win/api/control/rooms/'+room+'/actions/system.shutdown.schedule',
    {method:'POST',headers:{origin,'content-type':'application/json',...(email?{'x-silentstream-dev-email':email}:{})},body:'{"targetUtc":"2026-10-17T09:30:00Z"}'}),env);
  assert.equal((await send('m112','https://m112.silentstream.win','owner@example.com')).status,200);
  assert.equal((await send('m113','https://m112.silentstream.win','owner@example.com')).status,404);
  assert.equal((await send('m112','https://evil.example','owner@example.com')).status,403);
  assert.equal((await send('m112','https://m112.silentstream.win')).status,401);
});
