/* web-lib.js - in-page helper for the cu web layer (loaded by web.cs, injected before every DOM command).
   Installs window.__cu once per document (versioned). Plain ES5 so it also runs in old embedded pages.
   It is embedded as a JSON string by web.cs, so any quoting style is fine. */
(function () {
  var VERSION = 21;
  if (window.__cu && window.__cu.v === VERSION) return;
  var cu = { v: VERSION, els: null, last: null, lastInput: null };
  var norm = function (t) { return String(t == null ? '' : t).replace(/[\u200b-\u200d\ufeff]/g, '').replace(/\s+/g, ' ').trim(); };
  cu.norm = norm;
  var INTER = 'a[href],a[onclick],button,input,select,textarea,summary,label,[role=button],[role=link],[role=tab],[role=menuitem],[role=menuitemcheckbox],[role=menuitemradio],[role=option],[role=checkbox],[role=radio],[role=switch],[role=textbox],[role=combobox],[role=searchbox],[role=treeitem],[role=gridcell],[contenteditable]:not([contenteditable=false]),[onclick],[tabindex]:not([tabindex="-1"])';
  var SKIP = { script: 1, style: 1, noscript: 1, template: 1, head: 1, title: 1, meta: 1, link: 1 };

  cu.tag = function (el) { return el && el.tagName ? String(el.tagName).toLowerCase() : ''; };
  cu.visible = function (el) {
    if (!el || el.nodeType !== 1) return false;
    try { if (el.checkVisibility && !el.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true })) return false; } catch (e) { }
    var r = el.getBoundingClientRect();
    if (r.width > 0 || r.height > 0) return true;
    return el.getClientRects().length > 0;
  };
  // rect in top-document viewport CSS px (adds the offsets of enclosing same-origin iframes)
  cu.rect = function (el) {
    var r = el.getBoundingClientRect(), ox = 0, oy = 0, d = el.ownerDocument, guard = 0;
    while (d && d !== document && guard++ < 8) {
      var fe = null; try { fe = d.defaultView && d.defaultView.frameElement; } catch (e) { fe = null; }
      if (!fe) break;
      var fr = fe.getBoundingClientRect(); ox += fr.left; oy += fr.top; d = fe.ownerDocument;
    }
    return { x: r.left + ox, y: r.top + oy, w: r.width, h: r.height };
  };
  cu.isInter = function (el) { try { return !!(el && el.nodeType === 1 && el.matches(INTER)); } catch (e) { return false; } };
  cu.parent = function (el) { return el.parentElement || (el.parentNode && el.parentNode.host) || null; };
  // the element a person would consider clicked: nearest interactive ancestor (max 5 levels), else the element itself
  cu.clickable = function (el) {
    var e = el, n = 0;
    while (e && e.nodeType === 1 && n < 6) {
      if (cu.isInter(e)) return e;
      try { if (n > 0 && getComputedStyle(e).cursor === 'pointer' && !cu.isInter(cu.parent(e))) return e; } catch (x) { }
      e = cu.parent(e); n++;
    }
    return el;
  };
  // attribute / association based name (what a screen reader would say when there is no visible text)
  cu.label = function (el) {
    var parts = [];
    var a = function (n) { var v = null; try { v = el.getAttribute(n); } catch (e) { } if (v) parts.push(v); };
    a('aria-label'); a('title'); a('placeholder'); a('alt');
    var lb = null; try { lb = el.getAttribute('aria-labelledby'); } catch (e) { }
    if (lb) lb.split(/\s+/).forEach(function (id) { var r = el.ownerDocument.getElementById(id); if (r) parts.push(r.textContent); });
    var tag = cu.tag(el);
    if (tag === 'input') {
      var ty = String(el.type || '').toLowerCase();
      if (ty === 'submit' || ty === 'button' || ty === 'reset' || ty === 'image') parts.push(el.value || '');
    }
    try { if (el.labels && el.labels.length) for (var i = 0; i < el.labels.length; i++) parts.push(el.labels[i].textContent); } catch (e) { }
    if (tag === 'button' || tag === 'a' || cu.isInter(el)) {
      var img = null; try { img = el.querySelector('img[alt],svg title'); } catch (e) { }
      if (img) parts.push(img.getAttribute ? (img.getAttribute('alt') || img.textContent) : img.textContent);
    }
    return norm(parts.join(' '));
  };
  cu.textOf = function (el, cache) {
    var t = cache.get(el);
    if (t === undefined) { t = norm(el.textContent); cache.set(el, t); }
    return t;
  };
  cu.roots = function (el) {   // extra roots to descend into: open shadow root, same-origin iframe document
    var out = [];
    try { if (el.shadowRoot) out.push(el.shadowRoot); } catch (e) { }
    if (cu.tag(el) === 'iframe' || cu.tag(el) === 'frame') { try { if (el.contentDocument) out.push(el.contentDocument); } catch (e) { } }
    return out;
  };
  // depth-first text search with subtree pruning: a subtree is only entered when its text contains the needle
  cu.findText = function (root, needle, exact, out, cache, depth) {
    if (depth > 60 || out.length > 400) return;
    var lo = needle.toLowerCase();
    var kids = root.children || [];
    for (var i = 0; i < kids.length; i++) {
      var el = kids[i], tag = cu.tag(el);
      if (SKIP[tag]) continue;
      if (el.id === '__cu_marks') continue;
      var extra = cu.roots(el);
      var t = cu.textOf(el, cache);
      var has = t.toLowerCase().indexOf(lo) >= 0;
      if (has) {
        var deeper = false, ks = el.children || [];
        for (var k = 0; k < ks.length; k++) { var kt = cu.textOf(ks[k], cache); if (kt && kt.toLowerCase().indexOf(lo) >= 0) { deeper = true; break; } }
        if (deeper) cu.findText(el, needle, exact, out, cache, depth + 1);
        else if (!exact || t === needle || t.toLowerCase() === lo) out.push({ el: el, t: t, attr: false });
      }
      for (var x = 0; x < extra.length; x++) cu.findText(extra[x], needle, exact, out, cache, depth + 1);
    }
  };
  cu.findAttr = function (root, needle, exact, out, depth) {
    if (depth > 8) return;
    var lo = needle.toLowerCase(), list = [];
    try { list = root.querySelectorAll('[aria-label],[title],[placeholder],[alt],[aria-labelledby],input[type=submit],input[type=button],input[type=reset],input[type=image],input,textarea,select'); } catch (e) { }
    for (var i = 0; i < list.length; i++) {
      var el = list[i], lab = cu.label(el);
      if (!lab) continue;
      var ll = lab.toLowerCase();
      if (exact ? (lab === needle || ll === lo) : ll.indexOf(lo) >= 0) out.push({ el: el, t: lab, attr: true });
    }
    var fr = [];
    try { fr = root.querySelectorAll('iframe,frame'); } catch (e) { }
    for (var f = 0; f < fr.length; f++) { try { if (fr[f].contentDocument) cu.findAttr(fr[f].contentDocument, needle, exact, out, depth + 1); } catch (e) { } }
  };
  cu.area = function (el) { var r = el.getBoundingClientRect(); return Math.max(1, r.width * r.height); };
  cu.score = function (c, needle) {
    var n = needle.toLowerCase(), t = c.t.toLowerCase(), s;
    if (c.t === needle) s = 0; else if (t === n) s = 1; else if (t.indexOf(n) === 0) s = 2; else s = 3;
    if (c.attr) s += 0.5;
    var tgt = cu.clickable(c.el);
    if (!cu.isInter(tgt)) { s += 2; try { if (getComputedStyle(tgt).cursor === 'pointer') s -= 1; } catch (e) { } }
    if (!cu.visible(c.el)) s += 20;
    var li = cu.lastInput;
    if (li && li.isConnected && li !== tgt) {
      var lf = li.form || null, tf = tgt.form || null;
      if (lf && tf && lf === tf) s -= 1.5;
      else { var a1 = cu.rect(li), b1 = cu.rect(tgt); var dx = Math.max(0, Math.max(a1.x, b1.x) - Math.min(a1.x + a1.w, b1.x + b1.w)), dy = Math.max(0, Math.max(a1.y, b1.y) - Math.min(a1.y + a1.h, b1.y + b1.h)); if (Math.sqrt(dx * dx + dy * dy) < 300) s -= 0.75; }
    }
    var r = cu.rect(c.el);
    if (r.y + r.h < 0 || r.y > innerHeight || r.x + r.w < 0 || r.x > innerWidth) s += 0.25;
    return s;
  };
  cu.describe = function (el, t) {
    var cn = el.className; cn = (cn && cn.baseVal !== undefined) ? cn.baseVal : String(cn || '');
    var r = cu.rect(el);
    return { tag: cu.tag(el), id: String(el.id || ''), cls: cn.slice(0, 80), text: (t || norm(el.innerText) || norm(el.textContent) || cu.label(el)).slice(0, 160),
      rect: { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.w), h: Math.round(r.h) }, cx: Math.round(r.x + r.w / 2), cy: Math.round(r.y + r.h / 2) };
  };
  // q: {sel, text, exact, index, id, scope, raw}. raw=true returns the matched element itself instead of its clickable ancestor
  cu.locate = function (q) {
    var idx = q.index > 0 ? q.index : 1, el = null, count = 0, alts = [];
    if (q.id > 0) {
      if (!cu.els) return { ok: false, err: 'ERR_NO_MARKS', msg: 'run web els / web shot -Marks first' };
      el = cu.els[q.id - 1];
      if (!el) return { ok: false, err: 'ERR_NO_ELEMENT', msg: 'no element #' + q.id + ' (have ' + cu.els.length + ')' };
      if (!el.isConnected) return { ok: false, err: 'ERR_STALE', msg: 'element #' + q.id + ' left the page - run web els again' };
      count = 1;
    } else if (q.sel) {
      var list;
      try { list = document.querySelectorAll(q.sel); } catch (e) { return { ok: false, err: 'ERR_SEL', msg: String(e.message) }; }
      var vis = [];
      for (var i = 0; i < list.length; i++) if (cu.visible(list[i])) vis.push(list[i]);
      var pool = vis.length ? vis : Array.prototype.slice.call(list);
      count = pool.length;
      if (!count) return { ok: false, err: 'ERR_NOT_FOUND', msg: 'selector not found: ' + q.sel };
      if (idx > count) return { ok: false, err: 'ERR_INDEX', msg: 'only ' + count + ' match(es) for ' + q.sel, count: count };
      el = pool[idx - 1];
    } else if (q.text) {
      var needle = norm(q.text), cache = new Map(), out = [];
      var scope = document;
      if (q.scope) { try { scope = document.querySelector(q.scope) || document; } catch (e) { } }
      cu.findText(scope, needle, !!q.exact, out, cache, 0);
      cu.findAttr(scope, needle, !!q.exact, out, 0);
      if (!out.length) return { ok: false, err: 'ERR_TEXT_NOT_FOUND', msg: 'text not found: ' + needle };
      var seen = new Set(), uniq = [];
      for (var u = 0; u < out.length; u++) { var key = q.raw ? out[u].el : cu.clickable(out[u].el); if (seen.has(key)) continue; seen.add(key); uniq.push({ el: out[u].el, tgt: key, t: out[u].t, attr: out[u].attr, ord: u }); }
      for (var s = 0; s < uniq.length; s++) uniq[s].sc = cu.score(uniq[s], needle);
      uniq.sort(function (a, b) { return (a.sc - b.sc) || (a.t.length - b.t.length) || (cu.area(a.tgt) - cu.area(b.tgt)) || (a.ord - b.ord); });
      count = uniq.length;
      if (idx > count) return { ok: false, err: 'ERR_INDEX', msg: 'only ' + count + ' match(es) for ' + needle, count: count };
      el = uniq[idx - 1].tgt;
      for (var a2 = 0; a2 < uniq.length && alts.length < 4; a2++) { if (a2 === idx - 1) continue; var d2 = cu.describe(uniq[a2].tgt, uniq[a2].t); alts.push(d2.tag + (d2.id ? '#' + d2.id : '') + ' ' + JSON.stringify(d2.text.slice(0, 40))); }
    } else return { ok: false, err: 'ERR_ARGS', msg: 'need -Sel, -Text or -Id' };
    cu.last = el;
    var res = { ok: true, el: el, count: count };
    if (alts.length) res.alts = alts;
    return res;
  };
  // bring into view and pick a click point that really hits the element (center, then 4 inner offsets)
  cu.point = function (el) {
    var r = el.getBoundingClientRect();
    if (r.top < 0 || r.bottom > innerHeight || r.left < 0 || r.right > innerWidth || r.width === 0) {
      try { el.scrollIntoView({ block: 'center', inline: 'center' }); } catch (e) { try { el.scrollIntoView(); } catch (x) { } }
    }
    var doc = el.ownerDocument, top = cu.rect(el), lr = el.getBoundingClientRect();
    var pts = [[0.5, 0.5], [0.3, 0.5], [0.7, 0.5], [0.5, 0.3], [0.5, 0.7]], hit = null, hitEl = null, first = null;
    for (var i = 0; i < pts.length; i++) {
      var lx = lr.left + lr.width * pts[i][0], ly = lr.top + lr.height * pts[i][1];
      var h = null; try { h = doc.elementFromPoint(lx, ly); } catch (e) { h = null; }
      if (i === 0) first = h;
      if (h && (h === el || el.contains(h) || h.contains(el) || (h.shadowRoot && h.shadowRoot.contains(el)))) { hit = pts[i]; hitEl = h; break; }
    }
    var f = hit || pts[0];
    var out = { x: top.x + top.w * f[0], y: top.y + top.h * f[1], hit: !!hit };
    cu.lastRect = top;
    if (!hit && first) { var fd = cu.describe(first); out.cover = fd.tag + (fd.id ? '#' + fd.id : '') + (fd.cls ? '.' + fd.cls.split(' ')[0] : ''); }
    return out;
  };
  // post-dispatch check for a real mouse click: is the element still the one that a click at the point hits?
  // present=false -> element gone (navigation/removal: the click almost certainly worked)
  // moved -> the layout shifted since locate (a single retry is safe); offscreen -> do NOT auto-retry
  cu.check = function (el) {
    if (!el || el.nodeType !== 1) return { present: false, hit: false };
    var lr = el.getBoundingClientRect();
    if (lr.width === 0 && lr.height === 0) return { present: false, hit: false };
    var lx = lr.left + lr.width / 2, ly = lr.top + lr.height / 2;
    var off = lr.bottom <= 0 || lr.top >= innerHeight || lr.right <= 0 || lr.left >= innerWidth;
    var h = null; try { h = el.ownerDocument.elementFromPoint(lx, ly); } catch (e) { h = null; }
    var same = !!(h && (h === el || el.contains(h) || h.contains(el) || (h.shadowRoot && h.shadowRoot.contains(el))));
    var tr = cu.rect(el), lr0 = cu.lastRect;
    var moved = !lr0 || Math.abs(tr.x - lr0.x) > 2 || Math.abs(tr.y - lr0.y) > 2 || Math.abs(tr.w - lr0.w) > 2 || Math.abs(tr.h - lr0.h) > 2;
    var res = { present: true, hit: same, moved: moved, offscreen: off };
    if (!same && h) { var fd = cu.describe(h); res.cover = fd.tag + (fd.id ? '#' + fd.id : '') + (fd.cls ? '.' + fd.cls.split(' ')[0] : ''); }
    return res;
  };
  // list interactive elements (numbered; ids stay valid while the page does not change). vp=true keeps only what intersects the viewport
  cu.collect = function (scopeSel, all, max, vp) {
    var scope = document;
    if (scopeSel) { try { scope = document.querySelector(scopeSel); } catch (e) { scope = null; } if (!scope) return null; }
    var nodes = [], seen = new Set();
    var grab = function (root, depth) {
      if (depth > 6) return;
      var l = []; try { l = root.querySelectorAll(INTER + (all ? ',[role],li,td,th,dt,dd,option' : '')); } catch (e) { }
      for (var i = 0; i < l.length; i++) {
        var e = l[i]; if (seen.has(e)) continue; seen.add(e);
        if (!cu.visible(e)) continue;
        if (vp) { var vr = cu.rect(e); if (vr.x + vr.w <= 0 || vr.y + vr.h <= 0 || vr.x >= innerWidth || vr.y >= innerHeight) continue; }
        nodes.push(e);
      }
      var fr = []; try { fr = root.querySelectorAll('iframe,frame'); } catch (e) { }
      for (var f = 0; f < fr.length; f++) { try { if (fr[f].contentDocument) grab(fr[f].contentDocument, depth + 1); } catch (e) { } }
      var sh = []; try { sh = root.querySelectorAll('*'); } catch (e) { }
      for (var s = 0; s < sh.length && s < 5000; s++) { if (sh[s].shadowRoot) grab(sh[s].shadowRoot, depth + 1); }
    };
    grab(scope, 0);
    nodes.sort(function (a, b) { var ra = cu.rect(a), rb = cu.rect(b); return (Math.round(ra.y / 8) - Math.round(rb.y / 8)) || (ra.x - rb.x); });
    if (nodes.length > max) nodes = nodes.slice(0, max);
    cu.els = nodes;
    var out = [];
    for (var n = 0; n < nodes.length; n++) {
      var el = nodes[n], r = cu.rect(el), tag = cu.tag(el), kind = tag;
      if (tag === 'input') kind = 'input:' + String(el.type || 'text').toLowerCase();
      else { var role = null; try { role = el.getAttribute('role'); } catch (e) { } if (role) kind = tag + ':' + role; }
      var txt = '';
      if (tag === 'input' || tag === 'textarea' || tag === 'select') txt = cu.label(el) || (tag === 'select' && el.options && el.options[el.selectedIndex] ? norm(el.options[el.selectedIndex].text) : '') || norm(el.value);
      else txt = norm(el.innerText) || norm(el.textContent) || cu.label(el);
      out.push([n + 1, kind, txt.slice(0, 60), Math.round(r.x), Math.round(r.y), Math.round(r.w), Math.round(r.h)]);
    }
    return out;
  };
  cu.mark = function (on) {
    var old = document.getElementById('__cu_marks');
    if (old) old.parentNode.removeChild(old);
    if (!on || !cu.els) return 0;
    var box = document.createElement('div');
    box.id = '__cu_marks';
    box.setAttribute('style', 'position:absolute;left:0;top:0;width:0;height:0;pointer-events:none;z-index:2147483647;font:bold 11px/1 Arial,sans-serif;');
    var sx = window.scrollX || 0, sy = window.scrollY || 0, n = 0;
    for (var i = 0; i < cu.els.length; i++) {
      var r = cu.rect(cu.els[i]);
      if (r.w <= 0 && r.h <= 0) continue;
      var d = document.createElement('div');
      d.setAttribute('style', 'position:absolute;left:' + (r.x + sx) + 'px;top:' + (r.y + sy) + 'px;width:' + Math.max(1, r.w) + 'px;height:' + Math.max(1, r.h) + 'px;border:1.5px solid #e6007e;box-sizing:border-box;');
      var b = document.createElement('div');
      b.textContent = String(i + 1);
      b.setAttribute('style', 'position:absolute;left:-1px;top:-14px;background:#e6007e;color:#fff;padding:1px 3px;border-radius:2px;white-space:nowrap;');
      d.appendChild(b); box.appendChild(d); n++;
    }
    (document.body || document.documentElement).appendChild(box);
    return n;
  };
  // value / text setters that frameworks notice
  cu.setValue = function (el, text, append) {
    var tag = cu.tag(el);
    var cur = (el.value !== undefined && el.value !== null) ? String(el.value) : '';
    var next = append ? cur + text : text;
    var proto = tag === 'input' ? HTMLInputElement.prototype : tag === 'textarea' ? HTMLTextAreaElement.prototype : tag === 'select' ? HTMLSelectElement.prototype : null;
    var desc = proto ? Object.getOwnPropertyDescriptor(proto, 'value') : null;
    if (tag === 'select') {
      var opts = el.options || [], picked = false, want = norm(text);
      for (var i = 0; i < opts.length; i++) if (opts[i].value === text || norm(opts[i].text) === want) { if (desc && desc.set) desc.set.call(el, opts[i].value); else el.value = opts[i].value; picked = true; break; }
      if (!picked) for (var j = 0; j < opts.length; j++) if (norm(opts[j].text).toLowerCase().indexOf(want.toLowerCase()) >= 0) { if (desc && desc.set) desc.set.call(el, opts[j].value); else el.value = opts[j].value; picked = true; break; }
      if (!picked) return { ok: false, err: 'ERR_NOT_FOUND', msg: 'no matching <option>: ' + text };
      el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true }));
      return { ok: true, mode: 'select', val: String(el.value) };
    }
    if (desc && desc.set) desc.set.call(el, next); else if ('value' in el) el.value = next; else return { ok: false, err: 'ERR_TYPE', msg: 'cannot set value on <' + tag + '>' };
    var ev; try { ev = new InputEvent('input', { bubbles: true, inputType: 'insertText', data: text }); } catch (e) { ev = new Event('input', { bubbles: true }); }
    el.dispatchEvent(ev);
    el.dispatchEvent(new Event('change', { bubbles: true }));
    try { var last = text.slice(-1); el.dispatchEvent(new KeyboardEvent('keyup', { bubbles: true, key: last })); } catch (e) { }
    return { ok: true, mode: 'value', val: String(el.value) };
  };
  cu.readValue = function (el) {
    if (el.value !== undefined && el.value !== null && cu.tag(el) !== 'div') return String(el.value);
    return String(el.isContentEditable ? (el.innerText || el.textContent) : (el.value !== undefined ? el.value : (el.innerText || el.textContent || '')));
  };
  // ---- bot-check / risk-control page detection (report only; never attempt to defeat the check) ----------
  cu.wall = function () {
    try {
      var t = String(document.title || ''), h = String(location.href || '');
      var b = document.body ? String(document.body.innerText || '').slice(0, 600) : '';
      // content signals first: a real interstitial says so in its title/body/DOM
      if (document.querySelector('#challenge-form,#cf-chl-widget,.cf-turnstile,form[action*="challenge"],#captcha-form,input[name="captcha"],iframe[src*="challenges.cloudflare.com"]')) return 'cloudflare';
      if (/请稍候|Just a moment|Attention Required|安全验证|Verify you are human|Checking your browser|Are you a robot|人机验证|机器人验证/i.test(t)) return 'cloudflare';
      if (/正在进行安全验证|Verify you are human|Checking your browser|cf-error-details|人机验证|安全验证|机器人/i.test(b)) return 'cloudflare';
      // URL only as a last resort, limited to unmistakable risk-control endpoints
      if (/risk_handler|\/captcha|\/verify_?human|captcha\.|challenges\.cloudflare\.com/i.test(h)) return 'risk';
      // login wall: a sign-in endpoint plus either a password field or unmistakable sign-in wording (zhihu's
      // sign-in page has no password input by default - it opens on SMS-code login, so the text is the signal)
      if (/(^|[\/.])(login|signin|sign-?in|logon|passport)([\/.?&=#]|$)/i.test(h)) {
        if (document.querySelector('input[type=password]')) return 'login';
        if (/(密码|验证码|扫码登录|忘记密码|sign in|sign-in|log ?in|forgot password)/i.test(b)) return 'login';
      }
      return '';
    } catch (e) { return ''; }
  };
  // ---- Enter helpers ------------------------------------------------------------------------------------
  // An Enter keydown carries text "\r"; when the page's submit handler is not attached yet (or the box is a
  // textarea) the character lands in the value and nothing is submitted. Detect exactly that, drop the stray
  // newline from the input and hand back a click point on the form's own submit button (what a person would do).
  cu.enterSwallowed = function () {
    var el = cu.last; if (!el) return false;
    return /[\r\n]$/.test(cu.readValue(el));
  };
  cu.enterFallback = function () {
    var el = cu.last; if (!el) return { ok: false, err: 'ERR_STALE' };
    var f = el.form || (el.closest ? el.closest('form') : null);
    var b = f ? f.querySelector('input[type=submit],button[type=submit],input[type=image]') : null;
    if (!b) return { ok: false, reason: 'no-submit-control' };   // no form/submit: leave the value untouched
    var v = cu.readValue(el);
    if (/[\r\n]$/.test(v)) cu.setValue(el, v.replace(/[\r\n]+$/, ''), false);   // strip on the INPUT, before cu.last moves
    cu.last = b;
    var p = cu.point(b);
    p.ok = true; p.tag = cu.tag(b); p.id = String(b.id || '');
    return p;
  };
  // prepare an element for typing: focus, optionally select everything (so Input.insertText replaces)
  cu.focusFor = function (el, selectAll) {
    cu.lastInput = el;
    try { el.focus({ preventScroll: false }); } catch (e) { try { el.focus(); } catch (x) { } }
    if (selectAll) {
      try {
        if (el.select && (cu.tag(el) === 'input' || cu.tag(el) === 'textarea')) el.select();
        else if (el.isContentEditable) { var rg = el.ownerDocument.createRange(); rg.selectNodeContents(el); var sl = el.ownerDocument.getSelection(); sl.removeAllRanges(); sl.addRange(rg); }
      } catch (e) { }
    } else if (el.isContentEditable) {
      try { var r2 = el.ownerDocument.createRange(); r2.selectNodeContents(el); r2.collapse(false); var s2 = el.ownerDocument.getSelection(); s2.removeAllRanges(); s2.addRange(r2); } catch (e) { }
    } else if (cu.tag(el) === 'input' || cu.tag(el) === 'textarea') {
      try { var n = String(el.value || '').length; el.setSelectionRange(n, n); } catch (e) { }   // append: caret at the end
    }
    return el.ownerDocument.activeElement === el || (el.getRootNode && el.getRootNode().activeElement === el);
  };
  // wait inside the page: resolves as soon as the condition holds (MutationObserver + 100ms poll), or after timeout
  cu.waitFor = function (cond, timeoutMs) {
    return new Promise(function (resolve) {
      var done = false, t0 = Date.now(), mo = null, timer = null, poll = null;
      var finish = function (ok, extra) { if (done) return; done = true; try { if (mo) mo.disconnect(); } catch (e) { } clearTimeout(timer); clearInterval(poll); var o = { ok: ok, waited: Date.now() - t0 }; if (extra) for (var k in extra) o[k] = extra[k]; resolve(o); };
      var check = function () { if (done) return; var r; try { r = cond(); } catch (e) { r = { err: String(e.message) }; } if (r === true) finish(true); else if (r && r.err) finish(false, { err: 'ERR_SEL', msg: r.err }); };
      check(); if (done) return;
      try { mo = new MutationObserver(function () { check(); }); mo.observe(document.documentElement || document, { childList: true, subtree: true, attributes: true, characterData: true }); } catch (e) { }
      poll = setInterval(check, 100);
      timer = setTimeout(function () { finish(false, { timeout: true }); }, timeoutMs);
    });
  };
  cu.condition = function (q) {
    var lastLen = -1, stableSince = 0;
    return function () {
      if (q.ready && document.readyState !== 'complete') return false;
      if (q.sel) { var e; try { e = document.querySelector(q.sel); } catch (x) { return { err: String(x.message) }; } if (!e || !cu.visible(e)) return false; }
      if (q.text) { var t = (document.body ? document.body.innerText : '') + '\n' + (document.documentElement ? document.documentElement.textContent : ''); if (t.indexOf(q.text) < 0) return false; }
      if (q.gone) { var g; try { g = document.querySelector(q.gone); } catch (x2) { return { err: String(x2.message) }; } if (g && cu.visible(g)) return false; }
      if (q.url && location.href.indexOf(q.url) < 0) return false;
      if (q.stable) { var len = document.body ? document.body.innerText.length : 0; var now = Date.now(); if (len !== lastLen) { lastLen = len; stableSince = now; return false; } if (now - stableSince < q.stable) return false; }
      return true;
    };
  };
  // will real mouse input be processed now? A hidden document (minimized / fully occluded without the
  // no-occlusion flags) produces no frames and rAF-aligned mouse events stall for seconds. A busy but
  // visible page just delays the ack a little, so visibility alone decides (no rAF wait).
  cu.probe = function (d) {
    d.hidden = !!document.hidden;
    d.frames = !d.hidden;
    return d;
  };
  // new-tab hint: clicking this opens another tab (target=_blank / window.open-style attribute)
  cu.opensTab = function (el) {
    var a = null; try { a = el.closest ? el.closest('a[target],area[target],form[target]') : null; } catch (e) { }
    if (!a) return false;
    var t = (a.getAttribute('target') || '').toLowerCase();
    return t === '_blank' || (t !== '' && t !== '_self' && t !== '_top' && t !== '_parent');
  };
  window.__cu = cu;
})();
