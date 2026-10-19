
const shutdownDrafts={};
function shutdownTarget(value){
  if(!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(value))throw Error('종료 날짜와 시각을 선택하세요.');
  const date=new Date(value+':00+09:00'),ms=date.getTime();
  if(!Number.isFinite(ms)||new Date(ms+9*3600000).toISOString().slice(0,16)!==value)throw Error('올바른 날짜와 시각을 선택하세요.');
  if(ms-Date.now()<180000||ms-Date.now()>30*86400000)throw Error('현재부터 3분 이후, 30일 이내의 시각을 선택하세요.');
  return date.toISOString();
}
function shutdownPanel(value){
  const app=value.app||{},state=app.shutdownSchedule,room=value.roomId,cloud=value.cloudShutdownRequest;
  if(value.role!=="admin")return "";
  const supported=app.shutdownScheduleControl&&app.remoteShutdownScheduleCommands;
  const localActive=state&&(["pending","executing"].includes(state.status)||state.status==="scheduled"&&new Date(state.targetUtc)>new Date());
  const cloudActive=cloud&&["queued","dispatching"].includes(cloud.state);
  const labels={pending:"PC 예약됨",executing:"녹화 마감·안전 점검 중",scheduled:"예약 시각까지 종료 대기",cancelled:"취소됨",failed:"실패·확인 필요",missed:"예약 시각 지남"};
  const cloudLabels={queued:"PC 연결 후 등록 대기",dispatching:"PC 전달 중 · 결과 확인 필요",applied:"PC 등록 요청 전달 완료",cancelled:"클라우드 요청 취소됨",expired:"등록 기한 만료",failed:"등록 실패 · 상태 확인 필요"};
  const disabled=value.registered===false?"disabled":"",createDisabled=disabled||cloudActive||localActive||value.online&&!supported?"disabled":"";
  const format=value=>new Date(value).toLocaleString("ko-KR",{timeZone:"Asia/Seoul"});
  const cloudInfo=cloud?'<p class="meta" role="status"><b>'+esc(cloudLabels[cloud.state]||cloud.state)+'</b> · '+esc(format(cloud.targetUtc))+'<br>'+esc(cloud.message)+'</p>':"";
  return '<section class="section-card shutdown"><h2>PC 전원</h2><p class="meta">한국 시간 · 1회 예약. PC가 꺼져 있어도 요청을 저장할 수 있습니다.</p>'+cloudInfo+
    (state?'<p class="meta" role="status">마지막 PC 기록: '+esc(labels[state.status]||state.status)+' · '+esc(format(state.targetUtc))+'<br>'+esc(state.message)+'</p>':"")+
    (!supported&&value.online?'<p class="meta">PC 앱과 연결 도우미 업데이트 후 PC 예약을 사용할 수 있습니다.</p>':"")+
    '<div class="actions"><button class="danger" '+(value.online&&app.quietShutdownControl&&!localActive&&!cloudActive&&!app.windowsShutdownPending&&!actionPending?"":"disabled")+' data-action="system.shutdown" data-room="'+esc(room)+'">지금 PC 종료</button></div>'+
    '<label for="shutdown-'+esc(room)+'">종료 날짜·시각 (한국 시간)</label><input style="display:block;width:100%;margin:10px 0" id="shutdown-'+esc(room)+'" data-shutdown-input="'+esc(room)+'" type="datetime-local" value="'+esc(shutdownDrafts[room]||"")+'" '+disabled+'>'+
    '<div class="actions"><button '+createDisabled+' data-shutdown-create="'+esc(room)+'">'+(value.online?"종료 예약":"연결 후 종료 예약 등록")+'</button>'+
    (cloud?.state==="queued"?'<button '+disabled+' data-shutdown-cancel="'+esc(room)+'" data-schedule-id="'+esc(cloud.id)+'">연결 대기 요청 취소</button>':localActive?'<button '+(value.online&&supported?"":"disabled")+' data-shutdown-cancel="'+esc(room)+'" data-schedule-id="'+esc(state.id)+'">PC 예약 취소</button>':"")+
    '</div><p class="meta">오프라인 요청은 아직 PC에 등록된 예약이 아닙니다. 종료 3분 전까지 연결되지 않으면 만료됩니다. PC 등록 후에는 휴대폰을 닫아도 실행하며, 종료 2분 전부터 녹화·방송을 마감합니다. 예약 시각에 별도 대기 안내 없이 종료합니다. 안전 점검 실패 시 종료하지 않습니다.</p></section>';
}
async function changeShutdown(room,id){
  try{
    const targetUtc=id?null:shutdownTarget($('shutdown-'+room).value);
    const question=id?'PC 종료 예약을 취소할까요?':new Date(targetUtc).toLocaleString('ko-KR',{timeZone:'Asia/Seoul'})+' (한국 시간)에 종료하도록 예약할까요? 오프라인이면 클라우드에 요청을 저장하고 PC 연결 후 등록합니다. 종료 3분 전까지 연결되지 않으면 만료됩니다.';
    if(!confirm(room.toUpperCase()+': '+question))return;
    const action=id?'system.shutdown.schedule.cancel':'system.shutdown.schedule';
    const result=await api('/api/control/rooms/'+encodeURIComponent(room)+'/actions/'+action,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(id?{id}:{targetUtc})});
    let local={};try{local=JSON.parse(result.body||'{}')}catch{}
    show(local.message||result.error||(result.ok?'예약 요청을 처리했습니다.':'예약 처리에 실패했습니다.'),!result.ok);
    if(result.ok)await load();
  }catch(error){if(error.message!=='AUTH_REQUIRED')show(error.message,true)}
}

const $=id=>document.getElementById(id),roomEl=$("room"),notice=$("notice"),detail=$("detail");
const roomId=location.hostname.split(".")[0].toLowerCase();
const fallbackNames={m111:"M111",m112:"M112",m113:"M113",baek202:"백202"};
const HISTORY_PAGE_SIZE=5;
let session=null,device=null,installPrompt=null,playlistHistoryOpen=false,playlistHistoryAnchor="",uploadDate="",actionPending=false;
const openSections=new Set();
function kstTime(value,date=false){return UploadView.time(value,date)}
function controllerFresh(value){return !actionPending&&UploadView.fresh(value)}
function uploadPanel(value){
  if(!value.registered)return "";
  const date=uploadDate||koreaToday(),app=archivedApp(value,date),data=UploadView.model(app,{date});
  return `<details class="section-card uploads" data-disclosure="uploads" aria-label="교시별 영상 업로드" ${openSections.has("uploads")?"open":""}><summary>교시별 업로드 <span>완료·대기·예상 시간</span></summary><div class="fold-body"><div class="playlist-heading"><div><p class="section-kicker">CLASS VIDEOS</p><h2>교시별 업로드</h2></div><label class="upload-date-label" for="uploadDate">날짜<input id="uploadDate" type="date" max="${koreaToday()}" value="${esc(date)}"></label></div>${data.latest&&data.latest!==date?`<button class="text-button latest-upload" data-upload-latest="${esc(data.latest)}">최근 기록 ${esc(data.latest)} 보기 ↗</button>`:""}${UploadView.render(app,{date,fresh:UploadView.fresh(value)})}</div></details>`;
}
function archivedApp(value,date){
  const app=value.app||{},saved=value.uploads;
  if(!saved)return app;
  const slotId=item=>item.slotId||(item.period===0?"lunch":"period-"+item.period);
  const items=new Map((saved.items||[]).map(item=>[item.date+":"+slotId(item)+":"+(item.id||item.title||item.period),item]));
  for(const item of app.queue?.items||[]){
    const d=item.date||item.title?.match(/\|\s*(\d{4}-\d{2}-\d{2})\s*\|/)?.[1];
    const key=d+":"+slotId(item)+":"+(item.id||item.title||item.period);
    if(items.get(key)?.status!=="completed"||item.status==="completed")items.set(key,{...items.get(key),...item,slotId:slotId(item)});
  }
  return {...app,queue:{...app.queue,items:[...items.values()]},today:saved.days?.[date]?{date,periods:saved.days[date]}:app.today};
}

function esc(value){return String(value??"").replace(/[&<>"']/g,character=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[character]))}
function roomName(){return session?.roomName||fallbackNames[roomId]||roomId.toUpperCase()}
document.title=`SilentStream ${roomName()}`;$("roomHeading").textContent=roomName();document.querySelector('meta[name="apple-mobile-web-app-title"]').content=roomName();
function show(message,error=false){notice.textContent=message;notice.className=`notice show${error?" error":""}`;clearTimeout(show.timer);show.timer=setTimeout(()=>notice.className="notice",5000)}
function summary(value){if(!value.online)return "PC 연결이 끊겨 있습니다";const app=value.app||{},audio=value.audio||{},states={Live:"방송 중",Idle:"방송 대기",RecordingOnly:"녹화 중",Warmup:"방송 준비 중",ConnectingYouTube:"YouTube 연결 중",Retrying:"연결 복구 중",Stopping:"방송 종료 중"};return (states[app.state]||"상태 확인 중")+(audio.micWarning||app.audio?.micWarning?" · 마이크 확인 필요":"")}
function koreaToday(){const parts=new Intl.DateTimeFormat("en-US",{timeZone:"Asia/Seoul",year:"numeric",month:"2-digit",day:"2-digit"}).formatToParts(new Date()),part=type=>parts.find(value=>value.type===type)?.value||"";return `${part("year")}-${part("month")}-${part("day")}`}
function defaultHistoryDate(){return koreaToday()}
function playlistEmptyMessage(value=device){if(!value?.playlists)return value?.online?"재생목록 정보가 아직 동기화되지 않았습니다. 계속 표시되면 PC 앱과 연결 도우미의 업데이트 및 동기화 상태를 확인해 주세요.":"PC가 오프라인이고 동기화된 재생목록 정보가 없습니다. PC와 앱이 연결되어 목록을 동기화한 뒤에는 꺼져 있어도 조회할 수 있습니다.";return "선택한 날짜까지 클라우드에 저장된 재생목록이 없습니다."}
function selectPlaylistDate(value){if(!/^\d{4}-\d{2}-\d{2}$/.test(value))return;playlistHistoryAnchor=value>koreaToday()?koreaToday():value;render()}
function playlistItems(value=device){const today=koreaToday();if(value?.playlists?.room?.toLowerCase()!==roomId)return [];return (value.playlists.items||[]).filter(item=>item?.date&&item.date<=today).sort((left,right)=>right.date.localeCompare(left.date))}
function playlistRow(item){const labels={ready:"준비됨",creating:"생성 중","auth-required":"인증 필요",failed:"생성 실패"},usable=item.status==="ready"&&item.url,stateClass=usable?"ready":item.status==="creating"?"":"bad",todayLabel=item.date===koreaToday()?" · 오늘":"";return `<div class="playlist-item"><div><div class="playlist-date">${esc(item.date)}${todayLabel}</div><div class="playlist-state ${stateClass}">${esc(labels[item.status]||item.status||"상태 확인 필요")}</div>${item.error?`<div class="playlist-error">${esc(item.error)}</div>`:""}</div>${usable?`<div class="playlist-actions"><button class="primary" data-copy-playlist="${esc(item.url)}" data-playlist-date="${esc(item.date)}">링크 복사</button><a class="btn" href="${esc(item.url)}" target="_blank" rel="noopener">열기</a></div>`:""}</div>`}
function historyPage(items){if(!playlistHistoryAnchor)return {start:items.length,rows:[]};const found=items.findIndex(item=>item.date<=playlistHistoryAnchor),start=found<0?items.length:found;return {start,rows:start<items.length?items.slice(start,start+HISTORY_PAGE_SIZE):[]}}

function todayPlaylist(value=device){
  if(value?.playlists?.room?.toLowerCase()!==roomId)return null;
  return (value.playlists.items||[]).find(item=>item.date===koreaToday())||null;
}
function todayPlaylistPanel(value){
  const item=todayPlaylist(value),ready=item?.status==="ready"&&item.url;
  const labels={creating:"오늘 재생목록을 생성하고 있습니다.","auth-required":"YouTube 계정 인증이 필요합니다.",failed:"오늘 재생목록 생성에 실패했습니다."};
  const hint=ready?`${koreaToday()} · ${roomName()}`:!value.playlists?(value.online?"재생목록 정보가 아직 동기화되지 않았습니다.":"PC가 오프라인이고 동기화된 재생목록 정보가 없습니다. PC 앱 연결 후 다시 확인하세요."):labels[item?.status]||"오늘 생성된 재생목록이 없습니다. 다른 날짜를 선택해 보세요.";
  return `<div class="playlist-today"><p class="meta">${esc(hint)}</p>${ready?`<a class="playlist-url" href="${esc(item.url)}" target="_blank" rel="noopener">${esc(item.url)}</a>`:""}<div class="playlist-actions"><button class="primary" data-copy-today-playlist ${ready?"":"disabled"}>오늘 재생목록 링크 복사</button>${ready?`<a class="btn" href="${esc(item.url)}" target="_blank" rel="noopener">열기</a>`:""}</div></div>`;

}
roomEl.addEventListener("click",event=>{
  if(!event.target.closest("[data-copy-today-playlist]"))return;
  // Recheck the Korean date at click time; an open tab may have crossed midnight.
  const item=todayPlaylist();
  if(item?.status!=="ready"||!item.url){render();show("오늘 재생목록이 아직 준비되지 않았습니다.",true);return}
  copyPlaylist(item.url,item.date);
});

function playlistPanel(value){if(!value.registered)return "";const today=koreaToday(),items=playlistItems(value);if(playlistHistoryOpen&&!playlistHistoryAnchor)playlistHistoryAnchor=defaultHistoryDate();const page=historyPage(items),history=playlistHistoryOpen?`<div class="playlist-history"><div class="playlist-search"><label for="playlistDate">이 날짜까지의 재생목록</label><div><input id="playlistDate" type="date" max="${today}" value="${esc(playlistHistoryAnchor)}"><button data-playlist-search>찾기</button></div><small>선택한 날짜와 그 이전 목록을 최신순으로 표시합니다.</small></div><div class="playlist-nav"><button data-playlist-newer ${page.start===0||items.length===0?"disabled":""}>더 최근</button><button data-playlist-older ${page.start+HISTORY_PAGE_SIZE>=items.length?"disabled":""}>더 오래</button></div><div class="playlist-list history-results">${page.rows.length?page.rows.map(playlistRow).join(""):`<div class="meta" role="status">${esc(playlistEmptyMessage(value))}</div>`}</div></div>`:"";return `<section class="section-card playlists" aria-label="재생목록 공유 링크"><div class="playlist-heading"><div><p class="section-kicker">PLAYLIST</p><h2>강의 재생목록</h2></div><span>${today}</span></div>${todayPlaylistPanel(value)}<button class="playlist-history-toggle" data-playlist-history aria-expanded="${playlistHistoryOpen}">${playlistHistoryOpen?"날짜 검색 닫기":"다른 날짜 찾기"} <span aria-hidden="true">↗</span></button>${history}</section>`}
function replayPanel(value){const replay=value?.app?.liveReplay;if(!replay?.url||!replay.endedAtUtc)return "";return `<section class="playlists" aria-label="라이브 다시보기 링크"><h3>방송 전체 다시보기</h3><p class="meta">교시별 영상과 별도로 저장되는 전체 방송입니다. YouTube 처리가 끝나야 재생할 수 있습니다.</p><div class="playlist-actions"><button data-copy-replay="${esc(replay.url)}">링크 복사</button><a class="btn" href="${esc(replay.url)}" target="_blank" rel="noopener">열기</a></div></section>`}
function render(){
  if(!device){roomEl.innerHTML='<div class="empty">이 호실의 상태 정보가 없습니다.</div>';return}
  const admin=device.role==="admin",last=device.lastSeenAtUtc?kstTime(device.lastSeenAtUtc,true):"접속 기록 없음",state=device.app?.state,fresh=controllerFresh(device),disabled=fresh?"":"disabled";
  const startDisabled=fresh&&state==="Idle"?"":"disabled",stopDisabled=fresh&&state==="Live"?"":"disabled",retryDisabled=fresh&&["Live","Retrying"].includes(state)?"":"disabled";
  roomEl.innerHTML=`<article class="controller"><section class="section-card status-card"><div class="top"><span class="section-kicker">REMOTE CONTROL</span><span class="pill ${device.online?"on":"off"}">${device.online?"온라인":"오프라인"}</span></div><h2 class="summary">${esc(device.registered?summary(device):"강의실 PC 설치가 필요합니다")}</h2><p class="meta">${!device.online?"저장된 마지막 상태입니다. 현재 방송·녹화 여부는 확인할 수 없습니다.":!fresh?"최신 상태 수신을 기다리고 있습니다.":"상태는 10초마다 자동으로 갱신됩니다."}</p><div class="connection-meta">마지막 연결 ${esc(last)}<button class="text-button" data-refresh>새로고침</button></div></section><div class="content-grid">${playlistPanel(device)}${uploadPanel(device)}</div>${shutdownPanel(device)}<details class="section-card" data-disclosure="audio" ${openSections.has("audio")?"open":""}><summary>마이크 입력 <span>${device.audio?.micWarning?"확인 필요":"입력 상태·설정"}</span></summary>${microphonePanel(device)}</details><details class="section-card" data-disclosure="advanced" ${openSections.has("advanced")?"open":""}><summary>기기 관리 <span>점검·재시작·수동 방송</span></summary><p class="meta">방송은 PC 전원에 맞춰 자동으로 시작·종료됩니다. 아래는 문제가 생겼을 때 사용하는 수동 제어입니다.</p><div class="primary-actions"><button class="primary" ${startDisabled} data-action="live.start">방송 시작</button><button ${stopDisabled} data-action="live.stopBroadcast" data-confirm="방송만 중지할까요? 백업 녹화는 계속됩니다.">방송 중지</button></div><div class="actions"><button ${retryDisabled} data-action="live.retry">방송 재연결</button><button ${disabled} data-action="inspection.rerun">자동 점검</button><button ${disabled} data-action="app.restart" data-confirm="PC 앱을 재시작할까요? 진행 중인 방송과 녹화가 잠시 중단될 수 있습니다.">앱 재시작</button><button data-detail>기술 상세</button>${admin?`<button class="danger" ${fresh&&!["Idle","Stopping"].includes(state)?"":"disabled"} data-action="live.stopAll" data-confirm="앱과 방송을 모두 중지할까요? 녹화도 중단됩니다.">전체 중지</button>`:""}</div>${replayPanel(device)}<p class="meta device-meta">${esc(device.machineName||"기기 미등록")} · 연결 도우미 ${esc(device.agentVersion||"버전 확인 중")}</p></details></article>`;
}
async function api(path,options){const response=await fetch(path,options);if(response.status===401){location.replace("/");throw Error("AUTH_REQUIRED")}let data=null;try{data=await response.json()}catch{}if(!response.ok){let local;try{local=JSON.parse(data?.body||"{}")}catch{}throw Error(local?.message||data?.message||data?.error||`HTTP ${response.status}`);}return data}
async function load(){try{session=await api("/api/control/session");document.title=`SilentStream ${roomName()}`;$("roomHeading").textContent=roomName();document.querySelector('meta[name="apple-mobile-web-app-title"]').content=roomName();$("installTitle").textContent=`${roomName()} 담당자 앱 설치`;$("who").textContent=`${session.email} · ${session.admin?"관리자":"호실 담당자"}`;$("centralLink").href=session.centralUrl;document.body.classList.remove("auth-pending");const devices=await api("/api/control/rooms");device=devices[0]||null;render()}catch(error){if(error.message!=="AUTH_REQUIRED"){document.body.classList.remove("auth-pending");if(device){device={...device,online:false};render();show("연결 상태를 갱신하지 못했습니다. 마지막 기록을 표시합니다.",true)}else roomEl.innerHTML=`<div class="empty">${esc(error.message)}</div>`}}}
const dangerousActions={
  "system.shutdown":"지금 PC를 종료할까요? 녹화와 방송을 저장·마감하고 안전 점검 후 종료합니다. 실행 후에는 취소할 수 없으며, 다른 프로그램의 저장하지 않은 내용은 사라질 수 있습니다.",
  "app.restart":"PC 앱을 재시작할까요? 진행 중인 방송과 녹화가 잠시 중단될 수 있습니다.",
  "live.stopBroadcast":"방송만 중지할까요? 백업 녹화는 계속됩니다.",
  "live.stopAll":"방송과 녹화를 모두 중지할까요?",
  "live.retry":"방송을 재연결할까요? 송출이 잠시 중단될 수 있습니다.",
  "inspection.rerun":"자동 점검을 다시 실행할까요? PC 상태에 따라 복구 작업이 실행될 수 있습니다."
};
async function act(action,confirmation,body={}){
  if(!controllerFresh(device)){show("PC의 최신 상태를 받은 뒤 다시 시도해 주세요.",true);return}
  confirmation=dangerousActions[action]||confirmation;
  if(confirmation&&!confirm(roomName()+": "+confirmation))return;
  actionPending=true;render();
  try{
    const result=await api("/api/control/rooms/"+encodeURIComponent(roomId)+"/actions/"+encodeURIComponent(action),{method:"POST",headers:{"content-type":"application/json"},body:JSON.stringify(body)});
    let local={};try{local=JSON.parse(result.body||"{}")}catch{}
    const ok=result.ok&&local.ok!==false;
    show(local.message||local.error||result.error||(ok?"명령을 실행했습니다.":"명령을 완료하지 못했습니다."),!ok);
  }catch(error){if(error.message!=="AUTH_REQUIRED")show(error.message,true)}
  finally{actionPending=false;await load()}
}
async function copyPlaylist(url,date){let temporary=null;try{if(navigator.clipboard&&window.isSecureContext)await navigator.clipboard.writeText(url);else{temporary=document.createElement("textarea");temporary.value=url;temporary.setAttribute("readonly","");temporary.style.position="fixed";temporary.style.opacity="0";document.body.append(temporary);temporary.select();if(!document.execCommand("copy"))throw Error("copy failed")}show(`${date} 재생목록 링크를 복사했습니다.`)}catch{$("playlistCopyAddress").value=url;$("playlistCopyHelp").showModal();$("playlistCopyAddress").focus();$("playlistCopyAddress").select();show("자동 복사가 차단되었습니다. 링크를 길게 눌러 복사해주세요.",true)}finally{temporary?.remove()}}
async function copyReplay(url){let temporary=null;try{if(navigator.clipboard&&window.isSecureContext)await navigator.clipboard.writeText(url);else{temporary=document.createElement("textarea");temporary.value=url;temporary.setAttribute("readonly","");temporary.style.position="fixed";temporary.style.opacity="0";document.body.append(temporary);temporary.select();if(!document.execCommand("copy"))throw Error("copy failed")}show("라이브 다시보기 링크를 복사했습니다.")}catch{$("playlistCopyAddress").value=url;$("playlistCopyHelp").showModal();$("playlistCopyAddress").focus();$("playlistCopyAddress").select();show("자동 복사가 차단되었습니다. 아래 링크를 직접 복사해 주세요.",true)}finally{temporary?.remove()}}
function installed(){return matchMedia("(display-mode: standalone)").matches||navigator.standalone===true}
function ios(){return /iphone|ipad|ipod/i.test(navigator.userAgent)||navigator.platform==="MacIntel"&&navigator.maxTouchPoints>1}
function updateInstall(){const card=$("installCard"),button=$("installButton"),guide=$("installGuide");if(installed()){card.classList.remove("show");return}card.classList.add("show");button.hidden=false;button.textContent=installPrompt?"앱 설치":ios()?"설치 방법":"앱 설치";if(installPrompt)guide.textContent="버튼을 눌러 이 호실 앱을 홈 화면에 설치하세요.";else if(ios())guide.textContent="버튼을 누르면 iPhone 홈 화면 설치 방법을 안내합니다.";else guide.textContent="버튼을 눌러 설치하거나, 지원하지 않는 브라우저에서는 설치 방법을 확인하세요."}
function openInstallHelp(){const name=roomName();$("installHelpTitle").textContent=`${name} 담당자 앱 설치`;$("installHelpBody").innerHTML=ios()?"<p>Safari에서 이 주소를 연 뒤 <b>공유</b>를 누르고 <b>홈 화면에 추가</b>와 <b>웹 앱으로 열기</b>를 선택하세요.</p>":"<p>현재 브라우저가 직접 설치 창을 제공하지 않습니다. 설치 주소를 복사해 Chrome 또는 Edge에서 연 뒤 브라우저 메뉴의 <b>앱 설치</b>나 <b>홈 화면에 추가</b>를 선택하세요.</p>";$("installHelp").showModal()}
addEventListener("beforeinstallprompt",event=>{event.preventDefault();installPrompt=event;updateInstall()});
addEventListener("appinstalled",()=>{installPrompt=null;$("installCard").classList.remove("show");show(`${roomName()} 담당자 앱을 설치했습니다.`)});
$("installButton").addEventListener("click",async()=>{if(!installPrompt){openInstallHelp();return}await installPrompt.prompt();installPrompt=null;updateInstall()});
$("copyInstallAddress").addEventListener("click",async()=>{try{await navigator.clipboard.writeText(location.origin+"/");show(`${roomName()} 설치 주소를 복사했습니다.`);$("installHelp").close()}catch{show("주소를 복사하지 못했습니다. 주소 표시줄의 주소를 복사해 주세요.",true)}});
$("closeInstallHelp").addEventListener("click",()=>$("installHelp").close());
roomEl.addEventListener("click",event=>{const button=event.target.closest("button");if(!button)return;if(button.dataset.uploadLatest){uploadDate=button.dataset.uploadLatest;render()}else if(button.hasAttribute("data-refresh"))load();else if(button.hasAttribute("data-mic-select"))act("audio.select",null,{deviceId:$("microphoneDevice").value});else if(button.dataset.copyReplay)copyReplay(button.dataset.copyReplay);else if(button.dataset.copyPlaylist)copyPlaylist(button.dataset.copyPlaylist,button.dataset.playlistDate);else if(button.hasAttribute("data-playlist-history")){playlistHistoryOpen=!playlistHistoryOpen;if(playlistHistoryOpen&&!playlistHistoryAnchor)playlistHistoryAnchor=defaultHistoryDate();render()}else if(button.hasAttribute("data-playlist-search")){const selected=$("playlistDate")?.value;selectPlaylistDate(selected)}else if(button.hasAttribute("data-playlist-older")||button.hasAttribute("data-playlist-newer")){const items=playlistItems(),page=historyPage(items),next=button.hasAttribute("data-playlist-older")?page.start+HISTORY_PAGE_SIZE:Math.max(0,page.start-HISTORY_PAGE_SIZE);if(items[next])playlistHistoryAnchor=items[next].date;render()}else if(button.hasAttribute("data-detail")){$("detailTitle").textContent=`${roomName()} 상세 상태`;$("detailBody").textContent=JSON.stringify(device,null,2);detail.showModal()}else if(button.dataset.action)act(button.dataset.action,button.dataset.confirm)});
roomEl.addEventListener("change",event=>{if(event.target.id==="playlistDate")selectPlaylistDate(event.target.value);else if(event.target.id==="uploadDate"){uploadDate=event.target.value;render()}});
$("closeDetail").addEventListener("click",()=>detail.close());
addEventListener("pagehide",()=>document.body.classList.add("auth-pending"));
addEventListener("pageshow",event=>{if(event.persisted)location.reload()});
if("serviceWorker" in navigator)navigator.serviceWorker.register("/service-worker.js",{scope:"/"}).catch(()=>{});
updateInstall();load();setInterval(()=>{if(!document.hidden&&document.activeElement?.id!=="playlistDate"&&document.activeElement?.id!=="microphoneDevice"&&document.activeElement?.id!=="uploadDate"&&!document.activeElement?.dataset?.shutdownInput)load()},10000);

$("closePlaylistCopyHelp").onclick=()=>$("playlistCopyHelp").close();
$("playlistCopyAddress").onclick=()=>$("playlistCopyAddress").select();


roomEl.addEventListener('input',event=>{if(event.target.dataset.shutdownInput)shutdownDrafts[event.target.dataset.shutdownInput]=event.target.value});
roomEl.addEventListener('click',event=>{const button=event.target.closest('button');if(button?.dataset.shutdownCreate)changeShutdown(button.dataset.shutdownCreate,null);else if(button?.dataset.shutdownCancel)changeShutdown(button.dataset.shutdownCancel,button.dataset.scheduleId)});

roomEl.addEventListener("toggle",event=>{const key=event.target.dataset.disclosure;if(key){if(event.target.open)openSections.add(key);else openSections.delete(key)}},true);
function microphonePanel(value){
  const audio=value.audio||{};
  if(!audio.microphoneControl)return '<section><h3>마이크 입력</h3><p>마이크 원격 제어에는 PC 앱과 연결 도우미 업데이트가 필요합니다.</p></section>';
  const ready=audio.remoteMicrophoneCommands===true;
  const disabled=value.online&&ready?'':'disabled';
  const mics=(audio.sources||[]).filter(s=>s.kind==="mic");
  return '<section><h3>마이크 입력</h3>'+(ready?'':'<p>PC 앱의 자동 복구는 사용할 수 있습니다. 휴대폰 명령은 연결 도우미 업데이트 후 활성화됩니다.</p>')+mics.map(s=>'<p>'+esc(s.resolvedDeviceName||s.name)+' · '+(s.muted?'앱 음소거':s.available?'연결됨 · 실제 소리 확인 필요':'연결 안 됨')+(s.gate?' · 게이트 켜짐':'')+'</p><meter min="0" max="1" value="'+Math.max(0,Math.min(1,Number(s.peak)||0))+'" aria-label="마이크 음량"></meter>'+(s.captureError?'<p>'+esc(s.captureError)+'</p>':'')).join('')+
  '<label>입력 모드 (개별 선택 시 전체 자동 추가 해제)<select id="microphoneDevice" style="display:block;width:100%;max-width:100%;padding:10px;margin:8px 0;font:inherit" '+disabled+'><option value="">마이크 선택</option>'+(typeof audio.autoIncludeMicrophones==='boolean'?'<option value="*" '+(audio.autoIncludeMicrophones?'selected':'')+'>연결된 모든 입력 자동 사용</option>':'')+(audio.devices||[]).map(d=>'<option value="'+esc(d.id)+'">'+esc(d.name)+'</option>').join('')+'</select></label>'+
  '<div class="actions"><button '+disabled+' data-mic-select>선택 적용</button><button '+disabled+' data-action="audio.refresh">입력 다시 연결</button><button '+disabled+' data-action="audio.restore">입력 복구 (음소거·게이트 해제)</button></div><p>상태는 약 10초마다 갱신됩니다. 복구 후 실제 소리를 내어 음량을 확인하세요. Windows/장비 음소거는 현장에서 확인해야 합니다.</p></section>';
}

if(typeof window !== "undefined" && window.SilentStreamUsbDocuments) window.SilentStreamUsbDocuments.mount(document.getElementById("usbDocumentsHost"),{
  context:()=>device?{key:roomId,online:controllerFresh(device),
    supported:device.app?.usbDocumentsSupported===true&&device.app?.remoteUsbDocuments===true}:null,
  list:(c,date,skip)=>api("/api/control/rooms/"+encodeURIComponent(c.key)+"/usb-documents?date="+date+"&skip="+skip),
  change:(c,body)=>api("/api/control/rooms/"+encodeURIComponent(c.key)+"/usb-documents",
    {method:"POST",headers:{"content-type":"application/json"},body:JSON.stringify(body)}),
  download:async(c,id)=>"/api/control/rooms/"+encodeURIComponent(c.key)+"/usb-documents/"+encodeURIComponent(id)+"/download"
});
