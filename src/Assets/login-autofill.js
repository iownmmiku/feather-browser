// 轻羽浏览器：页面内的登录表单辅助脚本（由 LoginAutofill 注入）。

function featherVisible(el) {
  if (!el || el.disabled || el.readOnly) return false;
  var r = el.getBoundingClientRect();
  if (r.width < 2 || r.height < 2) return false;
  var s = getComputedStyle(el);
  return s.display !== 'none' && s.visibility !== 'hidden' && s.opacity !== '0';
}

function featherPwFields() {
  return Array.prototype.filter.call(
    document.querySelectorAll('input[type=password]'), featherVisible);
}

function featherUserField(pw) {
  var scope = pw.form || document;
  var list = Array.prototype.slice.call(scope.querySelectorAll('input'));
  var idx = list.indexOf(pw);
  var start = idx >= 0 ? idx - 1 : list.length - 1;
  for (var i = start; i >= 0; i--) {
    var el = list[i];
    if (el === pw || !featherVisible(el)) continue;
    var t = (el.type || 'text').toLowerCase();
    if (t === 'hidden' || t === 'submit' || t === 'button' ||
        t === 'checkbox' || t === 'radio' || t === 'password') continue;
    if (t === 'email' || t === 'text' || t === 'tel') return el;
  }
  return document.querySelector(
    'input[type=email],input[name*=user i],input[name*=email i],input[id*=user i],input[type=text]');
}

function featherSet(el, value) {
  if (!el) return;
  try {
    el.focus();
    el.value = value;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  } catch (e) { }
}

function featherPost(obj) {
  try {
    CefSharp.PostMessage('feather:' + JSON.stringify(obj));
  } catch (e) { }
}

var featherBox = String();

function featherHide() {
  if (featherBox && featherBox.parentNode) featherBox.parentNode.removeChild(featherBox);
  featherBox = String();
}

function featherShow(anchor) {
  featherHide();
  var pw = featherPwFields()[0];
  if (!pw) return;

  featherBox = document.createElement('div');
  featherBox.setAttribute('data-feather', '1');
  featherBox.style.cssText =
    'position:fixed;z-index:2147483647;background:#ffffff;color:#1c2026;' +
    'border:1px solid #dfe3ea;border-radius:10px;box-shadow:0 8px 28px rgba(20,40,80,.22);' +
    'font:13px/1.5 Microsoft YaHei,sans-serif;padding:5px;min-width:200px;max-width:340px';

  var title = document.createElement('div');
  title.textContent = '轻羽已保存的账号';
  title.style.cssText = 'font-size:11px;color:#8b93a2;padding:3px 9px 5px';
  featherBox.appendChild(title);

  ACCOUNTS.forEach(function (a) {
    var row = document.createElement('div');
    row.textContent = a.u ? a.u : '(无用户名)';
    row.style.cssText =
      'padding:7px 10px;border-radius:7px;cursor:pointer;white-space:nowrap;' +
      'overflow:hidden;text-overflow:ellipsis';
    row.onmouseenter = function () { row.style.background = '#e8efff'; };
    row.onmouseleave = function () { row.style.background = 'transparent'; };
    row.addEventListener('mousedown', function (e) {
      if (!(e instanceof MouseEvent) || !e.isTrusted || e.button !== 0) return;
      e.preventDefault();
      e.stopPropagation();
      featherPost({ type: 'pick', id: a.id, token: PICK_TOKEN });
      featherHide();
    });
    featherBox.appendChild(row);
  });

  var rect = anchor.getBoundingClientRect();
  featherBox.style.left = Math.max(4, rect.left) + 'px';
  featherBox.style.top = (rect.bottom + 4) + 'px';
  document.documentElement.appendChild(featherBox);

  var boxRect = featherBox.getBoundingClientRect();
  if (boxRect.bottom > window.innerHeight - 4) {
    featherBox.style.top = Math.max(4, rect.top - boxRect.height - 4) + 'px';
  }
}

// 宿主填密码的唯一入口，由用户在账号下拉里点选后触发
window.__featherFillPassword = function (user, password) {
  var pw = featherPwFields()[0];
  if (!pw) return false;
  featherSet(featherUserField(pw), user);
  featherSet(pw, password);
  return true;
};

document.addEventListener('focusin', function (e) {
  var el = e.target;
  if (!el || el.tagName !== 'INPUT') return;
  if (!ACCOUNTS.length) return;

  var pw = featherPwFields()[0];
  if (!pw) return;

  var hit = (el === pw);
  if (!hit && el === featherUserField(pw)) hit = true;
  if (!hit && (el.type === 'email' || el.type === 'text') && pw) hit = true;

  if (hit) featherShow(el); else featherHide();
}, true);

document.addEventListener('mousedown', function (e) {
  if (featherBox && !featherBox.contains(e.target)) featherHide();
}, true);

document.addEventListener('keydown', function (e) {
  if (e.key === 'Escape') featherHide();
}, true);

// 提交登录时把用户当前输入的值发回宿主，用于询问是否保存
var featherLastSubmit = 0;
document.addEventListener('submit', function (e) {
  var form = e.target;
  if (!form || form.tagName !== 'FORM') return;
  var pw = Array.prototype.find.call(form.querySelectorAll('input[type=password]'), featherVisible);
  if (!pw || !pw.value || Date.now() - featherLastSubmit < 1000) return;
  featherLastSubmit = Date.now();
  var user = featherUserField(pw);
  featherPost({ type: 'submit', username: user ? user.value : String(), password: pw.value });
}, true);
