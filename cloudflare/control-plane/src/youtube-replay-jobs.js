const ROOM_IDS = ['m111','m112','m113','baek202'];
const JOB = 'ytReplay:', GRANT = 'ytGrant:';
const id = value => typeof value === 'string' && /^[A-Za-z0-9_-]{6,160}$/.test(value);
const iso = ms => new Date(ms).toISOString();
const kday = ms => new Date(ms + 9*3600000).toISOString().slice(0,10);
export const YOUTUBE_SCOPE = 'https://www.googleapis.com/auth/youtube.force-ssl';
export function replayCandidate(room, replay, now=Date.now()) {
  if (!ROOM_IDS.includes(room) || !id(replay?.broadcastId)) return null;
  if (replay.room && String(replay.room).toLowerCase() !== room) return null;
  const started=Date.parse(replay.startedAtUtc);
  if (!Number.isFinite(started) || started > now+300000 || now-started>14*86400000) return null;
  const date=kday(started), weekday=new Date(date+'T12:00:00Z').getUTCDay();
  if ([0,6].includes(weekday)) return null;
  return {room,date,broadcastId:replay.broadcastId,startedAtUtc:iso(started),
    dueAt:Date.parse(date+'T17:25:00+09:00')};
}
export async function seal(value, secret, context) {
  if (!secret) throw new Error('token-key-unavailable');
  const bytes=Uint8Array.from(atob(secret),c=>c.charCodeAt(0));
  const key=await crypto.subtle.importKey('raw',bytes,'AES-GCM',false,['encrypt']);
  const iv=crypto.getRandomValues(new Uint8Array(12)), encoder=new TextEncoder();
  const data=await crypto.subtle.encrypt({name:'AES-GCM',iv,additionalData:encoder.encode(context)},key,encoder.encode(JSON.stringify(value)));
  return {iv:btoa(String.fromCharCode(...iv)),data:btoa(String.fromCharCode(...new Uint8Array(data)))};
}
export async function unseal(value, secret, context) {
  const bytes=Uint8Array.from(atob(secret),c=>c.charCodeAt(0));
  const key=await crypto.subtle.importKey('raw',bytes,'AES-GCM',false,['decrypt']);
  const decode=s=>Uint8Array.from(atob(s),c=>c.charCodeAt(0));
  const data=await crypto.subtle.decrypt({name:'AES-GCM',iv:decode(value.iv),additionalData:new TextEncoder().encode(context)},key,decode(value.data));
  return JSON.parse(new TextDecoder().decode(data));
}
class ApiFailure extends Error {
  constructor(code, permanent=false, auth=false){super(code);this.permanent=permanent;this.auth=auth;}
}

export class YouTubeReplayJobs {
  constructor(state,env,options={}) {
    this.storage=state.storage;this.env=env;this.fetch=options.fetch || fetch;this.now=options.now || Date.now;
  }
  enabled(room) { return String(this.env.YOUTUBE_REPLAY_ROOMS || '').split(',').includes(room); }
  async observe(device) {
    if (!this.enabled(device.roomId)) return;
    const known=new Set(await this.storage.get('ytSeen:'+device.deviceId)||[]);
    const replays=[...(Array.isArray(device.app?.liveReplays)?device.app.liveReplays.slice(0,100):[]),device.app?.liveReplay];
    let changed=false;
    for(const replay of replays) {
      const candidate=replayCandidate(device.roomId,replay,this.now());
      if(!candidate||known.has(candidate.broadcastId))continue;
      await this.enqueue(device,candidate);known.add(candidate.broadcastId);changed=true;
    }
    if(changed)await this.storage.put('ytSeen:'+device.deviceId,[...known].slice(-400));
  }
  async enqueue(device,candidate) {
    if (!candidate) return;
    const key=JOB+candidate.room+':'+candidate.broadcastId;
    await this.storage.transaction(async store=>{
      if(await store.get(key))return;
      const nextAt=Math.max(candidate.dueAt,this.now());
      await store.put(key,{...candidate,deviceId:device.deviceId,installationId:device.installationId,
        status:'pending',attempts:0,nextAt,createdAtUtc:iso(this.now())});
      const alarm=await store.getAlarm();
      if(alarm===null||alarm===undefined||alarm>nextAt)await store.setAlarm(Math.max(nextAt,this.now()+1000));
    });
  }
  async seedDevices() {
    for (const room of ROOM_IDS.filter(r=>this.enabled(r))) {
      const ids=await this.storage.get('roomDevices:'+room) || [];
      for (const deviceId of ids.slice(0,20)) {
        const device=await this.storage.get('device:'+deviceId);
        if (device && !device.revokedAtUtc && device.roomId===room) await this.observe(device);
      }
    }
  }
  async arm() {
    const jobs=await this.storage.list({prefix:JOB});
    const due=[...jobs.values()].filter(j=>this.enabled(j.room)&&['pending','waiting','running'].includes(j.status))
      .map(j=>j.status==='running'?j.leaseUntil:j.nextAt).filter(Number.isFinite);
    if(this.env.YOUTUBE_REPLAY_DISCOVERY==='true') {
      const discovery=await this.storage.get('ytDiscovery');
      if(discovery?.status==='pending')due.push(discovery.nextAt);
    }
    if (!due.length) { if(this.storage.deleteAlarm) await this.storage.deleteAlarm(); return; }
    const next=Math.max(this.now()+1000,Math.min(...due));
    const current=await this.storage.getAlarm();
    if (current===null || current===undefined || Math.abs(current-next)>1000) await this.storage.setAlarm(next);
  }
  async status(room) {
    const grants=await this.storage.list({prefix:GRANT});
    const jobs=await this.storage.list({prefix:JOB+room+':'});
    return {enabled:this.enabled(room),connected:[...grants.values()].some(g=>g.status==='connected'),
      jobs:[...jobs.values()].sort((a,b)=>b.startedAtUtc.localeCompare(a.startedAtUtc)).slice(0,20)
        .map(j=>({date:j.date,status:j.status,broadcastId:j.broadcastId,error:j.error||null,completedAtUtc:j.completedAtUtc||null}))};
  }
  async register(tokens) {
    if (!this.env.YOUTUBE_TOKEN_KEY || !tokens?.access_token || !tokens.refresh_token)
      throw new ApiFailure('offline-authorization-required',true);
    if (!String(tokens.scope || '').split(' ').some(s=>[YOUTUBE_SCOPE,'https://www.googleapis.com/auth/youtube'].includes(s)))
      throw new ApiFailure('youtube-scope-required',true);
    const response=await this.google('channels',{part:'id',mine:'true'},tokens.access_token);
    const channelId=response.items?.[0]?.id;
    if (!id(channelId) || response.items.length!==1) throw new ApiFailure('channel-selection-required',true);
    await this.storage.put(GRANT+channelId,{channelId,connectionId:crypto.randomUUID(),status:'connected',connectedAtUtc:iso(this.now()),
      expiresAt:this.now()+(Number(tokens.expires_in)||3600)*1000,
      encrypted:await seal({access_token:tokens.access_token,refresh_token:tokens.refresh_token},this.env.YOUTUBE_TOKEN_KEY,channelId)});
    const jobs=await this.storage.list({prefix:JOB});
    for(const [key,job] of jobs) if(job.status==='auth-required')
      await this.storage.put(key,{...job,status:'pending',nextAt:Math.max(job.dueAt,this.now()),error:null});
    if(this.env.YOUTUBE_REPLAY_DISCOVERY==='true')await this.storage.put('ytDiscovery',{status:'pending',nextAt:this.now()+1000});
    await this.seedDevices();await this.arm();
    return {connected:true,channelId};
  }
  async updateGrant(grant,patch) {
    await this.storage.transaction(async store=>{
      const current=await store.get(GRANT+grant.channelId);
      if(current?.connectionId===grant.connectionId)await store.put(GRANT+grant.channelId,{...current,...patch});
    });
  }
  async token(grant,signal) {
    let tokens=await unseal(grant.encrypted,this.env.YOUTUBE_TOKEN_KEY,grant.channelId);
    if (grant.expiresAt>this.now()+60000) return tokens.access_token;
    const response=await this.fetch('https://oauth2.googleapis.com/token',{method:'POST',
      headers:{'content-type':'application/x-www-form-urlencoded'},signal:signal?AbortSignal.any([signal,AbortSignal.timeout(10000)]):AbortSignal.timeout(10000),
      body:new URLSearchParams({client_id:this.env.GOOGLE_CLIENT_ID,client_secret:this.env.GOOGLE_CLIENT_SECRET,
        refresh_token:tokens.refresh_token,grant_type:'refresh_token'})});
    if (!response.ok) {
      if([400,401,403].includes(response.status)) {
        await this.updateGrant(grant,{status:'auth-required'});
        throw new ApiFailure('youtube-login-required',false,true);
      }
      throw new ApiFailure('token-service-unavailable');
    }
    const renewed=await response.json();
    if(!renewed.access_token) throw new ApiFailure('token-service-unavailable');
    tokens={access_token:renewed.access_token,refresh_token:renewed.refresh_token||tokens.refresh_token};
    await this.updateGrant(grant,{status:'connected',expiresAt:this.now()+(Number(renewed.expires_in)||3600)*1000,
      encrypted:await seal(tokens,this.env.YOUTUBE_TOKEN_KEY,grant.channelId)});
    return tokens.access_token;
  }
  async google(resource,params,token,{method='GET',body,signal}={}) {
    const url=new URL('https://www.googleapis.com/youtube/v3/'+resource);
    for(const [key,value] of Object.entries(params)) if(value!==undefined&&value!==null) url.searchParams.set(key,String(value));
    const response=await this.fetch(url.toString(),{method,signal:signal?AbortSignal.any([signal,AbortSignal.timeout(10000)]):AbortSignal.timeout(10000),
      headers:{authorization:'Bearer '+token,...(body?{'content-type':'application/json'}:{})},body:body?JSON.stringify(body):undefined});
    if(!response.ok) {
      if(response.status===401) throw new ApiFailure('youtube-login-required',false,true);
      if(response.status===404) throw new ApiFailure('youtube-resource-unavailable',true);
      throw new ApiFailure('youtube-api-'+response.status);
    }
    return response.status===204?{}:response.json();
  }
  async runOne(job,signal) {
    const google=(resource,params,token,options={})=>this.google(resource,params,token,{...options,signal});
    const device=await this.storage.get('device:'+job.deviceId);
    if (!device || device.revokedAtUtc || device.installationId!==job.installationId || device.roomId!==job.room)
      throw new ApiFailure('device-registration-changed',true);
    const grants=[...(await this.storage.list({prefix:GRANT})).values()].filter(g=>g.status==='connected');
    if (!grants.length) throw new ApiFailure('youtube-login-required',false,true);
    let token,grant,video;
    for (const candidate of grants.slice(0,4)) {
      try {
        const access=await this.token(candidate,signal);
        const result=await google('videos',{part:'snippet,status,liveStreamingDetails',id:job.broadcastId},access);
        const item=result.items?.find(v=>v.id===job.broadcastId && v.snippet?.channelId===candidate.channelId);
        if(item) {token=access;grant=candidate;video=item;break;}
      } catch(error) {
        if(!error.auth)throw error;
        await this.updateGrant(candidate,{status:'auth-required'});
      }
    }
    if(!video) throw new ApiFailure('youtube-channel-not-connected',false,true);
    const actualStart=Date.parse(video.liveStreamingDetails?.actualStartTime||'');
    if(Number.isFinite(actualStart)&&kday(actualStart)!==job.date)throw new ApiFailure('broadcast-date-mismatch',true);
    const broadcasts=await google('liveBroadcasts',{part:'snippet,status,contentDetails',id:job.broadcastId},token);
    const broadcast=broadcasts.items?.find(b=>b.id===job.broadcastId);
    const wait=patch=>({status:'waiting',nextAt:this.now()+15*60000,...patch});
    if (broadcast && broadcast.status?.lifeCycleStatus!=='complete') {
      if(!['live','testing'].includes(broadcast.status?.lifeCycleStatus))return wait();
      const current=device.app?.liveReplay?.broadcastId===job.broadcastId;
      const fresh=this.now()-Date.parse(device.lastSeenAtUtc||'')<10*60000;
      if(current&&fresh)return wait({inactiveSince:null});
      const bound=broadcast.contentDetails?.boundStreamId;
      if(!id(bound))return wait();
      const streams=await google('liveStreams',{part:'status',id:bound},token);
      if(streams.items?.[0]?.status?.streamStatus!=='inactive')return wait({inactiveSince:null});
      if(!job.inactiveSince)return wait({inactiveSince:this.now()});
      if(this.now()-job.inactiveSince<5*60000)return wait();
      await google('liveBroadcasts/transition',{part:'status',id:job.broadcastId,broadcastStatus:'complete'},token,{method:'POST'});
      return wait();
    }
    const end=video.liveStreamingDetails?.actualEndTime || broadcast?.snippet?.actualEndTime;
    if(!end || !Number.isFinite(Date.parse(end)) || video.status?.uploadStatus!=='processed')return wait();
    const marker=`SilentStream-Playlist-Key: ${job.room}/${job.date}`;
    let playlist;
    if(job.playlistId) {
      const result=await google('playlists',{part:'snippet',id:job.playlistId},token);
      playlist=result.items?.[0];
    } else {
      const saved=device.playlists?.items?.find(p=>p.date===job.date && p.status==='ready' && id(p.playlistId));
      if(saved) {
        const result=await google('playlists',{part:'snippet',id:saved.playlistId},token);playlist=result.items?.[0];
      }
      if(!playlist) {
        let pageToken;
        for(let page=0;page<10;page++) {
          const result=await google('playlists',{part:'snippet',mine:'true',maxResults:50,pageToken},token);
          playlist=result.items?.find(p=>p.snippet?.description?.includes(marker));
          if(playlist || !result.nextPageToken)break;pageToken=result.nextPageToken;
        }
      }
    }
    if(!playlist)return wait();
    if(playlist.snippet?.channelId!==grant.channelId || !playlist.snippet?.description?.split(/\r?\n/).some(line=>line.trim()===marker))
      throw new ApiFailure('playlist-owner-or-marker-mismatch',true);
    const contains=await google('playlistItems',{part:'id',playlistId:playlist.id,videoId:job.broadcastId,maxResults:1},token);
    if(!contains.items?.length) await google('playlistItems',{part:'snippet'},token,{method:'POST',body:{snippet:{playlistId:playlist.id,
      resourceId:{kind:'youtube#video',videoId:job.broadcastId}}}});
    return {status:'completed',playlistId:playlist.id,completedAtUtc:iso(this.now()),error:null};
  }
  async runDue() {
    await this.discoverDue();
    const jobs=[...(await this.storage.list({prefix:JOB})).entries()]
      .filter(([,j])=>this.enabled(j.room)&&(['pending','waiting'].includes(j.status)&&j.nextAt<=this.now()||j.status==='running'&&j.leaseUntil<=this.now()))
      .sort((a,b)=>a[1].nextAt-b[1].nextAt).slice(0,6);
    for(const [key] of jobs) {
      const lease=crypto.randomUUID();
      const job=await this.storage.transaction(async store=>{
        const value=await store.get(key);
        if(!value || !(['pending','waiting'].includes(value.status)&&value.nextAt<=this.now()
          ||value.status==='running'&&value.leaseUntil<=this.now()))return null;
        const claimed={...value,status:'running',lease,leaseUntil:this.now()+180000};await store.put(key,claimed);return claimed;
      });
      if(!job)continue;
      let result;
      try {
        if(this.now()-job.dueAt>14*86400000)throw new ApiFailure('replay-needs-review',true);
        result=await this.runOne(job,AbortSignal.timeout(60000));
      } catch(error) {
        const attempts=(job.attempts||0)+1;
        result={status:error.auth?'auth-required':error.permanent||attempts>=12?'failed':'pending',attempts,
          error:error instanceof ApiFailure?error.message:'temporary-service-error',
          nextAt:this.now()+Math.min(6*3600000,5*60000*2**Math.min(attempts-1,7))};
      }
      await this.storage.transaction(async store=>{
        const current=await store.get(key);if(current?.lease!==lease)return;
        await store.put(key,{...current,...result,lease:null,leaseUntil:null});
      });
    }
    await this.arm();
  }
  async discoverDue() {
    if(this.env.YOUTUBE_REPLAY_DISCOVERY!=='true')return;
    const state=await this.storage.get('ytDiscovery');
    if(state?.status!=='pending'||state.nextAt>this.now())return;
    const grants=[...(await this.storage.list({prefix:GRANT})).values()].filter(g=>g.status==='connected');
    if(!grants.length) {await this.storage.put('ytDiscovery',{status:'auth-required'});return;}
    try {
      for(const grant of grants.slice(0,4)) {
        const token=await this.token(grant);let pageToken;
        for(let page=0;page<4;page++) {
          const response=await this.google('liveBroadcasts',{part:'snippet,status',mine:'true',broadcastType:'all',maxResults:50,pageToken},token);
          for(const item of response.items||[]) {
            const match=item.snippet?.title?.match(/^\[LIVE\]\s+(m111|m112|m113|baek202|백202)\s*\|\s*(\d{4}-\d{2}-\d{2})$/i);
            if(!match||!item.snippet?.actualStartTime||item.snippet.channelId!==grant.channelId)continue;
            const room=match[1]==='백202'?'baek202':match[1].toLowerCase();
            if(!this.enabled(room))continue;
            const candidate=replayCandidate(room,{broadcastId:item.id,startedAtUtc:item.snippet.actualStartTime},this.now());
            if(!candidate||candidate.date!==match[2])continue;
            const ids=await this.storage.get('roomDevices:'+room)||[];
            const devices=(await Promise.all(ids.slice(0,20).map(id=>this.storage.get('device:'+id))))
              .filter(d=>d&&!d.revokedAtUtc&&d.roomId===room)
              .sort((a,b)=>String(b.lastSeenAtUtc||b.registeredAtUtc||'').localeCompare(String(a.lastSeenAtUtc||a.registeredAtUtc||'')));
            if(devices[0])await this.enqueue(devices[0],candidate);
          }
          if(!response.nextPageToken)break;pageToken=response.nextPageToken;
        }
      }
      const today=kday(this.now());let nextAt;
      for(let day=0;day<8;day++) {
        const date=new Date(Date.parse(today+'T00:00:00Z')+day*86400000).toISOString().slice(0,10);
        const when=Date.parse(date+'T17:25:00+09:00'),weekday=new Date(date+'T12:00:00Z').getUTCDay();
        if(when>this.now()&&![0,6].includes(weekday)){nextAt=when;break;}
      }
      await this.storage.put('ytDiscovery',{status:'pending',nextAt,lastScannedAtUtc:iso(this.now())});
    } catch(error) {
      const attempts=(state.attempts||0)+1;
      await this.storage.put('ytDiscovery',{status:error.auth?'auth-required':attempts>=12?'failed':'pending',attempts,
        nextAt:this.now()+Math.min(6*3600000,5*60000*2**Math.min(attempts-1,7)),error:'discovery-retry-required'});
    }
  }
}
