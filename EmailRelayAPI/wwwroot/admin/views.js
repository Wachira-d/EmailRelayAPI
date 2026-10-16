/* ===================== views: dashboard, messages, clients, profiles ===================== */
'use strict';

/* ======================= DASHBOARD ======================= */
Routes.dashboard = async function (params, query, content) {
  setTitle('Dashboard', `<button class="btn btn-outline-secondary btn-sm" id="dash-refresh">Refresh</button>`);
  $('#dash-refresh').addEventListener('click', () => renderDashboard(content));
  await renderDashboard(content);
  autoRefresh(() => renderDashboard(content, true), 30);
};

async function renderDashboard(content, silent) {
  const r = await apiGet('/api/admin/dashboard');
  const d = r.data;
  $('#brand-version').textContent = 'v' + d.version;
  const unhealthy = d.profiles.filter(p => p.isActive && !p.healthy);
  content.innerHTML = `
    <div class="kpi-grid">
      ${kpi('Sent today', num(d.sentToday), `${num(d.sent7Days)} in 7 days`, 'good')}
      ${kpi('Failed today', num(d.failedToday), `${num(d.failed7Days)} in 7 days`, d.failedToday > 0 ? 'bad' : '')}
      ${kpi('In queue', num(d.queuedNow + d.sendingNow), `${num(d.scheduledNow)} scheduled · ${num(d.sendingNow)} sending`, 'warn')}
      ${kpi('Success rate', d.successRateToday + '%', 'completed today')}
      ${kpi('Avg latency', d.avgDurationMsToday == null ? '—' : Math.round(d.avgDurationMsToday) + ' ms', 'per delivery today', 'primary')}
      ${kpi('Security events', num(d.securityEvents24h), `${num(d.criticalEvents24h)} critical · ${num(d.authFailures24h)} auth failures (24h)`, d.criticalEvents24h > 0 ? 'bad' : '')}
      ${kpi('Active blocks', num(d.activeBlocks), 'IPs / clients / keys', d.activeBlocks > 0 ? 'warn' : '')}
      ${kpi('Keys due rotation', num(d.keysDueForRotation), `${num(d.activeKeys)} active keys · ${num(d.activeClients)} clients`, d.keysDueForRotation > 0 ? 'warn' : '')}
    </div>
    ${unhealthy.length ? `<div class="alert alert-danger d-flex align-items-center gap-2"><strong>Sender profile problem:</strong> ${unhealthy.map(p => `<a href="#/profiles/${p.id}">${esc(p.name)}</a> (${p.consecutiveFailures} consecutive failures)`).join(', ')}</div>` : ''}
    <div class="row g-3 mb-3">
      <div class="col-lg-8"><div class="card h-100"><div class="card-header">Last 24 hours <span class="spacer"></span><span class="text-muted-sm">per hour</span></div><div class="card-body"><div class="chart-box"><canvas id="c-hourly"></canvas></div></div></div></div>
      <div class="col-lg-4"><div class="card h-100"><div class="card-header">Delivery today</div><div class="card-body"><div class="chart-box"><canvas id="c-status"></canvas></div></div></div></div>
    </div>
    <div class="row g-3 mb-3">
      <div class="col-lg-7"><div class="card h-100"><div class="card-header">Last 30 days <span class="spacer"></span><span class="text-muted-sm">per day</span></div><div class="card-body"><div class="chart-box"><canvas id="c-daily"></canvas></div></div></div></div>
      <div class="col-lg-5"><div class="card h-100"><div class="card-header">Top clients today</div><div class="table-responsive">${topClientsTable(d.topClients)}</div></div></div>
    </div>
    <div class="row g-3">
      <div class="col-lg-4"><div class="card h-100"><div class="card-header">Sender profiles</div><div class="card-body p-0"><ul class="list-compact px-3">${d.profiles.length ? d.profiles.map(p => `
        <li><a href="#/profiles/${p.id}"><span class="health-dot ${!p.isActive ? 'off' : p.healthy ? 'ok' : 'bad'}"></span>${esc(p.name)}</a> <span class="pill">${esc(p.type)}</span>
          <div class="text-muted-sm">${num(p.sentToday)} sent · ${num(p.failedToday)} failed today${p.lastError ? ` · <span class="text-danger">${esc(p.lastError.slice(0, 80))}</span>` : ''}</div></li>`).join('') : '<li class="empty">No sender profiles yet</li>'}</ul></div></div></div>
      <div class="col-lg-4"><div class="card h-100"><div class="card-header">Recent security events <span class="spacer"></span><a href="#/logs?tab=audit&category=Security" class="text-muted-sm">all</a></div><div class="card-body p-0"><ul class="list-compact px-3">${d.recentSecurity.length ? d.recentSecurity.map(a => `
        <li>${sevBadge(a.severity)} <strong>${esc(a.action)}</strong> <span class="text-muted-sm">${ago(a.timestamp)}</span>
          <div class="text-muted-sm">${esc((a.details || '').slice(0, 140))}${a.ipAddress ? ` · ${esc(a.ipAddress)}` : ''}</div></li>`).join('') : '<li class="empty">No security events</li>'}</ul></div></div></div>
      <div class="col-lg-4"><div class="card h-100"><div class="card-header">Recent failures <span class="spacer"></span><a href="#/messages?status=failed" class="text-muted-sm">all</a></div><div class="card-body p-0"><ul class="list-compact px-3">${d.recentFailures.length ? d.recentFailures.map(m => `
        <li><a href="#/messages/${m.id}">${esc(m.subject || '(no subject)')}</a> <span class="text-muted-sm">${ago(m.createdAt)}</span>
          <div class="text-muted-sm">${esc(m.clientName || '')} → ${esc(m.to)}<br><span class="text-danger">${esc((m.lastError || '').slice(0, 120))}</span></div></li>`).join('') : '<li class="empty">No failures</li>'}</ul></div></div></div>
    </div>
    <div class="text-muted-sm mt-3">Updated ${fmtDate(d.generatedAt, true)} · queue in-flight ${d.queueInFlight}, pending ${d.queuePending} · up since ${fmtDate(d.startedAt)}</div>`;

  const hl = d.hourly.map(b => { const t = new Date(b.bucket); return String(t.getHours()).padStart(2, '0') + ':00'; });
  lineChart('c-hourly', hl, [
    { label: 'Accepted', data: d.hourly.map(b => b.accepted), borderColor: STATUS_COLORS.accepted },
    { label: 'Sent', data: d.hourly.map(b => b.sent), borderColor: STATUS_COLORS.sent },
    { label: 'Failed', data: d.hourly.map(b => b.failed), borderColor: STATUS_COLORS.failed }
  ]);
  doughnutChart('c-status', ['Sent', 'Failed', 'Queued', 'Scheduled'], [d.sentToday, d.failedToday, d.queuedNow + d.sendingNow, d.scheduledNow], [STATUS_COLORS.sent, STATUS_COLORS.failed, STATUS_COLORS.queued, '#9aa3b2']);
  const dl = d.daily.map(b => { const t = new Date(b.bucket); return `${t.getMonth() + 1}/${t.getDate()}`; });
  barChart('c-daily', dl, [
    { label: 'Sent', data: d.daily.map(b => b.sent), backgroundColor: STATUS_COLORS.sent },
    { label: 'Failed', data: d.daily.map(b => b.failed), backgroundColor: STATUS_COLORS.failed }
  ], { stacked: true });
}

function kpi(label, value, sub, cls) {
  return `<div class="kpi ${cls || ''}"><div class="kpi-label">${esc(label)}</div><div class="kpi-value">${value}</div><div class="kpi-sub">${sub || ''}</div></div>`;
}
function topClientsTable(rows) {
  if (!rows.length) return '<div class="empty">No traffic today</div>';
  return `<table class="table table-sm table-hover"><thead><tr><th>Client</th><th class="text-end">Accepted</th><th class="text-end">Sent</th><th class="text-end">Failed</th><th class="text-end">Quota</th></tr></thead><tbody>
    ${rows.map(c => `<tr><td>${c.clientId ? `<a href="#/clients/${c.clientId}">${esc(c.clientName)}</a>` : esc(c.clientName)}</td><td class="text-end">${num(c.accepted)}</td><td class="text-end">${num(c.sent)}</td><td class="text-end ${c.failed ? 'text-danger' : ''}">${num(c.failed)}</td><td class="text-end text-muted-sm">${c.dailyQuota ? Math.round(c.accepted * 100 / c.dailyQuota) + '%' : '—'}</td></tr>`).join('')}
  </tbody></table>`;
}

/* ======================= MESSAGES ======================= */
Routes.messages = async function (params, query, content) {
  if (params[0]) return renderMessageDetail(params[0], content);
  setTitle('Messages', `<button class="btn btn-outline-secondary btn-sm" id="msg-export">Export CSV</button>`);
  const [clients, profiles] = await Promise.all([loadClients(), loadProfiles()]);
  const state = { status: query.status || '', clientId: query.clientId || '', profileId: query.profileId || '', search: query.search || '', from: query.from || '', to: query.to || '', page: Number(query.page || 1), pageSize: 50 };
  const statuses = ['queued', 'scheduled', 'sending', 'sent', 'failed', 'cancelled'];
  content.innerHTML = `
    <div class="filters" id="msg-filters">
      <div><label>Status</label><select class="form-select form-select-sm" name="status"><option value="">All</option>${statuses.map(s => `<option value="${s}" ${state.status === s ? 'selected' : ''}>${s}</option>`).join('')}</select></div>
      <div><label>Client</label><select class="form-select form-select-sm" name="clientId">${clientOptions(clients, state.clientId, 'All clients')}</select></div>
      <div><label>Profile</label><select class="form-select form-select-sm" name="profileId"><option value="">All profiles</option>${profiles.map(p => `<option value="${p.id}" ${String(state.profileId) === String(p.id) ? 'selected' : ''}>${esc(p.name)}</option>`).join('')}</select></div>
      <div><label>From</label><input type="date" class="form-control form-control-sm" name="from" value="${esc(state.from)}"></div>
      <div><label>To</label><input type="date" class="form-control form-control-sm" name="to" value="${esc(state.to)}"></div>
      <div style="flex:1;min-width:220px"><label>Search (subject, recipient, sender, error, id)</label><input class="form-control form-control-sm" name="search" value="${esc(state.search)}" placeholder="Type to search…"></div>
      <div><button class="btn btn-primary btn-sm" id="msg-apply">Apply</button> <button class="btn btn-outline-secondary btn-sm" id="msg-reset">Reset</button></div>
    </div>
    <div class="card"><div class="table-responsive" id="msg-table"><div class="loading">Loading…</div></div><div class="pager" id="msg-pager"></div></div>`;

  const filters = $('#msg-filters');
  const readFilters = () => { Object.assign(state, formData(filters)); state.page = 1; };
  const load = async () => {
    const q = { status: state.status, clientId: state.clientId, profileId: state.profileId, search: state.search, from: state.from, to: state.to ? state.to + 'T23:59:59' : '', page: state.page, pageSize: state.pageSize };
    const r = await apiGet('/api/admin/messages', q);
    renderMessageTable($('#msg-table'), r.data, $('#msg-pager'), p => { state.page = p; load(); });
  };
  $('#msg-apply').addEventListener('click', () => { readFilters(); load(); });
  $('#msg-reset').addEventListener('click', () => { location.hash = '#/messages'; });
  $('[name=search]', filters).addEventListener('keydown', e => { if (e.key === 'Enter') { readFilters(); load(); } });
  $$('select', filters).forEach(s => s.addEventListener('change', () => { readFilters(); load(); }));
  $('#msg-export').addEventListener('click', () => { readFilters(); downloadUrl('/api/admin/messages/export', { status: state.status, clientId: state.clientId, profileId: state.profileId, search: state.search, from: state.from, to: state.to ? state.to + 'T23:59:59' : '' }); });
  await load();
  autoRefresh(load, 30);
};

function renderMessageTable(container, page, pagerEl, onPage) {
  if (!page.items.length) { container.innerHTML = '<div class="empty">No messages match the filter</div>'; pagerEl.innerHTML = ''; return; }
  container.innerHTML = `<table class="table table-hover"><thead><tr><th>Created</th><th>Status</th><th>Client</th><th>Subject</th><th>To</th><th>Profile</th><th class="text-end">Att.</th><th class="text-end">Time</th></tr></thead><tbody>
    ${page.items.map(m => `<tr class="clickable" data-href="#/messages/${m.id}">
      <td class="nowrap" title="${esc(fmtDate(m.createdAt, true))}">${fmtDate(m.createdAt)}</td>
      <td>${badge(m.status)}</td>
      <td class="cell-trunc sm">${esc(m.clientName || '—')}</td>
      <td class="cell-trunc">${esc(m.subject)}${m.hasAttachments ? ' 📎' : ''}${m.priority ? ' <span class="pill">high</span>' : ''}${m.lastError ? `<div class="text-danger text-muted-sm cell-trunc">${esc(m.lastError)}</div>` : ''}</td>
      <td class="cell-trunc">${esc(m.to)}${m.recipientCount > 1 ? ` <span class="pill">${m.recipientCount}</span>` : ''}</td>
      <td class="cell-trunc sm">${esc(m.senderProfile || '')}</td>
      <td class="text-end">${m.attempts}</td>
      <td class="text-end nowrap">${m.durationMs != null ? ms(m.durationMs) : '—'}</td>
    </tr>`).join('')}</tbody></table>`;
  $$('tr[data-href]', container).forEach(tr => tr.addEventListener('click', () => { location.hash = tr.dataset.href; }));
  renderPager(pagerEl, page, onPage);
}
function renderPager(el, page, onPage) {
  const total = page.totalPages || 0;
  el.innerHTML = `<span>${num(page.totalCount)} results · page ${page.page} of ${Math.max(1, total)}</span>
    <span><button class="btn btn-sm btn-outline-secondary" ${page.page <= 1 ? 'disabled' : ''} data-p="${page.page - 1}">‹ Prev</button> <button class="btn btn-sm btn-outline-secondary" ${page.page >= total ? 'disabled' : ''} data-p="${page.page + 1}">Next ›</button></span>`;
  $$('[data-p]', el).forEach(b => b.addEventListener('click', () => onPage(Number(b.dataset.p))));
}

async function renderMessageDetail(id, content) {
  setTitle('Message', `<a class="btn btn-outline-secondary btn-sm" href="#/messages">← Back</a>`);
  const r = await apiGet(`/api/admin/messages/${id}`);
  const m = r.data;
  const addr = list => (list || []).map(a => esc(a.name ? `${a.name} <${a.address}>` : a.address)).join(', ') || '—';
  const canRequeue = ['failed', 'cancelled'].includes(m.status);
  const canCancel = ['queued', 'scheduled'].includes(m.status);
  content.innerHTML = `
    <div class="row g-3">
      <div class="col-lg-8">
        <div class="card mb-3"><div class="card-header">${badge(m.status)} <span class="ms-2">${esc(m.subject)}</span><span class="spacer"></span>
          <span class="admin-only">
            ${canRequeue ? `<button class="btn btn-sm btn-primary" id="m-requeue">Re-queue</button>` : ''}
            ${canCancel ? `<button class="btn btn-sm btn-warning" id="m-cancel">Cancel</button>` : ''}
            <button class="btn btn-sm btn-outline-danger" id="m-delete">Delete</button>
          </span></div>
          <div class="card-body"><dl class="kv">
            <dt>Message id</dt><dd class="mono">${esc(m.id)} <a href="#" data-copy="${esc(m.id)}" class="copy-btn">copy</a></dd>
            <dt>Client</dt><dd>${m.clientId ? `<a href="#/clients/${m.clientId}">${esc(m.clientName)}</a>` : esc(m.clientName || '—')} ${m.apiKeyId ? `<span class="text-muted-sm">key #${m.apiKeyId}</span>` : ''}</dd>
            <dt>Sender profile</dt><dd>${esc(m.senderProfile || '—')}</dd>
            <dt>From</dt><dd>${esc(m.from)}</dd>
            <dt>To</dt><dd>${addr(m.to)}</dd>
            ${m.cc && m.cc.length ? `<dt>Cc</dt><dd>${addr(m.cc)}</dd>` : ''}
            ${m.bcc && m.bcc.length ? `<dt>Bcc</dt><dd>${addr(m.bcc)}</dd>` : ''}
            <dt>Created</dt><dd>${fmtDate(m.createdAt, true)} <span class="text-muted-sm">from ${esc(m.requestIp || '?')}</span></dd>
            ${m.scheduledAt ? `<dt>Scheduled</dt><dd>${fmtDate(m.scheduledAt, true)}</dd>` : ''}
            ${m.nextAttemptAt ? `<dt>Next attempt</dt><dd>${fmtDate(m.nextAttemptAt, true)}</dd>` : ''}
            <dt>Sent</dt><dd>${fmtDate(m.sentAt, true)} ${m.durationMs != null ? `<span class="text-muted-sm">(${ms(m.durationMs)})</span>` : ''}</dd>
            <dt>Attempts</dt><dd>${m.attempts}</dd>
            <dt>Provider id</dt><dd class="mono">${esc(m.providerMessageId || '—')}</dd>
            ${m.lastError ? `<dt>Last error</dt><dd class="text-danger">${esc(m.lastError)}</dd>` : ''}
            ${m.idempotencyKey ? `<dt>Idempotency key</dt><dd class="mono">${esc(m.idempotencyKey)}</dd>` : ''}
            ${m.tags && m.tags.length ? `<dt>Tags</dt><dd>${m.tags.map(t => `<span class="pill">${esc(t)}</span>`).join('')}</dd>` : ''}
            <dt>Size</dt><dd>${bytes(m.totalSize)} · ${m.recipientCount} recipient(s) · priority ${m.priority ? 'high' : 'normal'}</dd>
            <dt>Trace id</dt><dd class="mono">${esc(m.traceId || '—')}</dd>
          </dl></div></div>
        <div class="card mb-3"><div class="card-header">Content</div><div class="card-body">
          <ul class="nav nav-tabs tab-nav" id="body-tabs">
            ${m.htmlBody ? '<li class="nav-item"><a class="nav-link active" data-tab="html" href="#">HTML</a></li>' : ''}
            ${m.textBody ? `<li class="nav-item"><a class="nav-link ${m.htmlBody ? '' : 'active'}" data-tab="text" href="#">Text</a></li>` : ''}
            ${m.headers ? '<li class="nav-item"><a class="nav-link" data-tab="headers" href="#">Headers</a></li>' : ''}
            ${m.providerResponse ? '<li class="nav-item"><a class="nav-link" data-tab="provider" href="#">Provider response</a></li>' : ''}
          </ul>
          <div data-pane="html" class="${m.htmlBody ? '' : 'd-none'}"><iframe class="preview-frame" sandbox="" id="html-preview" title="HTML preview"></iframe></div>
          <div data-pane="text" class="${m.htmlBody || !m.textBody ? 'd-none' : ''}"><pre class="code">${esc(m.textBody || '')}</pre></div>
          <div data-pane="headers" class="d-none"><pre class="code">${esc(m.headers || '')}</pre></div>
          <div data-pane="provider" class="d-none"><pre class="code">${esc(m.providerResponse || '')}</pre></div>
        </div></div>
      </div>
      <div class="col-lg-4">
        <div class="card mb-3"><div class="card-header">Timeline</div><div class="card-body">
          <ul class="timeline">${(m.events || []).map(e => `<li class="${esc(String(e.type).toLowerCase())}"><div><strong>${esc(e.type)}</strong>${e.attempt ? ` <span class="pill">attempt ${e.attempt}</span>` : ''}${e.durationMs != null ? ` <span class="pill">${ms(e.durationMs)}</span>` : ''}</div><div class="tl-time">${fmtDate(e.timestamp, true)}</div><div class="text-muted-sm">${esc(e.details || '')}</div></li>`).join('') || '<li>No events</li>'}</ul>
        </div></div>
        <div class="card"><div class="card-header">Attachments (${(m.attachments || []).length})</div><div class="card-body p-0"><ul class="list-compact px-3">
          ${(m.attachments || []).length ? m.attachments.map(a => `<li><a href="/api/admin/messages/${esc(m.id)}/attachments/${a.id}" target="_blank" rel="noopener">${esc(a.fileName)}</a> <span class="text-muted-sm">${bytes(a.size)} · ${esc(a.contentType)}${a.isInline ? ' · inline' : ''}</span></li>`).join('') : '<li class="empty">None</li>'}
        </ul></div></div>
      </div>
    </div>`;

  if (m.htmlBody) { $('#html-preview').srcdoc = m.htmlBody; }
  $$('#body-tabs a').forEach(a => a.addEventListener('click', ev => {
    ev.preventDefault();
    $$('#body-tabs a').forEach(x => x.classList.remove('active')); a.classList.add('active');
    $$('[data-pane]', content).forEach(p => p.classList.toggle('d-none', p.dataset.pane !== a.dataset.tab));
  }));
  const act = async (sel, path, confirmMsg, danger) => {
    const b = $(sel); if (!b) return;
    b.addEventListener('click', async () => {
      if (!(await confirmDialog(confirmMsg, { danger }))) return;
      try { const r = await api(path, { method: danger ? 'DELETE' : 'POST' }); toast(r.message, 'success'); if (danger) location.hash = '#/messages'; else renderMessageDetail(id, content); }
      catch (e) { toast(e.message, 'danger'); }
    });
  };
  act('#m-requeue', `/api/admin/messages/${id}/requeue`, 'Re-queue this message for delivery?');
  act('#m-cancel', `/api/admin/messages/${id}/cancel`, 'Cancel this message?');
  act('#m-delete', `/api/admin/messages/${id}`, 'Permanently delete this message and its attachments?', true);
}

/* ======================= CLIENTS ======================= */
Routes.clients = async function (params, query, content) {
  if (params[0]) return renderClientDetail(Number(params[0]), content);
  setTitle('Clients & API keys', `<button class="btn btn-primary btn-sm admin-only" id="client-new">+ New client</button>`);
  $('#client-new').addEventListener('click', () => clientForm(null, () => Routes.clients(params, query, content)));
  const clients = await loadClients(true);
  if (!clients.length) { content.innerHTML = `<div class="card"><div class="empty">No clients yet. Create a client, assign a sender profile and generate an API key.</div></div>`; return; }
  content.innerHTML = `<div class="card"><div class="table-responsive"><table class="table table-hover"><thead><tr><th>Client</th><th>Status</th><th>Sender profile</th><th class="text-end">Keys</th><th class="text-end">Today / quota</th><th class="text-end">Rate /min</th><th>Security</th><th>Updated</th></tr></thead><tbody>
    ${clients.map(c => `<tr class="clickable" data-href="#/clients/${c.id}">
      <td><strong>${esc(c.name)}</strong><div class="text-muted-sm cell-trunc">${esc(c.description || '')}</div></td>
      <td>${c.isActive ? badge('ok', 'active') : badge('neutral', 'disabled')}</td>
      <td>${esc(c.defaultSenderProfileName || '—')}</td>
      <td class="text-end">${c.activeKeyCount}</td>
      <td class="text-end">${num(c.usedToday)} / ${c.dailyQuota ? num(c.dailyQuota) : '∞'}</td>
      <td class="text-end">${c.rateLimitPerMinute || '∞'}</td>
      <td>${c.allowedIpRanges ? '<span class="pill">IP list</span>' : ''}${c.requireSignedRequests ? '<span class="pill">signed</span>' : ''}${c.webhookUrl ? '<span class="pill">webhook</span>' : ''}${c.keyRotationEnabled ? '<span class="pill">rotation</span>' : ''}</td>
      <td class="nowrap text-muted-sm">${fmtDate(c.updatedAt)}</td></tr>`).join('')}
  </tbody></table></div></div>`;
  $$('tr[data-href]', content).forEach(tr => tr.addEventListener('click', () => { location.hash = tr.dataset.href; }));
};

async function renderClientDetail(id, content) {
  setTitle('Client', `<a class="btn btn-outline-secondary btn-sm" href="#/clients">← Back</a>`);
  const r = await apiGet(`/api/admin/clients/${id}`);
  const c = r.data;
  setTitle(c.name, `<a class="btn btn-outline-secondary btn-sm" href="#/clients">← Back</a> <button class="btn btn-primary btn-sm admin-only" id="c-edit">Edit</button> <button class="btn btn-outline-danger btn-sm admin-only" id="c-delete">Delete</button>`);
  const reload = () => renderClientDetail(id, content);
  content.innerHTML = `
    <div class="row g-3">
      <div class="col-lg-5">
        <div class="card mb-3"><div class="card-header">${c.isActive ? badge('ok', 'active') : badge('neutral', 'disabled')} <span class="ms-1">Settings</span></div><div class="card-body"><dl class="kv">
          <dt>Description</dt><dd>${esc(c.description || '—')}</dd>
          <dt>Sender profile</dt><dd>${c.defaultSenderProfileId ? `<a href="#/profiles/${c.defaultSenderProfileId}">${esc(c.defaultSenderProfileName)}</a>` : '<span class="text-danger">not assigned</span>'}</dd>
          <dt>Rate limit</dt><dd>${c.rateLimitPerMinute || 'unlimited'} req/min</dd>
          <dt>Daily quota</dt><dd>${num(c.usedToday)} used of ${c.dailyQuota ? num(c.dailyQuota) : 'unlimited'}</dd>
          <dt>Max recipients</dt><dd>${c.maxRecipientsPerMessage} per message</dd>
          <dt>Allowed source IPs</dt><dd>${esc(c.allowedIpRanges || 'any')}</dd>
          <dt>Allowed From</dt><dd>${esc(c.allowedFromAddresses || 'profile default only')}</dd>
          <dt>Recipient domains</dt><dd>${esc(c.allowedRecipientDomains || 'any')}</dd>
          <dt>Signed requests</dt><dd>${c.requireSignedRequests ? badge('ok', 'required') : badge('neutral', 'off')} ${c.hasSigningSecret ? '<span class="pill">secret set</span>' : ''}</dd>
          <dt>Webhook</dt><dd>${esc(c.webhookUrl || '—')} ${c.hasWebhookSecret ? '<span class="pill">signed</span>' : ''}</dd>
          <dt>Key rotation</dt><dd>${c.keyRotationEnabled ? `every ${c.keyRotationDays} days, ${c.keyRotationGraceDays} day grace` : 'disabled (manual rotation only)'}</dd>
          <dt>Contact</dt><dd>${esc(c.contactEmail || '—')}</dd>
          <dt>Tags</dt><dd>${esc(c.tags || '—')}</dd>
          <dt>Created</dt><dd>${fmtDate(c.createdAt)} by ${esc(c.createdBy || '?')}</dd>
        </dl>
        <div class="admin-only mt-3 d-flex gap-2 flex-wrap">
          <button class="btn btn-sm btn-outline-primary" id="c-secret">${c.hasSigningSecret ? 'Regenerate' : 'Generate'} signing secret</button>
          ${c.hasSigningSecret ? '<button class="btn btn-sm btn-outline-danger" id="c-secret-del">Remove signing secret</button>' : ''}
        </div></div></div>
        <div class="card"><div class="card-header">Integration snippet</div><div class="card-body">
          <pre class="code">curl -X POST https://&lt;relay-host&gt;/api/v1/emails \\
  -H "X-API-Key: erk_..." -H "Content-Type: application/json" \\
  -d '{"to":["user@example.com"],"subject":"Hello","text":"Hi from ${esc(c.name)}"}'</pre>
          <div class="help">Full reference: <a href="/swagger" target="_blank" rel="noopener">Swagger</a> · docs/API_GUIDE.md</div>
        </div></div>
      </div>
      <div class="col-lg-7">
        <div class="card"><div class="card-header">API keys <span class="spacer"></span><button class="btn btn-sm btn-primary admin-only" id="k-new">+ New key</button></div>
          <div class="table-responsive">${c.keys.length ? `<table class="table table-sm table-hover"><thead><tr><th>Name</th><th>Prefix</th><th>Scopes</th><th>Status</th><th>Last used</th><th class="text-end">Uses</th><th class="admin-only"></th></tr></thead><tbody>
            ${c.keys.map(k => `<tr>
              <td>${esc(k.name)}<div class="text-muted-sm">created ${fmtDate(k.createdAt)} by ${esc(k.createdBy || '?')}</div></td>
              <td class="mono">${esc(k.prefix)}…</td>
              <td>${k.scopes.split(',').map(s => `<span class="pill">${esc(s)}</span>`).join('')}</td>
              <td>${keyStatus(k)}</td>
              <td class="text-muted-sm">${k.lastUsedAt ? `${ago(k.lastUsedAt)}<br>${esc(k.lastUsedIp || '')}` : 'never'}</td>
              <td class="text-end">${num(k.useCount)}</td>
              <td class="text-end nowrap admin-only">${k.isActive ? `<button class="btn btn-sm btn-outline-secondary" data-rotate="${k.id}" title="Issue a replacement key">Rotate</button> <button class="btn btn-sm btn-outline-danger" data-revoke="${k.id}">Revoke</button>` : ''}</td>
            </tr>`).join('')}</tbody></table>` : '<div class="empty">No keys yet</div>'}</div></div>
      </div>
    </div>`;

  $('#c-edit').addEventListener('click', () => clientForm(c, reload));
  $('#c-delete').addEventListener('click', async () => {
    if (!(await confirmDialog(`Delete client '${c.name}' and all of its keys? Message history is kept.`, { danger: true, okText: 'Delete' }))) return;
    try { await apiDelete(`/api/admin/clients/${id}`); toast('Client deleted', 'success'); App.cache.clients = null; location.hash = '#/clients'; } catch (e) { toast(e.message, 'danger'); }
  });
  $('#k-new').addEventListener('click', () => keyForm(c, reload));
  $('#c-secret').addEventListener('click', async () => {
    if (!(await confirmDialog('Generate a new request-signing secret? Any existing secret stops working immediately.'))) return;
    try {
      const r2 = await apiPost(`/api/admin/clients/${id}/signing-secret`);
      showSecret('Request signing secret', r2.data.signingSecret, 'Clients sign requests with HMAC-SHA256 over "{timestamp}.{METHOD}.{path}.{sha256(body)}" and send X-Timestamp / X-Signature headers. Enable "Require signed requests" in the client settings once the client is ready.');
      reload();
    } catch (e) { toast(e.message, 'danger'); }
  });
  const del = $('#c-secret-del');
  if (del) del.addEventListener('click', async () => {
    if (!(await confirmDialog('Remove the signing secret and disable signed requests for this client?', { danger: true }))) return;
    try { await apiDelete(`/api/admin/clients/${id}/signing-secret`); toast('Signing secret removed', 'success'); reload(); } catch (e) { toast(e.message, 'danger'); }
  });
  $$('[data-revoke]', content).forEach(b => b.addEventListener('click', async () => {
    const m = modal({ title: 'Revoke key', body: `<p>The key stops working immediately. Clients using it will receive 401.</p><label class="form-label">Reason</label><input class="form-control" id="revoke-reason" placeholder="e.g. leaked, integration retired">`, footer: `<button class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button><button class="btn btn-danger" id="revoke-ok">Revoke</button>` });
    $('#revoke-ok', m.el).addEventListener('click', async () => {
      try { await apiPost(`/api/admin/clients/${id}/keys/${b.dataset.revoke}/revoke`, { reason: $('#revoke-reason', m.el).value }); m.close(); toast('Key revoked', 'success'); reload(); } catch (e) { toast(e.message, 'danger'); }
    });
  }));
  $$('[data-rotate]', content).forEach(b => b.addEventListener('click', async () => {
    if (!(await confirmDialog(`Issue a replacement key? The current key keeps working for ${c.keyRotationGraceDays || 7} day(s) so the client can switch over.`))) return;
    try {
      const r2 = await apiPost(`/api/admin/clients/${id}/keys/${b.dataset.rotate}/rotate`);
      showSecret('New API key', r2.data.plainKey, r2.message);
      reload();
    } catch (e) { toast(e.message, 'danger'); }
  }));
}

function keyStatus(k) {
  const now = Date.now();
  if (k.revokedAt) return badge('failed', 'revoked');
  if (k.expiresAt && new Date(k.expiresAt).getTime() <= now) return badge('neutral', 'expired');
  if (k.rotatedAt) return badge('warning', `rotated · expires ${fmtDate(k.expiresAt)}`);
  if (k.rotationDueAt && new Date(k.rotationDueAt).getTime() <= now) return badge('warning', 'rotation due');
  if (k.expiresAt) return badge('ok', `active · expires ${fmtDate(k.expiresAt)}`);
  return badge('ok', 'active');
}
function showSecret(title, secret, note) {
  modal({ title, size: 'lg', body: `<div class="alert alert-warning py-2">Copy this value now. It is shown only once and cannot be recovered.</div>
    <div class="secret-box"><code>${esc(secret)}</code><button class="btn btn-sm btn-light" data-copy="${esc(secret)}">Copy</button></div>
    ${note ? `<p class="help mt-3">${esc(note)}</p>` : ''}` });
}

async function clientForm(existing, onSaved) {
  const profiles = await loadProfiles();
  const c = existing || { isActive: true, rateLimitPerMinute: 60, dailyQuota: 1000, maxRecipientsPerMessage: 50, keyRotationDays: 90, keyRotationGraceDays: 7 };
  const body = `<form id="client-form" class="row g-3">
    ${field('Name', 'name', { value: c.name, required: true, placeholder: 'e.g. HR Portal' })}
    ${selectField('Default sender profile', 'defaultSenderProfileId', [{ value: '', label: '— none —' }, ...profiles.map(p => ({ value: p.id, label: `${p.name} (${p.type}${p.isActive ? '' : ', disabled'})` }))], { value: c.defaultSenderProfileId, isInt: true })}
    ${field('Description', 'description', { value: c.description, col: 'col-md-8' })}
    ${checkField('Active', 'isActive', c.isActive, { col: 'col-md-4' })}
    <div class="col-12 form-section">Limits</div>
    ${field('Rate limit (requests / minute)', 'rateLimitPerMinute', { type: 'number', value: c.rateLimitPerMinute, col: 'col-md-4', help: '0 = unlimited' })}
    ${field('Daily quota (messages)', 'dailyQuota', { type: 'number', value: c.dailyQuota, col: 'col-md-4', help: '0 = unlimited' })}
    ${field('Max recipients per message', 'maxRecipientsPerMessage', { type: 'number', value: c.maxRecipientsPerMessage, col: 'col-md-4' })}
    <div class="col-12 form-section">Caller verification</div>
    ${textareaField('Allowed source IPs / CIDR', 'allowedIpRanges', { value: c.allowedIpRanges, rows: 2, col: 'col-md-6', placeholder: '10.0.0.5, 192.168.1.0/24', help: 'Comma separated. Empty = any address.' })}
    ${textareaField('Allowed From addresses', 'allowedFromAddresses', { value: c.allowedFromAddresses, rows: 2, col: 'col-md-6', placeholder: 'noreply@company.com, @hr.company.com', help: 'Addresses or @domains the client may use as From (profile From is always allowed).' })}
    ${textareaField('Allowed recipient domains', 'allowedRecipientDomains', { value: c.allowedRecipientDomains, rows: 2, col: 'col-md-6', placeholder: 'company.com, partner.co.th', help: 'Empty = any recipient.' })}
    ${checkField('Require signed requests (HMAC)', 'requireSignedRequests', c.requireSignedRequests, { col: 'col-md-6', help: 'Generate a signing secret on the client page first.' })}
    <div class="col-12 form-section">Status webhook</div>
    ${field('Webhook URL', 'webhookUrl', { value: c.webhookUrl, col: 'col-md-8', placeholder: 'https://app.example.com/hooks/email-status', help: 'Receives JSON for sent / failed events, signed with X-Relay-Signature when a secret is set.' })}
    ${field('Webhook secret', 'webhookSecret', { type: 'password', value: '', col: 'col-md-4', placeholder: existing && existing.hasWebhookSecret ? '(unchanged)' : '', help: 'Leave empty to keep the current secret.' })}
    <div class="col-12 form-section">Key rotation (optional)</div>
    ${checkField('Enable rotation policy', 'keyRotationEnabled', c.keyRotationEnabled, { col: 'col-md-4', help: 'Off by default: keys never expire unless you set a date.' })}
    ${field('Rotate every (days)', 'keyRotationDays', { type: 'number', value: c.keyRotationDays, col: 'col-md-4' })}
    ${field('Grace period (days)', 'keyRotationGraceDays', { type: 'number', value: c.keyRotationGraceDays, col: 'col-md-4' })}
    <div class="col-12 form-section">Contact</div>
    ${field('Contact email', 'contactEmail', { value: c.contactEmail, type: 'email' })}
    ${field('Tags', 'tags', { value: c.tags, placeholder: 'finance, internal' })}
  </form>`;
  const m = modal({ title: existing ? `Edit client: ${existing.name}` : 'New client', size: 'xl', body, footer: `<button class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button><button class="btn btn-primary" id="client-save">Save</button>` });
  $('#client-save', m.el).addEventListener('click', async () => {
    const form = $('#client-form', m.el);
    if (!form.reportValidity()) return;
    const data = formData(form);
    if (!data.webhookSecret) delete data.webhookSecret;
    try {
      const r = existing ? await apiPut(`/api/admin/clients/${existing.id}`, data) : await apiPost('/api/admin/clients', data);
      App.cache.clients = null;
      m.close(); toast(r.message, 'success');
      if (!existing) location.hash = `#/clients/${r.data.id}`; else onSaved();
    } catch (e) { toast(e.message, 'danger'); }
  });
}

function keyForm(client, onSaved) {
  const body = `<form id="key-form" class="row g-3">
    ${field('Key name', 'name', { value: '', required: true, placeholder: 'e.g. production', col: 'col-md-6' })}
    ${field('Expires at (optional)', 'expiresAt', { type: 'datetime-local', value: '', col: 'col-md-6' })}
    <div class="col-12"><label class="form-label">Scopes</label>
      <div class="d-flex gap-3"><div class="form-check"><input class="form-check-input" type="checkbox" id="s-send" checked><label class="form-check-label" for="s-send">send</label></div>
      <div class="form-check"><input class="form-check-input" type="checkbox" id="s-read" checked><label class="form-check-label" for="s-read">read</label></div>
      <div class="form-check"><input class="form-check-input" type="checkbox" id="s-admin"><label class="form-check-label" for="s-admin">admin (console API)</label></div></div></div>
  </form>`;
  const m = modal({ title: `New API key for ${client.name}`, body, footer: `<button class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button><button class="btn btn-primary" id="key-save">Create key</button>` });
  $('#key-save', m.el).addEventListener('click', async () => {
    const form = $('#key-form', m.el);
    if (!form.reportValidity()) return;
    const scopes = ['send', 'read', 'admin'].filter(s => $(`#s-${s}`, m.el).checked).join(',');
    const exp = $('[name=expiresAt]', m.el).value;
    try {
      const r = await apiPost(`/api/admin/clients/${client.id}/keys`, { name: $('[name=name]', m.el).value, scopes, expiresAt: exp ? new Date(exp).toISOString() : null });
      m.close();
      showSecret('API key created', r.data.plainKey, `Send it in the X-API-Key header. Scopes: ${scopes}.`);
      onSaved();
    } catch (e) { toast(e.message, 'danger'); }
  });
}

/* ======================= SENDER PROFILES ======================= */
Routes.profiles = async function (params, query, content) {
  if (params[0]) return renderProfileDetail(Number(params[0]), content);
  setTitle('Sender profiles', `<button class="btn btn-primary btn-sm admin-only" id="p-new">+ New profile</button>`);
  $('#p-new').addEventListener('click', () => profileForm(null, () => Routes.profiles(params, query, content)));
  const profiles = await loadProfiles(true);
  if (!profiles.length) { content.innerHTML = `<div class="card"><div class="empty">No sender profiles yet. Add an SMTP server or a mail API, then assign it to clients.</div></div>`; return; }
  content.innerHTML = `<div class="row g-3">${profiles.map(p => `
    <div class="col-md-6 col-xl-4"><div class="card h-100"><div class="card-header"><span class="health-dot ${!p.isActive ? 'off' : p.consecutiveFailures >= 5 ? 'bad' : 'ok'}"></span><a href="#/profiles/${p.id}">${esc(p.name)}</a><span class="spacer"></span><span class="pill">${esc(p.type)}${p.type === 'HttpApi' ? ' · ' + esc(p.apiProvider) : ''}</span></div>
      <div class="card-body"><dl class="kv">
        <dt>From</dt><dd>${esc(p.fromName ? `${p.fromName} <${p.fromAddress}>` : p.fromAddress)}</dd>
        <dt>Endpoint</dt><dd class="cell-trunc">${esc(p.type === 'Smtp' ? `${p.smtpHost}:${p.smtpPort} (${p.smtpSecurity})` : (p.apiEndpoint || p.apiProvider))}</dd>
        <dt>Today</dt><dd>${num(p.sentToday)} sent · <span class="${p.failedToday ? 'text-danger' : ''}">${num(p.failedToday)} failed</span></dd>
        <dt>Clients</dt><dd>${p.clientCount}</dd>
        <dt>Last success</dt><dd>${p.lastSuccessAt ? ago(p.lastSuccessAt) : 'never'}</dd>
        ${p.lastError ? `<dt>Last error</dt><dd class="text-danger cell-trunc">${esc(p.lastError)}</dd>` : ''}
      </dl></div></div></div>`).join('')}</div>`;
};

async function renderProfileDetail(id, content) {
  const r = await apiGet(`/api/admin/profiles/${id}`);
  const p = r.data;
  setTitle(p.name, `<a class="btn btn-outline-secondary btn-sm" href="#/profiles">← Back</a> <button class="btn btn-outline-primary btn-sm admin-only" id="p-test">Test connection</button> <button class="btn btn-outline-primary btn-sm admin-only" id="p-send">Send test email</button> <button class="btn btn-primary btn-sm admin-only" id="p-edit">Edit</button> <button class="btn btn-outline-danger btn-sm admin-only" id="p-delete">Delete</button>`);
  const reload = () => renderProfileDetail(id, content);
  const smtp = p.type === 'Smtp';
  content.innerHTML = `<div class="row g-3">
    <div class="col-lg-6"><div class="card"><div class="card-header">${p.isActive ? badge('ok', 'active') : badge('neutral', 'disabled')} <span class="ms-1">${esc(p.type)}${smtp ? '' : ' · ' + esc(p.apiProvider)}</span></div><div class="card-body"><dl class="kv">
      <dt>Description</dt><dd>${esc(p.description || '—')}</dd>
      <dt>From</dt><dd>${esc(p.fromName ? `${p.fromName} <${p.fromAddress}>` : p.fromAddress)}</dd>
      <dt>Reply-To</dt><dd>${esc(p.replyTo || '—')}</dd>
      ${smtp ? `
      <dt>Server</dt><dd>${esc(p.smtpHost)}:${p.smtpPort}</dd>
      <dt>Security</dt><dd>${esc(p.smtpSecurity)}${p.skipCertificateValidation ? ' <span class="badge-status warning">certificate validation disabled</span>' : ''}</dd>
      <dt>Credentials</dt><dd>${p.smtpUsername ? `${esc(p.smtpUsername)} ${p.hasSmtpPassword ? '<span class="pill">password set</span>' : '<span class="text-danger">no password</span>'}` : 'anonymous'}</dd>
      <dt>Local domain</dt><dd>${esc(p.smtpLocalDomain || '—')}</dd>` : `
      <dt>Endpoint</dt><dd>${esc(p.apiEndpoint || '(provider default)')}</dd>
      <dt>API key</dt><dd>${p.hasApiKey ? '<span class="pill">set</span>' : '<span class="text-muted">not set</span>'}</dd>
      ${p.apiDomain ? `<dt>Sending domain</dt><dd>${esc(p.apiDomain)}</dd>` : ''}
      ${p.oAuthTenantId ? `<dt>OAuth</dt><dd>tenant ${esc(p.oAuthTenantId)} · client ${esc(p.oAuthClientId)} ${p.hasOAuthClientSecret ? '<span class="pill">secret set</span>' : ''}</dd>` : ''}
      ${p.extraHeadersJson ? `<dt>Extra headers</dt><dd class="mono">${esc(p.extraHeadersJson)}</dd>` : ''}`}
      <dt>Timeout</dt><dd>${p.timeoutSeconds}s</dd>
      <dt>Concurrency</dt><dd>${p.maxConcurrency} parallel · ${p.maxPerMinute ? p.maxPerMinute + ' msg/min' : 'no throttle'}</dd>
      <dt>Used by</dt><dd>${p.clientCount} client(s)</dd>
    </dl></div></div></div>
    <div class="col-lg-6"><div class="card"><div class="card-header">Health</div><div class="card-body"><dl class="kv">
      <dt>Status</dt><dd>${!p.isActive ? badge('neutral', 'disabled') : p.consecutiveFailures >= 5 ? badge('failed', 'unhealthy') : badge('ok', 'healthy')}</dd>
      <dt>Consecutive failures</dt><dd>${p.consecutiveFailures}</dd>
      <dt>Last success</dt><dd>${fmtDate(p.lastSuccessAt, true)}</dd>
      <dt>Last failure</dt><dd>${fmtDate(p.lastFailureAt, true)}</dd>
      <dt>Last error</dt><dd class="text-danger">${esc(p.lastError || '—')}</dd>
      <dt>Updated</dt><dd>${fmtDate(p.updatedAt)}</dd>
    </dl><div id="p-result" class="mt-3"></div></div></div></div>
  </div>`;

  $('#p-edit').addEventListener('click', () => profileForm(p, reload));
  $('#p-delete').addEventListener('click', async () => {
    if (!(await confirmDialog(`Delete profile '${p.name}'?`, { danger: true, okText: 'Delete' }))) return;
    try { await apiDelete(`/api/admin/profiles/${id}`); App.cache.profiles = null; toast('Profile deleted', 'success'); location.hash = '#/profiles'; } catch (e) { toast(e.message, 'danger'); }
  });
  const showResult = (r2, ok) => { $('#p-result').innerHTML = `<div class="alert alert-${ok ? 'success' : 'danger'} mb-0"><strong>${esc(r2.message)}</strong>${r2.data && (r2.data.response || r2.data.error) ? `<pre class="code mt-2 mb-0">${esc(r2.data.error || '')}\n${esc(r2.data.response || '')}</pre>` : ''}</div>`; };
  $('#p-test').addEventListener('click', async () => {
    $('#p-result').innerHTML = '<div class="loading">Testing…</div>';
    try { const r2 = await apiPost(`/api/admin/profiles/${id}/test`); showResult(r2, true); } catch (e) { showResult({ message: e.message, data: e.data && e.data.data }, false); }
  });
  $('#p-send').addEventListener('click', () => {
    const m = modal({ title: 'Send test email', body: `<label class="form-label">Recipient</label><input class="form-control" id="test-to" type="email" placeholder="you@company.com">`, footer: `<button class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button><button class="btn btn-primary" id="test-ok">Send</button>` });
    $('#test-ok', m.el).addEventListener('click', async () => {
      const to = $('#test-to', m.el).value.trim(); if (!to) return;
      m.close(); $('#p-result').innerHTML = '<div class="loading">Sending…</div>';
      try { const r2 = await apiPost(`/api/admin/profiles/${id}/test-send`, { to }); showResult(r2, true); reload(); } catch (e) { showResult({ message: e.message, data: e.data && e.data.data }, false); }
    });
  });
}

function profileForm(existing, onSaved) {
  const p = existing || { type: 'Smtp', isActive: true, smtpPort: 587, smtpSecurity: 'Auto', apiProvider: 'Generic', timeoutSeconds: 30, maxConcurrency: 2, maxPerMinute: 0 };
  const body = `<form id="profile-form" class="row g-3">
    ${field('Name', 'name', { value: p.name, required: true, col: 'col-md-4', placeholder: 'e.g. Exchange-Internal' })}
    ${selectField('Transport', 'type', [{ value: 'Smtp', label: 'SMTP server' }, { value: 'HttpApi', label: 'HTTP mail API' }], { value: p.type, col: 'col-md-4' })}
    ${checkField('Active', 'isActive', p.isActive, { col: 'col-md-4' })}
    ${field('Description', 'description', { value: p.description, col: 'col-12' })}
    <div class="col-12 form-section">Sender identity</div>
    ${field('From address', 'fromAddress', { value: p.fromAddress, required: true, type: 'email', col: 'col-md-4' })}
    ${field('From name', 'fromName', { value: p.fromName, col: 'col-md-4' })}
    ${field('Reply-To', 'replyTo', { value: p.replyTo, type: 'email', col: 'col-md-4' })}
    <div data-section="Smtp">
      <div class="row g-3">
        <div class="col-12 form-section">SMTP</div>
        ${field('Host', 'smtpHost', { value: p.smtpHost, col: 'col-md-5', placeholder: 'smtp.company.com' })}
        ${field('Port', 'smtpPort', { type: 'number', value: p.smtpPort, col: 'col-md-2' })}
        ${selectField('Security', 'smtpSecurity', ['Auto', 'None', 'StartTls', 'StartTlsWhenAvailable', 'SslOnConnect'].map(v => ({ value: v, label: v })), { value: p.smtpSecurity, col: 'col-md-5', help: 'Auto picks TLS by port (465 = SSL, 587/25 = STARTTLS when offered).' })}
        ${field('Username', 'smtpUsername', { value: p.smtpUsername, col: 'col-md-4', attrs: 'autocomplete="off"' })}
        ${field('Password', 'smtpPassword', { type: 'password', value: '', col: 'col-md-4', placeholder: existing && existing.hasSmtpPassword ? '(unchanged)' : '', help: existing ? 'Leave empty to keep the stored password.' : '', attrs: 'autocomplete="new-password"' })}
        ${field('Local domain (EHLO)', 'smtpLocalDomain', { value: p.smtpLocalDomain, col: 'col-md-4', placeholder: 'relay.company.com' })}
        ${checkField('Skip certificate validation (not recommended)', 'skipCertificateValidation', p.skipCertificateValidation, { col: 'col-12' })}
      </div>
    </div>
    <div data-section="HttpApi">
      <div class="row g-3">
        <div class="col-12 form-section">HTTP API</div>
        ${selectField('Provider', 'apiProvider', [{ value: 'SendGrid', label: 'SendGrid' }, { value: 'Mailgun', label: 'Mailgun' }, { value: 'MicrosoftGraph', label: 'Microsoft Graph (Microsoft 365)' }, { value: 'Generic', label: 'Generic JSON endpoint' }], { value: p.apiProvider, col: 'col-md-4' })}
        ${field('Endpoint URL', 'apiEndpoint', { value: p.apiEndpoint, col: 'col-md-8', placeholder: 'leave empty for the provider default', help: 'SendGrid: https://api.sendgrid.com/v3/mail/send · Mailgun base: https://api.mailgun.net/v3 (EU: api.eu.mailgun.net) · Graph: https://graph.microsoft.com/v1.0' })}
        ${field('API key / token', 'apiKey', { type: 'password', value: '', col: 'col-md-6', placeholder: existing && existing.hasApiKey ? '(unchanged)' : '', help: 'Bearer token for SendGrid / Generic, private API key for Mailgun.', attrs: 'autocomplete="new-password"' })}
        ${field('Mailgun sending domain', 'apiDomain', { value: p.apiDomain, col: 'col-md-6', placeholder: 'mg.company.com' })}
        ${field('OAuth tenant id', 'oAuthTenantId', { value: p.oAuthTenantId, col: 'col-md-4', help: 'Microsoft Graph only' })}
        ${field('OAuth client id', 'oAuthClientId', { value: p.oAuthClientId, col: 'col-md-4' })}
        ${field('OAuth client secret', 'oAuthClientSecret', { type: 'password', value: '', col: 'col-md-4', placeholder: existing && existing.hasOAuthClientSecret ? '(unchanged)' : '', attrs: 'autocomplete="new-password"' })}
        ${textareaField('Extra headers (JSON object)', 'extraHeadersJson', { value: p.extraHeadersJson, rows: 2, mono: true, placeholder: '{"X-Tenant":"abc"}', help: 'Generic provider only.' })}
      </div>
    </div>
    <div class="col-12 form-section">Delivery</div>
    ${field('Timeout (seconds)', 'timeoutSeconds', { type: 'number', value: p.timeoutSeconds, col: 'col-md-4' })}
    ${field('Max parallel sends', 'maxConcurrency', { type: 'number', value: p.maxConcurrency, col: 'col-md-4' })}
    ${field('Max messages / minute', 'maxPerMinute', { type: 'number', value: p.maxPerMinute, col: 'col-md-4', help: '0 = no throttle' })}
  </form>`;
  const m = modal({ title: existing ? `Edit profile: ${existing.name}` : 'New sender profile', size: 'xl', body, footer: `<button class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button><button class="btn btn-primary" id="profile-save">Save</button>` });
  const form = $('#profile-form', m.el);
  const typeSel = $('[name=type]', form);
  const sync = () => $$('[data-section]', form).forEach(s => s.classList.toggle('d-none', s.dataset.section !== typeSel.value));
  typeSel.addEventListener('change', sync); sync();
  $('#profile-save', m.el).addEventListener('click', async () => {
    if (!form.reportValidity()) return;
    const data = formData(form);
    ['smtpPassword', 'apiKey', 'oAuthClientSecret'].forEach(k => { if (data[k] === null) delete data[k]; });
    try {
      const r = existing ? await apiPut(`/api/admin/profiles/${existing.id}`, data) : await apiPost('/api/admin/profiles', data);
      App.cache.profiles = null;
      m.close(); toast(r.message, 'success');
      if (!existing) location.hash = `#/profiles/${r.data.id}`; else onSaved();
    } catch (e) { toast(e.message, 'danger'); }
  });
}
