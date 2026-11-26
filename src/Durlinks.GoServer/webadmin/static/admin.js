// 管理控制台前端脚本：只做必要的交互（鉴权、行操作、表单保存、侧栏折叠）
(function () {
  'use strict';

  var TOKEN_KEY = 'durlinks_token';
  function token() { return localStorage.getItem(TOKEN_KEY) || ''; }

  function toLogin() {
    try { localStorage.clear(); } catch (e) {}
    location.href = '/';
  }
  if (!token()) { toLogin(); return; }

  function api(path, method, body) {
    return fetch(path, {
      method: method,
      headers: { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + token() },
      body: body === undefined ? null : JSON.stringify(body)
    }).then(function (r) {
      if (r.status === 401) { toLogin(); throw new Error('登录已失效'); }
      return r.json().catch(function () { return {}; }).then(function (data) {
        return { ok: r.ok, status: r.status, data: data };
      });
    });
  }

  function flash(msg, isErr) {
    var bar = document.getElementById('flashBar');
    if (!bar) {
      bar = document.createElement('div');
      bar.id = 'flashBar';
      bar.style.cssText = 'position:fixed;left:50%;transform:translateX(-50%);top:14px;z-index:9999;' +
        'padding:9px 18px;border-radius:8px;font-size:13px;box-shadow:0 4px 16px rgba(0,0,0,.12)';
      document.body.appendChild(bar);
    }
    bar.style.background = isErr ? '#fef2f2' : '#ecfdf5';
    bar.style.color = isErr ? '#b91c1c' : '#047857';
    bar.style.border = '1px solid ' + (isErr ? '#fecaca' : '#a7f3d0');
    bar.textContent = msg;
    clearTimeout(bar._t);
    bar._t = setTimeout(function () { bar.remove(); }, 2600);
  }

  // ---------- 侧栏折叠：状态存 localStorage ----------
  var foldBtn = document.getElementById('sideFold');
  var consoleEl = document.querySelector('.console');
  if (foldBtn && consoleEl) {
    if (localStorage.getItem('durlinks_navfolded') === '1') {
      consoleEl.classList.add('nav-folded');
      foldBtn.textContent = '»';
    }
    foldBtn.addEventListener('click', function () {
      var on = consoleEl.classList.toggle('nav-folded');
      foldBtn.textContent = on ? '»' : '«';
      localStorage.setItem('durlinks_navfolded', on ? '1' : '0');
    });
  }

  // ---------- 表格全选 ----------
  var checkAll = document.getElementById('checkAll');
  if (checkAll) {
    checkAll.addEventListener('change', function () {
      var boxes = document.querySelectorAll('.row-check');
      for (var i = 0; i < boxes.length; i++) boxes[i].checked = checkAll.checked;
    });
  }

  // ---------- 行操作：隐藏 / 删除 / AI 补料 ----------
  document.addEventListener('click', function (e) {
    var b = e.target.closest ? e.target.closest('button') : null;
    if (!b) return;

    if (b.classList.contains('js-hide')) {
      var id = b.getAttribute('data-id');
      var hidden = b.getAttribute('data-hidden') === '1';
      b.disabled = true;
      api('/api/admin/links/' + id + '/hidden', 'PATCH', { hidden: hidden })
        .then(function (r) {
          if (r.ok) { location.reload(); } else { flash(r.data.message || '操作失败', true); b.disabled = false; }
        })
        .catch(function () { b.disabled = false; });
      return;
    }

    if (b.classList.contains('js-del')) {
      var did = b.getAttribute('data-id');
      if (!confirm('确认删除这条链接？（软删除，可恢复）')) return;
      b.disabled = true;
      api('/api/admin/links/' + did, 'DELETE')
        .then(function (r) {
          if (r.ok) { location.reload(); } else { flash(r.data.message || '删除失败', true); b.disabled = false; }
        })
        .catch(function () { b.disabled = false; });
      return;
    }

    if (b.classList.contains('js-enrich')) {
      var eid = b.getAttribute('data-id');
      var old = b.textContent;
      b.disabled = true; b.textContent = '…';
      api('/api/admin/ai/enrich/' + eid, 'POST')
        .then(function (r) {
          b.disabled = false; b.textContent = old;
          if (r.ok) { flash('AI 补料完成，刷新中…'); setTimeout(function () { location.reload(); }, 700); }
          else { flash(r.data.message || 'AI 补料失败', true); }
        })
        .catch(function () { b.disabled = false; b.textContent = old; });
      return;
    }
  });

  // ---------- 编辑表单：走 API 保存（表单是 GET 页面，提交用 fetch 带 JWT） ----------
  var form = document.querySelector('form[action^="/console/link/"]');
  if (form) {
    form.addEventListener('submit', function (ev) {
      ev.preventDefault();
      var id = form.getAttribute('action').split('/').pop();
      var fd = new FormData(form);
      var tags = (fd.get('tags') || '').toString().split(/[;,；，]/).map(function (s) { return s.trim(); }).filter(Boolean);
      var payload = {
        title: fd.get('title') || '',
        url: fd.get('url') || '',
        linkType: fd.get('linkType') || '',
        groupName: fd.get('groupName') || '',
        platform: fd.get('platform') || '',
        version: fd.get('version') || '',
        extractCode: fd.get('extractCode') || '',
        expireAt: fd.get('expireAt') || '',
        remark: fd.get('remark') || '',
        summary: fd.get('summary') || '',
        tags: tags,
        isHidden: fd.get('isHidden') === '1'
      };
      var btn = form.querySelector('button[type=submit]');
      if (btn) { btn.disabled = true; btn.textContent = '保存中…'; }
      api('/api/admin/links/' + id, 'PUT', payload)
        .then(function (r) {
          if (r.ok) {
            flash('已保存');
            setTimeout(function () { location.href = form.querySelector('input[name=back]').value || '/console?view=publish'; }, 500);
          } else {
            flash(r.data.message || '保存失败', true);
            if (btn) { btn.disabled = false; btn.textContent = '保存'; }
          }
        })
        .catch(function () { if (btn) { btn.disabled = false; btn.textContent = '保存'; } });
    });
  }

  // ================= 分组 / 用户 / 大模型 / 账号 =================
  function fresh(path) { return fetch(path, { headers: { 'Authorization': 'Bearer ' + token() } }); }

  // ---------- 分类字典 ----------
  function catAdd() {
    var btn = document.getElementById('catAdd');
    var input = document.getElementById('catNew');
    var name = (input.value || '').trim();
    if (!btn || !name) return;
    api('/api/admin/categories/' + btn.getAttribute('data-kind'), 'POST', { name: name })
      .then(function (r) {
        if (r.ok) { flash('已创建'); setTimeout(function () { location.reload(); }, 500); }
        else { flash(r.data.message || '创建失败', true); }
      });
  }
  var catAddBtn = document.getElementById('catAdd');
  if (catAddBtn) {
    catAddBtn.addEventListener('click', catAdd);
    var catNew = document.getElementById('catNew');
    if (catNew) catNew.addEventListener('keydown', function (e) { if (e.key === 'Enter') catAdd(); });
  }
  document.addEventListener('click', function (e) {
    var b = e.target.closest ? e.target.closest('button') : null;
    if (!b) return;

    if (b.classList.contains('js-cat-rename')) {
      var kind = b.getAttribute('data-kind'), oldName = b.getAttribute('data-name');
      var nn = prompt('重命名「' + oldName + '」为：', oldName);
      if (nn === null) return;
      nn = nn.trim();
      if (!nn || nn === oldName) return;
      api('/api/admin/categories/' + kind, 'PUT', { oldName: oldName, newName: nn })
        .then(function (r) {
          if (r.ok) { flash('已重命名'); setTimeout(function () { location.reload(); }, 500); }
          else { flash(r.data.message || '重命名失败', true); }
        });
      return;
    }

    if (b.classList.contains('js-cat-del')) {
      var k2 = b.getAttribute('data-kind'), n2 = b.getAttribute('data-name');
      if (!confirm('删除「' + n2 + '」？被链接引用的选项会拒绝删除。')) return;
      api('/api/admin/categories/' + k2 + '?name=' + encodeURIComponent(n2), 'DELETE')
        .then(function (r) {
          if (r.ok) { flash('已删除'); setTimeout(function () { location.reload(); }, 500); }
          else { flash(r.data.message || '删除失败', true); }
        });
      return;
    }

    // ---------- 新建管理员 ----------
    if (b.id === 'createUser') {
      var un = (document.getElementById('newUserName') || {}).value || '';
      var pw = (document.getElementById('newUserPwd') || {}).value || '';
      if (!un.trim() || !pw) { flash('用户名与密码都要填', true); return; }
      api('/api/auth/users', 'POST', { username: un.trim(), password: pw })
        .then(function (r) {
          if (r.ok) { flash('已创建'); setTimeout(function () { location.reload(); }, 500); }
          else { flash(r.data.message || '创建失败', true); }
        });
      return;
    }

    // ---------- 大模型 ----------
    if (b.classList.contains('js-llm-test')) {
      var tid = b.getAttribute('data-id');
      b.disabled = true; var t0 = b.textContent; b.textContent = '…';
      api('/api/admin/llm/test', 'POST', { id: Number(tid) })
        .then(function (r) {
          b.disabled = false; b.textContent = t0;
          if (r.ok && r.data.ok) flash('连接成功 ' + r.data.latencyMs + 'ms');
          else flash((r.data && r.data.message) || '连接失败', true);
        })
        .catch(function () { b.disabled = false; b.textContent = t0; });
      return;
    }
    if (b.classList.contains('js-llm-default')) {
      api('/api/admin/llm/configs/' + b.getAttribute('data-id') + '/default', 'POST')
        .then(function (r) {
          if (r.ok) { flash('已设为默认'); setTimeout(function () { location.reload(); }, 500); }
          else { flash(r.data.message || '操作失败', true); }
        });
      return;
    }
    if (b.classList.contains('js-llm-del')) {
      if (!confirm('删除这个提供商配置？')) return;
      api('/api/admin/llm/configs/' + b.getAttribute('data-id'), 'DELETE')
        .then(function (r) {
          if (r.ok) { flash('已删除'); setTimeout(function () { location.reload(); }, 500); }
          else { flash(r.data.message || '删除失败', true); }
        });
      return;
    }
    if (b.classList.contains('js-llm-edit') || b.id === 'llmAdd') {
      var modal = document.getElementById('llmModal');
      if (!modal) return;
      var isEdit = b.classList.contains('js-llm-edit');
      document.getElementById('llmModalTitle').textContent = isEdit ? '编辑提供商' : '添加提供商';
      document.getElementById('llmId').value = isEdit ? b.getAttribute('data-id') : '0';
      document.getElementById('llmName').value = isEdit ? (b.getAttribute('data-name') || '') : '';
      document.getElementById('llmProtocol').value = isEdit ? (b.getAttribute('data-protocol') || 'openai') : 'openai';
      document.getElementById('llmEndpoint').value = isEdit ? (b.getAttribute('data-endpoint') || '') : '';
      document.getElementById('llmModel').value = isEdit ? (b.getAttribute('data-model') || '') : '';
      document.getElementById('llmModels').value = isEdit ? (b.getAttribute('data-models') || '') : '';
      document.getElementById('llmDefault').checked = isEdit ? b.getAttribute('data-default') === '1' : false;
      document.getElementById('llmKey').value = '';
      modal.style.display = 'flex';
      return;
    }
    if (b.id === 'llmClose' || b.id === 'llmCancel') {
      var m2 = document.getElementById('llmModal');
      if (m2) m2.style.display = 'none';
      return;
    }
    if (b.id === 'llmSave') {
      var modelsRaw = (document.getElementById('llmModels').value || '').split(/[,;，；]/)
        .map(function (s) { return s.trim(); }).filter(Boolean);
      var payload = {
        id: Number(document.getElementById('llmId').value || 0),
        name: document.getElementById('llmName').value || '',
        protocol: document.getElementById('llmProtocol').value,
        apiEndpoint: document.getElementById('llmEndpoint').value || '',
        apiKey: document.getElementById('llmKey').value || '',
        model: document.getElementById('llmModel').value || '',
        models: modelsRaw,
        isDefault: document.getElementById('llmDefault').checked,
        retryPolicy: 'default',
        maxRetries: 8,
        retryOn: ['empty', 'rate_limit', 'server', 'timeout', 'network']
      };
      b.disabled = true; b.textContent = '保存中…';
      api('/api/admin/llm/configs', 'POST', payload)
        .then(function (r) {
          if (r.ok) { flash('已保存'); setTimeout(function () { location.reload(); }, 500); }
          else { b.disabled = false; b.textContent = '保存'; flash(r.data.message || '保存失败', true); }
        })
        .catch(function () { b.disabled = false; b.textContent = '保存'; });
      return;
    }

    // ---------- MFA ----------
    if (b.id === 'mfaStart') {
      api('/api/auth/mfa/setup', 'POST')
        .then(function (r) {
          if (r.ok && r.data.secret) {
            document.getElementById('mfaSecret').textContent = r.data.secret;
            document.getElementById('mfaSetupBox').style.display = 'block';
            b.style.display = 'none';
          } else { flash(r.data.message || '获取密钥失败', true); }
        });
      return;
    }
    if (b.id === 'mfaVerify') {
      var code = (document.getElementById('mfaVerifyCode').value || '').trim();
      if (!code) { flash('请输入动态码', true); return; }
      api('/api/auth/mfa/verify', 'POST', { code: code })
        .then(function (r) {
          if (r.ok) { flash('MFA 已开启'); setTimeout(function () { location.reload(); }, 600); }
          else { flash(r.data.message || '验证失败', true); }
        });
      return;
    }
    if (b.id === 'mfaDisable') {
      var dc = (document.getElementById('mfaDisableCode').value || '').trim();
      if (!dc) { flash('请输入当前动态码', true); return; }
      if (!confirm('确定关闭 MFA？')) return;
      api('/api/auth/mfa/disable', 'POST', { code: dc })
        .then(function (r) {
          if (r.ok) { flash('MFA 已关闭'); setTimeout(function () { location.reload(); }, 600); }
          else { flash(r.data.message || '关闭失败', true); }
        });
      return;
    }

    // ---------- 导出数据包（返回二进制，需单独处理） ----------
    if (b.id === 'exportPkg') {
      var pwd = (document.getElementById('exportPwd').value || '');
      if (!pwd) { flash('请输入当前密码', true); return; }
      b.disabled = true; b.textContent = '打包中…';
      fetch('/api/admin/export/package', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + token() },
        body: JSON.stringify({ password: pwd })
      }).then(function (resp) {
        b.disabled = false; b.textContent = '生成并下载';
        if (!resp.ok) {
          return resp.json().catch(function () { return {}; }).then(function (d) {
            flash(d.message || '导出失败', true);
          });
        }
        return resp.blob().then(function (blob) {
          var a = document.createElement('a');
          a.href = URL.createObjectURL(blob);
          a.download = 'durlinks-demo-export.zip';
          document.body.appendChild(a); a.click(); a.remove();
          setTimeout(function () { URL.revokeObjectURL(a.href); }, 4000);
          flash('已开始下载');
        });
      }).catch(function () { b.disabled = false; b.textContent = '生成并下载'; });
      return;
    }

    // ---------- 改密码 ----------
    if (b.id === 'changePwd') {
      var oldP = (document.getElementById('oldPwd').value || '');
      var n1 = (document.getElementById('newPwd').value || '');
      var n2 = (document.getElementById('newPwd2').value || '');
      if (!oldP || !n1) { flash('请填写完整', true); return; }
      if (n1 !== n2) { flash('两次新密码不一致', true); return; }
      if (n1.length < 8) { flash('新密码至少 8 位', true); return; }
      b.disabled = true; b.textContent = '保存中…';
      api('/api/auth/password', 'POST', { currentPassword: oldP, newPassword: n1 })
        .then(function (r) {
          if (r.ok) {
            flash('密码已修改，请重新登录');
            setTimeout(toLogin, 900);
          } else {
            b.disabled = false; b.textContent = '保存';
            flash(r.data.message || '修改失败', true);
          }
        })
        .catch(function () { b.disabled = false; b.textContent = '保存'; });
      return;
    }
  });


  // ---------- 发布库：新建 / 编辑 / 删除 / 渠道切换 ----------
  function libModalOpen(editing) {
    var m = document.getElementById('libModal');
    if (!m) return false;
    document.getElementById('libModalTitle').textContent = editing.id ? '编辑发布库' : '新建发布库';
    document.getElementById('libId').value = editing.id || 0;
    document.getElementById('libName').value = editing.name || '';
    document.getElementById('libDesc').value = editing.desc || '';
    document.getElementById('libSort').value = editing.sort || 0;
    document.getElementById('libWeb').checked = editing.web !== false;
    document.getElementById('libClient').checked = editing.client !== false;
    m.style.display = 'flex';
    return true;
  }

  function libSave() {
    var id = Number(document.getElementById('libId').value || 0);
    var payload = {
      name: (document.getElementById('libName').value || '').trim(),
      description: (document.getElementById('libDesc').value || '').trim(),
      visibleOnWeb: document.getElementById('libWeb').checked,
      visibleOnClient: document.getElementById('libClient').checked,
      sortOrder: Number(document.getElementById('libSort').value || 0)
    };
    if (!payload.name) { flash('名称不能为空', true); return; }
    var btn = document.getElementById('libSave');
    btn.disabled = true; btn.textContent = '保存中…';
    var p = id > 0 ? api('/api/admin/libraries/' + id, 'PUT', payload)
                   : api('/api/admin/libraries', 'POST', payload);
    p.then(function (r) {
      btn.disabled = false; btn.textContent = '保存';
      if (r.ok) { flash('已保存'); setTimeout(function () { location.reload(); }, 500); }
      else { flash(r.data.message || '保存失败', true); }
    }).catch(function () { btn.disabled = false; btn.textContent = '保存'; });
  }

  function toggleChannel(id, side) {
    // 该接口是整对象更新，先取当前值再翻转单个字段
    fetch('/api/admin/libraries', { headers: { 'Authorization': 'Bearer ' + token() } })
      .then(function (r) { return r.json(); })
      .then(function (list) {
        var lib = (list || []).filter(function (x) { return String(x.id) === String(id); })[0];
        if (!lib) { flash('发布库不存在', true); return; }
        var payload = {
          name: lib.name,
          description: lib.description || '',
          visibleOnWeb: side === 'web' ? !lib.visibleOnWeb : lib.visibleOnWeb,
          visibleOnClient: side === 'client' ? !lib.visibleOnClient : lib.visibleOnClient,
          sortOrder: lib.sortOrder || 0
        };
        api('/api/admin/libraries/' + id, 'PUT', payload).then(function (r) {
          if (r.ok) { location.reload(); } else { flash(r.data.message || '切换失败', true); }
        });
      });
  }

  document.addEventListener('click', function (e) {
    var b = e.target.closest ? e.target.closest('button') : null;
    if (!b) return;

    if (b.id === 'libAdd') {
      libModalOpen({});
      return;
    }
    if (b.classList.contains('js-lib-edit')) {
      libModalOpen({
        id: b.getAttribute('data-id'),
        name: b.getAttribute('data-name'),
        desc: b.getAttribute('data-desc'),
        web: b.getAttribute('data-web') === '1',
        client: b.getAttribute('data-client') === '1',
        sort: b.getAttribute('data-sort')
      });
      return;
    }
    if (b.id === 'libClose' || b.id === 'libCancel') {
      document.getElementById('libModal').style.display = 'none';
      return;
    }
    if (b.id === 'libSave') { libSave(); return; }

    if (b.classList.contains('js-lib-del')) {
      var nm = b.getAttribute('data-name');
      if (!confirm('删除发布库「' + nm + '」？该库下的链接会一并软删除，且不可在界面恢复。')) return;
      var pwd = prompt('这是高危操作，请输入当前账号密码确认：');
      if (pwd === null) return;
      api('/api/admin/libraries/' + b.getAttribute('data-id') + '/delete', 'POST', { password: pwd })
        .then(function (r) {
          if (r.ok) { flash('已删除，连带软删除 ' + (r.data.deletedLinks || 0) + ' 条链接'); setTimeout(function () { location.reload(); }, 800); }
          else { flash(r.data.message || '删除失败', true); }
        });
      return;
    }

    if (b.classList.contains('js-lib-ch')) {
      toggleChannel(b.getAttribute('data-id'), b.getAttribute('data-side'));
      return;
    }
  });


  // ================= 批量选择 / 移动复制 / 去重 / 补料 =================
  function selectedIds() {
    var out = [];
    var boxes = document.querySelectorAll('.row-check');
    for (var i = 0; i < boxes.length; i++) if (boxes[i].checked) out.push(Number(boxes[i].value));
    return out;
  }
  function refreshSelCount() {
    var el = document.getElementById('selCount');
    if (el) el.textContent = String(selectedIds().length);
    var all = document.getElementById('checkAll');
    if (all) {
      var boxes = document.querySelectorAll('.row-check');
      var on = 0;
      for (var i = 0; i < boxes.length; i++) if (boxes[i].checked) on++;
      all.checked = boxes.length > 0 && on === boxes.length;
      all.indeterminate = on > 0 && on < boxes.length;
    }
  }
  document.addEventListener('change', function (e) {
    if (e.target && e.target.classList && e.target.classList.contains('row-check')) refreshSelCount();
  });
  refreshSelCount();

  function needSelection() {
    var ids = selectedIds();
    if (!ids.length) { flash('请先勾选链接', true); return null; }
    return ids;
  }

  function afterBatch(msg) {
    flash(msg);
    setTimeout(function () { location.reload(); }, 700);
  }

  document.addEventListener('click', function (e) {
    var b = e.target.closest ? e.target.closest('button') : null;
    if (!b) return;

    if (b.id === 'batchHide' || b.id === 'batchShow') {
      var ids = needSelection(); if (!ids) return;
      var hide = b.id === 'batchHide';
      api('/api/admin/links/batch/hidden', 'PATCH', { ids: ids, hidden: hide })
        .then(function (r) {
          if (r.ok) afterBatch('已' + (hide ? '隐藏' : '显示') + ' ' + (r.data.affected || ids.length) + ' 条');
          else flash(r.data.message || '操作失败', true);
        });
      return;
    }
    if (b.id === 'batchDel') {
      var ids2 = needSelection(); if (!ids2) return;
      if (!confirm('删除选中的 ' + ids2.length + ' 条链接？')) return;
      api('/api/admin/links/batch', 'DELETE', { ids: ids2 })
        .then(function (r) {
          if (r.ok) afterBatch('已删除 ' + (r.data.affected || ids2.length) + ' 条');
          else flash(r.data.message || '删除失败', true);
        });
      return;
    }
    if (b.id === 'moveBtn' || b.id === 'copyBtn') {
      var ids3 = needSelection(); if (!ids3) return;
      var sel = document.getElementById('targetLib');
      var target = sel ? Number(sel.value) : 0;
      if (!target) { flash('请选择目标发布库', true); return; }
      var isMove = b.id === 'moveBtn';
      b.disabled = true;
      api('/api/admin/links/' + (isMove ? 'move' : 'copy'), 'POST', { ids: ids3, targetLibraryId: target })
        .then(function (r) {
          b.disabled = false;
          if (r.ok) afterBatch('已' + (isMove ? '移动' : '复制') + ' ' + (r.data.affected || ids3.length) + ' 条');
          else flash(r.data.message || '操作失败', true);
        })
        .catch(function () { b.disabled = false; });
      return;
    }
    if (b.id === 'dedupeBtn') {
      b.disabled = true;
      var lib = new URLSearchParams(location.search).get('lib');
      api('/api/admin/links/dedupe', 'POST', { libraryId: lib ? Number(lib) : null, dryRun: true })
        .then(function (r) {
          b.disabled = false;
          if (!r.ok) { flash(r.data.message || '去重失败', true); return; }
          var d = r.data;
          if (!d.duplicates) { flash('没有发现重复链接（扫描 ' + d.scanned + ' 条）'); return; }
          var tips = (d.samples || []).slice(0, 3).map(function (x) {
            return '· ' + x.url + '  ×' + x.count;
          }).join('\n');
          if (!confirm('扫描 ' + d.scanned + ' 条，发现 ' + d.groups + ' 组重复，可清理 ' + d.duplicates + ' 条。\n\n' + tips + '\n\n确定清理？')) return;
          api('/api/admin/links/dedupe', 'POST', { libraryId: lib ? Number(lib) : null, dryRun: false })
            .then(function (r2) {
              if (r2.ok) afterBatch('已清理 ' + (r2.data.removed || 0) + ' 条重复');
              else flash(r2.data.message || '去重失败', true);
            });
        })
        .catch(function () { b.disabled = false; });
      return;
    }

    // ---------- 批量 AI 补料 ----------
    if (b.id === 'enrichStart') {
      var lim = Number((document.getElementById('enrichLimit') || {}).value || 0);
      if (!(lim > 0)) lim = 100;
      if (lim > 100000) lim = 100000;
      var mode = (document.getElementById('enrichMode') || {}).value || 'missing';
      if (mode === 'all' && !confirm('「全部」会覆盖已有摘要与标签，确定继续？')) return;
      b.disabled = true;
      api('/api/admin/ai/enrich/batch', 'POST', { mode: mode, limit: lim, concurrency: 3 })
        .then(function (r) {
          if (!r.ok) { b.disabled = false; flash(r.data.message || '启动失败', true); return; }
          pollEnrich();
        })
        .catch(function () { b.disabled = false; });
      return;
    }
    if (b.id === 'enrichCancel') {
      api('/api/admin/ai/enrich/batch/cancel', 'POST').then(function () { pollEnrich(); });
      return;
    }
  });

  var enrichTimer = null;
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
            bar.textContent = s.processed + '/' + s.total + '  ✓' + s.succeeded + '  ✗' + s.failed + '  ⊘' + s.skipped
              + (s.message ? '  ' + s.message : '');
          }
          if (cancelBtn) cancelBtn.style.display = s.running ? '' : 'none';
          if (startBtn) startBtn.disabled = !!s.running;
          if (s.running) {
            enrichTimer = setTimeout(tick, 1200);
          } else {
            if (s.processed > 0) setTimeout(function () { location.reload(); }, 900);
          }
        })
        .catch(function () {
          if (startBtn) startBtn.disabled = false;
        });
    }
    tick();
  }
  // 刷新页面时若服务端仍有任务在跑，恢复进度显示
  if (document.getElementById('enrichProgress')) {
    fetch('/api/admin/ai/enrich/batch/status', { headers: { 'Authorization': 'Bearer ' + token() } })
      .then(function (r) { return r.json(); })
      .then(function (s) { if (s.running) pollEnrich(); })
      .catch(function () {});
  }

  // ================= 导入弹窗 =================
  var importTimer = null;
  function importModal() { return document.getElementById('importModal'); }
  function openImport() {
    var m = importModal();
    if (!m) return;
    m.style.display = 'flex';
    refreshImportStatus();
  }
  function closeImport() {
    var m = importModal();
    if (m) m.style.display = 'none';
    if (importTimer) { clearTimeout(importTimer); importTimer = null; }
  }
  function importMsg(text, isErr) {
    var el = document.getElementById('importMsg');
    if (!el) return;
    if (!text) { el.style.display = 'none'; return; }
    el.textContent = text;
    el.className = 'msg' + (isErr ? ' err' : '');
    el.style.display = 'block';
  }
  function switchImportTab(tab) {
    var tabs = document.querySelectorAll('.import-tab');
    for (var i = 0; i < tabs.length; i++) {
      var on = tabs[i].getAttribute('data-tab') === tab;
      tabs[i].className = 'import-tab' + (on ? ' on' : '');
    }
    var p1 = document.getElementById('tabPaste'), p2 = document.getElementById('tabExcel');
    if (p1) p1.style.display = tab === 'paste' ? '' : 'none';
    if (p2) p2.style.display = tab === 'excel' ? '' : 'none';
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
          if (line) line.textContent = '已扫描 ' + s.processed + ' 行 · 已入库 ' + s.imported + ' 条 · 跳过 ' + s.skipped + ' · 失败 ' + s.failed;
          var bar = document.getElementById('excelBar');
          if (bar) {
            var pct = s.processed > 0 ? Math.min(100, Math.round(s.imported * 100 / Math.max(1, s.processed))) : 0;
            bar.style.width = pct + '%';
          }
          importTimer = setTimeout(refreshImportStatus, 1000);
        } else {
          box.style.display = 'none';
          if (s.message) importMsg(s.message, s.failed > 0);
        }
      })
      .catch(function () {});
  }

  document.addEventListener('click', function (e) {
    var b = e.target.closest ? e.target.closest('button') : null;
    if (!b) return;

    if (b.classList.contains('import-tab')) { switchImportTab(b.getAttribute('data-tab')); return; }
    if (b.id === 'importClose' || b.id === 'importClose2') { closeImport(); return; }

    if (b.id === 'parseBtn') {
      var text = (document.getElementById('pasteText') || {}).value || '';
      if (!text.trim()) { importMsg('请先粘贴链接', true); return; }
      b.disabled = true; var t0 = b.textContent; b.textContent = '识别中…';
      api('/api/admin/import/links/parse', 'POST', { text: text })
        .then(function (r) {
          b.disabled = false; b.textContent = t0;
          var out = document.getElementById('parseOut');
          if (!r.ok) { importMsg(r.data.message || '识别失败', true); return; }
          var arr = r.data || [];
          var recognized = arr.filter(function (x) { return x.recognized; }).length;
          if (out) {
            out.style.display = '';
            out.textContent = arr.map(function (x) {
              return (x.recognized ? '✓ ' : '· ') + x.title + '\n   ' + (x.url || x.rawText)
                + '\n   [' + x.linkType + '] [' + x.groupName + ']'
                + (x.platform ? ' [' + x.platform + ']' : '')
                + (x.tags && x.tags.length ? ' #' + x.tags.join(' #') : '')
                + (x.summary ? '\n   ' + x.summary : '');
            }).join('\n\n');
          }
          importMsg('识别完成：' + arr.length + ' 行，其中 ' + recognized + ' 行由模型识别' + (recognized < arr.length ? '，其余按未识别处理' : ''));
        })
        .catch(function () { b.disabled = false; b.textContent = t0; });
      return;
    }

    if (b.id === 'pasteImportBtn') {
      var txt = (document.getElementById('pasteText') || {}).value || '';
      if (!txt.trim()) { importMsg('请先粘贴链接', true); return; }
      b.disabled = true; var old = b.textContent; b.textContent = '导入中…';
      var lib = new URLSearchParams(location.search).get('lib');
      var url = '/api/admin/import/links' + (lib ? '?libraryId=' + lib : '');
      api('/api/admin/import/links/parse', 'POST', { text: txt })
        .then(function (r) {
          if (!r.ok) throw new Error(r.data.message || '识别失败');
          return api(url, 'POST', r.data);
        })
        .then(function (r2) {
          b.disabled = false; b.textContent = old;
          if (r2.ok) {
            importMsg('已导入 ' + (r2.data.imported || 0) + ' 条，正在刷新…');
            setTimeout(function () { location.href = location.pathname + location.search.replace(/[?&]import=1/, ''); }, 900);
          } else {
            importMsg(r2.data.message || '导入失败', true);
          }
        })
        .catch(function (err) {
          b.disabled = false; b.textContent = old;
          importMsg(err.message || '导入失败', true);
        });
      return;
    }

    if (b.id === 'excelBtn') {
      var input = document.getElementById('excelFile');
      if (!input || !input.files || !input.files.length) { importMsg('请选择 xlsx 文件', true); return; }
      var fd = new FormData();
      fd.append('file', input.files[0]);
      var lib2 = new URLSearchParams(location.search).get('lib');
      var big = input.files[0].size > 10 * 1024 * 1024;
      var ep = '/api/admin/import/excel' + (big ? '/jobs' : '') + (lib2 ? '?libraryId=' + lib2 : '');
      b.disabled = true; var ob = b.textContent; b.textContent = big ? '上传中（后台任务）…' : '上传中…';
      fetch(ep, { method: 'POST', headers: { 'Authorization': 'Bearer ' + token() }, body: fd })
        .then(function (resp) {
          return resp.json().then(function (d) { return { ok: resp.ok, data: d }; });
        })
        .then(function (r) {
          b.disabled = false; b.textContent = ob;
          if (!r.ok) { importMsg(r.data.message || '导入失败', true); return; }
          if (r.data.jobId || r.data.running || r.data.background) {
            importMsg('已提交后台任务，进度如下');
            refreshImportStatus();
          } else {
            importMsg('导入完成：新增 ' + (r.data.imported || 0) + ' 条 · 跳过 ' + (r.data.skipped || 0) + ' · 失败 ' + (r.data.failed || 0));
            setTimeout(function () { location.reload(); }, 1200);
          }
        })
        .catch(function () { b.disabled = false; b.textContent = ob; importMsg('上传失败', true); });
      return;
    }

    if (b.id === 'excelCancel') {
      api('/api/admin/import/excel/jobs/cancel', 'POST').then(function () { refreshImportStatus(); });
      return;
    }
  });

  // ?import=1 自动打开导入弹窗
  if (new URLSearchParams(location.search).get('import') === '1') openImport();

  // 分类列表本地筛选
  window.filterCat = function (kw) {
    var rows = document.querySelectorAll('#catTable tbody tr');
    var q = (kw || '').toLowerCase();
    for (var i = 0; i < rows.length; i++) {
      var name = (rows[i].getAttribute('data-name') || '').toLowerCase();
      rows[i].style.display = (!q || name.indexOf(q) >= 0) ? '' : 'none';
    }
  };

})();
