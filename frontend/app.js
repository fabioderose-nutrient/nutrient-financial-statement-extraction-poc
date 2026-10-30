const $ = (id) => document.getElementById(id);
const esc = (s) => (s == null ? "" : String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c])));
const tidy = (s) => String(s == null ? "" : s).replace(/\s+/g, " ").trim();

const CUR_SYM = { EUR: "€", USD: "$", GBP: "£" };

// One value cell: the amount as printed (with its currency symbol), and when the
// table is in thousands/millions, the full normalised figure underneath.
function cell(raw, normalized, sym, factor) {
  if (raw == null || raw === "") return "";
  const printed = esc(sym) + esc(raw);
  if (factor > 1 && normalized != null && !isNaN(normalized)) {
    return `${printed}<span class="norm">= ${esc(sym)}${Math.round(normalized).toLocaleString("en-US")}</span>`;
  }
  return printed;
}

// The Web SDK is loaded from the Nutrient CDN (see index.html).
const NUTRIENT_WEB_SDK_VERSION = "1.6.0";
const NUTRIENT_CDN = `https://cdn.cloud.pspdfkit.com/pspdfkit-web@${NUTRIENT_WEB_SDK_VERSION}/`;

let instance = null;      // Nutrient Web SDK instance
let currentFile = null;   // the PDF the user opened
let result = null;        // last extraction payload from the API
let highlights = [];      // ids of the highlight annotations we created
let busy = false;
let activeTab = "tables";
let tableFilter = "all"; // all | ok | mismatch — driven by the summary stat chips

/* ---------------------------------------------------------------- viewer */

// NUTRIENT_WEB_LICENSE_KEY from the server. Empty = evaluation mode.
let licenseKeyPromise = null;
function webLicenseKey() {
  licenseKeyPromise ??= fetch("/api/config")
    .then((r) => (r.ok ? r.json() : {}))
    .then((c) => c.licenseKey || "")
    .catch(() => "");
  return licenseKeyPromise;
}

async function openDocument(file) {
  if (!file) return;
  if (!/\.pdf$/i.test(file.name) && file.type !== "application/pdf") {
    toast("Please open a PDF file.", true);
    return;
  }

  if (instance) { PSPDFKit.unload("#viewer"); instance = null; }
  highlights = [];

  try {
    const buffer = await file.arrayBuffer();
    const licenseKey = await webLicenseKey();
    instance = await PSPDFKit.load({
      container: "#viewer",
      document: buffer,
      baseUrl: NUTRIENT_CDN,
      ...(licenseKey ? { licenseKey } : {}),
      toolbarItems: [
        ...PSPDFKit.defaultToolbarItems,
        { type: "custom", id: "validate-tables", title: "Validate tables", className: "ai-extract-btn", onPress: () => startExtraction("tables") },
        { type: "custom", id: "full-extract", title: "Full extract", onPress: () => startExtraction("full") },
      ],
    });
  } catch (err) {
    toast(`That file could not be opened: ${err.message}`, true);
    return;
  }

  currentFile = file;
  $("panel").hidden = true;
  $("dropzone").hidden = true;
  toast("Document open. Use “Validate tables” to check every table, or “Full extract” for all fields.");
}

/* ------------------------------------------------------------ extraction */

async function startExtraction(mode) {
  if (busy) return;
  if (!currentFile) { toast("Open a PDF first."); return; }

  busy = true;
  const tablesOnly = mode !== "full";
  const job = (crypto.randomUUID && crypto.randomUUID()) || String(Date.now());
  showOverlay(tablesOnly);
  const timer = setInterval(() => pollProgress(job), 900);

  try {
    const form = new FormData();
    form.append("file", currentFile);
    const url = `/api/process?job=${encodeURIComponent(job)}${tablesOnly ? "&only=tables" : ""}`;
    const res = await fetch(url, { method: "POST", body: form });
    const data = await res.json();
    if (data.error) throw new Error(data.detail ? `${data.error} ${data.detail}` : data.error);
    renderPanel(data);
  } catch (err) {
    toast(`Extraction failed: ${err.message}`, true);
  } finally {
    clearInterval(timer);
    hideOverlay();
    busy = false;
  }
}

async function pollProgress(job) {
  try {
    const p = await (await fetch(`/api/progress?job=${encodeURIComponent(job)}`)).json();
    if (!p.known) return;
    $("progressStage").textContent = p.stage || "Working";
    const pct = p.total > 0 ? Math.round((p.done / p.total) * 100) : 0;
    const fill = $("progressFill");
    fill.classList.toggle("is-indeterminate", p.total === 0);
    fill.style.width = p.total > 0 ? `${pct}%` : "";
    $("progressMeta").textContent = p.total > 0
      ? `${p.done} of ${p.total} passes complete · ${p.elapsed_seconds}s elapsed`
      : `${p.elapsed_seconds}s elapsed`;
  } catch { /* polling is best effort */ }
}

function showOverlay(tablesOnly) {
  $("progressStage").textContent = "Uploading the document";
  $("progressMeta").textContent = "";
  const note = $("progressNote");
  if (note) {
    note.textContent = tablesOnly
      ? "Reading every table and checking its arithmetic. This usually takes one to three minutes."
      : "Full extraction reads all fields, commentary and tables. This can take several minutes.";
  }
  const fill = $("progressFill");
  fill.style.width = "";
  fill.classList.add("is-indeterminate");
  $("overlay").hidden = false;
}
function hideOverlay() { $("overlay").hidden = true; }

/* -------------------------------------------------- locating in the page */

// XtractFlow does not report where a value sits on the page, so we find it by
// searching the document text. A bare amount is ambiguous (a note total is
// reprinted on the balance sheet), so we first pin the right PAGE using the
// most distinctive anchors of the table, then highlight the value on that page.

// Turn a printed amount into the strings worth searching for, most useful first.
// A dash / "Nil" / empty cell has nothing to search, so it yields nothing.
function amountVariants(raw) {
  if (raw == null) return [];
  const s = String(raw).replace(/[€£$ ]/g, "").trim();
  if (!/\d/.test(s)) return [];
  const out = new Set([s, s.replace(/[()]/g, "").trim()]);
  return [...out].filter((v) => (v.match(/\d/g) || []).length >= 2);
}

function caption(name) {
  return tidy(name).replace(/^\d+\.\s*/, "").replace(/\s*-\s*.*$/, "").trim();
}

async function search(query) {
  if (!query) return null;
  try {
    const hits = await instance.search(String(query).trim());
    return hits && hits.size ? hits : null;
  } catch { return null; }
}

// Which page do these queries point to most strongly? We count how many DISTINCT
// anchors land on each page, not how many times each one appears. Otherwise a
// statement that repeats the same equity figures many times in a matrix (the
// statement of changes in equity) would outscore the page that actually holds
// the table just because it prints the numbers more often.
async function bestPage(queries) {
  const count = new Map();
  for (const q of queries) {
    const hits = await search(q);
    if (!hits) continue;
    const pages = new Set(hits.map((h) => h.pageIndex).toArray());
    for (const p of pages) count.set(p, (count.get(p) || 0) + 1);
  }
  let best = null, bestN = 0;
  for (const [p, n] of count) if (n > bestN) { best = p; bestN = n; }
  return best;
}

async function clearHighlights() {
  if (!instance || highlights.length === 0) return;
  try { await instance.delete(highlights); } catch { /* already gone */ }
  highlights = [];
}

// The real table page is the one that actually prints the extracted numbers.
// A generic caption ("Statement of Financial Position") also appears in the
// table of contents, so we anchor the page on the DATA, not on the words: the
// page carrying the most of this table's figures is the table itself.
function dataAnchors(t) {
  const out = [];
  for (const r of (t.rows || [])) out.push(...amountVariants(r.raw_current), ...amountVariants(r.raw_prior));
  return out;
}

// Distinctive row labels ("Ordinary share of €1.00", "Trade creditors"). These
// pin the page when the figures cannot: a share-capital note whose only value is
// "1" is impossible to find by number, but its labels appear on exactly one page.
function labelAnchors(t) {
  const out = [];
  for (const r of (t.rows || [])) {
    const l = (r.label || "").trim();
    if (l.length < 6 || !/[a-z]/i.test(l)) continue;
    out.push(l);
    // A cleaned variant without a leading count/bracket or a trailing currency
    // amount ("1 (2022 - 1) Ordinary share of €1.00" -> "Ordinary share of"),
    // which matches the page text more reliably than the exact label.
    const cleaned = l.replace(/^[\d\s().,\-]+/, "").replace(/[€£$]?\s*[\d.,]+$/, "").trim();
    if (cleaned.length >= 6 && cleaned !== l) out.push(cleaned);
  }
  return out;
}

async function pageForTable(t) {
  // Anchor on everything distinctive at once: the figures AND the row labels AND
  // the caption. The page that carries the most of them together is the table.
  const anchors = [...dataAnchors(t), ...labelAnchors(t), caption(t.name)].filter(Boolean);
  return await bestPage(anchors);
}

// The strings worth searching for a value: the number variants, or the raw text
// for non-numeric fields (currency, dates).
function valueQueries(raw) {
  const v = amountVariants(raw);
  if (v.length) return v;
  const s = raw == null ? "" : String(raw).trim();
  return s.length > 1 ? [s] : [];
}

// Every rect (on `page`) for the first query that matches anything.
async function anyRects(queries, page) {
  for (const q of queries) {
    const hits = await search(q);
    if (!hits) continue;
    const rects = [];
    hits.filter((h) => h.pageIndex === page).forEach((h) => h.rectsOnPage.forEach((rc) => rects.push(rc)));
    if (rects.length) return rects;
  }
  return [];
}

// Find the ONE printed line that best matches this row. Each group is a tagged
// set of search strings (label, current value, prior value). A value like
// "2,277,507" appears on several rows, so we score every candidate line by how
// many distinct groups sit on it: the line carrying the label AND both figures
// wins over a line that only happens to reprint one of the numbers.
async function bestLineRects(page, groups) {
  const tagged = [];
  for (const g of groups) {
    const rects = await anyRects(g.queries, page);
    rects.forEach((rc) => tagged.push({ tag: g.tag, rc }));
  }
  if (!tagged.length) return {};

  const midY = (rc) => rc.top + rc.height / 2;
  let best = null;
  for (const seed of tagged) {
    const y = midY(seed.rc);
    const tol = Math.max(seed.rc.height, 4);
    const onLine = tagged.filter((x) => Math.abs(midY(x.rc) - y) <= tol);
    const score = new Set(onLine.map((x) => x.tag)).size * 1000 + onLine.length;
    if (!best || score > best.score) best = { score, onLine };
  }

  const byTag = {};
  for (const x of best.onLine) (byTag[x.tag] = byTag[x.tag] || []).push(x.rc);
  return byTag;
}

function unionRect(rects) {
  let left = Infinity, top = Infinity, right = -Infinity, bottom = -Infinity;
  for (const r of rects) {
    left = Math.min(left, r.left); top = Math.min(top, r.top);
    right = Math.max(right, r.left + r.width); bottom = Math.max(bottom, r.top + r.height);
  }
  return new PSPDFKit.Geometry.Rect({ left, top, width: right - left, height: bottom - top });
}

function makeHighlight(page, rects) {
  const box = unionRect(rects);
  return new PSPDFKit.Annotations.HighlightAnnotation({
    pageIndex: page,
    rects: PSPDFKit.Immutable.List([box]),
    boundingBox: box,
    color: new PSPDFKit.Color({ r: 255, g: 214, b: 0 }),
  });
}

// One yellow box spanning all the given rects (used for a whole table row).
async function highlightZone(page, rects) {
  if (!rects.length) return false;
  await clearHighlights();
  const ann = makeHighlight(page, rects);
  await instance.setViewState((vs) => vs.set("currentPageIndex", page));
  instance.jumpToRect(page, ann.boundingBox);
  const created = await instance.create(ann);
  highlights = created.map((a) => a.id);
  return true;
}

// One box PER group (used for fields: the current figure and the prior figure
// each get their own highlight, not a single box swallowing both).
async function highlightEach(page, rectGroups) {
  const groups = rectGroups.filter((g) => g.length);
  if (!groups.length) return false;
  await clearHighlights();
  const anns = groups.map((g) => makeHighlight(page, g));
  await instance.setViewState((vs) => vs.set("currentPageIndex", page));
  instance.jumpToRect(page, anns[0].boundingBox);
  const created = await instance.create(anns);
  highlights = created.map((a) => a.id);
  return true;
}

async function goToRow(r, t) {
  if (!instance) return;
  const page = await pageForTable(t);
  if (page == null) { toast("Could not locate this table in the document.", true); return; }
  const byTag = await bestLineRects(page, [
    { tag: "label", queries: [r.label].filter(Boolean) },
    { tag: "cur", queries: valueQueries(r.raw_current) },
    { tag: "pri", queries: valueQueries(r.raw_prior) },
  ]);
  const rects = [].concat(byTag.label || [], byTag.cur || [], byTag.pri || []);
  if (!await highlightZone(page, rects)) toast("Could not locate this row in the document.", true);
}

// Highlight the WHOLE table. XtractFlow gives us no table coordinates, so we
// locate every row we extracted (its label and figures), gather all their rects
// and let highlightZone span from the top-left-most to the bottom-right-most:
// one box covering the entire block, from the first line down to the total.
async function goToTable(t) {
  if (!instance) return;
  const page = await pageForTable(t);
  if (page == null) { toast("Could not locate this table in the document.", true); return; }

  const rects = [];
  for (const r of (t.rows || [])) {
    const byTag = await bestLineRects(page, [
      { tag: "label", queries: [r.label].filter(Boolean) },
      { tag: "cur", queries: valueQueries(r.raw_current) },
      { tag: "pri", queries: valueQueries(r.raw_prior) },
    ]);
    rects.push(...(byTag.label || []), ...(byTag.cur || []), ...(byTag.pri || []));
  }
  if (!rects.length) rects.push(...await anyRects([caption(t.name)], page));
  if (!await highlightZone(page, rects)) toast("Could not locate this table in the document.", true);
}

// A field: highlight its current and prior figures as TWO separate boxes.
async function goToField(f) {
  if (!instance) return;
  const years = result.years || [];
  const curRaw = f.values && f.values[years[0]] && f.values[years[0]].raw;
  const priRaw = f.values && f.values[years[1]] && f.values[years[1]].raw;
  const cur = valueQueries(curRaw), pri = valueQueries(priRaw);
  const labels = [f.official_name, f.label].filter(Boolean);

  // Anchor on the figures, which are distinctive. A label word like "Revenue"
  // also appears in the accounting-policy notes and would drag us to the wrong
  // page, so it is only a fallback for fields that have no numeric value.
  const page = (cur.length || pri.length) ? await bestPage([...cur, ...pri]) : await bestPage(labels);
  if (page == null) { toast("Could not locate this field in the document.", true); return; }

  const byTag = await bestLineRects(page, [
    { tag: "label", queries: labels },
    { tag: "cur", queries: cur },
    { tag: "pri", queries: pri },
  ]);
  if (!await highlightEach(page, [byTag.cur || [], byTag.pri || []]))
    toast("Could not locate this field in the document.", true);
}

// A note: highlight the sentence where it appears.
async function goToNote(text) {
  if (!instance) return;
  const page = await bestPage([text]);
  if (page == null) { toast("Could not locate this note in the document.", true); return; }
  const rects = await anyRects([text], page);
  if (!await highlightZone(page, rects)) toast("Could not locate this note in the document.", true);
}

/* ------------------------------------------------------------ the panel */

function renderPanel(d) {
  result = d;
  $("panel").hidden = false;
  $("panelSub").textContent = d.document_type ? d.document_type.label : "";
  tableFilter = "all";
  renderSummary(d);
  renderTab();
  const c = d.cost || {};
  $("panelFoot").textContent =
    `${c.model || "model"} · ${c.extraction_seconds != null ? c.extraction_seconds + "s" : ""}` +
    `${c.estimated_cost_usd != null ? " · ~$" + c.estimated_cost_usd : ""}`;
}

function renderSummary(d) {
  const t = d.tables || {};
  const s = t.summary || {};
  const size = d.company_size || {};
  const review = d.review || {};
  const cov = d.coverage || {};

  const chips = [];
  if (size.size && size.size !== "unknown") chips.push(`<span class="chip chip-size">${esc(size.size)}</span>`);
  if (size.framework && size.framework !== "unknown") chips.push(`<span class="chip">${esc(size.framework)}</span>`);
  if (d.currency && d.currency !== "unknown") chips.push(`<span class="chip">${esc(d.currency)}</span>`);

  const banner = review.needs_manual_review
    ? `<div class="banner banner-warn">Manual review suggested</div>`
    : `<div class="banner banner-ok">Everything we could check adds up</div>`;

  const on = (tab, filter) => (activeTab === tab && tableFilter === filter) ? " is-active" : "";
  $("summary").innerHTML = `
    <div class="chips">${chips.join("")}</div>
    ${banner}
    <div class="stats">
      <button class="stat${on("tables", "all")}" data-tab="tables" data-filter="all"><b>${s.tables_found || 0}</b><span>tables</span></button>
      <button class="stat stat-ok${on("tables", "ok")}" data-tab="tables" data-filter="ok"><b>${s.passed || 0}</b><span>balanced</span></button>
      <button class="stat stat-bad${on("tables", "mismatch")}" data-tab="tables" data-filter="mismatch"><b>${s.failed || 0}</b><span>to review</span></button>
      <button class="stat${activeTab === "fields" ? " is-active" : ""}" data-tab="fields"><b>${cov.extracted || 0}</b><span>fields</span></button>
    </div>`;
}

// Central place to change tab / filter so the tab buttons and the stat chips
// stay in sync.
function switchView(tab, filter) {
  activeTab = tab;
  if (filter != null) tableFilter = filter;
  [...$("tabs").children].forEach((b) => b.classList.toggle("is-active", b.dataset.tab === tab));
  if (result) renderSummary(result);
  renderTab();
}

function renderTab() {
  if (!result) return;
  const body = $("panelBody");
  if (activeTab === "tables") body.innerHTML = tablesHtml(result);
  else if (activeTab === "fields") body.innerHTML = fieldsHtml(result);
  else body.innerHTML = notesHtml(result);
}

function tablesHtml(d) {
  const all = (d.tables && d.tables.detected) || [];
  if (!all.length) return `<p class="empty">No table was detected in this document.</p>`;
  const years = d.years || ["Current", "Prior"];

  // Keep each table's ORIGINAL index (the click handler indexes into detected[]).
  const visible = all.map((t, i) => ({ t, i }))
    .filter(({ t }) => tableFilter === "all" || t.status === tableFilter);
  if (!visible.length) {
    const kind = tableFilter === "ok" ? "balanced" : "flagged";
    return `<p class="empty">No ${kind} table.</p>`;
  }

  return visible.map(({ t, i }) => {
    const label = t.status === "ok" ? "BALANCED" : t.status === "mismatch" ? "CHECK" : "NOT CHECKED";
    const total = (t.rows || []).find((r) => r.type === "total");
    const open = t.status === "mismatch" ? " open" : "";

    const sym = CUR_SYM[t.currency] || "";
    const factor = t.scale_factor || 1;
    const scaleBadge = (t.currency || factor > 1)
      ? `<span class="scale-badge">${esc(t.currency || "")}${t.scale_caption ? " " + esc(t.scale_caption) : ""}${factor > 1 ? " ·" + " normalised" : ""}</span>`
      : "";

    const checks = (t.checks || []).map((c) => `
      <div class="check check-${esc(c.status)}">
        <span class="check-year">${esc(c.year)}</span>
        <span>${esc(c.message)}</span>
      </div>`).join("");

    const rows = (t.rows || []).map((r, j) => `
      <tr class="row-${esc(r.type)}" data-table="${i}" data-row="${j}" title="Find this line in the document">
        <td class="row-label">${esc(r.label)}</td>
        <td class="row-type">${esc(r.type)}</td>
        <td class="num">${cell(r.raw_current, r.current, sym, factor)}</td>
        <td class="num">${cell(r.raw_prior, r.prior, sym, factor)}</td>
      </tr>`).join("");

    return `
      <details class="card card-${esc(t.status)}"${open}>
        <summary class="card-head">
          <span class="chev" aria-hidden="true">▸</span>
          <span class="status status-${esc(t.status)}">${label}</span>
          <span class="card-title">${esc(tidy(t.name))}</span>
          ${total ? `<span class="card-total">${esc(total.raw_current || "")}</span>` : ""}
        </summary>
        <div class="card-body">
          ${scaleBadge ? `<div class="scale-line">${scaleBadge}</div>` : ""}
          <table class="rows-table">
            <thead><tr><th>Line</th><th>Role</th><th class="num">${esc(years[0])}</th><th class="num">${esc(years[1])}</th></tr></thead>
            <tbody>${rows}</tbody>
          </table>
          <button class="locate-link" data-locate-table="${i}">Show this table in the document</button>
          <div class="checks-head">Arithmetic check</div>
          <div class="checks">${checks}</div>
        </div>
      </details>`;
  }).join("");
}

function fieldsHtml(d) {
  const fields = (d.fields || []).filter((f) => f.extracted);
  if (!fields.length) return `<p class="empty">No field was extracted.</p>`;

  const years = d.years || ["current", "prior"];
  return `<table class="fields">
    <thead><tr><th>Field</th><th class="num">${esc(years[0])}</th><th class="num">${esc(years[1])}</th></tr></thead>
    <tbody>${fields.map((f) => {
      const cur = f.values && f.values[years[0]];
      const pri = f.values && f.values[years[1]];
      return `<tr data-field="${esc(f.key)}">
        <td>${esc(f.label)}</td>
        <td class="num">${esc(cur ? cur.raw || "" : "")}</td>
        <td class="num">${esc(pri ? pri.raw || "" : "")}</td>
      </tr>`;
    }).join("")}</tbody></table>`;
}

function notesHtml(d) {
  const notes = Array.isArray(d.notes) ? d.notes : [];
  if (!notes.length) return `<p class="empty">No material note was extracted.</p>`;
  return notes.map((n) => `
    <article class="card note">
      <header class="card-head">
        <span class="card-title">${esc(n.title || "Note")}</span>
        ${n.sentiment ? `<span class="chip">${esc(n.sentiment)}</span>` : ""}
      </header>
      <p class="note-text" data-note="${esc((n.text || "").slice(0, 60))}">${esc(n.text || "")}</p>
    </article>`).join("");
}

/* ------------------------------------------------------------- wiring up */

$("panelBody").addEventListener("click", async (e) => {
  const locate = e.target.closest("[data-locate-table]");
  if (locate) {
    e.preventDefault();
    await goToTable(result.tables.detected[+locate.dataset.locateTable]);
    return;
  }

  const row = e.target.closest("tr[data-row]");
  if (row) {
    const t = result.tables.detected[+row.dataset.table];
    await goToRow(t.rows[+row.dataset.row], t);
    return;
  }

  const field = e.target.closest("tr[data-field]");
  if (field) {
    const f = (result.fields || []).find((x) => x.key === field.dataset.field);
    if (f) await goToField(f);
    return;
  }

  const note = e.target.closest("[data-note]");
  if (note) await goToNote(note.dataset.note);
});

$("tabs").addEventListener("click", (e) => {
  const tab = e.target.closest(".tab");
  if (tab) switchView(tab.dataset.tab, tab.dataset.tab === "tables" ? "all" : undefined);
});

// The summary counters double as filters: balanced / to review narrow the table
// list, tables shows them all, fields jumps to the fields tab.
$("summary").addEventListener("click", (e) => {
  const stat = e.target.closest(".stat");
  if (stat) switchView(stat.dataset.tab, stat.dataset.filter);
});

$("panelClose").addEventListener("click", () => { $("panel").hidden = true; });

$("browse").addEventListener("click", () => $("file").click());
$("file").addEventListener("change", (e) => openDocument(e.target.files[0]));

const dz = $("dropzone");
["dragenter", "dragover"].forEach((ev) => dz.addEventListener(ev, (e) => {
  e.preventDefault(); dz.classList.add("is-over");
}));
["dragleave", "drop"].forEach((ev) => dz.addEventListener(ev, (e) => {
  e.preventDefault(); dz.classList.remove("is-over");
}));
dz.addEventListener("drop", (e) => openDocument(e.dataTransfer.files[0]));

// Exposed so the app can be driven and inspected from the browser console.
window.demo = {
  get instance() { return instance; },
  get result() { return result; },
  get highlights() { return highlights; },
  openDocument, startExtraction, goToTable, goToRow, goToField, goToNote,
};

let toastTimer = null;
function toast(message, isError) {
  const el = $("toast");
  el.textContent = message;
  el.classList.toggle("is-error", !!isError);
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.hidden = true; }, 5200);
}
