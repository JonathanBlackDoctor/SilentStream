import {validateChunk} from "./usb-documents.js";
const API="https://www.googleapis.com/drive/v3";
const SCOPE="https://www.googleapis.com/auth/drive";
const CHUNK=256*1024;
const bytes=s=>new TextEncoder().encode(s);
const hex=b=>Array.from(new Uint8Array(b),v=>v.toString(16).padStart(2,"0")).join("");
const hash=async s=>hex(await crypto.subtle.digest("SHA-256",bytes(s)));
const base64=b=>btoa(String.fromCharCode(...new Uint8Array(b)));
const unbase64=s=>Uint8Array.from(atob(s),c=>c.charCodeAt(0));
const email=s=>typeof s==="string"&&/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(s)?s.trim().toLowerCase():"";
const id=s=>typeof s==="string"&&/^[A-Za-z0-9_-]{10,200}$/.test(s);
export class DriveFailure extends Error {}
export class CentralDocumentDrive {
  constructor(plane,send=(input,init)=>fetch(input,init)){this.plane=plane;this.storage=plane.state.storage;this.env=plane.env;this.send=send;}
  async seal(value,aad){
    if(!this.env.DOCUMENT_DRIVE_TOKEN_KEY)throw new DriveFailure("중앙 Drive 암호화 키 설정이 필요합니다.");
    const key=await crypto.subtle.importKey("raw",unbase64(this.env.DOCUMENT_DRIVE_TOKEN_KEY),"AES-GCM",false,["encrypt"]);
    const iv=crypto.getRandomValues(new Uint8Array(12));
    return {iv:base64(iv),data:base64(await crypto.subtle.encrypt({name:"AES-GCM",iv,additionalData:bytes(aad)},key,bytes(JSON.stringify(value))))};
  }
  async open(value,aad){
    const key=await crypto.subtle.importKey("raw",unbase64(this.env.DOCUMENT_DRIVE_TOKEN_KEY),"AES-GCM",false,["decrypt"]);
    return JSON.parse(new TextDecoder().decode(await crypto.subtle.decrypt({name:"AES-GCM",iv:unbase64(value.iv),additionalData:bytes(aad)},key,unbase64(value.data))));
  }
  async status(){
    const state=await this.storage.get("centralDrive")||{};
    return {configured:!!this.env.DOCUMENT_DRIVE_TOKEN_KEY,connected:!!state.credentials,account:state.account||"",
      error:state.error||"",message:state.credentials?"중앙 공용 계정 연결됨 · 각 PC의 Google 로그인 불필요":"중앙에서 공용 계정을 한 번 연결하세요."};
  }
  async begin(admin){
    if(await this.plane.role(admin)!=="admin")throw new DriveFailure("관리자 권한이 필요합니다.");
    const mappings=[...(await this.storage.list({prefix:"driveFolder:"})).values()];
    const accounts=[...new Set(mappings.filter(p=>p.revision).map(p=>p.account))];
    if(accounts.length!==1||!email(accounts[0]))throw new DriveFailure("기기별 폴더에 같은 공용 소유 계정을 먼저 지정하세요.");
    await this.seal({}, "configuration-check");
    const state="drive-"+base64(crypto.getRandomValues(new Uint8Array(32))).replaceAll("+","-").replaceAll("/","_").replaceAll("=","");
    const verifier=base64(crypto.getRandomValues(new Uint8Array(64))).replaceAll("+","-").replaceAll("/","_").replaceAll("=","");
    const challenge=base64(await crypto.subtle.digest("SHA-256",bytes(verifier))).replaceAll("+","-").replaceAll("/","_").replaceAll("=","");
    await this.storage.put("centralDriveOAuth:"+await hash(state),{admin,account:accounts[0],verifier,expires:Date.now()+600000});
    const url=new URL("https://accounts.google.com/o/oauth2/v2/auth");
    for(const [k,v] of Object.entries({client_id:this.env.GOOGLE_CLIENT_ID,redirect_uri:this.env.PUBLIC_BASE_URI.replace(/\/$/,"")+"/auth/callback",
      response_type:"code",scope:"openid email "+SCOPE,state,code_challenge:challenge,code_challenge_method:"S256",
      access_type:"offline",prompt:"consent select_account",login_hint:accounts[0]}))url.searchParams.set(k,v);
    return {url:String(url),state};
  }
  async callback({state,cookieState,code}){
    if(typeof state!=="string"||state!==cookieState||!state.startsWith("drive-"))throw new DriveFailure("Drive 연결 요청의 브라우저 확인이 일치하지 않습니다.");
    const key="centralDriveOAuth:"+await hash(state),saved=await this.storage.get(key);
    await this.storage.delete(key);
    if(!saved||saved.expires<=Date.now()||await this.plane.role(saved.admin)!=="admin")throw new DriveFailure("Drive 연결 요청이 만료됐습니다.");
    const r=await this.send("https://oauth2.googleapis.com/token",{method:"POST",redirect:"manual",signal:AbortSignal.timeout(30000),body:new URLSearchParams({
      code,client_id:this.env.GOOGLE_CLIENT_ID,client_secret:this.env.GOOGLE_CLIENT_SECRET,
      redirect_uri:this.env.PUBLIC_BASE_URI.replace(/\/$/,"")+"/auth/callback",grant_type:"authorization_code",code_verifier:saved.verifier})});
    if(!r.ok)throw new DriveFailure("Google 승인 코드를 교환하지 못했습니다. 중앙에서 다시 연결하세요.");
    const t=await r.json();
    if(!t.refresh_token||!t.access_token||!String(t.scope||"").split(" ").includes(SCOPE))throw new DriveFailure("공용 Drive 권한 전체를 승인하고 다시 연결하세요.");
    const identity=await this.request("/about?fields=user(emailAddress)",{token:t.access_token});
    if(email(identity.user?.emailAddress)!==saved.account)throw new DriveFailure("지정한 공용 소유 계정으로 연결하세요. 다른 계정은 저장하지 않았습니다.");
    const current=await this.storage.get("centralDrive");
    if(current?.account&&current.account!==saved.account)throw new DriveFailure("기존 공용 소유 계정은 변경할 수 없습니다.");
    if(await this.plane.role(saved.admin)!=="admin")throw new DriveFailure("Drive 연결 요청 관리자의 권한이 변경됐습니다.");
    const credentials=await this.seal({refreshToken:t.refresh_token},"centralDrive");
    await this.storage.put("centralDrive",{account:saved.account,credentials,connectedBy:saved.admin,error:""});
    this.access={token:t.access_token,expires:Date.now()+Math.min(Number(t.expires_in)||3600,3600)*1000-60000};
    for(const [key,p] of await this.storage.list({prefix:"driveFolder:"})){
      await this.storage.put(key,{...p,central:true,revision:crypto.randomUUID().replaceAll("-",""),verified:false,attempted:"",error:""});
    }
    await this.wake();
  }
  async token(){
    if(this.access?.expires>Date.now())return this.access.token;
    const state=await this.storage.get("centralDrive");
    if(!state?.credentials)throw new DriveFailure("중앙에서 공용 계정을 연결하세요.");
    if(state.error)throw new DriveFailure(state.error);
    try{
      const stored=await this.open(state.credentials,"centralDrive");
      const response=await this.send("https://oauth2.googleapis.com/token",{method:"POST",redirect:"manual",signal:AbortSignal.timeout(30000),body:new URLSearchParams({
        client_id:this.env.GOOGLE_CLIENT_ID,client_secret:this.env.GOOGLE_CLIENT_SECRET,refresh_token:stored.refreshToken,grant_type:"refresh_token"})});
      if(!response.ok)throw new DriveFailure("공용 Google 연결이 만료되거나 취소됐습니다. 중앙에서 다시 연결하세요.");
      const value=await response.json();if(!value.access_token)throw new DriveFailure("공용 Google 연결을 다시 확인하세요.");
      this.access={token:value.access_token,expires:Date.now()+Math.min(Number(value.expires_in)||3600,3600)*1000-60000};
      return value.access_token;
    }catch{
      const error="공용 Google 연결을 갱신하지 못했습니다. 중앙에서 다시 연결하세요.";
      await this.storage.put("centralDrive",{...state,error});throw new DriveFailure(error);
    }
  }
  async request(path,{method="GET",body,token,raw=false,headers={}}={}){
    const url=path.startsWith("/")?API+path:path;
    const parsed=new URL(url);
    if(parsed.protocol!=="https:"||parsed.hostname!=="www.googleapis.com"
      ||!(parsed.pathname.startsWith("/drive/v3/")||parsed.pathname==="/upload/drive/v3/files"))throw new DriveFailure("Google Drive 요청 주소를 확인할 수 없습니다.");
    const response=await this.send(url,{method,redirect:"manual",signal:AbortSignal.timeout(30000),headers:{authorization:"Bearer "+(token||await this.token()),...headers},body});
    if(raw){
      // Consume tiny metadata/upload replies before the next request releases an outbound slot.
      const data=await response.arrayBuffer();
      if(data.byteLength>1048576)throw new DriveFailure("Google 응답 크기 한도를 초과했습니다.");
      return new Response(data,{status:response.status,headers:response.headers});
    }
    if(!response.ok){
      let reason="";try{reason=(await response.json()).error?.errors?.[0]?.reason||""}catch{}
      throw new DriveFailure(reason==="accessNotConfigured"?"공용 Google 프로젝트에서 Drive API를 사용 설정하세요.":"Google Drive 요청 실패. 연결·권한·폴더·저장용량을 확인하고 재시도하세요.");
    }
    return response.status===204?{}:response.json();
  }
  async permissions(p){
    const result=[];let page="";
    for(let n=0;n<10;n++){
      const data=await this.request("/files/"+p.folderId+"/permissions?pageSize=100&fields=nextPageToken,permissions(id,type,role,emailAddress,permissionDetails(inherited))"+(page?"&pageToken="+encodeURIComponent(page):""));
      if(!Array.isArray(data.permissions))throw new DriveFailure("Drive 권한 응답을 확인할 수 없습니다.");
      result.push(...data.permissions);page=data.nextPageToken||"";
      if(!page)return result;
    }throw new DriveFailure("Drive 권한 조회 한도를 초과했습니다.");
  }
  owner(v,p){return v.type==="user"&&v.role==="owner"&&email(v.emailAddress)===p.account;}
  reader(v,p){return p.readerEmail&&v.type==="user"&&email(v.emailAddress)===p.readerEmail;}
  verifyPermissions(values,p){
    if(!values.some(v=>this.owner(v,p))||values.some(v=>!this.owner(v,p)&&!(this.reader(v,p)&&v.role==="reader"))
      ||p.readerEmail&&p.readerEmail!==p.account&&!values.some(v=>this.reader(v,p)&&v.role==="reader"))
      throw new DriveFailure("Drive 폴더의 소유자·담당자 전용 권한 검증에 실패했습니다.");
  }
  async restrict(p){
    const state=await this.storage.get("centralDrive");
    if(!id(p.folderId)||state?.account!==p.account)throw new DriveFailure("공용 소유 계정과 기기 폴더 설정이 일치하지 않습니다.");
    if(p.readerEmail&&await this.plane.role(p.readerEmail,p.roomId)==="none")throw new DriveFailure("자료 담당자의 중앙 호실 권한을 확인하세요.");
    const folder=await this.request("/files/"+p.folderId+"?fields=id,mimeType,trashed,owners(emailAddress),capabilities(canAddChildren)");
    if(folder.mimeType!=="application/vnd.google-apps.folder"||folder.trashed||!folder.capabilities?.canAddChildren
      ||!folder.owners?.some(v=>email(v.emailAddress)===p.account))throw new DriveFailure("공용 계정이 소유하고 업로드할 수 있는 폴더를 선택하세요.");
    const values=await this.permissions(p);
    if(!values.some(v=>this.owner(v,p)))throw new DriveFailure("공용 계정 소유 권한을 확인할 수 없습니다.");
    for(const v of values){
      if(this.owner(v,p)||this.reader(v,p)&&v.role==="reader")continue;
      if(v.permissionDetails?.some(d=>d.inherited))throw new DriveFailure("상위 폴더의 외부 공유를 해제하고 중앙에서 재시도하세요.");
      await this.request("/files/"+p.folderId+"/permissions/"+encodeURIComponent(v.id),{method:this.reader(v,p)?"PATCH":"DELETE",
        ...(this.reader(v,p)?{body:JSON.stringify({role:"reader"}),headers:{"content-type":"application/json"}}:{})});
    }
    if(p.readerEmail&&p.readerEmail!==p.account&&!values.some(v=>this.reader(v,p)))
      await this.request("/files/"+p.folderId+"/permissions?sendNotificationEmail=false",{method:"POST",headers:{"content-type":"application/json"},
        body:JSON.stringify({type:"user",role:"reader",emailAddress:p.readerEmail})});
    this.verifyPermissions(await this.permissions(p),p);
  }
  async applyPolicy(key,p){
    if(!p.central||p.verified||p.attempted===p.revision)return;
    await this.storage.put(key,{...p,attempted:p.revision});
    try{
      await this.restrict(p);
      await this.storage.transaction(async s=>{
        const current=await s.get(key);
        if(current?.revision===p.revision)await s.put(key,{...current,verified:true,verifiedBy:"central",effectiveReaderEmail:p.readerEmail,error:""});
      });
    }catch(e){
      await this.storage.transaction(async s=>{
        const current=await s.get(key);
        if(current?.revision===p.revision)await s.put(key,{...current,verified:false,error:e instanceof DriveFailure?e.message:"중앙 Drive 권한 적용 실패. 연결 확인 후 재시도하세요."});
      });
    }
  }
  async wake(delay=1000){
    const alarm=await this.storage.getAlarm();
    if(!alarm||alarm>Date.now()+delay)await this.storage.setAlarm(Date.now()+delay);
  }
  async retry(){
    for(const [key,p] of await this.storage.list({prefix:"driveFolder:"})){
      if(p.central)await this.storage.put(key,{...p,attempted:"",verified:false,error:""});
      await this.storage.delete("centralScan:"+p.roomId);
    }
    await this.storage.put("centralRetry",{cursor:""});
    await this.wake();
  }
  binding(room){
    const socket=this.plane.state.getWebSockets(room)[0];return socket?.deserializeAttachment();
  }
  async usb(room,operation,payload){
    const p=await this.storage.get("driveFolder:"+room);
    const response=await this.plane.usbRequest({roomId:room,email:p.requestedBy,operation,payload});
    if(!response.ok)throw new DriveFailure("PC 자료를 읽지 못했습니다. 연결·앱 버전·보관 상태를 확인하고 재시도하세요.");
    return response.json();
  }
  async discover(p){
    const binding=this.binding(p.roomId);if(!binding)return;
    if(await this.storage.get("centralScan:"+p.roomId))return;
    const device=await this.storage.get("device:"+binding.deviceId);
    if(!device?.app?.centralDocumentDrive||device.revokedAtUtc||device.installationId!==binding.installationId)return;
    const cursorKey="centralCursor:"+p.roomId;
    const skip=await this.storage.get(cursorKey)||0;
    const list=await this.usb(p.roomId,"list",{skip,date:null});
    if(!list.enabled)return;
    if(!Array.isArray(list.items)||list.items.length>100||!Number.isSafeInteger(list.total))throw new DriveFailure("PC 자료 목록 형식이 올바르지 않습니다.");
    for(const item of list.items){
      if(!["ready","duplicate"].includes(item.status)||!/^[a-f0-9]{64}$/i.test(item.sha256||"")
        ||!/^[a-f0-9]{32}$/i.test(item.id||"")||!/^\d{4}-\d{2}-\d{2}$/.test(item.date||"")||!Number.isSafeInteger(item.size)||item.size<1)continue;
      const key="centralUpload:"+await hash("central-drive-v1\n"+p.account+"\n"+p.folderId+"\n"+item.sha256.toLowerCase());
      const existing=await this.storage.get(key);
      if(existing){
        if(existing.status==="queued"&&existing.sha256===item.sha256.toLowerCase()&&existing.size===item.size)
          await this.storage.put(key,{...existing,sourceId:item.id,date:item.date,deviceId:binding.deviceId,installationId:binding.installationId,revision:p.revision});
        continue;
      }
      await this.storage.put(key,{roomId:p.roomId,folderId:p.folderId,revision:p.revision,sourceId:item.id,
        sha256:item.sha256.toLowerCase(),size:item.size,date:item.date,deviceId:binding.deviceId,installationId:binding.installationId,
        status:item.size>256*1024*1024?"failed":"queued",offset:0,error:item.size>256*1024*1024?"256 MiB를 넘는 파일은 수동으로 업로드하세요.":""});
      // One new file per room; retain page until remaining entries are examined.
      return;
    }
    await this.storage.put(cursorKey,skip+list.items.length>=list.total?0:skip+list.items.length);
  }
  verifyFile(value,r){
    if(value.id!==r.remoteId||value.trashed||value.size!==String(r.size)||value.sha256Checksum?.toLowerCase()!==r.sha256
      ||!value.parents?.includes(r.folderId)||value.appProperties?.silentStreamSha256!==r.sha256)
      throw new DriveFailure("Drive 파일 크기·내용·기기 폴더 검증에 실패했습니다.");
  }
  async metadata(r){
    return this.request("/files/"+r.remoteId+"?fields=id,size,sha256Checksum,parents,trashed,appProperties",{raw:true});
  }
  async transfer(key,r){
    const p=await this.storage.get("driveFolder:"+r.roomId),binding=this.binding(r.roomId);
    if(!binding)return false; // Offline is a gate, not a failed transfer.
    if(!p?.central||!p.verified||!p.readerEmail)return false;
    if(p.folderId!==r.folderId||p.revision!==r.revision||binding.deviceId!==r.deviceId||binding.installationId!==r.installationId)
      throw new DriveFailure("기기 또는 담당자 설정이 변경됐습니다. 중앙에서 확인 후 재시도하세요.");
    if(r.startedAt&&Date.now()-r.startedAt>600000)throw new DriveFailure("파일 전송 10분 한도를 초과했습니다. 연결 확인 후 재시도하세요.");
    this.verifyPermissions(await this.permissions(p),p);
    if(!r.remoteId){
      const generated=await this.request("/files/generateIds?count=1&space=drive&type=files");
      if(!id(generated.ids?.[0]))throw new DriveFailure("Drive 파일 번호를 받지 못했습니다.");
      r.remoteId=generated.ids[0];await this.storage.put(key,r);
    }
    const existing=await this.metadata(r);
    if(existing.ok){
      this.verifyFile(await existing.json(),r);await this.storage.put(key,{...r,status:"uploaded",encryptedSession:null,error:""});return true;
    }
    if(existing.status!==404)throw new DriveFailure("Drive 기존 파일 상태를 확인하지 못했습니다.");
    if(!r.encryptedSession){
      const first=await this.usb(r.roomId,"chunk",{id:r.sourceId,offset:0});validateChunk(first,r.sourceId,0);
      if(first.sha256.toLowerCase()!==r.sha256||first.size!==r.size||!/\.(pdf|pptx?)$/i.test(first.name))throw new DriveFailure("PC 보관 자료가 변경됐습니다.");
      const nameHash=await hash(first.name);
      const mime=/\.pdf$/i.test(first.name)?"application/pdf":/\.ppt$/i.test(first.name)?"application/vnd.ms-powerpoint":"application/vnd.openxmlformats-officedocument.presentationml.presentation";
      const begin=await this.request("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable",{method:"POST",raw:true,
        headers:{"content-type":"application/json","x-upload-content-length":String(r.size),"x-upload-content-type":mime},
        body:JSON.stringify({id:r.remoteId,name:r.date+" "+first.name,mimeType:mime,parents:[r.folderId],appProperties:{silentStreamSha256:r.sha256}})});
      if(!begin.ok)throw new DriveFailure("Drive 파일 전송을 시작하지 못했습니다.");
      const location=begin.headers.get("location");const u=new URL(location);
      if(u.protocol!=="https:"||u.hostname!=="www.googleapis.com"||u.pathname!=="/upload/drive/v3/files")throw new DriveFailure("Drive 전송 주소를 확인하지 못했습니다.");
      r={...r,nameHash,startedAt:Date.now(),offset:0,status:"uploading",encryptedSession:await this.seal({url:location},key)};await this.storage.put(key,r);
    }
    const session=await this.open(r.encryptedSession,key);
    // Probe acknowledged offset after every checkpoint. A lost reply never causes a new remote ID.
    const probe=await this.request(session.url,{method:"PUT",raw:true,headers:{"content-range":"bytes */"+r.size},body:new Uint8Array()});
    if(probe.status===200||probe.status===201){
      const meta=await this.metadata(r);if(!meta.ok)throw new DriveFailure("Drive 전송 결과를 확인하지 못했습니다.");
      this.verifyFile(await meta.json(),r);await this.storage.put(key,{...r,status:"uploaded",encryptedSession:null,error:""});return true;
    }
    if(probe.status!==308)throw new DriveFailure("중단된 Drive 전송 상태를 확인하지 못했습니다. 중앙에서 재시도하세요.");
    const range=probe.headers.get("range"),match=range?.match(/^bytes=0-(\d+)$/);
    if(range&&!match)throw new DriveFailure("Drive 전송 위치 응답이 올바르지 않습니다.");
    r.offset=match?Number(match[1])+1:0;
    if(r.offset<0||r.offset>=r.size||r.offset%CHUNK!==0)throw new DriveFailure("Drive 전송 위치가 올바르지 않습니다.");
    for(let n=0;n<8&&r.offset<r.size;n++){
      const current=await this.storage.get("driveFolder:"+r.roomId);
      if(current?.revision!==r.revision||!current.verified)throw new DriveFailure("전송 중 자료 담당자가 변경됐습니다.");
      const chunk=await this.usb(r.roomId,"chunk",{id:r.sourceId,offset:r.offset});
      const data=validateChunk(chunk,r.sourceId,r.offset);
      if(chunk.size!==r.size||chunk.sha256.toLowerCase()!==r.sha256||await hash(chunk.name)!==r.nameHash)throw new DriveFailure("전송 중 PC 자료가 변경됐습니다.");
      const end=r.offset+data.length;
      const sent=await this.request(session.url,{method:"PUT",raw:true,headers:{"content-type":"application/octet-stream",
        "content-range":"bytes "+r.offset+"-"+(end-1)+"/"+r.size},body:data});
      if(end===r.size){
        if(sent.status!==200&&sent.status!==201)throw new DriveFailure("Drive 전송 완료를 확인하지 못했습니다.");
        const meta=await this.metadata(r);if(!meta.ok)throw new DriveFailure("Drive 저장 결과를 확인하지 못했습니다.");
        this.verifyFile(await meta.json(),r);r={...r,status:"uploaded",encryptedSession:null,error:""};
      }else{
        if(sent.status!==308||sent.headers.get("range")!=="bytes=0-"+(end-1))throw new DriveFailure("Drive 조각 저장 위치를 확인하지 못했습니다.");
        r={...r,status:"uploading",offset:end};
      }
      await this.storage.put(key,r);
      if(r.status==="uploaded")return true;
    }
    return true;
  }
  async run(){
    if(this.running)return;
    this.running=true;let work=false;
    try{
      const state=await this.storage.get("centralDrive");if(!state?.credentials||state.error)return;
      const retry=await this.storage.get("centralRetry");
      if(retry){
        const page=await this.storage.list({prefix:"centralUpload:",limit:100,...(retry.cursor?{startAfter:retry.cursor}:{})});
        for(const [key,r] of page)if(r.status==="failed"){
          const policy=await this.storage.get("driveFolder:"+r.roomId);
          await this.storage.put(key,{...r,status:"queued",revision:policy?.revision||r.revision,startedAt:Date.now(),error:""});
        }
        if(page.size===100)await this.storage.put("centralRetry",{cursor:[...page.keys()].at(-1)});
        else await this.storage.delete("centralRetry");
        work=true;
      }
      for(const [key,p] of await this.storage.list({prefix:"driveFolder:"})){
        await this.applyPolicy(key,p);
        const current=await this.storage.get(key);
        if(current.central&&current.verified&&current.readerEmail&&this.binding(current.roomId)){
          try{await this.discover(current);}
          catch{await this.storage.put("centralScan:"+current.roomId,{error:"PC 자료 목록 확인 실패. 중앙 재시도에서 다시 확인합니다."});}
        }
      }
      const cursor=await this.storage.get("centralUploadCursor");
      const records=await this.storage.list({prefix:"centralUpload:",limit:100,...(cursor?{startAfter:cursor}:{})});
      await this.storage.put("centralUploadCursor",records.size===100?[...records.keys()].at(-1):"");
      if(records.size===100)work=true;
      for(const [key,r] of records){
        if(!["queued","uploading"].includes(r.status)||!this.binding(r.roomId))continue;
        const policy=await this.storage.get("driveFolder:"+r.roomId);
        if(!policy?.central||!policy.verified||!policy.readerEmail)continue;
        try{const progressed=await this.transfer(key,r);work=work||progressed;if(!progressed)continue;}
        catch(e){
          const current=await this.storage.get(key);
          await this.storage.put(key,{...current,status:"failed",error:e instanceof DriveFailure?e.message:"중앙 Drive 전송 실패. 연결·자료를 확인하고 재시도하세요."});
        }
        break; // One file, at most eight 256 KiB chunks per alarm.
      }
    }catch{
      const state=await this.storage.get("centralDrive");
      if(state)await this.storage.put("centralDrive",{...state,error:"중앙 Drive 작업이 중단됐습니다. 공용 계정 연결을 확인하세요."});
    }finally{
      this.running=false;
      const state=await this.storage.get("centralDrive");
      if(state?.credentials&&!state.error&&this.plane.state.getWebSockets().length)await this.wake(work?1000:60000);
    }
  }
}
