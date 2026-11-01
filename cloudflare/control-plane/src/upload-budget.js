import { authorizeUpdate } from "./private-updates.js";

// One fleet uses one production OAuth project. Deliberately pool channel pauses across the
// fleet too: a newly selected channel must not accidentally bypass the existing shared pause.
// These are application safety budgets, NOT a claim about YouTube's unpublished channel cap.
export const UPLOAD_BUDGET = Object.freeze({ daily: 80, backlog: 40 });
const pt = new Intl.DateTimeFormat("en-CA", { timeZone: "America/Los_Angeles", year: "numeric", month: "2-digit", day: "2-digit" });
const seoul = new Intl.DateTimeFormat("en-CA", { timeZone: "Asia/Seoul", year: "numeric", month: "2-digit", day: "2-digit" });
const day = (formatter, now) => {
  const parts = Object.fromEntries(formatter.formatToParts(new Date(now)).map(x => [x.type, x.value]));
  return `${parts.year}-${parts.month}-${parts.day}`;
};
export function nextPacificReset(now) {
  const current = day(pt, now);
  // Search UTC whole hours; LA midnight is an integral UTC hour, including DST changes.
  let next = Math.floor(now / 3600000) * 3600000 + 3600000;
  while (day(pt, next) === current) next += 3600000;
  return next;
}
const decision = (allowed, retry, reason) => Response.json({ allowed, retryAtUtc: new Date(retry).toISOString(), reason },
  { headers: { "cache-control": "no-store" } });

export async function handleUploadBudget(storage, proof, now = Date.now()) {
  const auth = await authorizeUpdate(storage, proof, now, "upload-budget");
  if (!auth.ok) return auth;
  const url = new URL(proof.target, "https://agent.silentstream.win");
  const q = url.searchParams, op = q.get("op");
  const expected = op === "reserve" ? ["op", "date", "attempt"] : ["op", "kind", "until"];
  if ([...q.keys()].length !== 3 || expected.some(k => q.getAll(k).length !== 1))
    return new Response(null, { status: 400 });
  const today = day(seoul, now), reset = nextPacificReset(now);
  const date = q.get("date"), attempt = q.get("attempt"), kind = q.get("kind"), until = Number(q.get("until")) * 1000;
  if (op === "reserve") {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(date || "") || date > today
      || !Number.isFinite(Date.parse(date + "T00:00:00Z"))
      || new Date(date + "T00:00:00Z").toISOString().slice(0, 10) !== date || !/^[a-f0-9]{32}$/.test(attempt || ""))
      return new Response(null, { status: 400 });
  } else if (op !== "pause" || !["api", "channel"].includes(kind) || !Number.isSafeInteger(until) || until > now + 25 * 3600000)
    return new Response(null, { status: 400 });
  return storage.transaction(async store => {
    const key = "upload-budget:v1";
    const state = await store.get(key) || { readyAfter: reset, date: "", used: 0, backlog: 0, attempts: {}, apiUntil: 0, channelUntil: 0 };
    if (state.date !== day(pt, now)) {
      state.date = day(pt, now); state.used = 0; state.backlog = 0; state.attempts = {};
    }
    if (op === "pause") {
      const field = kind === "channel" ? "channelUntil" : "apiUntil";
      state[field] = Math.max(state[field], until);
      await store.put(key, state);
      return Response.json({ ok: true }, { headers: { "cache-control": "no-store" } });
    }
    let retry = Math.max(state.readyAfter, state.apiUntil, state.channelUntil);
    if (retry > now) { await store.put(key, state); return decision(false, retry, "shared-pause"); }
    // Never issue a permit immediately before the project day changes.
    if (reset - now <= 120000) return decision(false, reset + 1000, "reset-boundary");
    const id = proof.keyId + ":" + attempt;
    if (state.attempts[id]) {
      const saved = state.attempts[id];
      return decision(saved.expires > now && saved.date === date, saved.expires > now ? saved.expires : now + 60000, "existing-reservation");
    }
    const backlog = date < today;
    if (state.used >= UPLOAD_BUDGET.daily || (backlog && state.backlog >= UPLOAD_BUDGET.backlog))
      return decision(false, reset + 1000, backlog && state.backlog >= UPLOAD_BUDGET.backlog ? "backlog-reserve" : "daily-budget");
    // Consume BEFORE the API request, including retries/failures/unknown acknowledgements.
    // No refunds: a crashed client may already have used the external quota.
    state.used++; if (backlog) state.backlog++;
    const expires = now + 60000;
    state.attempts[id] = { date, expires };
    await store.put(key, state);
    return decision(true, expires, "reserved");
  });
}
