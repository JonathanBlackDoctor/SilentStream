import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
import worker from '../src/index.js';
const source=await readFile(new URL('../public/room/upload-view.js',import.meta.url),'utf8');
const context=vm.createContext({Date,Intl});vm.runInContext(source,context);
const view=context.UploadView,now=Date.parse('2026-10-17T01:30:00Z'),date='2026-10-17';
const job={date,period:1,slotId:'period-1',slotLabel:'1교시',status:'uploading',
  estimatedCompletedAtUtc:'2026-10-17T01:40:00Z',uploadElapsedSeconds:120};
const app={state:'Live',recording:{file:'recording.mp4'},today:{date,periods:[
  {no:1,start:'08:25:00',end:'09:25:00',slotId:'period-1'},
  {no:2,start:'09:25:00',end:'10:25:00',slotId:'period-2'}]},queue:{items:[job],measuredCount:3,averageDurationSeconds:600}};
test('current date only, exact slot matching, and measured ETA',()=>{
  const data=view.model(app,{date,fresh:true,now});
  assert.equal(data.rows.length,2);assert.match(data.rows[0].detail,/10:40/);
  assert.equal(data.rows[1].stateLabel,'영상 준비 확인');
  assert.equal(view.model(app,{date:'2026-10-16',fresh:true,now}).rows.length,0);
});
test('offline or stale snapshot never promises current upload progress or ETA',()=>{
  const html=view.render(app,{date,fresh:false,now});
  assert.doesNotMatch(html,/10:40/);assert.match(html,/마지막 수신 상태/);
  assert.equal(view.fresh({online:false,app:{updatedAtUtc:new Date(now).toISOString()}},now),false);
  assert.equal(view.fresh({online:true,app:{updatedAtUtc:'2026-10-16T01:00:00Z'}},now),false);
  assert.equal(view.fresh({online:true,app:{updatedAtUtc:new Date(now).toISOString()}},now),true);
});
test('legacy explicit dates are preserved; unknown dates never become today',()=>{
  const legacy={queue:{items:[{...job,date:undefined,title:'[영상] m112 | 2026-10-16 | 1교시'},
    {...job,date:undefined,title:'알 수 없음'}]}};
  assert.equal(view.model(legacy,{date}).rows.length,0);
  assert.equal(view.model(legacy,{date:'2026-10-16'}).rows.length,1);
  assert.equal(view.model(legacy,{date}).unknownDate,1);
});
test('quota, retries, failed and overdue estimates remain honest',()=>{
  const quota=view.render({...app,queue:{...app.queue,pausedUntilUtc:'2026-10-17T07:00:00Z'}},{date,fresh:true,now});
  assert.match(quota,/한도 초과 재시도/);assert.doesNotMatch(quota,/10:40/);
  const retry=view.render({queue:{items:[{...job,status:'pending',attempts:1,estimatedCompletedAtUtc:null}]}},{date,fresh:true,now});
  assert.match(retry,/재시도 대기/);
  assert.match(view.render({queue:{items:[{...job,status:'failed'}]}},{date,fresh:true,now}),/실패 원인/);
  assert.doesNotMatch(view.render(app,{date,fresh:true,now:now+3600000}),/10:40/);
});
test('slot labels are escaped, video links only exist for completed transfers',()=>{
  const malicious={queue:{items:[{...job,slotLabel:'<script>',videoId:'x"><script>'}]}};
  const html=view.render(malicious,{date,fresh:true,now});
  assert.doesNotMatch(html,/<script>|href=/);assert.match(html,/&lt;script&gt;/);
  assert.match(view.render({queue:{items:[{...job,status:'completed',videoId:'abc'}]}},{date}),/https:\/\/youtu.be\/abc/);
});
test('shared renderer requires a room login and is served without public caching',async()=>{
  let path;
  const env={PUBLIC_BASE_URI:'https://admin.silentstream.win',DEVICE_BASE_URI:'https://agent.silentstream.win',
    GOOGLE_CLIENT_ID:'test-client',GOOGLE_CLIENT_SECRET:'test-secret',ENABLE_DEVELOPMENT_IDENTITY:'true',
    CONTROL:{idFromName:()=> 'global',get:()=>({fetch:async request=>{
      assert.equal(new URL(request.url).pathname,'/authorize-room');return new Response(null,{status:200});
    }})},
    ASSETS:{fetch:async request=>{path=new URL(request.url).pathname;return new Response(source,{headers:{'content-type':'text/javascript'}})}}};
  const denied=await worker.fetch(new Request('https://m112.silentstream.win/upload-view.js?v=1'),env);
  assert.equal(denied.status,302);assert.equal(path,undefined);
  const response=await worker.fetch(new Request('https://m112.silentstream.win/upload-view.js?v=1',{
    headers:{'x-silentstream-dev-email':'manager@example.com'}
  }),env);
  assert.equal(response.status,200);assert.equal(path,'/room/upload-view.js');
  assert.equal(response.headers.get('cache-control'),'no-store');
  assert.match(await response.text(),/UploadView/);
});
test('legacy lunch without slot identity matches its timetable and never duplicates a row',()=>{
 const data=view.model({queue:{items:[{date,period:0,slotLabel:"점심시간",status:"completed"}]},
   today:{date,periods:[{no:0,slotId:"lunch",slotLabel:"점심시간",start:"12:25:00",end:"13:25:00",slotOrder:5}]}},{date});
 assert.equal(data.rows.length,1);assert.equal(data.rows[0].key,"lunch");assert.equal(data.rows[0].order,5);
});
