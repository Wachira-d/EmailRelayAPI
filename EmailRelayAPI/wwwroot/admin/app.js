/* ===================== Email Relay admin console - core ===================== */
/* global bootstrap, Chart */
'use strict';

const App = {
  session: null,
  isAdmin: false,
  cache: { clients: null, profiles: null },
  charts: {},
  refreshTimer: null,
  currentRoute: null
};

/* ---------- icons (inline SVG, stroke based) ---------- */
const Icons = {
  dashboard: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="7" height="9" rx="1.5"/><rect x="14" y="3" width="7" height="5" rx="1.5"/><rect x="14" y="12" width="7" height="9" rx="1.5"/><rect x="3" y="16" width="7" height="5" rx="1.5"/></svg>',
  mail: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="5" width="18" height="14" rx="2"/><path d="m3 7 9 6 9-6"/></svg>',
  users: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="9" cy="8" r="3.5"/><path d="M2.5 20a6.5 6.5 0 0 1 13 0"/><circle cx="17" cy="9" r="2.5"/><path d="M15.5 14.5a5 5 0 0 1 6 5"/></svg>',
  send: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 2 11 13"/><path d="M22 2 15 22l-4-9-9-4z"/></svg>',
  file: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/><path d="M8 13h8M8 17h8"/></svg>',
  list: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M8 6h13M8 12h13M8 18h13"/><circle cx="3.5" cy="6" r="1"/><circle cx="3.5" cy="12" r="1"/><circle cx="3.5" cy="18" r="1"/></svg>',
  shield: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2 4 5v6c0 5 3.4 9.4 8 11 4.6-1.6 8-6 8-11V5z"/><path d="m9 12 2 2 4-4"/></svg>',
  chart: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 20h18"/><path d="M6 16V9M11 16V5M16 16v-6M21 16v-3"/></svg>',
  settings: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/></svg>',
  code: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m16 18 6-6-6-6M8 6l-6 6 6 6"/></svg>'
};

/* ---------- utilities ---------- */
const $ = (sel, root) => (root || document).querySelector(sel);
const $$ = (sel, root) => Array.from((root || document).querySelectorAll(sel));

function esc(v) {
  if (v === null || v === undefined) return '';
  return String(v).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
function fmtDate(iso, withSeconds) {
  if (!iso) return '—';
  const d = new Date(iso);
  if (isNaN(d)) return esc(iso);
  const pad = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}${withSeconds ? ':' + pad(d.getSeconds()) : ''}`;
}
function ago(iso) {
  if (!iso) return '—';
  const diff = (Date.now() - new Date(iso).getTime()) / 1000;
  if (diff < 0) return 'in ' + ago(new Date(Date.now() - diff * 1000).toISOString()).replace(' ago', '');
  if (diff < 60) return Math.floor(diff) + 's ago';
  if (diff < 3600) return Math.floor(diff / 60) + 'm ago';
  if (diff < 86400) return Math.floor(diff / 3600) + 'h ago';
  return Math.floor(diff / 86400) + 'd ago';
}
function num(n) { return (n === null || n === undefined) ? '—' : Number(n).toLocaleString(); }
function ms(n) { return (n === null || n === undefined) ? '—' : Math.round(n).toLocaleString() + ' ms'; }
function bytes(n) {
  if (n === null || n === undefined) return '—';
  const u = ['B', 'KB', 'MB', 'GB'];
  let i = 0; let v = Number(n);
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return v.toFixed(i === 0 ? 0 : 1) + ' ' + u[i];
}
function badge(status, text) {
  const s = String(status || '').toLowerCase();
  return `<span class="badge-status ${esc(s)}">${esc(text || status || '')}</span>`;
}
function sevBadge(sev) {
  const s = String(sev || '').toLowerCase();
  const cls = s === 'critical' ? 'critical' : s === 'warning' ? 'warning' : 'info';
  return `<span class="badge-status ${cls}">${esc(sev)}</span>`;
}
function dateInput(d) {
  const pad = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}
function qs(obj) {
  const p = new URLSearchParams();
  Object.entries(obj || {}).forEach(([k, v]) => { if (v !== undefined && v !== null && v !== '') p.set(k, v); });
  const s = p.toString();
  return s ? '?' + s : '';
}
function debounce(fn, ms) { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; }
function copyText(text) {
  if (navigator.clipboard && window.isSecureContext) {
    return navigator.clipboard.writeText(text).then(() => toast('Copied to clipboard', 'success'));
  }
  const ta = document.createElement('textarea');
  ta.value = text; document.body.appendChild(ta); ta.select();
  try { document.execCommand('copy'); toast('Copied to clipboard', 'success'); } catch (e) { toast('Copy failed', 'danger'); }
  document.body.removeChild(ta);
  return Promise.resolve();
}

/* ---------- toasts, modals, confirm ---------- */
function toast(message, type) {
  const el = document.createElement('div');
  el.className = `toast align-items-center text-bg-${type || 'primary'} border-0`;
  el.setAttribute('role', 'alert');
  el.innerHTML = `<div class="d-flex"><div class="toast-body">${esc(message)}</div><button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast" aria-label="Close"></button></div>`;
  $('#toasts').appendChild(el);
  const t = new bootstrap.Toast(el, { delay: type === 'danger' ? 7000 : 3500 });
  el.addEventListener('hidden.bs.toast', () => el.remove());
  t.show();
}

function modal({ title, body, footer, size, onShown }) {
  const root = $('#modal-root');
  const wrap = document.createElement('div');
  wrap.innerHTML = `
    <div class="modal fade" tabindex="-1">
      <div class="modal-dialog ${size ? 'modal-' + size : ''} modal-dialog-scrollable">
        <div class="modal-content">
          <div class="modal-header"><h5 class="modal-title">${esc(title)}</h5><button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button></div>
          <div class="modal-body">${body || ''}</div>
          ${footer !== null ? `<div class="modal-footer">${footer || '<button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Close</button>'}</div>` : ''}
        </div>
      </div>
    </div>`;
  const el = wrap.firstElementChild;
  root.appendChild(el);
  const m = new bootstrap.Modal(el);
  el.addEventListener('hidden.bs.modal', () => el.remove());
  if (onShown) el.addEventListener('shown.bs.modal', () => onShown(el));
  m.show();
  return { el, modal: m, close: () => m.hide() };
}

function confirmDialog(message, { title, okText, danger } = {}) {
  return new Promise(resolve => {
    const m = modal({
      title: title || 'Please confirm',
      body: `<p class="mb-0">${esc(message)}</p>`,
      footer: `<button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button><button type="button" class="btn btn-${danger ? 'danger' : 'primary'}" data-ok>${esc(okText || 'Confirm')}</button>`
    });
    let ok = false;
    $('[data-ok]', m.el).addEventListener('click', () => { ok = true; m.close(); });
    m.el.addEventListener('hidden.bs.modal', () => resolve(ok));
  });
}

/* ---------- API ---------- */
async function api(path, { method = 'GET', body, query, raw } = {}) {
  const headers = { 'X-Requested-With': 'XMLHttpRequest', 'Accept': 'application/json' };
  const init = { method, headers, credentials: 'same-origin' };
  if (body !== undefined) { headers['Content-Type'] = 'application/json'; init.body = JSON.stringify(body); }
  const res = await fetch(path + qs(query), init);
  if (res.status === 401) {
    showLogin();
    throw new Error('Sign in required');
  }
  if (raw) return res;
  let data = null;
  try { data = await res.json(); } catch (e) { /* no body */ }
  if (!res.ok || (data && data.success === false)) {
    const msg = (data && (data.error ? `${data.message}: ${data.error}` : data.message)) || `HTTP ${res.status}`;
    const err = new Error(msg);
    err.status = res.status; err.data = data;
    throw err;
  }
  return data;
}
const apiGet = (p, query) => api(p, { query });
const apiPost = (p, body, query) => api(p, { method: 'POST', body, query });
const apiPut = (p, body) => api(p, { method: 'PUT', body });
const apiDelete = p => api(p, { method: 'DELETE' });

function downloadUrl(path, query) {
  const a = document.createElement('a');
  a.href = path + qs(query);
  a.download = '';
  document.body.appendChild(a); a.click(); a.remove();
}

async function loadClients(force) {
  if (!App.cache.clients || force) App.cache.clients = (await apiGet('/api/admin/clients')).data || [];
  return App.cache.clients;
}
async function loadProfiles(force) {
  if (!App.cache.profiles || force) App.cache.profiles = (await apiGet('/api/admin/profiles')).data || [];
  return App.cache.profiles;
}
function clientOptions(clients, selected, includeAll) {
  return (includeAll ? `<option value="">${esc(includeAll)}</option>` : '') +
    clients.map(c => `<option value="${c.id}" ${String(selected) === String(c.id) ? 'selected' : ''}>${esc(c.name)}</option>`).join('');
}

/* ---------- forms ---------- */
function formData(form) {
  const out = {};
  $$('[name]', form).forEach(el => {
    const name = el.name;
    if (el.type === 'checkbox') out[name] = el.checked;
    else if (el.type === 'number') out[name] = el.value === '' ? null : Number(el.value);
    else if (el.tagName === 'SELECT' && el.dataset.type === 'int') out[name] = el.value === '' ? null : Number(el.value);
    else out[name] = el.value === '' ? null : el.value;
  });
  return out;
}
function fillForm(form, obj) {
  if (!obj) return;
  $$('[name]', form).forEach(el => {
    const v = obj[el.name];
    if (v === undefined) return;
    if (el.type === 'checkbox') el.checked = !!v;
    else if (v === null) el.value = '';
    else el.value = v;
  });
}
function field(label, name, { type = 'text', value = '', placeholder = '', help = '', required = false, col = 'col-md-6', attrs = '' } = {}) {
  return `<div class="${col}"><label class="form-label" for="f-${name}">${esc(label)}${required ? ' <span class="text-danger">*</span>' : ''}</label>
    <input class="form-control" type="${type}" id="f-${name}" name="${name}" value="${esc(value)}" placeholder="${esc(placeholder)}" ${required ? 'required' : ''} ${attrs} />
    ${help ? `<div class="help">${help}</div>` : ''}</div>`;
}
function selectField(label, name, options, { value, help = '', col = 'col-md-6', isInt = false } = {}) {
  return `<div class="${col}"><label class="form-label" for="f-${name}">${esc(label)}</label>
    <select class="form-select" id="f-${name}" name="${name}" ${isInt ? 'data-type="int"' : ''}>${options.map(o => `<option value="${esc(o.value)}" ${String(o.value) === String(value ?? '') ? 'selected' : ''}>${esc(o.label)}</option>`).join('')}</select>
    ${help ? `<div class="help">${help}</div>` : ''}</div>`;
}
function checkField(label, name, checked, { help = '', col = 'col-md-6' } = {}) {
  return `<div class="${col}"><div class="form-check mt-4"><input class="form-check-input" type="checkbox" id="f-${name}" name="${name}" ${checked ? 'checked' : ''} /><label class="form-check-label" for="f-${name}">${esc(label)}</label></div>${help ? `<div class="help">${help}</div>` : ''}</div>`;
}
function textareaField(label, name, { value = '', rows = 3, help = '', col = 'col-12', mono = false, placeholder = '' } = {}) {
  return `<div class="${col}"><label class="form-label" for="f-${name}">${esc(label)}</label>
    <textarea class="form-control ${mono ? 'mono' : ''}" id="f-${name}" name="${name}" rows="${rows}" placeholder="${esc(placeholder)}">${esc(value)}</textarea>
    ${help ? `<div class="help">${help}</div>` : ''}</div>`;
}

/* ---------- charts ---------- */
const SERIES = ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#6250d6', '#e34948'];
const STATUS_COLORS = { accepted: '#2a78d6', sent: '#008300', failed: '#e34948', queued: '#eda100' };

function chart(id, config) {
  const canvas = document.getElementById(id);
  if (!canvas) return null;
  if (App.charts[id]) { App.charts[id].destroy(); delete App.charts[id]; }
  Chart.defaults.font.family = getComputedStyle(document.body).fontFamily;
  Chart.defaults.color = '#6b7484';
  const c = new Chart(canvas, config);
  App.charts[id] = c;
  return c;
}
function destroyCharts() {
  Object.values(App.charts).forEach(c => { try { c.destroy(); } catch (e) { /* ignore */ } });
  App.charts = {};
}
const gridOpts = { color: '#eef0f4', drawBorder: false };
function lineChart(id, labels, datasets, opts) {
  return chart(id, {
    type: 'line',
    data: { labels, datasets: datasets.map(d => ({ tension: 0.3, borderWidth: 2, pointRadius: 0, pointHitRadius: 10, pointHoverRadius: 4, fill: false, ...d })) },
    options: {
      responsive: true, maintainAspectRatio: false, interaction: { mode: 'index', intersect: false },
      plugins: { legend: { position: 'top', labels: { usePointStyle: true, boxWidth: 8 } }, tooltip: { mode: 'index', intersect: false } },
      scales: { x: { grid: { display: false } }, y: { beginAtZero: true, grid: gridOpts, ticks: { precision: 0 } } },
      ...(opts || {})
    }
  });
}
function barChart(id, labels, datasets, opts) {
  return chart(id, {
    type: 'bar',
    data: { labels, datasets: datasets.map(d => ({ borderRadius: 4, borderSkipped: 'bottom', maxBarThickness: 28, ...d })) },
    options: {
      responsive: true, maintainAspectRatio: false, interaction: { mode: 'index', intersect: false },
      plugins: { legend: { position: 'top', labels: { usePointStyle: true, boxWidth: 8 } } },
      scales: { x: { grid: { display: false }, stacked: !!(opts && opts.stacked) }, y: { beginAtZero: true, grid: gridOpts, ticks: { precision: 0 }, stacked: !!(opts && opts.stacked) } }
    }
  });
}
function doughnutChart(id, labels, values, colors) {
  return chart(id, {
    type: 'doughnut',
    data: { labels, datasets: [{ data: values, backgroundColor: colors, borderWidth: 2, borderColor: '#fff' }] },
    options: { responsive: true, maintainAspectRatio: false, cutout: '68%', plugins: { legend: { position: 'right', labels: { usePointStyle: true, boxWidth: 8 } } } }
  });
}

/* ---------- auth & shell ---------- */
function showLogin() {
  $('#shell').classList.add('d-none');
  $('#login').classList.remove('d-none');
  if (App.refreshTimer) { clearInterval(App.refreshTimer); App.refreshTimer = null; }
  setTimeout(() => $('#login-username').focus(), 50);
}
function showShell() {
  $('#login').classList.add('d-none');
  $('#shell').classList.remove('d-none');
}
function applySession(s) {
  App.session = s;
  App.isAdmin = s.role === 'Admin';
  document.body.classList.toggle('viewer', !App.isAdmin);
  $('#user-name').textContent = s.displayName || s.username;
  $('#user-role').textContent = `${s.role} · ${s.mode}`;
  $('#user-avatar').textContent = (s.displayName || s.username || '?').trim().charAt(0).toUpperCase();
}
async function initSession() {
  try {
    const r = await api('/api/admin/auth/session');
    applySession(r.data);
    showShell();
    route();
  } catch (e) {
    showLogin();
  }
}
async function doLogin(ev) {
  ev.preventDefault();
  const btn = $('#login-submit'); const err = $('#login-error');
  btn.disabled = true; err.classList.add('d-none');
  try {
    const r = await api('/api/admin/auth/login', { method: 'POST', body: { username: $('#login-username').value.trim(), password: $('#login-password').value } });
    applySession(r.data);
    $('#login-password').value = '';
    showShell();
    toast(r.message || 'Signed in', 'success');
    route();
  } catch (e) {
    err.textContent = e.message || 'Login failed';
    err.classList.remove('d-none');
  } finally {
    btn.disabled = false;
  }
}
async function doLogout() {
  try { await api('/api/admin/auth/logout', { method: 'POST' }); } catch (e) { /* ignore */ }
  App.session = null; App.cache = { clients: null, profiles: null };
  showLogin();
}

/* ---------- router ---------- */
const Routes = {};
function setTitle(title, actionsHtml) {
  $('#page-title').textContent = title;
  $('#page-actions').innerHTML = actionsHtml || '';
  document.title = `${title} · Email Relay`;
}
function parseHash() {
  const h = (location.hash || '#/dashboard').replace(/^#\/?/, '');
  const [pathPart, queryPart] = h.split('?');
  const segs = pathPart.split('/').filter(Boolean);
  const query = Object.fromEntries(new URLSearchParams(queryPart || ''));
  return { name: segs[0] || 'dashboard', params: segs.slice(1), query };
}
async function route() {
  if (!App.session) return;
  const { name, params, query } = parseHash();
  const handler = Routes[name] || Routes.dashboard;
  App.currentRoute = name;
  if (App.refreshTimer) { clearInterval(App.refreshTimer); App.refreshTimer = null; }
  destroyCharts();
  $$('#nav .nav-link').forEach(a => a.classList.toggle('active', a.dataset.route === name));
  $('#shell').classList.remove('nav-open');
  const content = $('#content');
  content.innerHTML = '<div class="loading">Loading…</div>';
  try {
    await handler(params, query, content);
  } catch (e) {
    if (e.message !== 'Sign in required') {
      content.innerHTML = `<div class="alert alert-danger">${esc(e.message || e)}</div>`;
    }
  }
}
function autoRefresh(fn, seconds) {
  if (App.refreshTimer) clearInterval(App.refreshTimer);
  App.refreshTimer = setInterval(() => { if (document.visibilityState === 'visible') fn(); }, seconds * 1000);
}

/* ---------- boot ---------- */
document.addEventListener('DOMContentLoaded', () => {
  $$('[data-icon]').forEach(el => { el.innerHTML = Icons[el.dataset.icon] || ''; });
  $('#login-form').addEventListener('submit', doLogin);
  $('#logout-btn').addEventListener('click', doLogout);
  $('#menu-btn').addEventListener('click', () => $('#shell').classList.toggle('nav-open'));
  $('#sidebar-backdrop').addEventListener('click', () => $('#shell').classList.remove('nav-open'));
  window.addEventListener('hashchange', route);
  document.addEventListener('click', ev => {
    const copy = ev.target.closest('[data-copy]');
    if (copy) { ev.preventDefault(); copyText(copy.dataset.copy); }
  });
  initSession();
});
