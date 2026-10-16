/* ===================== views: templates, logs, security, reports, system ===================== */
'use strict';

/* ======================= TEMPLATES ======================= */
Routes.templates = async function (params, query, content) {
  setTitle('Templates', `<button class="btn btn-primary btn-sm admin-only" id="t-new">+ New template</button>`);
  const reload = () => Routes.templates(params, query, content);
  $('#t-new').addEventListener('click', () => templateForm(null, reload));
  const [r, clients] = await Promise.all([apiGet('/api/admin/templates'), loadClients()]);
  const items = r.data;
  content.innerHTML = `<div class="card"><div class="card-header">Templates <span class="spacer"></span><span class="help">Use <code>{{name}}</code> for escaped values and <code>{{{name}}}</code> for raw HTML. Clients send <code>"template": "name", "variables": {...}</code>.</span></div>
    <div class="table-responsive">${items.length ? `<table class="table table-hover"><thead><tr><th>Name</th><th>Scope</th><th>Subject</th><th>Bodies</th><th>Status</th><th>Updated</th><th></th></tr></thead><tbody>
    ${items.map(t => `<tr>
      <td><strong>${esc(t.name)}</strong><div class="text-muted-sm">${esc(t.description || '')}</div></td>
      <td>${t.clientId ? esc(t.clientName || '#' + t.clientId) : '<span class="pill">global</span>'}</td>
      <td class="cell-trunc">${esc(t.subject)}</td>
      <td>${t.htmlBody ? '<span class="pill">html</span>' : ''}${t.textBody ? '<span class="pill">text</span>' : ''}</td>
      <td>${t.isActive ? badge('ok', 'active') : badge('neutral', 'disabled')}</td>
      <td class="nowrap text-muted-sm">${fmtDate(t.updatedAt)}</td>
      <td class="text-end nowrap"><button class="btn btn-sm btn-outline-secondary" data-preview="${t.id}">Preview</button> <span class="admin-only"><button class="btn btn-sm btn-outline-primary" data-edit="${t.id}">Edit</button> <button class="btn btn-sm btn-outline-danger" data-del="${t.id}">Delete</button></span></td>
    </tr>`).join('')}</tbody></table>` : '<div class="empty">No templates yet</div>'}</div></div>`;
  $$('[data-edit]', content).forEach(b => b.addEventListener('click', () => templateForm(items.find(t => t.id === Number(b.dataset.edit)), reload)));
  $$('[data-del]', content).forEach(b => b.addEventListener('click', async () => {
    if (!(await confirmDialog('Delete this template?', { danger: true }))) return;
    try { await apiDelete(`/api/admin/templates/${b.dataset.del}`); toast('Template deleted', 'success'); reload(); } catch (e) { toast(e.message, 'danger'); }
  }));
  $$('[data-preview]', content).forEach(b => b.addEventListener('click', () => templatePreview(items.find(t => t.id === Number(b.dataset.preview)))));
  void clients;
};

async function templateForm(existing, onSaved) {
  const clients = await loadClients();
  const t = existing || { isActive: true };
  const body = `<form id="tpl-form" class="row g-3">
    ${field('Name', 'name', { value: t.name, required: true, col: 'col-md-4', placeholder: 'welcome-email', help: 'Letters, digits, _ . -' })}
    ${selectField('Scope', 'clientId', [{ value: '', label: 'Global (all clients)' }, ...clients.map(c => ({ value: c.id, label: c.name }))], { value: t.clientId, col: 'col-md-4', isInt: true })}
    ${checkField('Active', 'isActive', t.isActive, { col: 'col-md-4' })}
    ${field('Description', 'description', { value: t.description, col: 'col-12' })}
    ${field('Subject', 'subject', { value: t.subject, required: true, col: 'col-12', placeholder: 'Welcome {{name}}' })}
    ${textareaField('HTML body', 'htmlBody', { value: t.htmlBody, rows: 10, mono: true, placeholder: '<p>Hello {{name}},</p>' })}
    ${textareaField('Text body', 'textBody', { value: t.textBody, rows: 6, mono: true, placeholder: 'Hello {{name}},' })}
  </form>`;
  const m = modal({ title: existing ? `Edit template: ${existing.name}` : 'New template', size: 'xl', body, footer: `<button class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button><button class="btn btn-primary" id="tpl-save">Save</button>` });
  $('#tpl-save', m.el).addEventListener('click', async () => {
    const form = $('#tpl-form', m.el);
    if (!form.reportValidity()) return;
    const data = formData(form);
    try {
      const r = existing ? await apiPut(`/api/admin/templates/${existing.id}`, data) : await apiPost('/api/admin/templates', data);
      m.close(); toast(r.message, 'success'); onSaved();
    } catch (e) { toast(e.message, 'danger'); }
  });
}

function templatePreview(t) {
  const names = new Set();
  const re = /\{\{\{?\s*([\w.\-]+)\s*\}?\}\}/g;
  [t.subject, t.htmlBody, t.textBody].forEach(s => { let x; while ((x = re.exec(s || ''))) names.add(x[1]); });
  const vars = {}; names.forEach(n => { vars[n] = `<${n}>`; });
  const m = modal({ title: `Preview: ${t.name}`, size: 'xl', body: `<div class="row g-3"><div class="col-md-4"><label class="form-label">Variables (JSON)</label><textarea class="form-control mono" id="pv-vars" rows="12">${esc(JSON.stringify(vars, null, 2))}</textarea><button class="btn btn-primary btn-sm mt-2" id="pv-run">Render</button></div>
    <div class="col-md-8"><div class="mb-2"><strong>Subject:</strong> <span id="pv-subject"></span></div><iframe class="preview-frame" sandbox="" id="pv-frame" title="preview"></iframe><pre class="code mt-2" id="pv-text"></pre></div></div>` });
  const run = async () => {
    let v = {};
    try { v = JSON.parse($('#pv-vars', m.el).value || '{}'); } catch (e) { toast('Variables must be valid JSON', 'danger'); return; }
    try {
      const r = await apiPost(`/api/admin/templates/${t.id}/preview`, v);
      $('#pv-subject', m.el).textContent = r.data.subject;
      $('#pv-frame', m.el).srcdoc = r.data.html || '<p style="font-family:sans-serif;color:#888">(no HTML body)</p>';
      $('#pv-text', m.el).textContent = r.data.text || '';
    } catch (e) { toast(e.message, 'danger'); }
  };
  $('#pv-run', m.el).addEventListener('click', run);
  m.el.addEventListener('shown.bs.modal', run);
}

/* ======================= LOGS ======================= */
Routes.logs = async function (params, query, content) {
  setTitle('Logs', `<button class="btn btn-outline-secondary btn-sm" id="log-export">Export CSV</button>`);
  const clients = await loadClients();
  const tab = query.tab === 'requests' ? 'requests' : 'audit';
  const state = { tab, page: 1, pageSize: 50, category: query.category || '', severity: query.severity || '', action: query.action || '', username: query.username || '', clientId: query.clientId || '', ip: query.ip || '', success: query.success || '', from: query.from || '', to: query.to || '', search: query.search || '', status: '', path: '', errorsOnly: false };
  const categories = ['Authentication', 'ApiKey', 'Security', 'Admin', 'Delivery', 'System', 'Client'];
  content.innerHTML = `
    <ul class="nav nav-pills tab-nav"><li class="nav-item"><a class="nav-link ${tab === 'audit' ? 'active' : ''}" href="#/logs?tab=audit">Audit trail</a></li><li class="nav-item"><a class="nav-link ${tab === 'requests' ? 'active' : ''}" href="#/logs?tab=requests">API requests</a></li></ul>
    <div class="filters" id="log-filters">
      ${tab === 'audit' ? `
      <div><label>Category</label><select class="form-select form-select-sm" name="category"><option value="">All</option>${categories.map(c => `<option ${state.category === c ? 'selected' : ''}>${c}</option>`).join('')}</select></div>
      <div><label>Min severity</label><select class="form-select form-select-sm" name="severity"><option value="">Any</option><option ${state.severity === 'Warning' ? 'selected' : ''}>Warning</option><option ${state.severity === 'Critical' ? 'selected' : ''}>Critical</option></select></div>
      <div><label>Outcome</label><select class="form-select form-select-sm" name="success"><option value="">Any</option><option value="true" ${state.success === 'true' ? 'selected' : ''}>Success</option><option value="false" ${state.success === 'false' ? 'selected' : ''}>Failure</option></select></div>
      <div><label>Action</label><input class="form-control form-control-sm" name="action" value="${esc(state.action)}" list="action-list" placeholder="e.g. AdminLogin"><datalist id="action-list"></datalist></div>
      <div><label>User</label><input class="form-control form-control-sm" name="username" value="${esc(state.username)}"></div>` : `
      <div><label>Status</label><input class="form-control form-control-sm" name="status" type="number" placeholder="e.g. 401" style="width:100px"></div>
      <div><label>Path contains</label><input class="form-control form-control-sm" name="path" placeholder="/api/v1/emails"></div>
      <div><label>User</label><input class="form-control form-control-sm" name="username" value="${esc(state.username)}"></div>
      <div><label>&nbsp;</label><div class="form-check"><input class="form-check-input" type="checkbox" name="errorsOnly" id="f-errorsOnly"><label class="form-check-label" for="f-errorsOnly">Errors only</label></div></div>`}
      <div><label>Client</label><select class="form-select form-select-sm" name="clientId">${clientOptions(clients, state.clientId, 'All')}</select></div>
      <div><label>IP</label><input class="form-control form-control-sm" name="ip" value="${esc(state.ip)}" style="width:140px"></div>
      <div><label>From</label><input type="date" class="form-control form-control-sm" name="from" value="${esc(state.from)}"></div>
      <div><label>To</label><input type="date" class="form-control form-control-sm" name="to" value="${esc(state.to)}"></div>
      ${tab === 'audit' ? `<div style="flex:1;min-width:200px"><label>Search details</label><input class="form-control form-control-sm" name="search" value="${esc(state.search)}"></div>` : ''}
      <div><button class="btn btn-primary btn-sm" id="log-apply">Apply</button></div>
    </div>
    <div class="card"><div class="table-responsive" id="log-table"><div class="loading">Loading…</div></div><div class="pager" id="log-pager"></div></div>`;

  if (tab === 'audit') { apiGet('/api/admin/logs/audit/actions').then(r => { $('#action-list').innerHTML = r.data.map(a => `<option value="${esc(a)}">`).join(''); }).catch(() => {}); }
  const filters = $('#log-filters');
  const buildQuery = () => {
    const f = formData(filters);
    const common = { clientId: f.clientId, ip: f.ip, username: f.username, from: f.from, to: f.to ? f.to + 'T23:59:59' : '', page: state.page, pageSize: state.pageSize };
    return tab === 'audit'
      ? { ...common, category: f.category, severity: f.severity, action: f.action, success: f.success, search: f.search }
      : { ...common, status: f.status, path: f.path, errorsOnly: f.errorsOnly ? 'true' : '' };
  };
  const load = async () => {
    const q = buildQuery();
    const r = await apiGet(tab === 'audit' ? '/api/admin/logs/audit' : '/api/admin/logs/requests', q);
    const page = r.data;
    const el = $('#log-table');
    if (!page.items.length) { el.innerHTML = '<div class="empty">No entries</div>'; $('#log-pager').innerHTML = ''; return; }
    el.innerHTML = tab === 'audit' ? auditTable(page.items) : requestTable(page.items);
    $$('tr[data-detail]', el).forEach(tr => tr.addEventListener('click', () => {
      const item = page.items[Number(tr.dataset.detail)];
      modal({ title: tab === 'audit' ? `${item.action}` : `${item.method} ${item.path}`, size: 'lg', body: `<pre class="code">${esc(JSON.stringify(item, null, 2))}</pre>` });
    }));
    renderPager($('#log-pager'), page, p => { state.page = p; load(); });
  };
  $('#log-apply').addEventListener('click', () => { state.page = 1; load(); });
  $$('select', filters).forEach(s => s.addEventListener('change', () => { state.page = 1; load(); }));
  $$('input', filters).forEach(i => i.addEventListener('keydown', e => { if (e.key === 'Enter') { state.page = 1; load(); } }));
  $('#log-export').addEventListener('click', () => { const q = buildQuery(); delete q.page; delete q.pageSize; downloadUrl(tab === 'audit' ? '/api/admin/logs/audit/export' : '/api/admin/logs/requests/export', q); });
  await load();
  autoRefresh(load, 30);
};

function auditTable(items) {
  return `<table class="table table-hover table-sm"><thead><tr><th>Time</th><th>Severity</th><th>Category</th><th>Action</th><th>Result</th><th>Who</th><th>IP</th><th>Details</th></tr></thead><tbody>
    ${items.map((a, i) => `<tr class="clickable" data-detail="${i}">
      <td class="nowrap">${fmtDate(a.timestamp, true)}</td><td>${sevBadge(a.severity)}</td><td>${esc(a.category)}</td><td><strong>${esc(a.action)}</strong></td>
      <td>${a.success ? '<span class="text-success">✓</span>' : '<span class="text-danger">✗</span>'}</td>
      <td class="cell-trunc sm">${esc(a.username || a.clientName || (a.clientId ? '#' + a.clientId : ''))}</td>
      <td class="mono">${esc(a.ipAddress || '')}</td>
      <td class="cell-trunc lg" title="${esc(a.details || '')}">${esc(a.details || '')}</td></tr>`).join('')}</tbody></table>`;
}
function requestTable(items) {
  return `<table class="table table-hover table-sm"><thead><tr><th>Time</th><th>Status</th><th>Method</th><th>Path</th><th>Client / user</th><th>IP</th><th class="text-end">Time</th><th class="text-end">Bytes</th></tr></thead><tbody>
    ${items.map((r, i) => `<tr class="clickable" data-detail="${i}">
      <td class="nowrap">${fmtDate(r.timestamp, true)}</td>
      <td>${badge(r.statusCode >= 500 ? 'failed' : r.statusCode >= 400 ? 'warning' : 'ok', r.statusCode)}</td>
      <td class="mono">${esc(r.method)}</td><td class="mono cell-trunc">${esc(r.path)}${r.query ? `<span class="text-muted-sm">${esc(r.query)}</span>` : ''}</td>
      <td class="cell-trunc sm">${esc(r.clientName || r.username || '—')}</td><td class="mono">${esc(r.ipAddress)}</td>
      <td class="text-end nowrap">${ms(r.durationMs)}</td><td class="text-end">${r.requestBytes != null ? bytes(r.requestBytes) : '—'}</td></tr>`).join('')}</tbody></table>`;
}

/* ======================= SECURITY ======================= */
Routes.security = async function (params, query, content) {
  setTitle('Security', `<button class="btn btn-primary btn-sm admin-only" id="blk-new">+ Block</button> <button class="btn btn-outline-secondary btn-sm admin-only" id="cache-inv">Invalidate caches</button>`);
  const reload = () => Routes.security(params, query, content);
  $('#blk-new').addEventListener('click', () => blockForm(reload));
  $('#cache-inv').addEventListener('click', async () => { try { const r = await apiPost('/api/admin/security/cache/invalidate'); toast(r.message, 'success'); } catch (e) { toast(e.message, 'danger'); } });

  const showHistory = query.history === '1';
  const [blocks, seen, keyHealth, usersRes, authen, clients] = await Promise.all([
    apiGet('/api/admin/security/blocks', { includeHistory: showHistory }),
    apiGet('/api/admin/security/seen-ips', { limit: 200 }),
    apiGet('/api/admin/security/key-health'),
    App.isAdmin ? apiGet('/api/admin/security/users').catch(() => ({ data: [] })) : Promise.resolve({ data: [] }),
    App.isAdmin ? apiGet('/api/admin/auth/authen-status').catch(e => ({ data: null, message: e.message })) : Promise.resolve(null),
    loadClients()
  ]);

  content.innerHTML = `
    <div class="row g-3">
      <div class="col-lg-7">
        <div class="card mb-3"><div class="card-header">Block list <span class="spacer"></span><a class="text-muted-sm" href="#/security?history=${showHistory ? '0' : '1'}">${showHistory ? 'active only' : 'show history'}</a></div>
          <div class="table-responsive">${blocks.data.length ? `<table class="table table-sm table-hover"><thead><tr><th>Target</th><th>Reason</th><th>By</th><th>Expires</th><th class="text-end">Hits</th><th class="admin-only"></th></tr></thead><tbody>
            ${blocks.data.map(b => `<tr class="${b.isActive ? '' : 'text-muted'}"><td><span class="pill">${esc(b.targetType)}</span><span class="mono">${esc(b.target)}</span></td><td class="cell-trunc" title="${esc(b.reason)}">${esc(b.reason)}</td><td>${esc(b.createdBy)}<div class="text-muted-sm">${fmtDate(b.createdAt)}</div></td>
              <td class="nowrap">${b.removedAt ? `removed ${fmtDate(b.removedAt)}` : b.isPermanent ? badge('failed', 'permanent') : fmtDate(b.expiresAt)}</td><td class="text-end">${num(b.hitCount)}</td>
              <td class="text-end admin-only">${b.isActive ? `<button class="btn btn-sm btn-outline-secondary" data-unblock="${b.id}">Unblock</button>` : ''}</td></tr>`).join('')}</tbody></table>` : '<div class="empty">Nothing is blocked</div>'}</div></div>
        <div class="card mb-3"><div class="card-header">Caller verification: source IPs per client</div>
          <div class="table-responsive" style="max-height:360px;overflow:auto">${seen.data.length ? `<table class="table table-sm"><thead><tr><th>Client</th><th>IP</th><th>First seen</th><th>Last seen</th><th class="text-end">Requests</th><th class="admin-only"></th></tr></thead><tbody>
            ${seen.data.map(s => `<tr><td>${esc(s.clientName || '#' + s.clientId)}</td><td class="mono">${esc(s.ipAddress)}</td><td class="nowrap">${fmtDate(s.firstSeenAt)}</td><td class="nowrap">${fmtDate(s.lastSeenAt)}</td><td class="text-end">${num(s.requestCount)}</td><td class="text-end admin-only"><button class="btn btn-sm btn-outline-danger" data-blockip="${esc(s.ipAddress)}">Block</button></td></tr>`).join('')}</tbody></table>` : '<div class="empty">No API traffic recorded yet</div>'}</div></div>
      </div>
      <div class="col-lg-5">
        <div class="card mb-3"><div class="card-header">Key health</div><div class="card-body p-0"><ul class="list-compact px-3">
          ${keyHealth.data.length ? keyHealth.data.map(x => `<li><a href="#/clients/${x.key.clientId}">${esc(x.key.clientName || '')}</a> · ${esc(x.key.name)} <span class="mono">${esc(x.key.prefix)}…</span><div>${x.expired ? badge('failed', 'expired') : ''}${x.expiringSoon ? badge('warning', 'expires ' + fmtDate(x.key.expiresAt)) : ''}${x.rotationDue ? badge('warning', 'rotation due') : ''}${x.stale ? badge('neutral', 'unused 90d+') : ''}</div></li>`).join('') : '<li class="empty">All keys healthy</li>'}</ul></div></div>
        ${App.isAdmin ? `<div class="card mb-3"><div class="card-header">Authentication backend</div><div class="card-body">
          ${authen && authen.data ? `<dl class="kv"><dt>AuthenAPI</dt><dd>${authen.data.configured ? (authen.data.reachable ? badge('ok', 'reachable') : badge('failed', 'unreachable')) : badge('neutral', 'not configured')}</dd>
            ${authen.data.baseUrl ? `<dt>Base URL</dt><dd class="mono">${esc(authen.data.baseUrl)}</dd>` : ''}
            <dt>Local admin</dt><dd>${authen.data.localAdminEnabled ? badge('warning', 'enabled') : badge('neutral', 'disabled')}</dd>
            ${authen.data.cacheDays ? `<dt>Credential cache</dt><dd>${authen.data.cacheDays} days</dd>` : ''}
            <dt>Result</dt><dd>${esc(authen.message || '')}</dd></dl>` : `<div class="text-danger">${esc(authen ? authen.message : '')}</div>`}
        </div></div>
        <div class="card"><div class="card-header">Console users (cached from AuthenAPI)</div><div class="table-responsive">${usersRes.data.length ? `<table class="table table-sm"><thead><tr><th>User</th><th>Role</th><th>Last login</th><th></th></tr></thead><tbody>
          ${usersRes.data.map(u => `<tr class="${u.isEnabled ? '' : 'text-muted'}"><td>${esc(u.displayName || u.username)}<div class="text-muted-sm">${esc(u.username)} · ${esc(u.source)}${u.lockedUntil && new Date(u.lockedUntil) > new Date() ? ' · <span class="text-danger">locked</span>' : ''}</div></td><td><span class="pill">${esc(u.role)}</span></td><td class="text-muted-sm nowrap">${u.lastLogin ? `${ago(u.lastLogin)}<br>${esc(u.lastLoginIp || '')}` : '—'}</td>
            <td class="text-end nowrap"><button class="btn btn-sm btn-outline-secondary" data-user-cache="${esc(u.username)}" title="Force re-verification by AuthenAPI">Clear cache</button> ${u.isEnabled ? `<button class="btn btn-sm btn-outline-danger" data-user-disable="${esc(u.username)}">Disable</button>` : `<button class="btn btn-sm btn-outline-success" data-user-enable="${esc(u.username)}">Enable</button>`}</td></tr>`).join('')}</tbody></table>` : '<div class="empty">No console users yet</div>'}</div></div>` : ''}
      </div>
    </div>`;

  $$('[data-unblock]', content).forEach(b => b.addEventListener('click', async () => { try { await apiDelete(`/api/admin/security/blocks/${b.dataset.unblock}`); toast('Block removed', 'success'); reload(); } catch (e) { toast(e.message, 'danger'); } }));
  $$('[data-blockip]', content).forEach(b => b.addEventListener('click', () => blockForm(reload, { targetType: 'Ip', target: b.dataset.blockip })));
  const userAct = (attr, path, label) => $$(`[${attr}]`, content).forEach(b => b.addEventListener('click', async () => {
    try { const r = await apiPost(`/api/admin/security/users/${encodeURIComponent(b.getAttribute(attr))}/${path}`); toast(r.message || label, 'success'); reload(); } catch (e) { toast(e.message, 'danger'); }
  }));
  userAct('data-user-cache', 'clear-cache', 'Cache cleared');
  userAct('data-user-disable', 'disable', 'User disabled');
  userAct('data-user-enable', 'enable', 'User enabled');
  void clients;
};

function blockForm(onSaved, preset) {
  const p = preset || {};
  const body = `<form id="blk-form" class="row g-3">
    ${selectField('Target type', 'targetType', [{ value: 'Ip', label: 'IP address / CIDR' }, { value: 'Client', label: 'Client (id)' }, { value: 'ApiKey', label: 'API key (prefix)' }], { value: p.targetType || 'Ip' })}
    ${field('Target', 'target', { value: p.target || '', required: true, placeholder: '203.0.113.0/24' })}
    ${field('Reason', 'reason', { value: '', required: true, col: 'col-12', placeholder: 'Why is this blocked?' })}
    ${field('Duration (minutes)', 'durationMinutes', { type: 'number', value: 60 })}
    ${checkField('Permanent', 'permanent', false)}
  </form>`;
  const m = modal({ title: 'Add block', body, footer: `<button class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button><button class="btn btn-danger" id="blk-save">Block</button>` });
  $('#blk-save', m.el).addEventListener('click', async () => {
    const form = $('#blk-form', m.el);
    if (!form.reportValidity()) return;
    try { const r = await apiPost('/api/admin/security/blocks', formData(form)); m.close(); toast(r.message, 'success'); onSaved(); } catch (e) { toast(e.message, 'danger'); }
  });
}

/* ======================= REPORTS ======================= */
Routes.reports = async function (params, query, content) {
  setTitle('Usage reports', `<button class="btn btn-outline-secondary btn-sm" id="rep-export">Export CSV</button>`);
  const clients = await loadClients();
  const today = new Date();
  const state = { from: query.from || dateInput(new Date(today.getTime() - 29 * 86400000)), to: query.to || dateInput(today), clientId: query.clientId || '' };
  content.innerHTML = `
    <div class="filters" id="rep-filters">
      <div><label>From</label><input type="date" class="form-control form-control-sm" name="from" value="${esc(state.from)}"></div>
      <div><label>To</label><input type="date" class="form-control form-control-sm" name="to" value="${esc(state.to)}"></div>
      <div><label>Client</label><select class="form-select form-select-sm" name="clientId">${clientOptions(clients, state.clientId, 'All clients')}</select></div>
      <div><button class="btn btn-primary btn-sm" id="rep-apply">Apply</button>
        <button class="btn btn-outline-secondary btn-sm" data-range="7">7d</button><button class="btn btn-outline-secondary btn-sm" data-range="30">30d</button><button class="btn btn-outline-secondary btn-sm" data-range="90">90d</button></div>
    </div>
    <div id="rep-body"><div class="loading">Loading…</div></div>`;
  const filters = $('#rep-filters');
  const read = () => Object.assign(state, formData(filters));
  const q = () => ({ from: state.from, to: state.to ? state.to + 'T23:59:59' : '', clientId: state.clientId });
  const load = async () => {
    read();
    const r = await apiGet('/api/admin/reports/usage', q());
    const d = r.data;
    const totals = d.byStatus.reduce((a, s) => { a[s.status] = s.count; return a; }, {});
    const all = d.byStatus.reduce((a, s) => a + s.count, 0);
    $('#rep-body').innerHTML = `
      <div class="kpi-grid">
        ${kpi('Accepted', num(all), 'messages in period')}
        ${kpi('Sent', num(totals.sent || 0), all ? Math.round((totals.sent || 0) * 100 / all) + '% of accepted' : '', 'good')}
        ${kpi('Failed', num(totals.failed || 0), all ? Math.round((totals.failed || 0) * 100 / all) + '% of accepted' : '', (totals.failed || 0) > 0 ? 'bad' : '')}
        ${kpi('Recipients', num(d.byClient.reduce((a, c) => a + c.recipients, 0)), 'addresses delivered to')}
        ${kpi('API requests', num(d.byClient.reduce((a, c) => a + c.requests, 0)), 'authenticated calls')}
      </div>
      <div class="row g-3 mb-3">
        <div class="col-lg-8"><div class="card h-100"><div class="card-header">Per day</div><div class="card-body"><div class="chart-box tall"><canvas id="r-daily"></canvas></div></div></div></div>
        <div class="col-lg-4"><div class="card h-100"><div class="card-header">By status</div><div class="card-body"><div class="chart-box tall"><canvas id="r-status"></canvas></div></div></div></div>
      </div>
      <div class="row g-3">
        <div class="col-lg-7"><div class="card"><div class="card-header">By client</div><div class="table-responsive"><table class="table table-sm table-hover"><thead><tr><th>Client</th><th class="text-end">Accepted</th><th class="text-end">Sent</th><th class="text-end">Failed</th><th class="text-end">Queued</th><th class="text-end">Recipients</th><th class="text-end">Avg ms</th><th class="text-end">Requests</th></tr></thead><tbody>
          ${d.byClient.map(c => `<tr><td>${c.clientId ? `<a href="#/clients/${c.clientId}">${esc(c.clientName)}</a>` : esc(c.clientName)}</td><td class="text-end">${num(c.accepted)}</td><td class="text-end">${num(c.sent)}</td><td class="text-end ${c.failed ? 'text-danger' : ''}">${num(c.failed)}</td><td class="text-end">${num(c.queued)}</td><td class="text-end">${num(c.recipients)}</td><td class="text-end">${c.avgDurationMs != null ? num(Math.round(c.avgDurationMs)) : '—'}</td><td class="text-end">${num(c.requests)}</td></tr>`).join('') || '<tr><td colspan="8" class="empty">No data</td></tr>'}
        </tbody></table></div></div></div>
        <div class="col-lg-5">
          <div class="card mb-3"><div class="card-header">By sender profile</div><div class="table-responsive"><table class="table table-sm"><thead><tr><th>Profile</th><th class="text-end">Sent</th><th class="text-end">Failed</th><th class="text-end">Avg ms</th></tr></thead><tbody>
            ${d.byProfile.map(p => `<tr><td>${esc(p.profileName)} <span class="pill">${esc(p.type)}</span></td><td class="text-end">${num(p.sent)}</td><td class="text-end ${p.failed ? 'text-danger' : ''}">${num(p.failed)}</td><td class="text-end">${p.avgDurationMs != null ? num(Math.round(p.avgDurationMs)) : '—'}</td></tr>`).join('') || '<tr><td colspan="4" class="empty">No data</td></tr>'}
          </tbody></table></div></div>
          <div class="card"><div class="card-header">Top delivery errors</div><div class="card-body p-0"><ul class="list-compact px-3">${d.topErrors.map(e => `<li><span class="pill">${num(e.count)}</span> ${esc(e.error)}</li>`).join('') || '<li class="empty">No failures</li>'}</ul></div></div>
        </div>
      </div>`;
    const labels = d.byDay.map(b => { const t = new Date(b.bucket); return `${t.getMonth() + 1}/${t.getDate()}`; });
    barChart('r-daily', labels, [
      { label: 'Sent', data: d.byDay.map(b => b.sent), backgroundColor: STATUS_COLORS.sent },
      { label: 'Failed', data: d.byDay.map(b => b.failed), backgroundColor: STATUS_COLORS.failed }
    ], { stacked: true });
    doughnutChart('r-status', d.byStatus.map(s => s.status), d.byStatus.map(s => s.count), d.byStatus.map(s => STATUS_COLORS[s.status] || '#9aa3b2'));
  };
  $('#rep-apply').addEventListener('click', load);
  $$('[data-range]', filters).forEach(b => b.addEventListener('click', () => {
    const n = Number(b.dataset.range);
    $('[name=from]', filters).value = dateInput(new Date(Date.now() - (n - 1) * 86400000));
    $('[name=to]', filters).value = dateInput(new Date());
    load();
  }));
  $('#rep-export').addEventListener('click', () => { read(); downloadUrl('/api/admin/reports/usage/export', q()); });
  await load();
};

/* ======================= SYSTEM ======================= */
Routes.system = async function (params, query, content) {
  setTitle('System');
  const r = await apiGet('/api/admin/system/info');
  const s = r.data;
  const flag = v => v ? badge('ok', 'on') : badge('neutral', 'off');
  content.innerHTML = `<div class="row g-3">
    <div class="col-lg-6"><div class="card mb-3"><div class="card-header">Runtime</div><div class="card-body"><dl class="kv">
      <dt>Version</dt><dd>${esc(s.version)}</dd><dt>Environment</dt><dd>${esc(s.environment)}</dd><dt>Started</dt><dd>${fmtDate(s.startedAt, true)} (${ago(s.startedAt)})</dd>
      <dt>Database</dt><dd>${esc(s.databaseProvider)}</dd><dt>Queue</dt><dd>${s.queueInFlight} in flight · ${s.queuePending} pending</dd>
      <dt>Workers</dt><dd>${s.workerCount} · max ${s.maxAttempts} attempts</dd>
      <dt>Attachments</dt><dd>${bytes(s.attachmentStorageBytes)} in <span class="mono">${esc(s.attachmentStoragePath)}</span></dd>
      <dt>Retention</dt><dd>messages ${s.messageRetentionDays}d · audit ${s.auditRetentionDays}d · requests ${s.requestLogRetentionDays}d</dd>
    </dl></div></div>
    <div class="card"><div class="card-header">Security configuration</div><div class="card-body"><dl class="kv">
      <dt>API key header</dt><dd class="mono">${esc(s.apiKeyHeaderName)}</dd>
      <dt>Anomaly detection</dt><dd>${flag(s.anomalyDetectionEnabled)}</dd>
      <dt>Global IP allow list</dt><dd>${flag(s.ipAllowListEnabled)}</dd>
      <dt>Global rate limit</dt><dd>${flag(s.globalRateLimitEnabled)}</dd>
      <dt>Alerts</dt><dd>${flag(s.alertsEnabled)}</dd>
      <dt>AuthenAPI</dt><dd>${s.authenApiConfigured ? `${badge('ok', 'configured')} <span class="mono">${esc(s.authenApiBaseUrl || '')}</span>` : badge('neutral', 'not configured')}</dd>
      <dt>Local admin</dt><dd>${s.localAdminEnabled ? badge('warning', 'enabled') : badge('neutral', 'disabled')}</dd>
    </dl><div class="help mt-2">Settings are read from appsettings.json (sections Security, Anomaly, Delivery, Retention, Alerts, AuthenApi).</div></div></div></div>
    <div class="col-lg-6"><div class="card mb-3 admin-only"><div class="card-header">Maintenance</div><div class="card-body d-flex flex-column gap-2">
      <button class="btn btn-outline-primary" id="sys-purge">Run retention purge now</button>
      <button class="btn btn-outline-primary" id="sys-rotation">Run key rotation check</button>
      <button class="btn btn-outline-secondary" id="sys-cache">Invalidate caches</button>
      <div id="sys-result"></div></div></div>
    <div class="card"><div class="card-header">Resources</div><div class="card-body"><ul class="mb-0">
      <li><a href="/swagger" target="_blank" rel="noopener">Swagger / OpenAPI</a> — public API (v1) and administration API</li>
      <li><a href="/health" target="_blank" rel="noopener">/health</a> · <a href="/health/ready" target="_blank" rel="noopener">/health/ready</a></li>
      <li>Documentation: README.md, docs/ARCHITECTURE.md, docs/API_GUIDE.md, docs/SECURITY.md, docs/DEPLOYMENT.md</li>
    </ul></div></div></div></div>`;
  const run = async (sel, path, confirmMsg) => {
    const b = $(sel); if (!b) return;
    b.addEventListener('click', async () => {
      if (confirmMsg && !(await confirmDialog(confirmMsg))) return;
      b.disabled = true;
      try { const r2 = await apiPost(path); $('#sys-result').innerHTML = `<div class="alert alert-success py-2 mb-0">${esc(r2.message)} ${r2.data ? `<code>${esc(JSON.stringify(r2.data))}</code>` : ''}</div>`; }
      catch (e) { $('#sys-result').innerHTML = `<div class="alert alert-danger py-2 mb-0">${esc(e.message)}</div>`; }
      finally { b.disabled = false; }
    });
  };
  run('#sys-purge', '/api/admin/system/purge', 'Delete messages, attachments and logs older than the retention policy now?');
  run('#sys-rotation', '/api/admin/system/rotation-check');
  run('#sys-cache', '/api/admin/security/cache/invalidate');
};
