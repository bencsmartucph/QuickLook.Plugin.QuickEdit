// QuickLook.Plugin.QuickEdit -- runs inside QuickLook's Markdown preview page.
// Copyright (C) 2026 Ben Smart. Licensed under the GNU GPL v3 or later (see LICENSE).
//
// Double-click rendered text to edit it in place. Enter (or clicking away)
// finishes the edit, Esc cancels it, Ctrl+S writes the file, Ctrl+Z / Ctrl+Y
// undo and redo finished edits. Nothing is saved unless you press Ctrl+S.
//
// How an edit gets back into the Markdown source:
//  1. The page's own markdown-it instance re-renders the source with each
//     block tagged with its source lines, so every rendered block (paragraph,
//     list item, heading, table row, code block) knows which lines it came from.
//  2. The edit is reduced to one changed span of the block's text.
//  3. Candidate source edits are generated for that span inside those lines,
//     and each is rendered; the first whose rendered text is exactly what you
//     typed (and nothing else changed) wins. If none renders correctly, the
//     edit is undone rather than guessed.
//
// Talks to the host (Plugin.cs) through chrome.webview string messages:
//   page -> host: "hello", "editing:1", "editing:0", "save:<text>"
//   host -> page: "file:<text>", "nofile:<reason>", "saved", "error:<message>", "keys:missing"

(function () {
  "use strict";

  if (window.__qe) return;

  function ready() {
    return document.getElementById("text-input") && document.getElementById("preview") && window.md;
  }

  function boot() {
    if (window.__qe || !ready()) return;
    window.__qe = { version: "1.0.0" };
    init();
  }

  if (ready()) boot();
  else if (!window.__qePending) {
    window.__qePending = true;
    document.addEventListener("DOMContentLoaded", boot);
    window.addEventListener("load", boot);
  }

  function init() {
    var md = window.md;
    var preview = document.getElementById("preview");
    var webview = window.chrome && window.chrome.webview;

    var T = document.getElementById("text-input").value; // the source, as the page rendered it (LF line endings)
    var savedT = T; // T as it is on disk
    var fileState = "waiting"; // waiting | ok | blocked
    var blockedReason = "";
    var keysWarned = false;
    var undoStack = [];
    var redoStack = [];
    var editing = null;
    var index = null;
    var MAX_CANDIDATES = T.length > 400000 ? 8 : 80;

    // ---- host messages ---------------------------------------------------

    function post(message) {
      try {
        if (webview) webview.postMessage(message);
      } catch (e) {}
    }

    if (webview) {
      webview.addEventListener("message", function (e) {
        var data = typeof e.data === "string" ? e.data : "";
        if (data.indexOf("file:") === 0) {
          var file = data.substring(5).replace(/\r\n/g, "\n");
          if (file === savedT) fileState = "ok";
          else block("Can't edit here: the preview doesn't match the file on disk. If it changed, reopen it.");
        } else if (data.indexOf("nofile:") === 0) {
          block(data.substring(7));
        } else if (data === "saved") {
          savedT = T;
          updateStatus("saved");
        } else if (data.indexOf("error:") === 0) {
          updateStatus("error", data.substring(6));
        } else if (data === "keys:missing") {
          keysWarned = true;
        }
      });
      post("hello");
    } else {
      fileState = "ok"; // no host: in a browser, for testing
    }

    function block(reason) {
      fileState = "blocked";
      blockedReason = reason || "This file can't be edited here.";
    }

    // ---- styles and status pill -------------------------------------------

    var style = document.createElement("style");
    style.textContent = [
      ".qe-editing{outline:2px solid var(--fgColor-accent,#0969da);outline-offset:3px;border-radius:3px;",
      "background:var(--bgColor-accent-muted,rgba(84,174,255,.12));caret-color:var(--fgColor-accent,#0969da);cursor:text}",
      ".qe-editing:focus{outline:2px solid var(--fgColor-accent,#0969da)}",
      "#qe-pill{position:fixed;right:14px;bottom:14px;z-index:10000;display:none;align-items:center;gap:8px;",
      "font:12px/1.4 'Segoe UI',system-ui,sans-serif;padding:6px 12px;border-radius:999px;",
      "background:var(--bgColor-default,#fff);color:var(--fgColor-default,#1f2328);",
      "border:1px solid var(--borderColor-default,#d0d7de);box-shadow:0 4px 14px rgba(0,0,0,.14);",
      "user-select:none;pointer-events:none;transition:opacity .25s ease}",
      "#qe-pill.qe-show{display:flex}",
      "#qe-pill .qe-dot{width:8px;height:8px;border-radius:50%;background:var(--fgColor-attention,#9a6700);flex:none}",
      "#qe-pill.qe-editing-state .qe-dot{background:var(--fgColor-accent,#0969da)}",
      "#qe-pill.qe-saved .qe-dot{background:var(--fgColor-success,#1a7f37)}",
      "#qe-pill.qe-error .qe-dot{background:var(--fgColor-danger,#d1242f)}",
      "#qe-pill.qe-error{color:var(--fgColor-danger,#d1242f)}",
      "#qe-pill kbd{font:11px 'Segoe UI',system-ui,sans-serif;padding:0 5px;border:1px solid var(--borderColor-default,#d0d7de);",
      "border-bottom-width:2px;border-radius:4px;background:var(--bgColor-muted,#f6f8fa)}",
      "#qe-pill .qe-sep{opacity:.45}"
    ].join("");
    document.head.appendChild(style);

    var pill = document.createElement("div");
    pill.id = "qe-pill";
    document.body.appendChild(pill);
    var pillTimer = 0;

    function kbd(k) {
      return "<kbd>" + k + "</kbd>";
    }

    function setPill(cls, html, hideAfter) {
      clearTimeout(pillTimer);
      pill.className = "qe-show " + cls;
      pill.style.opacity = "1";
      pill.innerHTML = '<span class="qe-dot"></span><span>' + html + "</span>";
      if (hideAfter) {
        pillTimer = setTimeout(function () {
          pill.style.opacity = "0";
          pillTimer = setTimeout(function () {
            updateStatus();
          }, 260);
        }, hideAfter);
      }
    }

    var SEP = ' <span class="qe-sep">·</span> ';

    // The pill always shows the current state; transient messages (saved,
    // errors, notices) fall back to it when they expire.
    function updateStatus(kind, message) {
      if (kind === "saved") return setPill("qe-saved", "Saved", 1400);
      if (kind === "error") return setPill("qe-error", escapeHtml(message || "Couldn't save."), 4500);
      if (kind === "notice") return setPill("qe-error", escapeHtml(message), 3200);
      if (editing) {
        var hint = kbd("Enter") + " done" + SEP + kbd("Esc") + " cancel";
        if (keysWarned) hint += SEP + "QuickLook may react to Space/Enter";
        return setPill("qe-editing-state", "Editing" + SEP + hint);
      }
      if (T !== savedT) {
        return setPill("", "Unsaved" + SEP + kbd("Ctrl") + "+" + kbd("S") + " save" + SEP + kbd("Ctrl") + "+" + kbd("Z") + " undo");
      }
      clearTimeout(pillTimer);
      pill.className = "";
    }

    function escapeHtml(s) {
      return String(s).replace(/[&<>"]/g, function (c) {
        return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c];
      });
    }

    // ---- annotated rendering ----------------------------------------------

    // Tag every block token with its source lines, but only for our own
    // renders (env.qe); the page's render is untouched.
    md.core.ruler.push("qe_lines", function (state) {
      if (!state.env || !state.env.qe) return;
      for (var i = 0; i < state.tokens.length; i++) {
        var t = state.tokens[i];
        if (t.map && t.nesting === 1) t.attrSet("data-qe-line", t.map[0] + "," + t.map[1]);
      }
    });

    // Code blocks render through the highlighter, which drops token attributes.
    ["fence", "code_block"].forEach(function (rule) {
      var original = md.renderer.rules[rule];
      if (!original) return;
      md.renderer.rules[rule] = function (tokens, idx, options, env, self) {
        var html = original.call(this, tokens, idx, options, env, self);
        var t = tokens[idx];
        if (env && env.qe && t.map) {
          html = html.replace(/^(\s*)<(\w+)/, function (m, ws, tag) {
            return ws + "<" + tag + ' data-qe-line="' + t.map[0] + "," + t.map[1] + '" data-qe-code="1"';
          });
        }
        return html;
      };
    });

    function renderFragment(src) {
      var template = document.createElement("template");
      template.innerHTML = md.render(src, { qe: true });
      return template.content;
    }

    function pathOf(el, root) {
      var path = [];
      while (el && el !== root) {
        var parent = el.parentNode;
        path.unshift(Array.prototype.indexOf.call(parent.children, el));
        el = parent;
      }
      return path;
    }

    function follow(root, path) {
      var el = root;
      for (var i = 0; i < path.length && el; i++) el = el.children[path[i]];
      return el || null;
    }

    function signature(frag) {
      var tags = [];
      var nodes = frag.querySelectorAll("[data-qe-line]");
      for (var i = 0; i < nodes.length; i++) tags.push(nodes[i].tagName);
      return tags.join(",");
    }

    function buildIndex() {
      var frag = renderFragment(T);
      var units = new WeakMap();
      var nodes = frag.querySelectorAll("[data-qe-line]");
      for (var i = 0; i < nodes.length; i++) {
        var det = nodes[i];
        var path = pathOf(det, frag);
        var live = follow(preview, path);
        if (!live || live.tagName !== det.tagName) continue;
        var range = det.getAttribute("data-qe-line").split(",");
        units.set(live, {
          live: live,
          det: det,
          path: path,
          start: +range[0],
          end: +range[1],
          code: det.hasAttribute("data-qe-code")
        });
      }
      index = { frag: frag, units: units, text: frag.textContent, signature: signature(frag) };
    }

    function ensureIndex() {
      if (!index) buildIndex();
      return index;
    }

    function unitFor(node) {
      var el = node && node.nodeType === 1 ? node : node && node.parentElement;
      var units = ensureIndex().units;
      while (el && el !== preview) {
        if (units.has(el)) return units.get(el);
        el = el.parentElement;
      }
      return null;
    }

    function editable(unit) {
      if (unit.live.querySelector(".mermaid, svg, mjx-container, math")) return false;
      if (unit.live.closest(".mermaid")) return false;
      return unit.live.textContent === unit.det.textContent;
    }

    // Offset of el's text within root's textContent.
    function textOffset(root, el) {
      var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      var offset = 0;
      var node;
      while ((node = walker.nextNode())) {
        if (el.contains(node)) return offset;
        if (el.compareDocumentPosition(node) & Node.DOCUMENT_POSITION_FOLLOWING) return offset;
        offset += node.data.length;
      }
      return offset;
    }

    // ---- mapping an edit back to the source -------------------------------

    function lineStarts(src) {
      var starts = [0];
      for (var i = 0; i < src.length; i++) if (src.charCodeAt(i) === 10) starts.push(i + 1);
      return starts;
    }

    // Greedy alignment of the block's rendered text onto its source lines:
    // m[i] is the source index of rendered character i, or -1.
    function align(rendered, seg) {
      var m = new Array(rendered.length);
      var j = 0;
      for (var i = 0; i < rendered.length; i++) {
        var c = rendered[i];
        if (/\s/.test(c)) {
          m[i] = -1;
          continue;
        }
        var k = seg.indexOf(c, j);
        if (k < 0) {
          m[i] = -1;
          continue;
        }
        m[i] = k;
        j = k + 1;
      }
      return m;
    }

    function afterPrevious(m, i) {
      for (var k = i - 1; k >= 0; k--) if (m[k] >= 0) return m[k] + 1;
      return null;
    }

    function atNext(m, i) {
      for (var k = i; k < m.length; k++) if (m[k] >= 0) return m[k];
      return null;
    }

    function escapeMarkdown(s) {
      return s.replace(/([\\`*_\[\]<>|#])/g, "\\$1");
    }

    var MARKUP = "*_~`";

    // Words, runs of spaces and single punctuation marks.
    function tokenize(s) {
      return s.match(/\s+|[\p{L}\p{N}_]+|[^\s\p{L}\p{N}_]/gu) || [];
    }

    // The edit as separate changes: [{ start, end, text }] in old-text offsets.
    function hunks(a, b) {
      var p = 0;
      var max = Math.min(a.length, b.length);
      while (p < max && a[p] === b[p]) p++;
      var q = 0;
      while (q < max - p && a[a.length - 1 - q] === b[b.length - 1 - q]) q++;
      var single = [{ start: p, end: a.length - q, text: b.substring(p, b.length - q) }];

      var A = tokenize(a.substring(p, a.length - q));
      var B = tokenize(b.substring(p, b.length - q));
      var n = A.length;
      var m = B.length;
      if (!n || !m || n * m > 250000) return single;

      // L[i][j]: longest common subsequence of A[i..] and B[j..].
      var w = m + 1;
      var L = new Int32Array((n + 1) * w);
      for (var i = n - 1; i >= 0; i--)
        for (var j = m - 1; j >= 0; j--)
          L[i * w + j] = A[i] === B[j] ? L[(i + 1) * w + j + 1] + 1 : Math.max(L[(i + 1) * w + j], L[i * w + j + 1]);

      var out = [];
      var cur = null;
      var pos = p;
      i = 0;
      j = 0;
      while (i < n || j < m) {
        if (i < n && j < m && A[i] === B[j]) {
          cur = null;
          pos += A[i].length;
          i++;
          j++;
        } else {
          if (!cur) out.push((cur = { start: pos, end: pos, text: "" }));
          if (j < m && (i === n || L[i * w + j + 1] >= L[(i + 1) * w + j])) {
            cur.text += B[j];
            j++;
          } else {
            pos += A[i].length;
            cur.end = pos;
            i++;
          }
        }
      }
      return out.length && out.length <= 24 ? out : single;
    }

    function candidates(seg, rendered, m, h) {
      var p = h.start;
      var oldEnd = h.end;
      var oldMid = rendered.substring(p, oldEnd);
      var newMid = h.text;
      var list = [];
      var seen = {};
      function add(a, b, text) {
        if (a == null || b == null || a < 0 || b > seg.length || a > b) return;
        var key = a + ":" + b + ":" + text;
        if (seen[key]) return;
        seen[key] = true;
        list.push({ a: a, b: b, text: text });
      }

      var replacements = [newMid];
      var escaped = escapeMarkdown(newMid);
      if (escaped !== newMid) replacements.push(escaped);

      var starts = [atNext(m, p), afterPrevious(m, p)];
      var ends = oldMid.length ? [afterPrevious(m, oldEnd), atNext(m, oldEnd)] : starts;
      starts.forEach(function (a) {
        ends.forEach(function (b) {
          replacements.forEach(function (r) {
            if (oldMid.length || a === b) add(a, b, r);
          });
        });
      });

      // Every literal occurrence of the old text in the block's source.
      if (oldMid.length) {
        for (var at = seg.indexOf(oldMid); at >= 0 && list.length < MAX_CANDIDATES; at = seg.indexOf(oldMid, at + 1)) {
          replacements.forEach(function (r) {
            add(at, at + oldMid.length, r);
          });
        }
      }

      // The same, widened to whole words, for edits that end inside markup.
      var wp = p;
      while (wp > 0 && /[\p{L}\p{N}_]/u.test(rendered[wp - 1])) wp--;
      var we = oldEnd;
      while (we < rendered.length && /[\p{L}\p{N}_]/u.test(rendered[we])) we++;
      if (wp !== p || we !== oldEnd) {
        var oldWord = rendered.substring(wp, we);
        var newWord = rendered.substring(wp, p) + newMid + rendered.substring(oldEnd, we);
        if (oldWord.length) {
          for (var w = seg.indexOf(oldWord); w >= 0 && list.length < MAX_CANDIDATES; w = seg.indexOf(oldWord, w + 1)) {
            add(w, w + oldWord.length, newWord);
            add(w, w + oldWord.length, escapeMarkdown(newWord));
          }
        }
      }

      // Deleting or retyping a whole formatted span: take its markup too.
      list.slice(0, 8).forEach(function (c) {
        for (var left = 0; left <= 3; left++) {
          if (left && (c.a - left < 0 || MARKUP.indexOf(seg[c.a - left]) < 0)) break;
          for (var right = 0; right <= 3; right++) {
            if (right && (c.b + right - 1 >= seg.length || MARKUP.indexOf(seg[c.b + right - 1]) < 0)) break;
            if (left || right) add(c.a - left, c.b + right, c.text);
          }
        }
      });

      return list.slice(0, MAX_CANDIDATES);
    }

    // The element structure of a rendered document: tags, link targets and
    // image sources, without text. An edit that only changes words leaves it
    // alone; one that drops formatting does not.
    function shapeOf(root) {
      var out = [];
      (function walk(node) {
        for (var c = node.firstElementChild; c; c = c.nextElementSibling) {
          var tag = c.tagName;
          if (tag === "A") tag += "=" + (c.getAttribute("href") || "");
          else if (tag === "IMG") tag += "=" + (c.getAttribute("src") || "");
          out.push("<" + tag + ">");
          walk(c);
          out.push("</>");
        }
      })(root);
      return out.join("");
    }

    // The shape once formatted spans whose text was deleted entirely are gone.
    function shapeWithout(unit, removedRanges) {
      var clone = index.frag.cloneNode(true);
      var det = follow(clone, unit.path);
      if (det) {
        var doomed = [];
        var all = det.querySelectorAll("*");
        for (var i = 0; i < all.length; i++) {
          var el = all[i];
          var len = el.textContent.length;
          if (!len) continue;
          var s = textOffset(det, el);
          for (var k = 0; k < removedRanges.length; k++) {
            var r = removedRanges[k];
            if (s >= r.start && s + len <= r.end) {
              doomed.push(el);
              break;
            }
          }
        }
        doomed.forEach(function (el) {
          if (el.parentNode) el.parentNode.removeChild(el);
        });
      }
      return shapeOf(clone);
    }

    // Returns { src, frag, sameShape } or null when the edit can't be placed.
    function applyEdit(unit, oldText, newText) {
      var starts = lineStarts(T);
      var segStart = starts[unit.start] != null ? starts[unit.start] : T.length;
      var segEnd = starts[unit.end] != null ? starts[unit.end] : T.length;
      var seg = T.substring(segStart, segEnd);
      var head = T.substring(0, segStart);
      var foot = T.substring(segEnd);

      var unitOffset = textOffset(index.frag, unit.det);
      var textHead = index.text.substring(0, unitOffset);
      var textFoot = index.text.substring(unitOffset + oldText.length);

      var m = align(oldText, seg);
      var changes = hunks(oldText, newText);
      var work = seg;
      var unitText = oldText;
      var shape = shapeOf(index.frag);
      var removed = [];
      var frag = null;

      // Right to left, so earlier offsets stay valid in both text and source.
      for (var k = changes.length - 1; k >= 0; k--) {
        var h = changes[k];
        var expectedUnit = unitText.substring(0, h.start) + h.text + unitText.substring(h.end);
        var expected = textHead + expectedUnit + textFoot;
        if (h.end > h.start) removed.push({ start: h.start, end: h.end });
        var relaxed = null;

        var list = candidates(work, oldText, m, h);
        var strict = null;
        var loose = null;
        for (var i = 0; i < list.length && !strict; i++) {
          var c = list[i];
          var next = work.substring(0, c.a) + c.text + work.substring(c.b);
          var f;
          try {
            f = renderFragment(head + next + foot);
          } catch (e) {
            continue;
          }
          if (f.textContent !== expected) continue;
          var s = shapeOf(f);
          if (s === shape) strict = { seg: next, frag: f, shape: s };
          else if (!loose) {
            if (relaxed === null) relaxed = shapeWithout(unit, removed);
            if (s === relaxed) loose = { seg: next, frag: f, shape: s };
          }
        }

        var pick = strict || loose;
        if (!pick) return null;
        work = pick.seg;
        frag = pick.frag;
        shape = pick.shape;
        unitText = expectedUnit;
      }

      if (!frag) return null;
      return { src: head + work + foot, frag: frag, sameShape: signature(frag) === index.signature };
    }

    // ---- showing the new source --------------------------------------------

    function stripMarks(root) {
      var marked = root.querySelectorAll("[data-qe-line]");
      for (var i = 0; i < marked.length; i++) {
        marked[i].removeAttribute("data-qe-line");
        marked[i].removeAttribute("data-qe-code");
      }
    }

    function patchUnit(unit, frag) {
      var det = follow(frag, unit.path);
      var live = follow(preview, unit.path);
      if (!det || !live) return false;
      var copy = det.cloneNode(true);
      stripMarks(copy);
      live.innerHTML = copy.innerHTML;
      if (/^H[1-6]$/.test(live.tagName) && live.id) {
        var link = document.querySelector('.table-of-contents a[href="#' + cssEscape(live.id) + '"]');
        if (link) link.textContent = live.textContent;
      }
      return true;
    }

    function cssEscape(s) {
      return window.CSS && CSS.escape ? CSS.escape(s) : s.replace(/"/g, '\\"');
    }

    // Re-render the whole preview, for structural changes, undo and redo.
    function renderAll() {
      var scrollY = window.scrollY;
      preview.innerHTML = md.render(T);
      try {
        if (window.mermaid) window.mermaid.init(undefined, preview.querySelectorAll(".mermaid"));
      } catch (e) {}
      try {
        if (window.MathJax && window.MathJax.typesetPromise) window.MathJax.typesetPromise([preview]);
      } catch (e) {}
      rebuildToc();
      window.scrollTo(0, scrollY);
    }

    // Same contents as the page's own table of contents.
    function rebuildToc() {
      var toc = document.getElementById("toc");
      if (!toc) return;
      var html = "<nav role='navigation' class='table-of-contents'><h2>Contents</h2><ul>";
      var level = 0;
      var baseLevel = 0;
      var count = 0;
      preview.querySelectorAll("h1, h2, h3, h4").forEach(function (heading) {
        count++;
        if (!heading.id) heading.id = "h-" + count;
        var prev = level || 0;
        level = +heading.nodeName.substr(1);
        if (!baseLevel) baseLevel = level;
        var line;
        if (prev === 0) line = "<li>";
        else if (level === prev) line = "</li><li>";
        else if (level > prev) line = "</li><ul><li>";
        else line = "</li></ul>".repeat(prev - level) + "</li><li>";
        html += line + '<a href="#' + escapeHtml(heading.id) + '">' + escapeHtml(heading.textContent) + "</a>";
      });
      html += "</li></ul>".repeat(Math.max(0, level - baseLevel)) + "</li></ul></nav>";
      toc.innerHTML = html;
      toc.style.display = count < 2 ? "none" : "";
    }

    function setSource(src) {
      T = src;
      index = null;
    }

    // ---- editing -------------------------------------------------------------

    function startEdit(unit, host, range, point) {
      if (editing) finish(true);
      if (!host.isConnected) return; // finishing the previous edit re-rendered this block
      unit = unitFor(host) || unit;
      if (!unit || !unit.live.isConnected) return;

      editing = {
        unit: unit,
        host: host,
        oldText: unit.live.textContent,
        snapshot: unit.live.innerHTML
      };

      try {
        host.contentEditable = "plaintext-only";
      } catch (e) {
        host.contentEditable = "true";
      }
      if (host.contentEditable !== "plaintext-only") host.contentEditable = "true";
      host.spellcheck = false;
      host.classList.add("qe-editing");
      host.focus({ preventScroll: true });

      var sel = window.getSelection();
      if (range && host.contains(range.startContainer) && host.contains(range.endContainer)) {
        trimRange(range);
        sel.removeAllRanges();
        sel.addRange(range);
      } else if (point && document.caretRangeFromPoint) {
        var caret = document.caretRangeFromPoint(point.x, point.y);
        if (caret && host.contains(caret.startContainer)) {
          sel.removeAllRanges();
          sel.addRange(caret);
        }
      }

      host.addEventListener("blur", onBlur);
      post("editing:1");
      updateStatus();
    }

    // Windows double-click selects a word plus the space after it; typing over
    // that would swallow the space. Select just the word.
    function trimRange(range) {
      if (range.collapsed) return;
      var end = range.endContainer;
      if (end.nodeType === 3) {
        var e = range.endOffset;
        var floor = end === range.startContainer ? range.startOffset : 0;
        while (e > floor && /\s/.test(end.data[e - 1])) e--;
        if (e > floor || end !== range.startContainer) range.setEnd(end, e);
      }
      var start = range.startContainer;
      if (start.nodeType === 3 && !range.collapsed) {
        var s = range.startOffset;
        var ceiling = start === range.endContainer ? range.endOffset : start.data.length;
        while (s < ceiling && /\s/.test(start.data[s])) s++;
        if (s < ceiling) range.setStart(start, s);
      }
    }

    function onBlur() {
      if (editing && !editing.finishing) finish(true);
    }

    function endHost(ed) {
      ed.host.removeEventListener("blur", onBlur);
      ed.host.removeAttribute("contenteditable");
      ed.host.classList.remove("qe-editing");
    }

    function restore(ed) {
      if (ed.unit.live.isConnected) ed.unit.live.innerHTML = ed.snapshot;
      index = null;
    }

    // keep: true applies the edit to the source, false throws it away.
    function finish(keep) {
      var ed = editing;
      if (!ed) return;
      ed.finishing = true;
      editing = null;
      endHost(ed);
      post("editing:0");
      if (document.activeElement === ed.host) ed.host.blur();
      window.getSelection().removeAllRanges();

      var newText = ed.unit.live.textContent;
      if (!keep || newText === ed.oldText) {
        restore(ed);
        updateStatus();
        return;
      }

      var result = null;
      try {
        ensureIndex();
        result = applyEdit(ed.unit, ed.oldText, newText);
      } catch (e) {
        result = null;
      }

      if (!result) {
        restore(ed);
        updateStatus("notice", "Couldn't place that change in the file, so it was undone.");
        return;
      }

      undoStack.push(T);
      redoStack = [];
      setSource(result.src);
      if (!(result.sameShape && patchUnit(ed.unit, result.frag))) renderAll();
      updateStatus();
    }

    function undo() {
      if (!undoStack.length) return false;
      redoStack.push(T);
      setSource(undoStack.pop());
      renderAll();
      updateStatus();
      return true;
    }

    function redo() {
      if (!redoStack.length) return false;
      undoStack.push(T);
      setSource(redoStack.pop());
      renderAll();
      updateStatus();
      return true;
    }

    function save() {
      if (editing) finish(true);
      if (fileState === "blocked") return updateStatus("error", blockedReason);
      if (T === savedT) return updateStatus("saved");
      if (!webview) {
        savedT = T;
        return updateStatus("saved");
      }
      post("save:" + T);
    }

    // ---- input ---------------------------------------------------------------

    function refuse(reason) {
      updateStatus("notice", reason);
    }

    preview.addEventListener("dblclick", function (e) {
      if (editing && editing.host.contains(e.target)) {
        // Another word in the same block: select just the word, as on the first double-click.
        var current = window.getSelection();
        if (current.rangeCount) {
          var word = current.getRangeAt(0).cloneRange();
          trimRange(word);
          current.removeAllRanges();
          current.addRange(word);
        }
        return;
      }
      if (fileState === "blocked") return refuse(blockedReason);
      if (fileState !== "ok") return;

      var sel = window.getSelection();
      var range = sel.rangeCount ? sel.getRangeAt(0).cloneRange() : null;
      var unit = unitFor(e.target);
      if (!unit) return;
      if (!editable(unit)) return refuse("This part can't be edited here.");

      var cell = e.target.nodeType === 1 ? e.target.closest("td, th") : null;
      var host = cell && unit.live.contains(cell) ? cell : unit.code ? unit.live.querySelector("code") || unit.live : unit.live;
      startEdit(unit, host, range, { x: e.clientX, y: e.clientY });
    });

    // A single click on a link waits a moment, so a double-click can edit
    // the link's text instead of opening it.
    var linkTimer = 0;
    preview.addEventListener(
      "click",
      function (e) {
        var a = e.target.nodeType === 1 && e.target.closest("a[href]");
        if (!a || !preview.contains(a)) return;
        if (editing && editing.host.contains(a)) {
          e.preventDefault();
          return;
        }
        if (fileState !== "ok") return;
        if (a.__qeGo) {
          a.__qeGo = false;
          return;
        }
        e.preventDefault();
        e.stopPropagation();
        clearTimeout(linkTimer);
        if (e.detail > 1) return;
        linkTimer = setTimeout(function () {
          a.__qeGo = true;
          a.click();
        }, 280);
      },
      true
    );

    window.addEventListener(
      "keydown",
      function (e) {
        if (e.isComposing) return;
        var key = e.key;
        var ctrl = e.ctrlKey || e.metaKey;

        if (ctrl && !e.altKey && (key === "s" || key === "S")) {
          e.preventDefault();
          e.stopImmediatePropagation();
          save();
          return;
        }

        if (editing) {
          if (key === "Enter" && editing.unit.code && e.shiftKey) {
            e.preventDefault();
            e.stopImmediatePropagation();
            document.execCommand("insertText", false, "\n");
            return;
          }
          if (key === "Enter" || key === "Tab") {
            e.preventDefault();
            e.stopImmediatePropagation();
            finish(true);
            return;
          }
          if (key === "Escape") {
            e.preventDefault();
            e.stopImmediatePropagation();
            finish(false);
            return;
          }
          if (key === " " && !ctrl) {
            // The page cancels Space (it dismisses the preview); type it ourselves.
            e.preventDefault();
            e.stopImmediatePropagation();
            document.execCommand("insertText", false, " ");
            return;
          }
          e.stopImmediatePropagation(); // the page's own shortcuts stay out of the edit
          return;
        }

        if (ctrl && !e.altKey && !e.shiftKey && (key === "z" || key === "Z")) {
          if (undo()) {
            e.preventDefault();
            e.stopImmediatePropagation();
          }
          return;
        }
        if (ctrl && !e.altKey && (key === "y" || key === "Y" || (e.shiftKey && (key === "z" || key === "Z")))) {
          if (redo()) {
            e.preventDefault();
            e.stopImmediatePropagation();
          }
        }
      },
      true
    );

    // For tests and debugging.
    window.__qe.state = function () {
      return { source: T, saved: savedT, dirty: T !== savedT, editing: !!editing, file: fileState, undo: undoStack.length };
    };
  }
})();
