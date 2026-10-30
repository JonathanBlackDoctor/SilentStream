import test from 'node:test';
import assert from 'node:assert/strict';
import worker, { ControlPlane } from '../src/index.js';

function setup() {
  const values = new Map([['ownerEmail','owner@example.com']]);
  const storage = {get:async k=>values.get(k), put:async(k,v)=>values.set(k,v),
    list:async ({prefix})=>new Map([...values].filter(([k])=>k.startsWith(prefix))),
    delete:async keys=>{for(const key of Array.isArray(keys)?keys:[keys])values.delete(key)}};
  storage.transaction = async callback=>callback(storage);
  const attachment = {roomId:'m112',deviceId:'device112',installationId:'installation112'};
  const sent=[];
  const socket={deserializeAttachment:()=>attachment,send:v=>sent.push(JSON.parse(v))};
  return {values,storage,sent,socket,plane:new ControlPlane({storage,getWebSockets:()=>[socket]}, {})};
}

test('room command history is authenticated, room-bound and still requires admin authorization', async()=>{
  const calls=[];
  const control={fetch:async request=>{
    const path=new URL(request.url).pathname, body=await request.json();calls.push({path,body});
    return path==='/authorize-room'?Response.json({allowed:true}):Response.json([{state:'unknown'}]);
  }};
  const env={PUBLIC_BASE_URI:'https://admin.silentstream.win',DEVICE_BASE_URI:'https://agent.silentstream.win',
    ENABLE_DEVELOPMENT_IDENTITY:'true',GOOGLE_CLIENT_ID:'test-client-id',GOOGLE_CLIENT_SECRET:'test-secret',
    CONTROL:{idFromName:()=>'',get:()=>control}};
  const send=(room,authenticated=true)=>worker.fetch(new Request(`https://m112.silentstream.win/api/control/rooms/${room}/recent-commands`,
    {headers:authenticated?{'x-silentstream-dev-email':'owner@example.com'}:{}}),env);
  assert.equal((await send('m112')).status,200);
  assert.equal(calls.at(-1).path,'/recent-commands');
  assert.equal(calls.at(-1).body.roomId,'m112');
  assert.equal((await send('m113')).status,404);
  assert.equal((await send('m112',false)).status,401);
  const {plane}=setup();
  const response=await plane.fetch(new Request('https://control/recent-commands',{method:'POST',body:JSON.stringify({email:'outsider@example.com',roomId:'m112'})}));
  assert.equal(response.status,403);
});

test('timeout stays unknown and a later acknowledgement cannot be overwritten by an older timeout',async t=>{
  const {plane,values,sent}=setup();
  t.mock.method(globalThis,'setTimeout',fn=>{queueMicrotask(fn);return 0});
  const response=await plane.command({email:'owner@example.com',roomId:'m112',action:'app.restart',body:{}});
  const result=await response.json();
  assert.equal(response.status,504);assert.equal(result.outcomeUnknown,true);assert.equal(sent.length,1);
  assert.equal(values.get('command:'+result.commandId).state,'unknown');
  await plane.completeCommand({commandId:result.commandId,ok:true,statusCode:200,completedAtUtc:'2026-10-30T02:00:02Z'});
  await plane.completeCommand(result);
  assert.equal(values.get('command:'+result.commandId).state,'completed');
  assert.equal(values.get('command:'+result.commandId).statusCode,200);
});

test('a command result must come from the original room and installation',async()=>{
  const {plane,values,socket}=setup();
  const commandId='1234567890abcdef1234567890abcdef';
  values.set('command:'+commandId,{commandId,roomId:'m112',deviceId:'device112',installationId:'installation112',state:'unknown'});
  const result=JSON.stringify({schemaVersion:1,type:'commandResult',payload:{commandId,ok:true,statusCode:200}});
  for(const attachment of [{roomId:'m113',deviceId:'device113',installationId:'installation113'},
    {roomId:'m112',deviceId:'device112',installationId:'replaced-installation'}]) {
    await plane.webSocketMessage({deserializeAttachment:()=>attachment},result);
    assert.equal(values.get('command:'+commandId).state,'unknown');
  }
  await plane.webSocketMessage(socket,result);
  assert.equal(values.get('command:'+commandId).state,'completed');
});
