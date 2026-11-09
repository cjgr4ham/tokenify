// Local development backend for TOKENIFY — never used in production.
//
// Serves the website on http://localhost:8090 (and 127.0.0.1:8090, a separate browser origin, so two
// test accounts can be signed in side by side) plus a small stand-in for the Supabase APIs the app
// uses on http://127.0.0.1:54321: anonymous auth sessions (JWTs + refresh tokens) and PostgREST-style
// RPC calls that run the real supabase/accounts-v2.sql inside PGlite. No realtime: the app falls back
// to polling. Open the site once with ?backend=local to point that origin at this backend.
//
//   node local-supabase.mjs            (in-memory database, wiped on restart)
//   node local-supabase.mjs --persist  (keeps data in dev/.localdb)
//   node local-supabase.mjs --season-start=2019-01-01  (lets old sample replays count: for e2e-companion.mjs)
import http from 'node:http';
import { createHmac, randomBytes, randomUUID } from 'node:crypto';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createDb, rpc } from './pg-env.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const SITE_ROOT = path.resolve(here, '..');
const API_PORT = 54321, SITE_PORT = Number(process.env.SITE_PORT || 8090);
const SECRET = 'tokenify-local-development-only';
const ANON_KEY = 'local-dev-key';
const ACCESS_TTL = 3600;

const persist = process.argv.includes('--persist');
// --season-start=2019-01-01: test only, so the public sample replays (2019-2024) count as this season
const seasonArg = process.argv.find((a) => a.startsWith('--season-start='));
const db = await createDb({ ...(persist ? { dataDir: path.join(here, '.localdb') } : {}), ...(seasonArg ? { seasonStart: seasonArg.split('=')[1] } : {}) });
if (seasonArg) console.log('Season start moved to ' + seasonArg.split('=')[1] + ' (test only)');
const refreshTokens = new Map();               // token -> user id

// ------------------------------------------------------------------------------------------- JWTs
const b64u = (buf) => Buffer.from(buf).toString('base64url');
function signJwt(payload) {
  const head = b64u(JSON.stringify({ alg: 'HS256', typ: 'JWT' })), body = b64u(JSON.stringify(payload));
  return `${head}.${body}.${b64u(createHmac('sha256', SECRET).update(`${head}.${body}`).digest())}`;
}
function verifyJwt(token) {
  const parts = String(token || '').split('.');
  if (parts.length !== 3) return null;
  const sig = b64u(createHmac('sha256', SECRET).update(`${parts[0]}.${parts[1]}`).digest());
  if (sig !== parts[2]) return null;
  try { const p = JSON.parse(Buffer.from(parts[1], 'base64url').toString()); return p.exp > Date.now() / 1000 ? p : null; } catch { return null; }
}
async function userRow(id) { return (await db.query('select id, created_at from auth.users where id = $1', [id])).rows[0] || null; }
function userJson(u) {
  return { id: u.id, aud: 'authenticated', role: 'authenticated', email: '', phone: '', is_anonymous: true,
    app_metadata: { provider: 'anonymous', providers: ['anonymous'] }, user_metadata: {}, identities: [],
    created_at: new Date(u.created_at).toISOString(), updated_at: new Date().toISOString() };
}
function session(u) {
  const now = Math.floor(Date.now() / 1000), refresh = b64u(randomBytes(24));
  refreshTokens.set(refresh, u.id);
  const access = signJwt({ sub: u.id, role: 'authenticated', aud: 'authenticated', is_anonymous: true, iat: now, exp: now + ACCESS_TTL, session_id: randomUUID() });
  return { access_token: access, token_type: 'bearer', expires_in: ACCESS_TTL, expires_at: now + ACCESS_TTL, refresh_token: refresh, user: userJson(u) };
}

// ------------------------------------------------------------------------------------------- HTTP
function send(res, status, body, headers = {}) {
  const data = body === undefined ? '' : JSON.stringify(body);
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', ...headers });
  res.end(data);
}
function cors(req, res) {
  res.setHeader('access-control-allow-origin', req.headers.origin || '*');
  res.setHeader('access-control-allow-headers', 'authorization, apikey, content-type, x-client-info, prefer, accept-profile, content-profile, x-supabase-api-version');
  res.setHeader('access-control-allow-methods', 'GET, POST, PATCH, DELETE, OPTIONS');
  res.setHeader('access-control-max-age', '600');
  res.setHeader('vary', 'origin');
}
async function body(req) {
  const chunks = []; for await (const c of req) chunks.push(c);
  const raw = Buffer.concat(chunks).toString('utf8');
  if (!raw) return {};
  try { return JSON.parse(raw); } catch { return null; }
}
function bearer(req) { const h = req.headers.authorization || ''; return h.startsWith('Bearer ') ? h.slice(7) : null; }
const PG_STATUS = { '42501': 403, '28000': 403, '42883': 404, 'P0001': 400, '22023': 400, '22P02': 400, '23505': 409, '40P01': 503 };

const api = http.createServer(async (req, res) => {
  cors(req, res);
  if (req.method === 'OPTIONS') { res.writeHead(204); return res.end(); }
  const url = new URL(req.url, `http://${req.headers.host}`);
  const t0 = Date.now();
  try {
    if (req.headers.apikey !== ANON_KEY) return send(res, 401, { message: 'Invalid API key' });
    // ---- auth ----
    if (url.pathname === '/auth/v1/settings') return send(res, 200, { external: { anonymous_users: true, email: false }, disable_signup: false });
    if (url.pathname === '/auth/v1/signup' && req.method === 'POST') {
      const b = await body(req);
      if (b && (b.email || b.phone)) return send(res, 422, { code: 422, error_code: 'email_provider_disabled', msg: 'Only anonymous sessions are available locally' });
      const u = (await db.query('insert into auth.users default values returning id, created_at')).rows[0];
      return send(res, 200, session(u));
    }
    if (url.pathname === '/auth/v1/token' && req.method === 'POST') {
      const b = await body(req) || {};
      const uid = refreshTokens.get(b.refresh_token);
      if (url.searchParams.get('grant_type') !== 'refresh_token' || !uid) return send(res, 400, { error: 'invalid_grant', error_description: 'Invalid Refresh Token: Refresh Token Not Found' });
      refreshTokens.delete(b.refresh_token);
      const u = await userRow(uid);
      if (!u) return send(res, 400, { error: 'invalid_grant', error_description: 'Invalid Refresh Token: User Not Found' });
      return send(res, 200, session(u));
    }
    if (url.pathname === '/auth/v1/logout' && req.method === 'POST') {
      const claims = verifyJwt(bearer(req));
      if (claims) for (const [tok, uid] of refreshTokens) if (uid === claims.sub) refreshTokens.delete(tok);
      res.writeHead(204); return res.end();
    }
    if (url.pathname === '/auth/v1/user') {
      const claims = verifyJwt(bearer(req));
      const u = claims && await userRow(claims.sub);
      if (!u) return send(res, 403, { code: 403, error_code: 'user_not_found', msg: 'User from sub claim in JWT does not exist' });
      return send(res, 200, userJson(u));
    }
    // ---- PostgREST RPC ----
    const m = url.pathname.match(/^\/rest\/v1\/rpc\/([a-z_][a-z0-9_]*)$/);
    if (m && req.method === 'POST') {
      const token = bearer(req);
      let who = { uid: null, ip: req.socket.remoteAddress };
      if (token && token !== ANON_KEY) {
        const claims = verifyJwt(token);
        if (!claims) return send(res, 401, { code: 'PGRST301', message: 'JWT expired or invalid', details: null, hint: null });
        who.uid = claims.sub;
      }
      const args = await body(req);
      if (args === null || typeof args !== 'object' || Array.isArray(args)) return send(res, 400, { code: 'PGRST102', message: 'Invalid body', details: null, hint: null });
      try {
        const out = await rpc(db, who, m[1], args);
        log(req, 200, m[1], t0);
        return send(res, 200, out);
      } catch (e) {
        const status = PG_STATUS[e.code] || (/does not exist/.test(e.message) ? 404 : 400);
        log(req, status, m[1] + ' ✗ ' + e.message, t0);
        return send(res, status === 403 && !who.uid ? 401 : status, { code: e.code || 'XX000', message: e.message, details: e.detail || null, hint: e.hint || null });
      }
    }
    if (url.pathname.startsWith('/realtime/')) return send(res, 404, { message: 'Realtime is not available on the local backend' });
    return send(res, 404, { message: 'Not found: ' + url.pathname });
  } catch (e) {
    console.error(e);
    return send(res, 500, { message: String(e.message || e) });
  }
});
function log(req, status, what, t0) { console.log(`${new Date().toISOString().slice(11, 19)} ${status} ${what} (${Date.now() - t0} ms)`); }

// --------------------------------------------------------------------------------- static website
const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8', '.json': 'application/json', '.webmanifest': 'application/manifest+json', '.png': 'image/png',
  '.webp': 'image/webp', '.svg': 'image/svg+xml', '.ico': 'image/x-icon', '.txt': 'text/plain; charset=utf-8', '.md': 'text/plain; charset=utf-8',
  '.sql': 'text/plain; charset=utf-8', '.woff2': 'font/woff2' };
const serveSite = async (req, res) => {
  try {
    let rel = decodeURIComponent(new URL(req.url, 'http://x').pathname);
    if (rel.endsWith('/')) rel += 'index.html';
    const full = path.resolve(SITE_ROOT, '.' + rel);
    if (!full.startsWith(SITE_ROOT + path.sep) || full.includes(`${path.sep}node_modules${path.sep}`) || full.includes(`${path.sep}.localdb`)) { res.writeHead(403); return res.end(); }
    const st = await stat(full).catch(() => null);
    if (!st || !st.isFile()) { res.writeHead(404); return res.end('Not found'); }
    res.writeHead(200, { 'content-type': TYPES[path.extname(full).toLowerCase()] || 'application/octet-stream', 'cache-control': 'no-store' });
    res.end(await readFile(full));
  } catch (e) { res.writeHead(500); res.end(String(e)); }
};

// Loopback only (IPv4 and IPv6), never the local network.
api.listen(API_PORT, '127.0.0.1', () => console.log(`Local Supabase stand-in: http://127.0.0.1:${API_PORT}${persist ? ' (persistent)' : ' (in-memory)'}`));
http.createServer(serveSite).listen(SITE_PORT, '127.0.0.1', () => console.log(`TOKENIFY site: http://localhost:${SITE_PORT}/?backend=local  and  http://127.0.0.1:${SITE_PORT}/?backend=local`));
http.createServer(serveSite).listen(SITE_PORT, '::1').on('error', () => { /* no IPv6 loopback: fine */ });
