// 管理控制台前端脚本：SSR 页面 + 少量 fetch 交互（对应 .NET 版的 code-behind）
(function () {
  'use strict';

  var TOKEN_KEY = 'durlinks_token';
  function token() { return localStorage.getItem(TOKEN_KEY) || ''; }

  // ---------- 基础 ----------
  window.go = function (href) { location.href = href; };
  window.logout = function () {
    try { localStorage.clear(); } catch (e) {}
    location.href = '/console/logout';
  };

  var flashTimer = null;
  window.flash = function (msg, isErr) {
    var bar = document.getElementById('flashBar');
    if (!bar) return;
    bar.textContent = msg;
    bar.className = 'flash-bar' + (isErr ? ' err' : '');
    bar.hidden = false;
    clearTimeout(flashTimer);
    flashTimer = setTimeout(function () { bar.hidden = true; }, isErr ? 4200 : 2400);
  };

  function api(path, method, body) {
    return fetch(path, {
      method: method,
      headers: { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + token() },
      body: body === undefined ? null : JSON.stringify(body)
    }).then(function (r) {
      if (r.status === 401) { logout(); throw new Error('登录已失效'); }
      return r.json().catch(function () { return {}; }).then(function (data) {
        return { ok: r.ok, status: r.status, data: data };
      });
    });
  }

  window.closeModal = function (id) {
    var m = document.getElementById(id);
    if (m) m.style.display = 'none';
  };
  document.addEventListener('click', function (e) {
    if (e.target && e.target.classList && e.target.classList.contains('modal-mask')) {
      e.target.style.display = 'none';
    }
  });

  function show(id, text) {
    var el = document.getElementById(id);
    if (!el) { flash(text, true); return; }
    el.textContent = text;
    el.className = (el.className.indexOf('err') >= 0 ? el.className : 'msg err');
    el.style.display = '';
  }

  // 未登录时（cookie 过期）直接回登录页
  if (!token()) { location.href = '/'; return; }

  // 侧栏折叠：走 URL 参数，刷新后保持
  var navFold = document.getElementById('navFold');
  if (navFold) navFold.addEventListener('click', function () {
    var u = new URL(location.href);
    if (u.searchParams.get('nav') === '1') u.searchParams.delete('nav');
    else u.searchParams.set('nav', '1');
    location.href = u.toString();
  });

  // ---------- 发布管理：筛选 / 分页 ----------
  function withParam(k, v, resetPage) {
    var u = new URL(location.href);
    if (v === '' || v === null || v === undefined) u.searchParams.delete(k);
    else u.searchParams.set(k, v);
    if (resetPage) u.searchParams.delete('page');
    u.searchParams.delete('edit');
    location.href = u.toString();
  }
  window.applySearch = function (v) { withParam('q', (v || '').trim(), true); };
  window.applySrc = function (v) { withParam('src', v, true); };
  window.applySize = function (v) { withParam('size', v, true); };
  window.applyPage = function (v) { withParam('page', v, false); };

  // 勾选
  function selectedIds() {
    var out = [], boxes = document.querySelectorAll('.row-check');
    for (var i = 0; i < boxes.length; i++) if (boxes[i].checked) out.push(Number(boxes[i].value));
    return out;
  }
  function refreshSel() {
    var el = document.getElementById('selCount');
    if (el) el.textContent = String(selectedIds().length);
    var all = document.getElementById('checkAll');
    if (!all) return;
    var boxes = document.querySelectorAll('.row-check'), on = 0;
    for (var i = 0; i < boxes.length; i++) if (boxes[i].checked) on++;
    all.checked = boxes.length > 0 && on === boxes.length;
    all.indeterminate = on > 0 && on < boxes.length;
  }
  var checkAll = document.getElementById('checkAll');
  if (checkAll) checkAll.addEventListener('change', function () {
    var boxes = document.querySelectorAll('.row-check');
    for (var i = 0; i < boxes.length; i++) boxes[i].checked = checkAll.checked;
    refreshSel();
  });
  document.addEventListener('change', function (e) {
    if (e.target && e.target.classList && e.target.classList.contains('row-check')) refreshSel();
  });
  refreshSel();

  function needSel() {
    var ids = selectedIds();
    if (!ids.length) { flash('请先勾选链接', true); return null; }
    return ids;
  }
  function reloadSoon(msg, ms) {
    if (msg) flash(msg);
    setTimeout(function () { location.reload(); }, ms || 650);
  }

  window.batchHidden = function (hide) {
    var ids = needSel(); if (!ids) return;
    api('/api/admin/links/batch/hidden', 'PATCH', { ids: ids, hidden: hide }).then(function (r) {
      if (r.ok) reloadSoon('已' + (hide ? '隐藏' : '显示') + ' ' + (r.data.affected || ids.length) + ' 条');
      else flash(r.data.message || '操作失败', true);
    });
  };
  window.batchDel = function () {
    var ids = needSel(); if (!ids) return;
    ask('批量删除', '将删除选中的 ' + ids.length + ' 条链接（软删除，界面不再展示）。', 'delete', '', function () {
      api('/api/admin/links/batch', 'DELETE', { ids: ids }).then(function (r) {
        if (r.ok) reloadSoon('已删除 ' + (r.data.affected || ids.length) + ' 条');
        else flash(r.data.message || '删除失败', true);
      });
    });
  };
  window.transfer = function (isCopy) {
    var ids = needSel(); if (!ids) return;
    var sel = document.getElementById('targetLib');
    var target = sel ? Number(sel.value) : 0;
    if (!target) { flash('请选择目标发布库', true); return; }
    api('/api/admin/links/' + (isCopy ? 'copy' : 'move'), 'POST', { ids: ids, targetLibraryId: target })
      .then(function (r) {
        if (r.ok) reloadSoon('已' + (isCopy ? '复制' : '移动') + ' ' + (r.data.affected || r.data.copied || ids.length) + ' 条');
        else flash(r.data.message || '操作失败', true);
      });
  };

  window.setHidden = function (id, hide) {
    api('/api/admin/links/' + id + '/hidden', 'PATCH', { hidden: hide }).then(function (r) {
      if (r.ok) location.reload(); else flash(r.data.message || '操作失败', true);
    });
  };
  window.delOne = function (id) {
    ask('删除链接', '确定删除这条链接？软删除后界面不再展示。', 'delete', '', function () {
      api('/api/admin/links/' + id, 'DELETE').then(function (r) {
        if (r.ok) reloadSoon('已删除'); else flash(r.data.message || '删除失败', true);
      });
    });
  };
  window.enrichOne = function (id) {
    flash('AI 补料中…');
    api('/api/admin/ai/enrich/' + id, 'POST').then(function (r) {
      if (r.ok) reloadSoon('补料完成：' + ((r.data.tags || []).join('、') || '无标签'));
      else flash(r.data.message || '补料失败', true);
    });
  };
  window.exportLinks = function () {
    var lib = new URL(location.href).searchParams.get('lib');
    location.href = '/api/admin/ai/enrich/export' + (lib ? '?libraryId=' + lib : '');
  };

  // ---------- 批量 AI 补料 ----------
  function pollEnrich() {
    var bar = document.getElementById('enrichProgress');
    var startBtn = document.getElementById('enrichStart');
    var cancelBtn = document.getElementById('enrichCancel');
    function tick() {
      fetch('/api/admin/ai/enrich/batch/status', { headers: { 'Authorization': 'Bearer ' + token() } })
        .then(function (r) { return r.json(); })
        .then(function (s) {
          if (bar) {
            bar.style.display = '';
            bar.textContent = s.processed + '/' + s.total + '  ✓' + s.succeeded + '  ✗' + s.failed + '  ⊘' + s.skipped +
              (s.message ? '  ' + s.message : '');
          }
          if (cancelBtn) cancelBtn.style.display = s.running ? '' : 'none';
          if (startBtn) startBtn.disabled = !!s.running;
          if (s.running) setTimeout(tick, 1200);
          else if (s.processed > 0) setTimeout(function () { location.reload(); }, 900);
        })
        .catch(function () { if (startBtn) startBtn.disabled = false; });
    }
    tick();
  }
  window.startEnrich = function () {
    var lim = Number((document.getElementById('enrichLimit') || {}).value || 0);
    if (!(lim > 0)) lim = 100;
    if (lim > 100000) lim = 100000;
    var mode = (document.getElementById('enrichMode') || {}).value || 'missing';
    var run = function () {
      api('/api/admin/ai/enrich/batch', 'POST', { mode: mode, limit: lim, concurrency: 3 }).then(function (r) {
        if (!r.ok) { flash(r.data.message || '启动失败', true); return; }
        pollEnrich();
      });
    };
    if (mode === 'all') ask('批量补料（全部）', '「全部」会覆盖已有摘要并追加标签，确定继续？', 'confirm', '', run);
    else run();
  };
  window.cancelEnrich = function () {
    api('/api/admin/ai/enrich/batch/cancel', 'POST').then(function () { pollEnrich(); });
  };
  if (document.getElementById('enrichProgress')) {
    fetch('/api/admin/ai/enrich/batch/status', { headers: { 'Authorization': 'Bearer ' + token() } })
      .then(function (r) { return r.json(); })
      .then(function (s) { if (s.running) pollEnrich(); })
      .catch(function () {});
  }

  // ---------- 通用确认弹窗 ----------
  var askHandler = null;
  function ask(title, message, kind, value, onConfirm) {
    document.getElementById('askTitle').textContent = title;
    document.getElementById('askMessage').textContent = message;
    document.getElementById('askRenameBox').style.display = kind === 'rename' ? '' : 'none';
    var input = document.getElementById('askValue');
    input.value = value || '';
    var btn = document.getElementById('askConfirm');
    btn.className = kind === 'delete' ? 'danger-solid' : 'primary';
    btn.textContent = '确认';
    askHandler = onConfirm;
    document.getElementById('askModal').style.display = 'flex';
    if (kind === 'rename') setTimeout(function () { input.focus(); input.select(); }, 30);
  }
  window.ask = ask;
  var askConfirmBtn = document.getElementById('askConfirm');
  if (askConfirmBtn) askConfirmBtn.addEventListener('click', function () {
    var fn = askHandler; askHandler = null;
    var v = document.getElementById('askValue').value;
    closeModal('askModal');
    if (fn) fn(v);
  });

  // ---------- 发布库：就地新建 / 就地编辑 / 删除 / 渠道（与 .NET 版一致） ----------
  function libPayload(nameId, descId, webId, clientId) {
    return {
      name: (document.getElementById(nameId).value || '').trim(),
      description: (document.getElementById(descId).value || '').trim(),
      visibleOnWeb: document.getElementById(webId).checked,
      visibleOnClient: document.getElementById(clientId).checked
    };
  }
  window.saveLibInline = function (id) {
    var payload = libPayload('libEditName', 'libEditDesc', 'libEditWeb', 'libEditClient');
    if (!payload.name) { show('libEditMsg', '名称不能为空'); return; }
    api('/api/admin/libraries/' + id, 'PUT', payload).then(function (r) {
      if (r.ok) reloadSoon('已保存'); else show('libEditMsg', r.data.message || '保存失败');
    });
  };
  window.saveLibNew = function () {
    var payload = libPayload('libNewName', 'libNewDesc', 'libNewWeb', 'libNewClient');
    if (!payload.name) { show('libNewMsg', '名称不能为空'); return; }
    api('/api/admin/libraries', 'POST', payload).then(function (r) {
      if (r.ok) reloadSoon('已保存'); else show('libNewMsg', r.data.message || '保存失败');
    });
  };
  var libDelId = 0;
  window.openLibDelete = function (btn) {
    libDelId = btn.getAttribute('data-id');
    var name = btn.getAttribute('data-name') || '', count = btn.getAttribute('data-count') || '0';
    document.getElementById('libDelWarn').textContent = '将删除发布库「' + name + '」及其下 ' + count + ' 条链接（软删除，界面上不可恢复）。';
    document.getElementById('libDelPwd').value = '';
    document.getElementById('libDelMsg').style.display = 'none';
    document.getElementById('libDelModal').style.display = 'flex';
    setTimeout(function () { document.getElementById('libDelPwd').focus(); }, 30);
  };
  window.confirmLibDelete = function () {
    var pwd = document.getElementById('libDelPwd').value || '';
    if (!pwd) { show('libDelMsg', '请输入当前账号密码'); return; }
    var btn = document.getElementById('libDelConfirm');
    btn.disabled = true; btn.textContent = '删除中…';
    api('/api/admin/libraries/' + libDelId + '/delete', 'POST', { password: pwd }).then(function (r) {
      btn.disabled = false; btn.textContent = '确认删除';
      if (r.ok) { closeModal('libDelModal'); reloadSoon('已删除，连带软删除 ' + (r.data.deletedLinks || 0) + ' 条链接', 900); }
      else show('libDelMsg', r.data.message || '删除失败');
    });
  };
  window.toggleChannel = function (id, isWeb) {
    fetch('/api/admin/libraries', { headers: { 'Authorization': 'Bearer ' + token() } })
      .then(function (r) { return r.json(); })
      .then(function (list) {
        var lib = (list || []).filter(function (x) { return String(x.id) === String(id); })[0];
        if (!lib) { flash('发布库不存在', true); return; }
        api('/api/admin/libraries/' + id, 'PUT', {
          name: lib.name,
          description: lib.description || '',
          visibleOnWeb: isWeb ? !lib.visibleOnWeb : lib.visibleOnWeb,
          visibleOnClient: isWeb ? lib.visibleOnClient : !lib.visibleOnClient
        }).then(function (r) {
          if (r.ok) location.reload(); else flash(r.data.message || '切换失败', true);
        });
      });
  };

  // ---------- 链接新建 / 编辑 ----------
  function toggleNetdiskRow() {
    var t = document.getElementById('lfLinkType').value;
    document.getElementById('lfNetdiskRow').style.display = (t === '网盘') ? '' : 'none';
  }
  window.toggleNetdiskRow = toggleNetdiskRow;

  // 链接形式选项（从模板 datalist 之外的隐藏选项取样：这里用分类页数据不方便，退而用已知选项 + 页面注入）
  function linkTypeOptions() {
    var sel = document.getElementById('lfLinkType');
    var out = [];
    if (sel) for (var i = 0; i < sel.options.length; i++) {
      if (sel.options[i].value) out.push(sel.options[i].value);
    }
    return out;
  }
  var draftSeq = 0;
  function draftRowHTML(opt) {
    opt = opt || {};
    var types = linkTypeOptions();
    var sel = '<select class="draft-type" title="链接形式"><option value="">形式</option>' +
      types.map(function (t) { return '<option value="' + esc(t) + '"' + (t === (opt.linkType || '') ? ' selected' : '') + '>' + esc(t) + '</option>'; }).join('') + '</select>';
    return '<div class="link-draft" data-seq="' + (++draftSeq) + '">' +
      '<div class="draft-row">' +
        '<span class="draft-no"></span>' + sel +
        '<input class="d-title" placeholder="名称" value="' + esc(opt.title || '') + '">' +
        '<input class="d-url" placeholder="URL" value="' + esc(opt.url || '') + '">' +
        '<input class="d-group" list="cat-group-list" placeholder="分组" title="分组：可选可新建，多个用逗号分隔" value="' + esc(opt.groupName || '') + '">' +
        '<input class="d-platform" list="cat-platform-list" placeholder="平台" title="平台：可选可新建，多个用逗号分隔" value="' + esc(opt.platform || '') + '">' +
        '<input class="d-tags" list="cat-tag-list" placeholder="标签" title="标签：可选可新建，多个用逗号分隔" value="' + esc((opt.tags || []).join(',')) + '">' +
        '<input class="d-version" placeholder="版本" value="' + esc(opt.version || '') + '">' +
        '<input class="d-remark" placeholder="备注" title="备注" value="' + esc(opt.remark || '') + '">' +
        '<span class="draft-ops"><button type="button" onclick="duplicateLinkDraft(this)" title="复制这一行">克隆</button>' +
          '<button type="button" class="danger" onclick="removeLinkDraft(this)" title="删除">×</button></span>' +
      '</div>' +
      '<div class="draft-netdisk" style="display:none"><span class="draft-spacer"></span>' +
        '<span class="draft-net-label">网盘提取码</span><input class="d-extract" placeholder="提取码" value="' + esc(opt.extractCode || '') + '">' +
        '<span class="draft-net-label">过期时间</span><input class="d-expire" placeholder="2026-12-31" value="' + esc(opt.expireAt || '') + '"></div>' +
    '</div>';
  }
  function esc(s) { return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'); }
  function renumberDrafts() {
    var rows = document.querySelectorAll('#linkDraftsBody .link-draft');
    for (var i = 0; i < rows.length; i++) {
      rows[i].querySelector('.draft-no').textContent = String(i + 1);
      var del = rows[i].querySelector('.draft-ops .danger');
      if (del) del.disabled = rows.length === 1;
    }
  }
  function syncDraftNetdisk(row) {
    var t = row.querySelector('.draft-type').value;
    row.querySelector('.draft-netdisk').style.display = (t === '网盘') ? '' : 'none';
  }
  window.addLinkDraft = function () {
    var body = document.getElementById('linkDraftsBody');
    body.insertAdjacentHTML('beforeend', draftRowHTML());
    renumberDrafts();
    var rows = body.querySelectorAll('.link-draft');
    var last = rows[rows.length - 1];
    last.querySelector('.draft-type').addEventListener('change', function () { syncDraftNetdisk(last); });
  };
  window.duplicateLinkDraft = function (btn) {
    var row = btn.closest('.link-draft');
    function v(cls) { var e = row.querySelector(cls); return e ? e.value : ''; }
    var opt = {
      linkType: v('.draft-type'), title: v('.d-title'), url: v('.d-url'), groupName: v('.d-group'),
      platform: v('.d-platform'), tags: v('.d-tags').split(/[,;，；]/).map(function (s) { return s.trim(); }).filter(Boolean),
      version: v('.d-version'), remark: v('.d-remark'), extractCode: v('.d-extract'), expireAt: v('.d-expire')
    };
    var body = document.getElementById('linkDraftsBody');
    body.insertAdjacentHTML('beforeend', draftRowHTML(opt));
    renumberDrafts();
    var rows = body.querySelectorAll('.link-draft'), last = rows[rows.length - 1];
    last.querySelector('.draft-type').addEventListener('change', function () { syncDraftNetdisk(last); });
    syncDraftNetdisk(last);
  };
  window.removeLinkDraft = function (btn) {
    var rows = document.querySelectorAll('#linkDraftsBody .link-draft');
    if (rows.length <= 1) return;
    btn.closest('.link-draft').remove();
    renumberDrafts();
  };

  window.openLinkNew = function () {
    document.getElementById('linkModalTitle').textContent = '新建链接';
    document.getElementById('linkModalBox').className = 'modal wide';
    document.getElementById('linkFormSingle').style.display = 'none';
    document.getElementById('linkFormDrafts').style.display = '';
    var body = document.getElementById('linkDraftsBody');
    body.innerHTML = '';
    window.addLinkDraft();
    document.getElementById('lfMsg').style.display = 'none';
    document.getElementById('linkModal').style.display = 'flex';
  };

  window.openLinkEdit = function (id) {
    fetch('/api/admin/links?page=1&pageSize=1&id=' + id, { headers: { 'Authorization': 'Bearer ' + token() } })
      .then(function (r) { return r.json(); })
      .then(function (paged) {
        var l = (paged.items || [])[0];
        if (!l) { flash('链接不存在', true); return; }
        document.getElementById('linkModalTitle').textContent = '编辑链接';
        document.getElementById('linkModalBox').className = 'modal';
        document.getElementById('linkFormDrafts').style.display = 'none';
        document.getElementById('linkFormSingle').style.display = '';
        document.getElementById('lfId').value = l.id;
        document.getElementById('lfLinkType').value = l.linkType || '';
        document.getElementById('lfGroup').value = l.groupName || '';
        document.getElementById('lfTitle').value = l.title || '';
        document.getElementById('lfUrl').value = l.url || '';
        document.getElementById('lfExtractCode').value = l.extractCode || '';
        document.getElementById('lfExpireAt').value = l.expireAt || '';
        document.getElementById('lfPlatform').value = l.platform || '';
        document.getElementById('lfVersion').value = l.version || '';
        document.getElementById('lfTags').value = (l.tags || []).join(',');
        document.getElementById('lfRemark').value = l.remark || '';
        document.getElementById('lfMsg').style.display = 'none';
        toggleNetdiskRow();
        document.getElementById('linkModal').style.display = 'flex';
      });
  };

  function splitTags(s) { return (s || '').split(/[,;，；]/).map(function (x) { return x.trim(); }).filter(Boolean); }
  window.saveLink = function () {
    var id = Number(document.getElementById('lfId').value || 0);
    var draftsMode = document.getElementById('linkFormDrafts').style.display !== 'none';
    var lib = Number(new URL(location.href).searchParams.get('lib') || 0);
    var payload = {
      libraryId: lib,
      linkType: (document.getElementById('lfLinkType').value || '').trim() || '官网/普通网址',
      groupName: (document.getElementById('lfGroup').value || '').trim() || '未分类',
      title: (document.getElementById('lfTitle').value || '').trim(),
      url: (document.getElementById('lfUrl').value || '').trim(),
      extractCode: (document.getElementById('lfExtractCode').value || '').trim(),
      expireAt: (document.getElementById('lfExpireAt').value || '').trim(),
      platform: (document.getElementById('lfPlatform').value || '').trim(),
      version: (document.getElementById('lfVersion').value || '').trim(),
      remark: (document.getElementById('lfRemark').value || '').trim(),
      tags: splitTags(document.getElementById('lfTags').value)
    };
    if (id > 0 || !draftsMode) {
      if (!payload.url) { show('lfMsg', 'URL 不能为空'); return; }
      var p = id > 0 ? api('/api/admin/links/' + id, 'PUT', payload) : api('/api/admin/links', 'POST', payload);
      p.then(function (r) {
        if (r.ok) { closeModal('linkModal'); reloadSoon('已保存'); }
        else show('lfMsg', r.data.message || '保存失败');
      });
      return;
    }
    // 新建：批量草稿逐条创建（与 .NET 版 SaveLinkForm 一致）
    var rows = document.querySelectorAll('#linkDraftsBody .link-draft'), created = 0, failed = 0, lastMsg = '';
    var list = [];
    for (var i = 0; i < rows.length; i++) {
      var row = rows[i];
      function rv(cls) { var e = row.querySelector(cls); return e ? e.value.trim() : ''; }
      var title = rv('.d-title'), url = rv('.d-url');
      if (!title && !url) continue;
      list.push({
        libraryId: lib, title: title, url: url,
        linkType: rv('.draft-type') || '官网/普通网址',
        groupName: rv('.d-group') || '未分类',
        platform: rv('.d-platform'), version: rv('.d-version'), remark: rv('.d-remark'),
        extractCode: rv('.d-extract'), expireAt: rv('.d-expire'),
        tags: splitTags(rv('.d-tags'))
      });
    }
    if (!list.length) { show('lfMsg', '请至少填写一条链接'); return; }
    var btn = document.querySelector('#linkModal .modal-foot .primary');
    btn.disabled = true; btn.textContent = '保存中…';
    var chain = Promise.resolve();
    list.forEach(function (item) {
      chain = chain.then(function () {
        return api('/api/admin/links', 'POST', item).then(function (r) {
          if (r.ok) created++; else { failed++; lastMsg = r.data.message || '保存失败'; }
        });
      });
    });
    chain.then(function () {
      btn.disabled = false; btn.textContent = '保存';
      if (failed === 0) { closeModal('linkModal'); reloadSoon('已保存 ' + created + ' 条链接'); }
      else show('lfMsg', '已保存 ' + created + ' 条，失败 ' + failed + ' 条：' + lastMsg);
    });
  };

  // ---------- 导入 ----------
  window.openImport = function () {
    var m = document.getElementById('importModal');
    if (!m) { flash('请先进入发布管理页', true); return; }
    m.style.display = 'flex';
    refreshImportStatus();
  };
  window.importTab = function (tab) {
    var tabs = document.querySelectorAll('.import-tab');
    for (var i = 0; i < tabs.length; i++) {
      tabs[i].className = 'import-tab' + (tabs[i].getAttribute('data-tab') === tab ? ' on' : '');
    }
    document.getElementById('tabPaste').style.display = tab === 'paste' ? '' : 'none';
    document.getElementById('tabExcel').style.display = tab === 'excel' ? '' : 'none';
  };
  function importMsg(text, isErr) {
    var el = document.getElementById('importMsg');
    if (!el) return;
    if (!text) { el.style.display = 'none'; return; }
    el.textContent = text;
    el.className = 'msg' + (isErr ? ' err' : '');
    el.style.display = '';
  }
  function refreshImportStatus() {
    var box = document.getElementById('excelProgress');
    if (!box) return;
    fetch('/api/admin/import/excel/jobs/status', { headers: { 'Authorization': 'Bearer ' + token() } })
      .then(function (r) { return r.json(); })
      .then(function (s) {
        if (s.running) {
          box.style.display = '';
          var line = document.getElementById('excelLine');
          if (line) line.textContent = '已扫描 ' + s.processed + ' 行 · 已入库 ' + s.imported + ' 条';
          var bar = document.getElementById('excelBar');
          if (bar) bar.style.width = (s.processed > 0 ? Math.min(100, Math.round(s.processed * 100 / Math.max(1, s.total || s.processed))) : 0) + '%';
          setTimeout(refreshImportStatus, 1000);
        } else {
          box.style.display = 'none';
          if (s.message) importMsg(s.message, s.failed > 0);
        }
      })
      .catch(function () {});
  }
  window.parsePaste = function () {
    var text = (document.getElementById('pasteText') || {}).value || '';
    if (!text.trim()) { importMsg('请先粘贴链接', true); return; }
    var btn = document.getElementById('parseBtn');
    btn.disabled = true; var t0 = btn.textContent; btn.textContent = '识别中…';
    api('/api/admin/import/links/parse', 'POST', { text: text }).then(function (r) {
      btn.disabled = false; btn.textContent = t0;
      if (!r.ok) { importMsg(r.data.message || '识别失败', true); return; }
      var arr = r.data || [];
      var recognized = arr.filter(function (x) { return x.recognized; }).length;
      var out = document.getElementById('parseOut');
      out.style.display = '';
      out.textContent = arr.map(function (x) {
        return (x.recognized ? '✓ ' : '· ') + (x.title || '') + '\n   ' + (x.url || x.rawText || '') +
          '\n   [' + (x.linkType || '') + '] [' + (x.groupName || '') + ']' +
          (x.platform ? ' [' + x.platform + ']' : '') +
          (x.tags && x.tags.length ? ' #' + x.tags.join(' #') : '') +
          (x.summary ? '\n   ' + x.summary : '');
      }).join('\n\n');
      importMsg('识别完成：' + arr.length + ' 行，其中 ' + recognized + ' 行由模型识别');
    }).catch(function () { btn.disabled = false; btn.textContent = t0; });
  };
  window.importPaste = function () {
    var text = (document.getElementById('pasteText') || {}).value || '';
    if (!text.trim()) { importMsg('请先粘贴链接', true); return; }
    var btn = document.getElementById('pasteImportBtn');
    btn.disabled = true; var t0 = btn.textContent; btn.textContent = '导入中…';
    var lib = new URL(location.href).searchParams.get('lib');
    var url = '/api/admin/import/links' + (lib ? '?libraryId=' + lib : '');
    api('/api/admin/import/links/parse', 'POST', { text: text }).then(function (r) {
      if (!r.ok) throw new Error(r.data.message || '识别失败');
      return api(url, 'POST', r.data);
    }).then(function (r2) {
      btn.disabled = false; btn.textContent = t0;
      if (r2.ok) { importMsg('已导入 ' + (r2.data.imported || 0) + ' 条'); setTimeout(function () { location.reload(); }, 800); }
      else importMsg(r2.data.message || '导入失败', true);
    }).catch(function (e) {
      btn.disabled = false; btn.textContent = t0;
      importMsg(e.message || '导入失败', true);
    });
  };
  window.showExcelFile = function (input) {
    var hint = document.getElementById('excelFileHint');
    if (!hint) return;
    if (!input.files || !input.files.length) { hint.style.display = 'none'; return; }
    var f = input.files[0];
    hint.textContent = '已选择：' + f.name + '（' + (Math.round(f.size / 102.4) / 10) + ' KB）';
    hint.style.display = '';
  };
  window.importExcel = function () {
    var input = document.getElementById('excelFile');
    if (!input || !input.files || !input.files.length) { importMsg('请选择 xlsx 文件', true); return; }
    var fd = new FormData();
    fd.append('file', input.files[0]);
    var lib = new URL(location.href).searchParams.get('lib');
    var big = input.files[0].size > 10 * 1024 * 1024;
    var ep = '/api/admin/import/excel' + (big ? '/jobs' : '') + (lib ? '?libraryId=' + lib : '');
    var btn = document.getElementById('excelBtn');
    btn.disabled = true; var t0 = btn.textContent; btn.textContent = '上传中…';
    fetch(ep, { method: 'POST', headers: { 'Authorization': 'Bearer ' + token() }, body: fd })
      .then(function (resp) { return resp.json().then(function (d) { return { ok: resp.ok, data: d }; }); })
      .then(function (r) {
        btn.disabled = false; btn.textContent = t0;
        if (!r.ok) { importMsg(r.data.message || '导入失败', true); return; }
        if (r.data.jobId || r.data.running || r.data.background) { importMsg('已提交后台任务'); refreshImportStatus(); }
        else {
          importMsg('导入完成：新增 ' + (r.data.imported || 0) + ' 条 · 跳过 ' + (r.data.skipped || 0));
          setTimeout(function () { location.reload(); }, 1100);
        }
      })
      .catch(function () { btn.disabled = false; btn.textContent = t0; importMsg('上传失败', true); });
  };
  window.cancelExcel = function () {
    api('/api/admin/import/excel/jobs/cancel', 'POST').then(function () { refreshImportStatus(); });
  };
  if (new URL(location.href).searchParams.get('import') === '1') {
    var im = document.getElementById('importModal');
    if (im) { im.style.display = 'flex'; refreshImportStatus(); }
  }

  // ---------- 去重 ----------
  var dedupeState = null;
  window.openDedupe = function () {
    document.getElementById('dedupeSummary').style.display = 'none';
    document.getElementById('dedupeMsg').style.display = 'none';
    document.getElementById('dedupeConfirm').disabled = true;
    dedupeState = null;
    document.getElementById('dedupeModal').style.display = 'flex';
  };
  function dedupePayload(dryRun) {
    var all = document.getElementById('dedupeAll').checked;
    var lib = new URL(location.href).searchParams.get('lib');
    return { scope: all ? 'all' : 'lib', dryRun: dryRun, libraryId: all ? null : (lib ? Number(lib) : null) };
  }
  window.dedupePreview = function () {
    api('/api/admin/links/dedupe', 'POST', dedupePayload(true)).then(function (r) {
      if (!r.ok) { show('dedupeMsg', r.data.message || '预览失败'); return; }
      dedupeState = r.data;
      var d = r.data;
      var box = document.getElementById('dedupeSummary');
      box.style.display = '';
      var html = '扫描 ' + d.scanned + ' 条，' + d.groups + ' 组重复，可清理 ' + d.duplicates + ' 条';
      if (d.samples && d.samples.length) {
        html += '<ul>' + d.samples.slice(0, 8).map(function (s) {
          return '<li title="' + esc(s.url || '') + '">' + esc(s.url || '') + ' <b>×' + s.count + '</b></li>';
        }).join('') + '</ul>';
      }
      box.innerHTML = html;
      document.getElementById('dedupeConfirm').disabled = !(d.duplicates > 0);
    });
  };
  window.dedupeRun = function () {
    if (!dedupeState) return;
    var btn = document.getElementById('dedupeConfirm');
    btn.disabled = true; btn.textContent = '处理中…';
    api('/api/admin/links/dedupe', 'POST', dedupePayload(false)).then(function (r) {
      btn.textContent = '确认去重';
      if (r.ok) { closeModal('dedupeModal'); reloadSoon('已清理 ' + (r.data.removed || 0) + ' 条重复', 900); }
      else { btn.disabled = false; show('dedupeMsg', r.data.message || '去重失败'); }
    });
  };

  // ---------- 分组管理 ----------
  function catSelected() {
    var out = [], boxes = document.querySelectorAll('.cat-check');
    for (var i = 0; i < boxes.length; i++) if (boxes[i].checked) out.push(boxes[i].value);
    return out;
  }
  function refreshCatSel() {
    var n = catSelected().length;
    var hint = document.getElementById('catSelHint');
    if (hint) {
      hint.style.display = n ? '' : 'none';
      document.getElementById('catBatchDel').style.display = n ? '' : 'none';
      document.getElementById('catClear').style.display = n ? '' : 'none';
      document.getElementById('catSelCount').textContent = String(n);
    }
    var all = document.getElementById('catCheckAll');
    if (!all) return;
    var boxes = document.querySelectorAll('.cat-check'), on = 0;
    for (var i = 0; i < boxes.length; i++) if (boxes[i].checked) on++;
    all.checked = boxes.length > 0 && on === boxes.length;
    all.indeterminate = on > 0 && on < boxes.length;
  }
  var catAll = document.getElementById('catCheckAll');
  if (catAll) catAll.addEventListener('change', function () {
    var boxes = document.querySelectorAll('.cat-check');
    for (var i = 0; i < boxes.length; i++) {
      var item = boxes[i].closest('.category-item');
      if (!item || item.style.display !== 'none') boxes[i].checked = catAll.checked;
    }
    refreshCatSel();
  });
  document.addEventListener('change', function (e) {
    if (e.target && e.target.classList && e.target.classList.contains('cat-check')) {
      var item = e.target.closest('.category-item');
      if (item) {
        if (e.target.checked) item.classList.add('selected');
        else item.classList.remove('selected');
      }
      refreshCatSel();
    }
  });
  window.expandCat = function () {
    var extras = document.querySelectorAll('.category-item.cat-extra');
    for (var i = 0; i < extras.length; i++) extras[i].style.display = '';
    var btn = document.getElementById('catMore');
    if (btn) btn.style.display = 'none';
    var shown = document.getElementById('catShown');
    if (shown) shown.textContent = String(document.querySelectorAll('.category-item').length);
  };
  window.filterCat = function (kw) {
    var items = document.querySelectorAll('.category-item'), q = (kw || '').toLowerCase(), shown = 0;
    for (var i = 0; i < items.length; i++) {
      var name = (items[i].getAttribute('data-name') || '').toLowerCase();
      var hit = !q || name.indexOf(q) >= 0;
      items[i].style.display = hit ? '' : 'none';
      if (hit) shown++;
    }
    var el = document.getElementById('catShown');
    if (el) el.textContent = String(shown);
  };
  window.catClear = function () {
    var boxes = document.querySelectorAll('.cat-check');
    for (var i = 0; i < boxes.length; i++) { boxes[i].checked = false; var it = boxes[i].closest('.category-item'); if (it) it.classList.remove('selected'); }
    refreshCatSel();
  };
  function catKind() { return new URL(location.href).searchParams.get('kind') || 'group'; }
  window.catAdd = function () {
    var el = document.getElementById('catNew');
    var name = (el.value || '').trim();
    if (!name) { flash('请输入名称', true); return; }
    api('/api/admin/categories/' + catKind(), 'POST', { name: name }).then(function (r) {
      if (r.ok) reloadSoon('已创建');
      else show('catMsg', r.data.message || '创建失败');
    });
  };
  window.catRename = function (btn) {
    var name = btn.closest('.category-item').getAttribute('data-name');
    ask('重命名', '将「' + name + '」重命名，已使用该选项的链接会同步更新。', 'rename', name, function (nn) {
      nn = (nn || '').trim();
      if (!nn || nn === name) return;
      api('/api/admin/categories/' + catKind(), 'PUT', { oldName: name, newName: nn }).then(function (r) {
        if (r.ok) reloadSoon('已重命名');
        else show('catMsg', r.data.message || '重命名失败');
      });
    });
  };
  window.catDel = function (btn) {
    var name = btn.closest('.category-item').getAttribute('data-name');
    ask('删除选项', '确定删除「' + name + '」？仍被链接使用的选项会被拒绝删除。', 'delete', '', function () {
      api('/api/admin/categories/' + catKind() + '?name=' + encodeURIComponent(name), 'DELETE').then(function (r) {
        if (r.ok) reloadSoon('已删除');
        else show('catMsg', r.data.message || '删除失败');
      });
    });
  };
  window.catBatchDelete = function () {
    var names = catSelected();
    if (!names.length) return;
    ask('批量删除分类', '将删除选中的 ' + names.length + ' 项（跨分类累计）。仍被链接使用的会被跳过。', 'delete', '', function () {
      api('/api/admin/categories/' + catKind() + '/batch-delete', 'POST', { names: names }).then(function (r) {
        if (r.ok) reloadSoon('已删除 ' + (r.data.deleted || 0) + ' 项，跳过 ' + (r.data.skipped || 0) + ' 项');
        else show('catMsg', r.data.message || '批量删除失败');
      });
    });
  };
  refreshCatSel();

  // ---------- 用户 ----------
  window.createUser = function () {
    var u = (document.getElementById('newUser').value || '').trim();
    var p = (document.getElementById('newPwd').value || '');
    if (!u || !p) { flash('用户名与密码都要填', true); return; }
    api('/api/auth/users', 'POST', { username: u, password: p }).then(function (r) {
      if (r.ok) reloadSoon('已创建');
      else flash(r.data.message || '创建失败', true);
    });
  };

  // ---------- LLM ----------
  var pfModels = [], pfActive = '';
  function renderChips() {
    var box = document.getElementById('pfModelChips');
    if (!box) return;
    if (!pfModels.length) {
      box.innerHTML = '<span class="hint" style="margin:0">还没有模型，在下方输入后点「添加模型」</span>';
      return;
    }
    box.innerHTML = '';
    pfModels.forEach(function (m) {
      var chip = document.createElement('span');
      chip.className = 'model-chip' + (m === pfActive ? ' on' : '');
      var name = document.createElement('span');
      name.className = 'chip-name';
      name.textContent = m;
      chip.appendChild(name);
      if (m === pfActive) {
        var tag = document.createElement('span');
        tag.className = 'chip-tag';
        tag.textContent = '当前';
        chip.appendChild(tag);
      }
      var x = document.createElement('button');
      x.type = 'button'; x.className = 'chip-x'; x.textContent = '×'; x.title = '删除';
      x.addEventListener('click', function (e) {
        e.stopPropagation();
        pfModels = pfModels.filter(function (v) { return v !== m; });
        if (pfActive === m) pfActive = pfModels[0] || '';
        renderChips();
      });
      chip.appendChild(x);
      chip.addEventListener('click', function () { pfActive = m; renderChips(); });
      box.appendChild(chip);
    });
  }
  window.addModel = function () {
    var input = document.getElementById('pfNewModel');
    var v = (input.value || '').trim();
    if (!v) return;
    if (pfModels.indexOf(v) < 0) pfModels.push(v);
    if (!pfActive) pfActive = v;
    input.value = '';
    renderChips();
  };
  var pfFetched = [];
  function setRetryDefaults() {
    document.getElementById('pfRetryPolicy').value = 'default';
    document.getElementById('pfMaxRetries').value = '8';
    var defaults = ['empty', 'rate_limit', 'server', 'timeout', 'network'];
    var boxes = document.querySelectorAll('.pf-retry');
    for (var i = 0; i < boxes.length; i++) boxes[i].checked = defaults.indexOf(boxes[i].value) >= 0;
  }
  window.openProvider = function (btn) {
    var isEdit = !!(btn && btn.getAttribute && btn.getAttribute('data-id'));
    document.getElementById('providerTitle').textContent = isEdit ? '编辑提供商' : '添加自定义提供商';
    document.getElementById('pfId').value = isEdit ? btn.getAttribute('data-id') : '0';
    document.getElementById('pfName').value = isEdit ? (btn.getAttribute('data-name') || '') : '';
    document.getElementById('pfKey').value = '';
    document.getElementById('pfKey').placeholder = isEdit && btn.getAttribute('data-haskey') === '1' ? '********' : 'sk-...';
    document.getElementById('pfProtocol').value = isEdit ? (btn.getAttribute('data-protocol') || 'openai') : 'openai';
    document.getElementById('pfEndpoint').value = isEdit ? (btn.getAttribute('data-endpoint') || '') : '';
    document.getElementById('pfKeyHint').style.display = isEdit && btn.getAttribute('data-haskey') === '1' ? '' : 'none';
    var ms = isEdit ? (btn.getAttribute('data-models') || '') : '';
    pfModels = ms ? ms.split(',').map(function (s) { return s.trim(); }).filter(Boolean) : [];
    pfActive = isEdit ? (btn.getAttribute('data-model') || '') : '';
    if (pfActive && pfModels.indexOf(pfActive) < 0) pfModels.push(pfActive);
    // 重试设置回填
    var policy = isEdit ? (btn.getAttribute('data-retrypolicy') || 'default') : 'default';
    document.getElementById('pfRetryPolicy').value = policy;
    document.getElementById('pfMaxRetries').value = isEdit ? (btn.getAttribute('data-maxretries') || '8') : '8';
    var retryOn = isEdit ? (btn.getAttribute('data-retryon') || '').split(',').filter(Boolean) : [];
    var boxes = document.querySelectorAll('.pf-retry');
    for (var i = 0; i < boxes.length; i++) boxes[i].checked = retryOn.indexOf(boxes[i].value) >= 0;
    document.getElementById('pfDefault').checked = !!(isEdit && btn.getAttribute('data-default') === '1');
    document.getElementById('pfPreset').value = '';
    pfFetched = [];
    document.getElementById('pfModelCatalog').style.display = 'none';
    document.getElementById('pfModelsMsg').style.display = 'none';
    renderChips();
    document.getElementById('pfMsg').style.display = 'none';
    document.getElementById('pfTestMsg').style.display = 'none';
    document.getElementById('providerModal').style.display = 'flex';
  };
  var PRESETS = {
    openai: { name: 'OpenAI', protocol: 'openai', endpoint: 'https://api.openai.com/v1', models: ['gpt-4o-mini', 'gpt-4o'], model: 'gpt-4o-mini' },
    anthropic: { name: 'Anthropic', protocol: 'anthropic', endpoint: 'https://api.anthropic.com', models: ['claude-3-5-sonnet-latest', 'claude-3-5-haiku-latest'], model: 'claude-3-5-sonnet-latest' },
    gemini: { name: 'Gemini', protocol: 'gemini', endpoint: 'https://generativelanguage.googleapis.com', models: ['gemini-1.5-flash', 'gemini-1.5-pro'], model: 'gemini-1.5-flash' },
    deepseek: { name: 'DeepSeek', protocol: 'openai', endpoint: 'https://api.deepseek.com/v1', models: ['deepseek-chat', 'deepseek-reasoner'], model: 'deepseek-chat' },
    qwen: { name: '通义千问', protocol: 'openai', endpoint: 'https://dashscope.aliyuncs.com/compatible-mode/v1', models: ['qwen-plus', 'qwen-turbo', 'qwen-max'], model: 'qwen-plus' },
    moonshot: { name: 'Kimi 月之暗面', protocol: 'openai', endpoint: 'https://api.moonshot.cn/v1', models: ['moonshot-v1-8k', 'moonshot-v1-32k'], model: 'moonshot-v1-8k' },
    zhipu: { name: '智谱 GLM', protocol: 'openai', endpoint: 'https://open.bigmodel.cn/api/paas/v4', models: ['glm-4-flash', 'glm-4-plus'], model: 'glm-4-flash' },
    siliconflow: { name: '硅基流动', protocol: 'openai', endpoint: 'https://api.siliconflow.cn/v1', models: ['Qwen/Qwen2.5-7B-Instruct'], model: 'Qwen/Qwen2.5-7B-Instruct' }
  };
  window.applyProviderPreset = function (v) {
    var p = PRESETS[v];
    if (!p) return;
    document.getElementById('pfName').value = p.name;
    document.getElementById('pfProtocol').value = p.protocol;
    document.getElementById('pfEndpoint').value = p.endpoint;
    pfModels = p.models.slice(); pfActive = p.model;
    document.getElementById('pfNewModel').value = '';
    renderChips();
  };
  function renderFetchedCatalog() {
    var box = document.getElementById('pfModelCatalog');
    if (!pfFetched.length) { box.style.display = 'none'; return; }
    box.style.display = '';
    document.getElementById('pfCatalogCount').textContent = '站点可用模型 ' + pfFetched.length;
    document.getElementById('pfCatalogList').innerHTML = pfFetched.map(function (m) {
      var added = pfModels.indexOf(m.id) >= 0;
      return '<div class="mc-row' + (added ? ' added' : '') + '" onclick="addFetchedModel(this)" data-id="' + esc(m.id) + '">' +
        '<span class="mc-id">' + esc(m.id) + '</span>' +
        (m.displayName && m.displayName !== m.id ? '<span class="mc-name">' + esc(m.displayName) + '</span>' : '') +
        '<span class="mc-op">' + (added ? '✓' : '+') + '</span></div>';
    }).join('');
  }
  window.addFetchedModel = function (el) {
    var id = el.getAttribute('data-id');
    if (pfModels.indexOf(id) < 0) { pfModels.push(id); if (!pfActive) pfActive = id; }
    renderChips(); renderFetchedCatalog();
  };
  window.addAllFetched = function () {
    pfFetched.forEach(function (m) { if (pfModels.indexOf(m.id) < 0) pfModels.push(m.id); });
    if (!pfActive && pfFetched.length) pfActive = pfFetched[0].id;
    renderChips(); renderFetchedCatalog();
  };
  window.saveProvider = function () {
    var id = Number(document.getElementById('pfId').value || 0);
    var retryOn = [], boxes = document.querySelectorAll('.pf-retry');
    for (var i = 0; i < boxes.length; i++) if (boxes[i].checked) retryOn.push(boxes[i].value);
    var payload = {
      id: id,
      name: (document.getElementById('pfName').value || '').trim(),
      protocol: document.getElementById('pfProtocol').value,
      apiEndpoint: (document.getElementById('pfEndpoint').value || '').trim(),
      apiKey: document.getElementById('pfKey').value || '',
      model: pfActive,
      models: pfModels,
      retryPolicy: document.getElementById('pfRetryPolicy').value,
      maxRetries: Number(document.getElementById('pfMaxRetries').value || 8),
      retryOn: retryOn,
      isDefault: document.getElementById('pfDefault').checked
    };
    if (!payload.name) { show('pfMsg', '名称不能为空'); return; }
    var btn = document.getElementById('pfSaveBtn');
    btn.disabled = true; btn.textContent = '保存中…';
    api('/api/admin/llm/configs', 'POST', payload).then(function (r) {
      btn.disabled = false; btn.textContent = '保存';
      if (r.ok) { closeModal('providerModal'); reloadSoon('已保存'); }
      else show('pfMsg', r.data.message || '保存失败');
    }).catch(function () { btn.disabled = false; btn.textContent = '保存'; });
  };
  window.fetchModels = function () {
    var btn = document.getElementById('pfFetchBtn');
    btn.disabled = true; var t0 = btn.textContent; btn.textContent = '获取中…';
    api('/api/admin/llm/models', 'POST', {
      apiEndpoint: (document.getElementById('pfEndpoint').value || '').trim(),
      apiKey: document.getElementById('pfKey').value || '',
      protocol: document.getElementById('pfProtocol').value,
      id: Number(document.getElementById('pfId').value || 0)
    }).then(function (r) {
      btn.disabled = false; btn.textContent = t0;
      if (!r.ok || !r.data.ok) { show('pfModelsMsg', (r.data && r.data.message) || '获取失败', true); return; }
      pfFetched = (r.data.models || []).filter(function (m) { return m.id; });
      renderFetchedCatalog();
      var el = document.getElementById('pfModelsMsg');
      el.className = 'msg';
      el.style.display = '';
      el.textContent = '已获取 ' + pfFetched.length + ' 个可用模型';
    }).catch(function () { btn.disabled = false; btn.textContent = t0; });
  };
  window.testProviderForm = function () {
    var btn = document.getElementById('pfTestBtn');
    btn.disabled = true; var t0 = btn.textContent; btn.textContent = '检测中…';
    api('/api/admin/llm/test', 'POST', {
      id: Number(document.getElementById('pfId').value || 0),
      apiEndpoint: (document.getElementById('pfEndpoint').value || '').trim(),
      apiKey: document.getElementById('pfKey').value || '',
      protocol: document.getElementById('pfProtocol').value,
      model: pfActive
    }).then(function (r) {
      btn.disabled = false; btn.textContent = t0;
      var el = document.getElementById('pfTestMsg');
      var ok = r.ok && r.data && r.data.ok;
      el.className = 'msg' + (ok ? '' : ' err');
      el.style.display = '';
      el.textContent = (ok ? '✓ ' : '✗ ') + ((r.data && r.data.message) || '') +
        (ok && r.data.latencyMs ? ' · ' + r.data.latencyMs + ' ms' : '');
    }).catch(function () { btn.disabled = false; btn.textContent = t0; });
  };
  window.testProvider = function (id) {
    api('/api/admin/llm/test', 'POST', { id: id }).then(function (r) {
      var el = document.getElementById('llmMsg');
      var ok = r.ok && r.data && r.data.ok;
      el.className = 'msg' + (ok ? '' : ' err');
      el.style.display = '';
      el.textContent = (ok ? '✓ ' : '✗ ') + ((r.data && r.data.message) || '') +
        (ok && r.data.latencyMs ? ' · ' + r.data.latencyMs + ' ms' : '');
    });
  };
  window.setDefaultProvider = function (id) {
    api('/api/admin/llm/configs/' + id + '/default', 'POST').then(function (r) {
      if (r.ok) reloadSoon('已设为默认'); else flash(r.data.message || '操作失败', true);
    });
  };
  window.switchModel = function (id, model) {
    api('/api/admin/llm/configs/' + id + '/model', 'POST', { model: model }).then(function (r) {
      if (r.ok) reloadSoon('已切换为 ' + model); else flash(r.data.message || '切换失败', true);
    });
  };
  window.delProvider = function (id) {
    ask('删除提供商', '确定删除这个提供商配置？已配置的密钥会一并删除。', 'delete', '', function () {
      api('/api/admin/llm/configs/' + id, 'DELETE').then(function (r) {
        if (r.ok) reloadSoon('已删除'); else flash(r.data.message || '删除失败', true);
      });
    });
  };

  // ---------- 账号 ----------
  var mfaSecretText = '';
  window.mfaStart = function () {
    api('/api/auth/mfa/setup', 'POST').then(function (r) {
      if (!r.ok || !r.data.secret) { show('mfaMsg', (r.data && r.data.message) || '获取密钥失败'); return; }
      mfaSecretText = r.data.secret;
      document.getElementById('mfaSecret').textContent = mfaSecretText;
      document.getElementById('mfaSetup').style.display = '';
      document.getElementById('mfaEnableBtn').style.display = 'none';
    });
  };
  window.mfaCancel = function () {
    document.getElementById('mfaSetup').style.display = 'none';
    document.getElementById('mfaEnableBtn').style.display = '';
  };
  window.copySecret = function () {
    if (!mfaSecretText) return;
    if (navigator.clipboard) navigator.clipboard.writeText(mfaSecretText).then(function () { flash('已复制密钥'); });
    else flash('请手动选中复制', true);
  };
  window.mfaConfirm = function () {
    var code = (document.getElementById('mfaCode').value || '').trim();
    if (!code) { show('mfaMsg', '请输入动态码'); return; }
    api('/api/auth/mfa/verify', 'POST', { code: code }).then(function (r) {
      if (r.ok) reloadSoon('MFA 已开启', 700);
      else show('mfaMsg', r.data.message || '验证失败');
    });
  };
  window.mfaDisable = function () {
    var code = (document.getElementById('mfaCodeOff').value || '').trim();
    if (!code) { show('mfaMsg', '请输入当前动态码'); return; }
    api('/api/auth/mfa/disable', 'POST', { code: code }).then(function (r) {
      if (r.ok) reloadSoon('MFA 已关闭', 700);
      else show('mfaMsg', r.data.message || '关闭失败');
    });
  };
  window.exportPackage = function () {
    var pwd = document.getElementById('exportPwd').value || '';
    if (!pwd) { show('exportMsg', '请输入当前密码'); return; }
    var btn = document.getElementById('exportPkgBtn');
    btn.disabled = true; var t0 = btn.textContent; btn.textContent = '正在打包…';
    fetch('/api/admin/export/package', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + token() },
      body: JSON.stringify({ password: pwd })
    }).then(function (resp) {
      btn.disabled = false; btn.textContent = t0;
      if (!resp.ok) {
        return resp.json().catch(function () { return {}; }).then(function (d) { show('exportMsg', d.message || '导出失败'); });
      }
      return resp.blob().then(function (blob) {
        var a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = 'durlinks-demo-export.zip';
        document.body.appendChild(a); a.click(); a.remove();
        setTimeout(function () { URL.revokeObjectURL(a.href); }, 4000);
      });
    }).catch(function () { btn.disabled = false; btn.textContent = t0; });
  };
  window.changePwd = function () {
    var o = document.getElementById('oldPwd').value || '';
    var n1 = document.getElementById('newPwd1').value || '';
    var n2 = document.getElementById('newPwd2').value || '';
    if (!o || !n1) { show('pwdMsg', '请填写完整'); return; }
    if (n1 !== n2) { show('pwdMsg', '两次新密码不一致'); return; }
    if (n1.length < 8) { show('pwdMsg', '新密码至少 8 位'); return; }
    var btn = document.getElementById('pwdBtn');
    btn.disabled = true; btn.textContent = '保存中…';
    api('/api/auth/password', 'POST', { currentPassword: o, newPassword: n1 }).then(function (r) {
      if (r.ok) {
        show('pwdMsg', '密码已修改，请重新登录');
        setTimeout(logout, 1100);
      } else {
        btn.disabled = false; btn.textContent = '保存';
        show('pwdMsg', r.data.message || '修改失败');
      }
    }).catch(function () { btn.disabled = false; btn.textContent = '保存'; });
  };
})();
