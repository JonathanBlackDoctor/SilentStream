export const FEED_URL = typeof location !== 'undefined' && location.hostname === 'm112.silentstream.win'
  ? '/api/room/playlist-links' : 'https://silentstream.win/api/public/m112/playlists';
export function koreaToday(now = new Date()) {
  return new Date(now.valueOf() + 9 * 3600000).toISOString().slice(0, 10);
}
export function selectRecent(payload, now = new Date()) {
  if (!payload || payload.room !== 'm112' || !Array.isArray(payload.items)) throw new Error('목록 형식 오류');
  const today = koreaToday(now), seen = new Set();
  return payload.items.filter(item => {
    if (!item || !/^\d{4}-\d{2}-\d{2}$/.test(item.date) || item.date > today || item.status !== 'ready') return false;
    const parsed = new Date(item.date + 'T00:00:00Z');
    if (!Number.isFinite(+parsed) || parsed.toISOString().slice(0, 10) !== item.date) return false;
    try { const url = new URL(item.url); if (url.origin !== 'https://www.youtube.com' || url.pathname !== '/playlist' || !/^[A-Za-z0-9_-]+$/.test(url.searchParams.get('list') || '')) return false; } catch { return false; }
    return true;
  }).sort((a,b) => b.date.localeCompare(a.date)).filter(item => {
    if (seen.has(item.date)) return false; seen.add(item.date); return true;
  }).slice(0, 3).map(item => ({date:item.date, url:item.url}));
}
