import { FEED_URL, selectRecent } from './data.mjs';
const list = document.querySelector('#playlists'), status = document.querySelector('#status');
let loading = false, current = null;
const playIcon = '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4 2.3v11.4L13 8z"/></svg>';
function render(payload) {
  const items = selectRecent(payload);
  list.replaceChildren();
  for (const [index, item] of items.entries()) {
    const date = new Date(item.date + 'T12:00:00+09:00');
    const link = document.createElement('a'); link.className = 'playlist' + (index === 0 ? ' featured' : '');
    link.href = item.url; link.target = '_blank'; link.rel = 'noopener noreferrer';
    const day = new Intl.DateTimeFormat('ko-KR', {weekday:'long',timeZone:'Asia/Seoul'}).format(date);
    link.setAttribute('aria-label', `${item.date} ${day} M112 재생목록 — YouTube에서 새 창으로 열기`);
    link.innerHTML = `<time datetime="${item.date}">${item.date.slice(5).replace('-','.')}</time><span class="play" aria-hidden="true">${playIcon}</span>`;
    list.append(link);
  }
  if (!items.length) { const message = document.createElement('p'); message.className = 'empty'; message.textContent = '아직 재생목록이 없습니다.'; list.append(message); }
  list.setAttribute('aria-busy','false');
  current = payload;
}
function checkedAt(payload) {
  const date = new Date(payload.updatedAtUtc);
  return payload.updatedAtUtc && Number.isFinite(+date) ? new Intl.DateTimeFormat('ko-KR',{month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',hour12:false,timeZone:'Asia/Seoul'}).format(date) : '';
}
async function update() {
  if (loading) return; loading = true;
  try {
    const response = await fetch(FEED_URL, {cache:'no-store',credentials:'omit',signal:AbortSignal.timeout(10000)});
    if (!response.ok) throw new Error('조회 실패');
    const payload = await response.json(); render(payload);
    try { localStorage.setItem('m112-playlists-v1',JSON.stringify(payload)); } catch {}
    status.hidden = true; status.textContent = '';
  } catch {
    status.hidden = false;
    if (current) status.textContent = ['저장본',checkedAt(current)].filter(Boolean).join(' · ');
    else { list.setAttribute('aria-busy','false'); list.innerHTML = '<p class="empty">연결을 확인해 주세요.</p>'; status.textContent = ''; status.hidden = true; }
  } finally { loading = false; }
}
try { const cached = JSON.parse(localStorage.getItem('m112-playlists-v1')); if(cached) render(cached); } catch {}
if (!current) {
  try { const seed = await fetch('./playlists.json',{cache:'no-store'}); if(seed.ok) render(await seed.json()); } catch {}
}
document.addEventListener('visibilitychange',()=> { if(!document.hidden) { if(current) render(current); update(); } });
window.addEventListener('online',update);
setInterval(()=> { if(!document.hidden) { if(current) render(current); update(); } }, 60000);
update();
