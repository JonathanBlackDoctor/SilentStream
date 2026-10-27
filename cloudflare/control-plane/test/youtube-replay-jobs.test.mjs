import test from 'node:test';
import assert from 'node:assert/strict';
import {YouTubeReplayJobs,replayCandidate,seal,unseal,YOUTUBE_SCOPE} from '../src/youtube-replay-jobs.js';
const VIDEO='video_one_1', CHANNEL='UC_test_owned_channel', NOW=Date.parse('2026-10-27T09:00:00Z');
const tokens={access_token:'test-private-access',refresh_token:'test-private-refresh',expires_in:3600,scope:YOUTUBE_SCOPE};
const replay={broadcastId:VIDEO,startedAtUtc:'2026-10-27T00:00:00Z'};
function storageFixture(){
  const values=new Map();let alarm=null,lock=Promise.resolve();
  const storage={get:async key=>structuredClone(values.get(key)),put:async(key,value)=>values.set(key,structuredClone(value)),
    delete:async key=>values.delete(key),list:async({prefix})=>new Map([...values].filter(([key])=>key.startsWith(prefix)).map(([k,v])=>[k,structuredClone(v)])),
    getAlarm:async()=>alarm,setAlarm:async value=>{alarm=value},deleteAlarm:async()=>{alarm=null},
    transaction:fn=>{const result=lock.then(()=>fn(storage));lock=result.catch(()=>{});return result}};
  return {storage,values,alarm:()=>alarm};
}
async function setup(authorized=true){
  const fixture=storageFixture();let clock=NOW;
  const env={YOUTUBE_REPLAY_ROOMS:'m111,m112,m113,baek202',YOUTUBE_TOKEN_KEY:btoa('01234567890123456789012345678901'),GOOGLE_CLIENT_ID:'client',GOOGLE_CLIENT_SECRET:'secret'};
  const device={roomId:'m112',deviceId:'device-one',installationId:'install-one',lastSeenAtUtc:'2026-10-27T07:00:00Z',app:{liveReplay:replay},
    playlists:{items:[{date:'2026-10-27',status:'ready',playlistId:'PL_owned_playlist'}]}};
  await fixture.storage.put('device:device-one',device);await fixture.storage.put('roomDevices:m112',['device-one']);
  const state={lifecycle:'complete',upload:'processed',stream:'inactive',owner:CHANNEL,marker:true,fail:false,insertThenFail:false,remote:false,inserts:0,transitions:0,calls:[]};
  const fetcher=async(address,options={})=>{
    const url=new URL(address),name=url.pathname.split('/').at(-1);state.calls.push({name,method:options.method||'GET'});
    if(state.fail)return Response.json({error:{}},{status:503});
    if(name==='token')return Response.json({...tokens,access_token:'test-renewed-access'});
    if(name==='channels')return Response.json({items:[{id:CHANNEL}]});
    if(name==='videos')return Response.json({items:[{id:VIDEO,snippet:{channelId:state.owner},status:{uploadStatus:state.upload},
      liveStreamingDetails:{actualEndTime:state.lifecycle==='complete'?'2026-10-27T08:25:00Z':undefined}}]});
    if(name==='liveBroadcasts')return Response.json({items:[{id:VIDEO,status:{lifeCycleStatus:state.lifecycle},contentDetails:{boundStreamId:'stream_owned_1'}}]});
    if(name==='liveStreams')return Response.json({items:[{status:{streamStatus:state.stream}}]});
    if(name==='transition'){state.transitions++;state.lifecycle='complete';return Response.json({});}
    if(name==='playlists')return Response.json({items:[{id:'PL_owned_playlist',snippet:{channelId:CHANNEL,
      description:state.marker?'SilentStream 날짜별 자동 재생목록\nSilentStream-Playlist-Key: m112/2026-10-27':'wrong marker'}}]});
    if(name==='playlistItems'){
      if(options.method==='POST'){
        state.inserts++;state.remote=true;
        if(state.insertThenFail){state.insertThenFail=false;throw new Error('response lost');}
        assert.equal(JSON.parse(options.body).snippet.resourceId.videoId,VIDEO);
        return Response.json({id:'membership'});
      }
      return Response.json({items:state.remote?[{id:'membership'}]:[]});
    }
    throw new Error('Unexpected test resource '+name);
  };
  const create=()=>new YouTubeReplayJobs({storage:fixture.storage},env,{fetch:fetcher,now:()=>clock});
  const service=create();
  if(authorized)await service.register(tokens);else await service.observe(device);
  const key='ytReplay:m112:'+VIDEO;
  return {...fixture,env,device,state,service,create,key,advance:ms=>{clock+=ms},setClock:ms=>{clock=ms}};
}
test('KST deadline is fixed and invalid room, future, ancient and weekend observations are excluded',()=>{
  assert.equal(replayCandidate('m112',replay,NOW).dueAt,Date.parse('2026-10-27T08:25:00Z'));
  for(const [room,item] of [['unknown',replay],['m112',{...replay,room:'m111'}],['m112',{...replay,startedAtUtc:'2026-11-30T00:00:00Z'}],['m112',{...replay,startedAtUtc:'2026-01-01T00:00:00Z'}],['m112',{...replay,startedAtUtc:'2026-10-24T00:00:00Z'}]])assert.equal(replayCandidate(room,item,NOW),null);
});
test('registered job and alarm survive a fresh service instance without another device message',async()=>{
  const app=await setup();assert.ok(app.alarm());await app.create().runDue();
  assert.equal(app.values.get(app.key).status,'completed');assert.equal(app.state.inserts,1);assert.equal(app.alarm(),null);
});
test('no work runs before the Korean last-class deadline',async()=>{
  const app=await setup();app.setClock(Date.parse('2026-10-27T08:24:59Z'));await app.service.runDue();assert.equal(app.state.inserts,0);
});
test('an absent YouTube connection is persisted and suspended until a new grant',async()=>{
  const app=await setup(false);await app.service.runDue();assert.equal(app.values.get(app.key).status,'auth-required');assert.equal(app.alarm(),null);
  await app.service.register(tokens);await app.service.runDue();assert.equal(app.values.get(app.key).status,'completed');
});
test('tokens are authenticated-encrypted at rest and excluded from status metadata',async()=>{
  const app=await setup();const raw=JSON.stringify([...app.values]);
  assert.equal(raw.includes(tokens.refresh_token),false);assert.equal(raw.includes(tokens.access_token),false);
  const status=JSON.stringify(await app.service.status('m112'));assert.equal(status.includes('encrypted'),false);assert.equal(status.includes('refresh_token'),false);
  const boxed=await seal(tokens,app.env.YOUTUBE_TOKEN_KEY,CHANNEL);
  assert.deepEqual(await unseal(boxed,app.env.YOUTUBE_TOKEN_KEY,CHANNEL),tokens);
  await assert.rejects(()=>unseal(boxed,app.env.YOUTUBE_TOKEN_KEY,'other-channel'));
});
test('archives being processed remain pending and are retried later',async()=>{
  const app=await setup();app.state.upload='uploaded';await app.service.runDue();assert.equal(app.values.get(app.key).status,'waiting');assert.equal(app.state.inserts,0);
  app.state.upload='processed';app.advance(15*60000);await app.service.runDue();assert.equal(app.state.inserts,1);
});
test('an active YouTube feed is not stopped even if the PC heartbeat is stale',async()=>{
  const app=await setup();app.state.lifecycle='live';app.state.stream='active';await app.service.runDue();
  assert.equal(app.state.transitions,0);assert.equal(app.state.inserts,0);
});
test('a fresh current session is preserved even when its YouTube stream is temporarily inactive',async()=>{
  const app=await setup();app.state.lifecycle='live';await app.storage.put('device:device-one',{...app.device,lastSeenAtUtc:new Date(NOW).toISOString()});
  await app.service.runDue();assert.equal(app.state.transitions,0);assert.equal(app.values.get(app.key).inactiveSince,null);
});
test('a stale inactive orphan requires two observations before completion, then archive readiness',async()=>{
  const app=await setup();app.state.lifecycle='live';await app.service.runDue();assert.equal(app.state.transitions,0);
  app.advance(15*60000);await app.service.runDue();assert.equal(app.state.transitions,1);assert.equal(app.state.inserts,0);
  app.advance(15*60000);await app.service.runDue();assert.equal(app.state.inserts,1);
});
test('a lost insertion response is reconciled against actual membership without duplicate insertion',async()=>{
  const app=await setup();app.state.insertThenFail=true;await app.service.runDue();assert.equal(app.state.inserts,1);assert.equal(app.values.get(app.key).status,'pending');
  app.advance(5*60000);await app.create().runDue();assert.equal(app.state.inserts,1);assert.equal(app.values.get(app.key).status,'completed');
});
test('simultaneous duplicate alarm deliveries claim the job once',async()=>{
  const app=await setup();await Promise.all([app.create().runDue(),app.create().runDue()]);assert.equal(app.state.inserts,1);
});
test('unrelated channels, playlist markers and revoked installations never receive writes',async()=>{
  const a=await setup();a.state.owner='UC_different_channel';await a.service.runDue();assert.equal(a.state.inserts,0);assert.equal(a.values.get(a.key).status,'auth-required');
  const b=await setup();b.state.marker=false;await b.service.runDue();assert.equal(b.state.inserts,0);assert.equal(b.values.get(b.key).status,'failed');
  const c=await setup();await c.storage.put('device:device-one',{...c.device,revokedAtUtc:'2026-10-27T00:00:00Z'});await c.service.runDue();assert.equal(c.state.inserts,0);assert.equal(c.values.get(c.key).status,'failed');
});
test('temporary failure backs off and refresh-token renewal preserves encrypted storage',async()=>{
  const app=await setup();app.state.fail=true;await app.service.runDue();assert.equal(app.values.get(app.key).nextAt,NOW+5*60000);
  app.state.fail=false;app.advance(2*3600000);await app.service.runDue();assert.equal(app.state.inserts,1);
  assert.ok(app.state.calls.some(c=>c.name==='token'));assert.equal(JSON.stringify([...app.values]).includes('test-renewed-access'),false);
});
test('grant registration rejects missing offline refresh or unsupported scope',async()=>{
  const app=await setup(false);await assert.rejects(()=>app.service.register({...tokens,refresh_token:null}));
  await assert.rejects(()=>app.service.register({...tokens,scope:'openid email profile'}));
});
test('all offline history parts are registered once when a device reconnects',async()=>{
  const app=await setup(false);
  const device={...app.device,app:{liveReplays:[replay,{...replay,broadcastId:'video_two_2'}]}};
  await app.service.observe(device);await app.service.observe(device);
  assert.equal([...app.values.keys()].filter(k=>k.startsWith('ytReplay:')).length,2);
});
test('daily channel discovery recovers a broadcast that never reached the server before power loss',async()=>{
  const app=await setup();app.env.YOUTUBE_REPLAY_DISCOVERY='true';
  await app.storage.put('ytDiscovery',{status:'pending',nextAt:NOW});
  const original=app.service.fetch;
  app.service.fetch=async(address,options)=>{
    const url=new URL(address);
    if(url.pathname.endsWith('/liveBroadcasts')&&url.searchParams.get('mine')==='true') {
      assert.equal(url.searchParams.has('broadcastStatus'),false);
      return Response.json({items:[{id:'video_lost_1',snippet:{title:'[LIVE] m112 | 2026-10-27',channelId:CHANNEL,actualStartTime:'2026-10-27T00:05:00Z'}},
        {id:'video_other_1',snippet:{title:'unrelated personal broadcast',channelId:CHANNEL,actualStartTime:'2026-10-27T00:05:00Z'}}]});
    }
    return original(address,options);
  };
  await app.service.discoverDue();
  assert.ok(app.values.has('ytReplay:m112:video_lost_1'));
  assert.equal(app.values.has('ytReplay:m112:video_other_1'),false);
  assert.equal(app.values.get('ytDiscovery').nextAt,Date.parse('2026-10-28T08:25:00Z'));
});
test('a stale refresh result cannot replace a freshly reconnected grant',async()=>{
  const app=await setup();const old=await app.storage.get('ytGrant:'+CHANNEL);
  await app.service.register(tokens);await app.service.updateGrant(old,{status:'auth-required'});
  assert.equal(app.values.get('ytGrant:'+CHANNEL).status,'connected');
  assert.notEqual(app.values.get('ytGrant:'+CHANNEL).connectionId,old.connectionId);
});
