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
})();
