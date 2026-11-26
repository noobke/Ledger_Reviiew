const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));

const state = {
  batches: [], batch: null, pages: [], findings: [], persons: [], types: {},
  filter: { type: 'ALL', problem: false }, page: null, pageIndex: -1, zoom: 1, poll: null
};

async function api(path, opts) {
  const r = await fetch(path, opts);
  if (!r.ok) {
    let msg = r.status + ' ' + r.statusText;
    try { const j = await r.json(); msg = j.error || j.title || msg; } catch {}
    throw new Error(msg);
  }
  const ct = r.headers.get('content-type') || '';
  return ct.includes('application/json') ? r.json() : r.text();
}

let toastTimer;
function toast(msg, isErr = false) {
  const t = $('#toast');
  t.textContent = msg;
  t.className = 'toast' + (isErr ? ' err' : '');
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.hidden = true, 4200);
}

const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const fmtTime = s => (s || '').replace('T', ' ').slice(0, 16);
const typeName = code => state.types[code]?.name || (code && code !== 'Unknown' ? code : '未识别');
const statusText = s => ({ pending: '待处理', processing: '处理中', completed: '已完成', failed: '失败' }[s] || s);

async function loadStatus() {
  try {
    const s = await api('/api/status');
    $('#statusOcr').textContent = s.ocr;
    $('#statusSig').textContent = s.signature;
    $('#statusWs').textContent = '工作区：' + s.workspace;
    const dots = $$('#sideStatus .dot');
    dots[0].className = 'dot ' + (s.ocrAvailable ? 'ok' : 'bad');
    dots[1].className = 'dot ' + (s.signatureReady ? 'ok' : 'bad');
    window.__status = s;
  } catch (e) {
    $('#statusOcr').textContent = '服务未连接';
  }
}

async function loadTypes() {
  try {
    const list = await api('/api/dict/types');
    list.forEach(t => state.types[t.type] = t);
  } catch {}
}

async function loadBatches() {
  try {
    state.batches = await api('/api/batches');
    renderBatches();
  } catch (e) { toast('读取批次失败：' + e.message, true); }
}

function renderBatches() {
  const box = $('#batchList');
  if (!state.batches.length) {
    box.innerHTML = '<div class="empty">还没有批次。导入一册 PDF 开始。</div>';
    return;
  }
  box.innerHTML = state.batches.map(b => {
    const pct = b.totalPages ? Math.round(b.processedPages * 100 / b.totalPages) : 0;
    return `<div class="batch-row" data-id="${b.id}">
      <div>
        <div class="batch-name">${esc(b.name)}</div>
        <div class="batch-sub">${esc(b.station || '未填写站点')} · ${fmtTime(b.createdAt)}</div>
        ${b.status === 'processing' ? `<div class="progress"><i style="width:${pct}%"></i></div>` : ''}
      </div>
      <div class="batch-cell"><b>${b.processedPages}/${b.totalPages}</b>页</div>
      <div class="batch-cell"><span class="pill ${b.status}">${statusText(b.status)}</span></div>
      <div class="batch-cell"><b style="color:${b.problemCount ? 'var(--red)' : 'var(--ink)'}">${b.problemCount}</b>严重问题</div>
      <div class="batch-cell">${pct}%</div>
    </div>`;
  }).join('');
  $$('.batch-row', box).forEach(r => r.onclick = () => openBatch(r.dataset.id));
}

function showView(name) {
  $$('.view').forEach(v => v.hidden = true);
  $('#view-' + name).hidden = false;
  $$('.nav-item').forEach(n => n.classList.toggle('active', n.dataset.view === name));
  if (name === 'home') {
    $('#crumb').textContent = '批次';
    $('#topActions').innerHTML = '';
    stopPoll();
  }
}

async function openBatch(id) {
  stopPoll();
  showView('batch');
  try {
    const d = await api('/api/batches/' + id);
    state.batch = d.batch; state.pages = d.pages; state.findings = d.findings;
    renderBatch(d);
    $('#crumb').textContent = '批次 / ' + d.batch.name;
    if (d.batch.status === 'pending' || d.batch.status === 'processing') startPoll(id);
  } catch (e) { toast('打开批次失败：' + e.message, true); }
}

function renderBatch(d) {
  const b = d.batch;
  $('#bTitle').textContent = b.name;
  const tail = b.status === 'pending'
    ? '排队中（前面还有批次在处理，完成后自动开始）'
    : b.status === 'processing'
      ? `处理中 ${d.stats.done}/${d.stats.total}`
      : statusText(b.status);
  $('#bMeta').textContent = `${b.station || '未填写站点'} · 导入于 ${fmtTime(b.createdAt)} · ${tail}`;
  $('#bStats').innerHTML = `
    <div class="stat"><div class="k">总页数</div><div class="v">${d.stats.total}</div></div>
    <div class="stat"><div class="k">已处理</div><div class="v green">${d.stats.done}</div></div>
    <div class="stat"><div class="k">未识别</div><div class="v amber">${d.stats.unknown}</div></div>
    <div class="stat"><div class="k">严重问题</div><div class="v red">${d.stats.serious}</div></div>
    <div class="stat"><div class="k">一般问题</div><div class="v amber">${d.stats.warning}</div></div>
    <div class="stat"><div class="k">待处理问题</div><div class="v">${d.stats.open}</div></div>`;

  const sel = $('#filterType');
  const cur = state.filter.type;
  sel.innerHTML = '<option value="ALL">全部类型</option>' +
    d.byType.map(t => `<option value="${t.type}">${esc(t.name)}（${t.count}）</option>`).join('');
  sel.value = cur;
  if (sel.value !== cur) { sel.value = 'ALL'; state.filter.type = 'ALL'; }

  renderPages();
  renderFindings();
}

function pageCard(p) {
  const thumb = p.thumbPath ? `/api/pages/${p.id}/thumb` : '';
  const badge = p.seriousFindings ? `<div class="badge red">${p.seriousFindings}</div>`
    : (p.openFindings ? `<div class="badge amber">${p.openFindings}</div>` : '');
  const st = p.status || 'pending';
  const done = st === 'done';
  const meta = [p.docDate, p.equipmentId].filter(Boolean).join(' · ');
  const typeLabel = done ? typeName(p.docType)
    : st === 'failed' ? '处理失败'
      : st === 'pending' ? '排队中' : '处理中';
  const metaLabel = done ? (meta || '未识别日期/设备')
    : st === 'failed' ? (p.error || '处理失败，请重试')
      : '等待审查';
  const phText = st === 'pending' ? '排队中' : st === 'failed' ? '失败' : '无图';
  return `<div class="page-card" data-id="${p.id}">
    ${badge}
    <div class="page-thumb">${thumb ? `<img loading="lazy" src="${thumb}" alt="">` : `<span style="color:#94a3b8;font-size:12px">${phText}</span>`}</div>
    <div class="page-info">
      <div class="page-no">第 ${p.pageNo} 页</div>
      <div class="page-type">${esc(typeLabel)}</div>
      <div class="page-meta">${esc(metaLabel)}</div>
    </div>
  </div>`;
}

function familyOf(docType) {
  if (!docType) return '其他/监督';
  if (docType.startsWith('Chuiti')) return '垂梯';
  if (docType.startsWith('Futi')) return '扶梯';
  return '其他/监督';
}

function renderPages() {
  const grid = $('#pageGrid');
  let list = state.pages;
  if (state.filter.type !== 'ALL') list = list.filter(p => p.docType === state.filter.type);
  if (state.filter.problem) list = list.filter(p => p.openFindings > 0);
  if (!list.length) {
    grid.innerHTML = '<div class="empty">没有符合条件的页。</div>';
    return;
  }

  const groups = new Map();
  list.forEach(p => {
    const station = p.station || '未识别站点';
    const family = familyOf(p.docType);
    const equip = p.equipmentId || '未识别设备';
    // 设备号没识别出来时按源 PDF 分开，避免多台梯的“未识别设备”被合并成一组
    const key = `${station}|${family}|${p.equipmentId || 'file:' + (p.sourceFileId || '')}`;
    if (!groups.has(key)) groups.set(key, { station, family, equip, items: [] });
    groups.get(key).items.push(p);
  });

  const familyOrder = { '垂梯': 0, '扶梯': 1, '其他/监督': 2 };
  const keys = [...groups.keys()].sort((a, b) => {
    const ga = groups.get(a), gb = groups.get(b);
    if (ga.station !== gb.station) return ga.station.localeCompare(gb.station, 'zh');
    if (ga.family !== gb.family) return (familyOrder[ga.family] ?? 9) - (familyOrder[gb.family] ?? 9);
    return ga.equip.localeCompare(gb.equip, 'zh');
  });

  grid.innerHTML = keys.map(k => {
    const g = groups.get(k);
    return `<div class="page-group">
      <div class="group-head"><b>${esc(g.station)}</b><span>${esc(g.family)}</span><span>${esc(g.equip)}</span><em>${g.items.length} 页</em></div>
      <div class="page-grid">${g.items.map(pageCard).join('')}</div>
    </div>`;
  }).join('');

  const flat = keys.flatMap(k => groups.get(k).items);
  $$('.page-card', grid).forEach((c, i) => {
    c.onclick = () => openPage(c.dataset.id, flat[i]?.pageNo);
  });
}

function renderFindings() {
  const box = $('#findingList');
  const list = state.findings.filter(f => f.status === 'open');
  $('#findingHint').textContent = `共 ${list.length} 条待处理；点击查看原页`;
  if (!list.length) { box.innerHTML = '<div class="empty">没有待处理问题。</div>'; return; }
  box.innerHTML = list.map(f => `<div class="finding" data-id="${f.id}" data-page="${f.pageId}">
    <div class="f-sev ${f.severity}"></div>
    <div class="f-page">第 ${f.pageNo || '?'} 页</div>
    <div class="f-msg">${esc(f.message)}</div>
    <div class="f-rule">${esc(f.ruleCode)} · ${esc(typeName(f.docType))}</div>
    <div class="f-actions">
      <button class="btn tiny" data-act="confirm">确认</button>
      <button class="btn tiny" data-act="false">误报</button>
    </div>
  </div>`).join('');
  $$('.finding', box).forEach(row => {
    row.onclick = e => {
      if (e.target.closest('button')) return;
      if (row.dataset.page) openPage(row.dataset.page);
    };
    $$('button', row).forEach(btn => btn.onclick = async e => {
      e.stopPropagation();
      const status = btn.dataset.act === 'confirm' ? 'confirmed' : 'false_positive';
      try {
        await api(`/api/findings/${row.dataset.id}/status`, {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ status })
        });
        row.style.opacity = '.4';
        setTimeout(() => { row.style.display = 'none'; }, 200);
        toast(status === 'confirmed' ? '已确认' : '已标为误报');
      } catch (err) { toast('操作失败：' + err.message, true); }
    });
  });
}

function startPoll(id) {
  stopPoll();
  state.poll = setInterval(async () => {
    try {
      const d = await api('/api/batches/' + id);
      state.batch = d.batch; state.pages = d.pages; state.findings = d.findings;
      renderBatch(d);
      if (d.batch.status === 'completed' || d.batch.status === 'failed') stopPoll();
    } catch {}
  }, 1600);
}
function stopPoll() { if (state.poll) { clearInterval(state.poll); state.poll = null; } }

/* ---------- drawer ---------- */
async function openPage(id, knownNo) {
  try {
    const d = await api('/api/pages/' + id);
    state.page = d;
    state.zoom = 1;
    const img = $('#pageImage');
    img.src = `/api/pages/${id}/image?t=${Date.now()}`;
    img.onload = () => { layoutImage(); drawOverlay(); };

    const idx = state.pages.findIndex(p => p.id === id);
    state.pageIndex = idx;

    $('#dTitle').textContent = `${typeName(d.docType)} · 第 ${d.pageNo} 页`;
    $('#dSub').textContent = [
      d.docDate, d.equipmentId, d.station,
      d.typeConfidence ? '类型置信度 ' + Number(d.typeConfidence).toFixed(2) : ''
    ].filter(Boolean).join(' · ') || '未识别关键字段';

    renderDetail(d);
    $('#drawer').hidden = false;
    document.body.style.overflow = 'hidden';
  } catch (e) { toast('打开页失败：' + e.message, true); }
}

function layoutImage() {
  const img = $('#pageImage');
  const wrap = $('#imageWrap');
  const base = Math.max(320, wrap.clientWidth - 36);
  img.style.width = Math.round(base * state.zoom) + 'px';
  img.style.height = 'auto';
  $('#zoomLabel').textContent = Math.round(state.zoom * 100) + '%';
}

function drawOverlay() {
  const img = $('#pageImage');
  const ov = $('#overlay');
  const rect = img.getBoundingClientRect();
  const wrapRect = $('#imageWrap').getBoundingClientRect();
  ov.style.left = (rect.left - wrapRect.left) + 'px';
  ov.style.top = (rect.top - wrapRect.top) + 'px';
  ov.style.width = rect.width + 'px';
  ov.style.height = rect.height + 'px';
  const d = state.page;
  if (!d) { ov.innerHTML = ''; return; }
  ov.innerHTML = (d.findings || []).filter(f => f.w > 0.002 && f.h > 0.002).map(f =>
    `<div class="bbox ${f.severity}" style="left:${f.x * 100}%;top:${f.y * 100}%;width:${f.w * 100}%;height:${f.h * 100}%"></div>`
  ).join('');
}

function renderDetail(d) {
  // findings
  const fs = (d.findings || []).filter(f => f.status === 'open');
  $('#dFindings').innerHTML = `<h3>问题（${fs.length}）</h3>` + (fs.length ? fs.map(f =>
    `<div class="find-mini ${f.severity}"><b>${esc(f.ruleCode)}</b> · ${esc(f.message)}</div>`
  ).join('') : '<div class="tiny">本页没有待处理问题。</div>');

  // fields
  const map = { station: '站点', equipment: '设备编号', date: '日期', seq: '第 N 次', runCount: '运行次数' };
  const fields = (d.fields || []).filter(f => f.raw);
  $('#dFields').innerHTML = `<h3>关键字段</h3><div class="kv">` +
    (fields.length ? fields.map(f => `<div class="k">${map[f.code] || f.code}</div><div>${esc(f.raw)}</div>`).join('')
      : '<div class="k">—</div><div>未识别（手写字段可能无法识别，请以原图为准）</div>') + `</div>
    <div class="seq-fix"><span>第 N 次</span>
      <input id="seqFixInput" type="number" min="1" max="300" value="${d.seqNo || ''}" placeholder="人工填写">
      <button class="btn tiny" id="seqFixSave">保存并入库</button>
      <span class="tiny">保存后该数字切片会进入样本库，后续识别越用越准</span>
    </div>`;
  const seqBtn = $('#seqFixSave');
  if (seqBtn) seqBtn.onclick = async () => {
    const v = Number($('#seqFixInput').value);
    if (!v || v < 1) return toast('请输入正确的次数', true);
    try {
      const r = await api(`/api/pages/${d.id}/seq`, { method: 'POST', body: JSON.stringify({ value: v }) });
      toast(`已保存，样本入库 ${r.learned} 张`);
    } catch (e) { toast('保存失败：' + e.message, true); }
  };

  // checks
  const checks = d.checks || [];
  const blanks = checks.filter(c => c.result === 'BLANK').length;
  const nas = checks.filter(c => c.result === 'NA').length;
  $('#dChecks').innerHTML = checks.length ? `<h3>检查项（空白 ${blanks} / N/A ${nas} / 共 ${checks.length}）</h3>
    <div class="checks">${checks.map(c => `<div class="chk ${c.result}" title="墨迹 ${(c.ink * 100).toFixed(1)}%">${c.itemNo}${c.result === 'BLANK' ? '空' : c.result === 'NA' ? 'N/A' : '✓'}</div>`).join('')}</div>` : '';

  // signatures
  const sigs = d.signatures || [];
  $('#dSignatures').innerHTML = sigs.length ? `<h3>签名</h3>` + sigs.map(s => {
    const role = { MAINTAINER: '保养人员', CUSTOMER: '客户/使用单位', SUPERVISOR: '安全管理人员' }[s.role] || s.code;
    if (!s.hasInk) return `<div class="sig-card"><div class="sig-head"><div class="sig-role">${role}</div><div class="sig-state NOT_IN_ROSTER">未签</div></div></div>`;
    if (s.role !== 'MAINTAINER') return `<div class="sig-card"><div class="sig-head"><div class="sig-role">${role}</div><div class="sig-state MATCH">已签（只校存在）</div></div></div>`;
    const matches = s.matches || [];
    if (!matches.length) return `<div class="sig-card"><div class="sig-head"><div class="sig-role">${role}</div><div class="sig-state UNDETERMINED">已签（未做归属比对）</div></div></div>`;
    return `<div class="sig-card"><div class="sig-head"><div class="sig-role">${role}</div>
      <div class="sig-state ${matches[0].verdict}">${verdictText(matches[0].verdict)}</div></div>` +
      matches.map(m => {
        let cands = [];
        try { cands = JSON.parse(m.candidates || '[]').filter(c => Number.isFinite(c.score)); } catch {}
        const crop = `/api/matches/${m.id}/crop`;
        const sure = m.verdict === 'MATCH' || m.confirmed === true;
        const pick = sure && m.personId ? m.personId : '';
        const personOpts = state.persons.map(p => `<option value="${p.id}" ${p.id === pick ? 'selected' : ''}>${esc(p.name)}</option>`).join('');
        const nameLine = sure
          ? `<b>${esc(m.personName || '—')}</b> <span class="tiny">${Number.isFinite(m.score) ? (m.score * 100).toFixed(0) + '分' : '—'}</span>`
          : '<b>未确认</b> <span class="tiny">字迹比对置信不足，请对照原图人工选择</span>';
        return `<div class="sig-body">
          <img class="sig-crop" src="${crop}" alt="">
          <div>
            <div>${nameLine}</div>
            <div class="cands">${sure ? '候选' : '最接近（仅供参考）'}：${cands.map(c => `${esc(c.personName)} ${(c.score * 100).toFixed(0)}`).join(' / ') || '无候选'}</div>
            <div class="sig-actions">
              <select data-match="${m.id}"><option value="">选择人员…</option>${personOpts}</select>
              <button class="btn tiny" data-confirm="${m.id}">确认此人</button>
              <button class="btn tiny" data-not="${m.id}">不是本人</button>
            </div>
          </div>
        </div>`;
      }).join('') + `</div>`;
  }).join('') : '';

  bindSignatureActions();

  $('#dOcr').textContent = d.ocrText || '（无 OCR 文本）';
}

function verdictText(v) {
  return { MATCH: '是某人', NOT_IN_ROSTER: '不是在册人员', UNDETERMINED: '无法判断' }[v] || v;
}

function bindSignatureActions() {
  $$('[data-confirm]').forEach(btn => btn.onclick = async () => {
    const id = btn.dataset.confirm;
    const sel = $(`select[data-match="${id}"]`);
    if (!sel || !sel.value) return toast('请先选择人员', true);
    try {
      await api(`/api/matches/${id}/confirm`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ personId: sel.value, verdict: 'MATCH' })
      });
      toast('已确认为该人员');
      openPage(state.page.id);
    } catch (e) { toast('确认失败：' + e.message, true); }
  });
  $$('[data-not]').forEach(btn => btn.onclick = async () => {
    const id = btn.dataset.not;
    try {
      await api(`/api/matches/${id}/confirm`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ personId: null, verdict: 'NOT_IN_ROSTER' })
      });
      toast('已标为不是本人');
      openPage(state.page.id);
    } catch (e) { toast('操作失败：' + e.message, true); }
  });
}

function closeDrawer() {
  $('#drawer').hidden = true;
  document.body.style.overflow = '';
  state.page = null;
}

/* ---------- import ---------- */
let picked = [];
function setupImport() {
  const dz = $('#dropzone'), input = $('#fileInput');
  dz.onclick = () => input.click();
  input.onchange = () => { picked = Array.from(input.files); renderPicked(); };
  ['dragenter', 'dragover'].forEach(ev => dz.addEventListener(ev, e => { e.preventDefault(); dz.classList.add('drag'); }));
  ['dragleave', 'drop'].forEach(ev => dz.addEventListener(ev, e => { e.preventDefault(); dz.classList.remove('drag'); }));
  dz.addEventListener('drop', e => {
    const files = Array.from(e.dataTransfer.files).filter(f => f.name.toLowerCase().endsWith('.pdf'));
    if (files.length) { picked = files; renderPicked(); }
  });
}
function renderPicked() {
  $('#pickedFiles').innerHTML = picked.length
    ? `已选择 ${picked.length} 个文件：` + picked.map(f => esc(f.name)).join('、')
    : '';
}

async function doImport() {
  if (!picked.length) return toast('请先选择 PDF 文件', true);
  const btn = $('#btnImport');
  btn.disabled = true; btn.textContent = '正在上传…';
  try {
    const fd = new FormData();
    fd.append('name', $('#batchName').value.trim());
    fd.append('station', $('#batchStation').value.trim());
    fd.append('equipment', $('#batchEquipment').value.trim());
    picked.forEach(f => fd.append('files', f, f.name));
    const r = await api('/api/batches', { method: 'POST', body: fd });
    toast(`已导入 ${r.files} 个文件、${r.pages} 页，开始审查`);
    picked = []; renderPicked(); $('#fileInput').value = '';
    $('#batchName').value = ''; $('#batchStation').value = ''; $('#batchEquipment').value = '';
    await loadBatches();
    openBatch(r.batchId);
  } catch (e) { toast('导入失败：' + e.message, true); }
  finally { btn.disabled = false; btn.textContent = '开始导入并审查'; }
}

/* ---------- export ---------- */
async function doExport() {
  if (!state.batch) return;
  try {
    const d = await api(`/api/batches/${state.batch.id}/export/default`);
    const dir = prompt('导出到哪个目录？（可修改）', d.dir);
    if (!dir) return;
    toast('正在导出…');
    const r = await api(`/api/batches/${state.batch.id}/export`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ outputDir: dir })
    });
    toast(`导出完成：成功 ${r.exported} 份，跳过 ${r.skipped} 份，失败 ${r.failed} 份\n${r.outputDir}${r.errors.length ? '\n' + r.errors.join('\n') : ''}`);
  } catch (e) { toast('导出失败：' + e.message, true); }
}

async function reprocess(all = false) {
  if (!state.batch) return;
  if (all && !confirm('将清空本批全部结果并按当前规则重新识别，继续？')) return;
  try {
    await api(`/api/batches/${state.batch.id}/reprocess${all ? '?all=true' : ''}`, { method: 'POST' });
    toast(all ? '已重置全部页并加入队列' : '已加入队列');
    startPoll(state.batch.id);
  } catch (e) { toast('操作失败：' + e.message, true); }
}

/* ---------- init ---------- */
function bind() {
  $$('.nav-item').forEach(n => n.onclick = () => {
    if (n.dataset.view === 'home') { showView('home'); loadBatches(); }
    else showView(n.dataset.view);
  });
  $('#btnRefresh').onclick = loadBatches;
  $('#btnImport').onclick = doImport;
  $('#btnBack').onclick = () => { stopPoll(); showView('home'); loadBatches(); };
  $('#btnExport').onclick = doExport;
  $('#btnReprocess').onclick = () => reprocess(false);
  $('#btnReprocessAll').onclick = () => reprocess(true);
  $('#btnCloseDrawer').onclick = closeDrawer;
  $('#drawerBackdrop').onclick = closeDrawer;
  $('#filterType').onchange = e => { state.filter.type = e.target.value; renderPages(); };
  $('#filterProblem').onchange = e => { state.filter.problem = e.target.checked; renderPages(); };
  $('#btnZoomIn').onclick = () => { state.zoom = Math.min(3, state.zoom + .2); layoutImage(); drawOverlay(); };
  $('#btnZoomOut').onclick = () => { state.zoom = Math.max(.5, state.zoom - .2); layoutImage(); drawOverlay(); };
  $('#btnPrevPage').onclick = () => navPage(-1);
  $('#btnNextPage').onclick = () => navPage(1);
  window.addEventListener('resize', () => { if (state.page) { layoutImage(); drawOverlay(); } });
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape') closeDrawer();
    if (!state.page) return;
    if (e.key === 'j' || e.key === 'J') navPage(1);
    if (e.key === 'k' || e.key === 'K') navPage(-1);
  });
  const wrap = $('#imageWrap');
  wrap.addEventListener('scroll', () => { if (state.page) drawOverlay(); });
}

function navPage(delta) {
  if (!state.pages.length) return;
  let idx = state.pageIndex + delta;
  if (idx < 0 || idx >= state.pages.length) return;
  state.pageIndex = idx;
  openPage(state.pages[idx].id);
}

(async function init() {
  bind();
  setupImport();
  await loadTypes();
  await loadStatus();
  await loadPersons();
  await loadBatches();
})();

async function loadPersons() {
  try { state.persons = await api('/api/persons'); } catch {}
}


