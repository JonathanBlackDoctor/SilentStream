import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
const source=await readFile(new URL('../public/room/app.js',import.meta.url),'utf8');
const upload=await readFile(new URL('../public/room/upload-view.js',import.meta.url),'utf8');
function setup(){
  const fields={},notices=[],calls=[],classes={add(){},remove(){},contains(){return false}};
  const element=id=>fields[id]??={innerHTML:'',textContent:'',value:'',classList:classes,
    addEventListener(){},setAttribute(){},showModal(){},close(){},focus(){},select(){}};
  const context=vm.createContext({Date,Intl,Set,URL,console,encodeURIComponent,clearTimeout(){},setTimeout(){},setInterval(){},
    document:{getElementById:element,querySelector:()=>({content:''}),body:{classList:classes},activeElement:null},
    location:{hostname:'m112.silentstream.win'},window:{isSecureContext:true},
    navigator:{userAgent:'test'},matchMedia:()=>({matches:true}),addEventListener(){},
    fetch:()=>new Promise(()=>{}),confirm:()=>true,fields,calls,notices});
  vm.runInContext(upload+'\n'+source,context);
  context.sample={roomId:'m112',registered:true,online:true,role:'admin',
    app:{state:'Live',updatedAtUtc:new Date().toISOString()},audio:{},playlists:null};
  vm.runInContext('device=sample;show=(message,error)=>notices.push({message,error});load=async()=>{};render()',context);
  return {context,fields,calls,notices,run:code=>vm.runInContext(code,context)};
}
test('offline hero never reuses a cached live claim and disables actions',()=>{
  const app=setup();app.run('device.online=false;render()');
  assert.match(app.fields.room.innerHTML,/PC 연결이 끊겨 있습니다/);
  assert.doesNotMatch(app.fields.room.innerHTML,/class="summary">방송 중/);
  assert.match(app.fields.room.innerHTML,/disabled data-action="live.start"/);
});
test('control layout groups advanced actions and does not render an empty replay section',()=>{
  const app=setup();
  assert.match(app.fields.room.innerHTML,/<details[^>]*data-disclosure="advanced"/);
  assert.doesNotMatch(app.fields.room.innerHTML,/라이브 다시보기 링크/);
  assert.match(app.fields.room.innerHTML,/disabled data-action="live.start"/);
  assert.match(app.fields.room.innerHTML,/<button  data-action="live.stopBroadcast"/);
  app.run('openSections.add("audio");render()');assert.match(app.fields.room.innerHTML,/data-disclosure="audio" open/);
});
test('app-level failures are shown even when the transport succeeded',async()=>{
  const app=setup();
  app.run('api=async()=>({ok:true,body:JSON.stringify({ok:false,error:"실제 앱 실패"})})');
  await app.run('act("live.retry")');
  assert.deepEqual(app.notices.map(n=>n.message),['실제 앱 실패']);
  assert.equal(app.notices[0].error,true);
});
test('stale data and duplicate clicks cannot dispatch commands',async()=>{
  const app=setup();
  app.run('api=async()=>{calls.push("sent");return {ok:true}};device.app.updatedAtUtc="2020-01-01T00:00:00Z"');
  await app.run('act("live.retry")');assert.equal(app.calls.length,0);
  app.run('device.app.updatedAtUtc=new Date().toISOString();actionPending=true');
  await app.run('act("live.retry")');assert.equal(app.calls.length,0);
});
test('playlist record filtering blocks another room even in older snapshots',()=>{
  const app=setup();
  app.run('device.playlists={room:"m111",items:[{date:koreaToday(),status:"ready",url:"https://example.com/"}]}');
  assert.equal(app.run('playlistItems().length'),0);
  assert.equal(app.run('todayPlaylist()'),null);
});
test('manual broadcast actions and upload details are collapsed by default; cloud schedule remains visible',()=>{
  const app=setup(),html=app.fields.room.innerHTML;
  const hero=html.slice(html.indexOf('class="section-card status-card"'),html.indexOf('class="content-grid"'));
  assert.doesNotMatch(hero,/data-action="live/);
  assert.match(html,/<details[^>]*data-disclosure="uploads"[^>]*>/);
  assert.doesNotMatch(html,/<details[^>]*data-disclosure="uploads"[^>]*\sopen[\s>]/);
  assert.match(html,/<section class="section-card shutdown">/);
});
test('cloud archive remains usable after the current PC status becomes unavailable',()=>{
 const app=setup();
 app.run('device.app={available:false};device.uploads={items:[{date:koreaToday(),period:1,status:"completed",videoId:"saved-video",slotLabel:"1교시"}],days:{}};openSections.add("uploads");render()');
 assert.match(app.fields.room.innerHTML,/saved-video/);
});
