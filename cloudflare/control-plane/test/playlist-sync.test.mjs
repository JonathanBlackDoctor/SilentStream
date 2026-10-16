import test from 'node:test';
import assert from 'node:assert/strict';
import {ControlPlane} from '../src/index.js';
const history={room:'m112',items:[{date:'2026-10-12',status:'ready',playlistId:'PL_known'}]};
function setup(){
 const values=new Map([['device:device-1',{roomId:'m112'}],['ownerEmail','owner@example.com'],['roomDevices:m112',['device-1']]]);
 const storage={async get(k){return values.get(k)},async put(k,v){values.set(k,v)},async list(){return new Map()}};
 const plane=new ControlPlane({storage,getWebSockets:()=>[]},{});
 const socket={deserializeAttachment:()=>({roomId:'m112',installationId:'install-1',deviceId:'device-1'})};
 return {values,plane,send:payload=>plane.webSocketMessage(socket,JSON.stringify({schemaVersion:1,type:'snapshot',payload:{roomId:'m112',installationId:'install-1',...payload}}))};
}
test('legacy coordinator status forwards playlist history and retains it after app failure',async()=>{
 const app=setup();await app.send({app:{playlists:history}});
 assert.equal(app.values.get('device:device-1').playlists.items[0].date,'2026-10-12');
 await app.send({app:{available:false},playlists:{available:false,statusCode:403}});
 assert.equal(app.values.get('device:device-1').playlists.items[0].playlistId,'PL_known');
});
test('failed bridge query falls back to authenticated app status',async()=>{
 const app=setup();await app.send({playlists:{available:false,statusCode:403},app:{playlists:history}});
 assert.equal(app.values.get('device:device-1').playlists.items[0].playlistId,'PL_known');
});
test('wrong room or installation cannot seed fallback history',async()=>{
 const app=setup();await app.send({roomId:'m111',app:{playlists:history}});assert.equal(app.values.get('device:device-1').playlists,undefined);
 await app.send({installationId:'other',app:{playlists:history}});assert.equal(app.values.get('device:device-1').playlists,undefined);
 await app.send({app:{playlists:{...history,room:'m111'}}});assert.equal(app.values.get('device:device-1').playlists,null);
});
