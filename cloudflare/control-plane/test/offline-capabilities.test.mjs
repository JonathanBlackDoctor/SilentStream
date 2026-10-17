import test from 'node:test';
import assert from 'node:assert/strict';
import {ControlPlane} from '../src/index.js';
import {mergeUploads} from '../src/offline-history.js';
function setup(){
 const device={roomId:'m112',deviceId:'dev1',installationId:'inst1',registeredAtUtc:new Date().toISOString()};
 const values=new Map([['device:dev1',device],['roomDevices:m112',['dev1']],['ownerEmail','owner@example.com']]);
 let tail=Promise.resolve(),sockets=[];
 const storage={get:async key=>structuredClone(values.get(key)),put:async(key,value)=>values.set(key,structuredClone(value)),
   list:async({prefix}={})=>new Map([...values].filter(([key])=>!prefix||key.startsWith(prefix))),
   transaction:fn=>{const next=tail.then(()=>fn(storage));tail=next.catch(()=>{});return next}};
 const state={storage,getWebSockets:()=>sockets};
 const plane=new ControlPlane(state,{}),socket={deserializeAttachment:()=>({roomId:'m112',deviceId:'dev1',installationId:'inst1'})};
 const command=plane.command.bind(plane),future=()=>new Date(Date.now()+3600000).toISOString();
 const create=()=>command({email:'owner@example.com',roomId:'m112',action:'system.shutdown.schedule',body:{targetUtc:future()}});
 const send=app=>plane.webSocketMessage(socket,JSON.stringify({schemaVersion:1,type:'snapshot',
   payload:{roomId:'m112',installationId:'inst1',capturedAtUtc:new Date().toISOString(),app}}));
 return {plane,values,storage,state,device,socket,create,send,command,future,connect:()=>sockets=[socket]};
}
const ready={shutdownScheduleControl:true,remoteShutdownScheduleCommands:true};
test('offline admin can persist, inspect after restart, and cancel a queued request',async()=>{
 const app=setup();assert.equal((await app.create()).status,202);
 const request=app.values.get('shutdownRequest:m112');assert.equal(request.state,'queued');
 const restarted=new ControlPlane(app.state,{});
 const rooms=await(await restarted.rooms('owner@example.com')).json();
 const room=rooms.find(item=>item.roomId==="m112");
 assert.equal(room.cloudShutdownRequest.id,request.id);assert.equal(room.online,false);
 const result=await app.command({email:'owner@example.com',roomId:'m112',action:'system.shutdown.schedule.cancel',body:{id:request.id}});
 assert.equal(result.status,200);assert.equal(app.values.get('shutdownRequest:m112').state,'cancelled');
});
test('queued request only dispatches once after a capable PC snapshot',async()=>{
 const app=setup();await app.create();let calls=0;
 app.plane.command=async body=>{calls++;assert.equal(body.body.targetUtc,app.values.get('shutdownRequest:m112').targetUtc);return Response.json({ok:true,body:'{"ok":true}'})};
 await app.send({shutdownScheduleControl:true});assert.equal(calls,0);
 await Promise.all([app.send(ready),app.send(ready)]);
 assert.equal(calls,1);assert.equal(app.values.get('shutdownRequest:m112').state,'applied');
});
test('late reconnect expires a request instead of executing it',async()=>{
 const app=setup();await app.create();const request=app.values.get('shutdownRequest:m112');
 app.values.set('shutdownRequest:m112',{...request,targetUtc:new Date(Date.now()+179000).toISOString()});
 let calls=0;app.plane.command=async()=>{calls++;return Response.json({ok:true})};
 await app.send(ready);assert.equal(calls,0);assert.equal(app.values.get('shutdownRequest:m112').state,'expired');
});
test('changed PC identity and revoked admin rights cannot activate offline shutdown',async()=>{
 const app=setup();await app.create();const request=app.values.get('shutdownRequest:m112');
 app.values.set('shutdownRequest:m112',{...request,deviceId:'old-device'});
 let calls=0;app.plane.command=async()=>{calls++;return Response.json({ok:true})};await app.send(ready);assert.equal(calls,0);
 app.values.set('shutdownRequest:m112',request);app.values.set('ownerEmail','new-owner@example.com');
 await app.send(ready);assert.equal(calls,0);assert.equal(app.values.get('shutdownRequest:m112').state,'failed');
});
test('ambiguous delivery is not automatically retried on later reconnects',async()=>{
 const app=setup();await app.create();let calls=0;
 app.plane.command=async()=>{calls++;return Response.json({ok:false,statusCode:504,error:'timeout'})};
 await app.send(ready);await app.send(ready);
 assert.equal(calls,1);assert.equal(app.values.get('shutdownRequest:m112').state,'failed');
});
test('duplicate create, stale cancel, unregistered PC and manager permission are rejected',async()=>{
 const app=setup();await app.create();assert.equal((await app.create()).status,409);
 assert.equal((await app.command({email:'owner@example.com',roomId:'m112',action:'system.shutdown.schedule.cancel',body:{id:'cloud-wrong'}})).status,409);
 app.values.set('member:manager@example.com',{email:'manager@example.com',role:'manager',rooms:['m112']});
 assert.equal((await app.command({email:'manager@example.com',roomId:'m112',action:'system.shutdown.schedule',body:{targetUtc:app.future()}})).status,403);
 const empty=setup();empty.values.set('roomDevices:m112',[]);assert.equal((await empty.create()).status,409);
});
test('missing or partial PC snapshots preserve dated upload records and timetables',async()=>{
 const app=setup(),date='2026-10-17',item={date,period:1,title:'1교시',status:'completed',videoId:'video1',filePath:'SECRET'};
 await app.send({queue:{items:[item]},today:{date,periods:[{no:1,start:'08:25:00',end:'09:25:00'}]}});
 await app.send({available:false});
 const rooms=await(await app.plane.rooms('owner@example.com')).json();
 const room=rooms.find(item=>item.roomId==="m112");
 assert.equal(room.uploads.items[0].videoId,'video1');
 assert.equal(room.uploads.days[date][0].no,1);assert.doesNotMatch(JSON.stringify(room.uploads),/SECRET/);
});
test('archive rejects unknown dates and cannot regress a completed transfer',()=>{
 const item={date:'2026-10-17',period:1,title:'1교시',status:'completed',videoId:'video1'};
 const first=mergeUploads(null,{queue:{items:[item,null,{...item,date:'bad'}]}});
 const second=mergeUploads(first,{queue:{items:[{...item,status:'pending'}]}});
 assert.equal(second.items.length,1);assert.equal(second.items[0].status,'completed');
});
test('empty and partial playlist snapshots retain known ready links',async()=>{
 const app=setup();
 await app.send({playlists:{room:'m112',items:[{date:'2026-10-16',status:'ready',playlistId:'PL_previous'}]}});
 await app.send({playlists:{room:'m112',items:[{date:'2026-10-17',status:'ready',playlistId:'PL_today'}]}});
 await app.send({playlists:{room:'m112',items:[{date:'2026-10-16',status:'failed'}]}});
 const stored=app.values.get('device:dev1').playlists;
 assert.equal(stored.items.length,2);assert.equal(stored.items.find(i=>i.date==='2026-10-16').status,'ready');
});
test('maximum Unicode timetable histories also stay within the storage payload bound',()=>{
 const days={};
 for(let i=0;i<90;i++){const date=new Date(Date.UTC(2026,0,1+i)).toISOString().slice(0,10);
   days[date]=Array.from({length:30},(_,no)=>({no,slotId:"교".repeat(100),slotLabel:"시".repeat(100),start:"08:00:00",end:"09:00:00"}));}
 const result=mergeUploads({items:[],days},null);
 assert.ok(new TextEncoder().encode(JSON.stringify(result)).length<=900000);
});
