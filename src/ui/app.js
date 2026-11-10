/* Kenny PC Cleaner & Optimization Studio - Interactive Frontend */

const SESSION_TOKEN = new URLSearchParams(window.location.search).get("token") || "";

const state = {
  isAdmin: false,
  dryRun: false,
  soundEnabled: true,
  telemetry: null,
  systemScan: null,
  browserScan: null,
  registryScan: null,
  backups: null,
  selectedSystemIds: new Set(["user_temp", "system_temp", "nvidia_dxcache", "recycle_bin", "windows_update", "game_dumps"]),
  browserSelections: {}, // { browserId: Set(['history','cache','leftovers']) }
  selectedRegistryIds: new Set(),
  selectedMemAreas: new Set(["working_set", "low_standby", "system_file_cache", "combine_memory"]),
  memoryStatus: null,
  advisorData: null,
  audioCtx: null,
};

function esc(str) {
  return String(str ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function formatBytes(bytes) {
  const n = Math.max(0, Number(bytes) || 0);
  if (n >= 1024 ** 3) return (n / 1024 ** 3).toFixed(2) + " GB";
  if (n >= 1024 ** 2) return (n / 1024 ** 2).toFixed(2) + " MB";
  if (n >= 1024) return (n / 1024).toFixed(1) + " KB";
  return n + " B";
}

function playTone(freq = 640, duration = 0.05, type = "sine", gainVal = 0.03) {
  if (!state.soundEnabled) return;
  try {
    if (!state.audioCtx) {
      state.audioCtx = new (window.AudioContext || window.webkitAudioContext)();
    }
    const ctx = state.audioCtx;
    if (ctx.state === "suspended") ctx.resume();
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = type;
    osc.frequency.setValueAtTime(freq, ctx.currentTime);
    gain.gain.setValueAtTime(gainVal, ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + duration);
    osc.connect(gain);
    gain.connect(ctx.destination);
    osc.start();
    osc.stop(ctx.currentTime + duration);
  } catch (_) {}
}

function playSuccessChime() {
  playTone(523.25, 0.09, "triangle", 0.04);
  setTimeout(() => playTone(659.25, 0.12, "triangle", 0.04), 80);
  setTimeout(() => playTone(783.99, 0.18, "triangle", 0.045), 160);
}

async function apiFetch(url, options = {}) {
  const headers = { ...(options.headers || {}) };
  if (SESSION_TOKEN) headers["X-Session-Token"] = SESSION_TOKEN;
  if (options.body && typeof options.body !== "string") {
    headers["Content-Type"] = "application/json";
    options.body = JSON.stringify(options.body);
  }
  const resp = await fetch(url, { ...options, headers });
  const data = await resp.json().catch(() => ({ ok: false, error: resp.statusText }));
  if (!resp.ok && data.ok !== false) data.ok = false;
  return data;
}

function appendConsoleLog(msg, level = "info") {
  const body = document.getElementById("console-body");
  const last = document.getElementById("console-last-line");
  const ts = new Date().toLocaleTimeString("en-GB", { hour12: false });
  const div = document.createElement("div");
  div.className = `log-line log-${level}`;
  div.textContent = `[${ts}] ${msg}`;
  body.appendChild(div);
  body.scrollTop = body.scrollHeight;
  last.textContent = msg;
}

/* ---------------- Tab Navigation ---------------- */
function switchTab(tabId) {
  playTone(520, 0.04);
  document.querySelectorAll(".nav-btn").forEach((b) => {
    b.classList.toggle("active", b.dataset.tab === tabId);
  });
  document.querySelectorAll(".tab-view").forEach((v) => {
    v.classList.toggle("active", v.id === `view-${tabId}`);
  });
  if (tabId === "memory") refreshMemoryStatus();
  if (tabId === "advisor" && !state.advisorData) runAdvisorScan(true);
}

document.querySelectorAll(".nav-btn").forEach((btn) => {
  btn.addEventListener("click", () => switchTab(btn.dataset.tab));
});

/* ---------------- Telemetry ---------------- */
async function refreshTelemetry() {
  const data = await apiFetch("/api/telemetry");
  if (!data.ok) return;
  state.telemetry = data;
  state.isAdmin = Boolean(data.is_admin);
  state.soundEnabled = data.settings?.sound_enabled ?? true;
  document.getElementById("btn-sound-toggle").textContent = state.soundEnabled ? "🔊" : "🔇";

  document.getElementById("pill-host").textContent = `PC: ${data.computer_name} (${data.username})`;
  document.getElementById("pill-os").textContent = `${data.os.product} • ${data.os.build}`;

  const adminBtn = document.getElementById("btn-admin-status");
  if (state.isAdmin) {
    adminBtn.className = "meta-pill admin-pill is-admin";
    adminBtn.textContent = "🛡️ ADMINISTRATOR MODE";
  } else {
    adminBtn.className = "meta-pill admin-pill no-admin";
    adminBtn.textContent = "⚠️ STANDARD MODE • Click to Elevate (UAC)";
  }

  document.getElementById("tele-cpu-val").textContent = `${data.cpu_percent}%`;
  document.getElementById("tele-cpu-bar").style.width = `${data.cpu_percent}%`;
  document.getElementById("tele-ram-val").textContent = `${data.memory.used_gb} / ${data.memory.total_gb} GB (${data.memory.percent}%)`;
  document.getElementById("tele-ram-bar").style.width = `${data.memory.percent}%`;
  const memBadge = document.getElementById("badge-memory");
  if (memBadge) memBadge.textContent = `${data.memory.percent}%`;

  if (data.c_drive) {
    document.getElementById("sidebar-c-free").textContent = `${data.c_drive.free_gb} GB free`;
    document.getElementById("sidebar-c-bar").style.width = `${data.c_drive.used_percent}%`;
  }

  const drivesWrap = document.getElementById("dashboard-drives");
  drivesWrap.innerHTML = (data.drives || [])
    .map(
      (d) => `
      <div class="drive-chip">
        <div class="drive-chip-top">
          <span>Drive ${esc(d.letter)}:</span>
          <span class="cyan">${d.free_gb} GB free</span>
        </div>
        <div class="progress-track">
          <div class="progress-fill" style="width:${d.used_percent}%"></div>
        </div>
        <div class="muted" style="margin-top:4px;font-size:11px">${d.used_gb} / ${d.total_gb} GB used</div>
      </div>`
    )
    .join("");

  const st = data.stats || {};
  document.getElementById("stat-lifetime-freed").textContent = st.total_bytes_freed_human || "0 B";
  document.getElementById("stat-lifetime-files").textContent = Number(st.total_files_cleaned || 0).toLocaleString();
  document.getElementById("stat-lifetime-reg").textContent = Number(st.total_registry_keys_cleaned || 0).toLocaleString();
  document.getElementById("last-clean-label").textContent = st.last_clean_at ? `Last run: ${st.last_clean_at}` : "No runs recorded yet";

  const actList = document.getElementById("activity-list");
  if (st.history && st.history.length) {
    actList.innerHTML = st.history
      .slice(0, 8)
      .map(
        (h) => `
        <div class="activity-row">
          <div>
            <strong>${esc(h.label)}</strong>
            <span class="muted"> • ${esc(h.timestamp)}</span>
          </div>
          <div>
            <span class="tag tag-emerald">${esc(h.bytes_freed_human)} freed</span>
            <span class="tag">${h.files_cleaned} files</span>
            <span class="tag tag-violet">${h.registry_keys_cleaned} reg keys</span>
          </div>
        </div>`
      )
      .join("");
  }
}

/* ---------------- Hero Gauge ---------------- */
function updateHeroGauge() {
  const sysBytes = state.systemScan?.total_bytes || 0;
  const brBytes = state.browserScan?.total_bytes || 0;
  const regCount = state.registryScan?.total_count || 0;
  const totalBytes = sysBytes + brBytes;

  const valEl = document.getElementById("hero-reclaim-size");
  const subEl = document.getElementById("hero-reclaim-sub");
  const ring = document.getElementById("hero-gauge-ring");

  if (!state.systemScan && !state.browserScan && !state.registryScan) {
    valEl.textContent = "READY";
    subEl.textContent = "Run Deep Scan";
    return;
  }

  valEl.textContent = formatBytes(totalBytes);
  subEl.textContent = `${regCount} empty reg keys`;

  const ratio = Math.min(1, totalBytes / (2 * 1024 ** 3)); // scale up to 2GB ring fill
  const circumference = 477.5;
  ring.style.strokeDashoffset = String(circumference * (1 - Math.max(0.08, ratio)));
  ring.style.stroke = totalBytes === 0 && regCount === 0 ? "#10b981" : "#00e5ff";
}

/* ---------------- System & Game Cleaner ---------------- */
function renderSystemTargets() {
  const grid = document.getElementById("system-targets-grid");
  const scan = state.systemScan;
  if (!scan) return;

  document.getElementById("badge-system").textContent = scan.total_human;

  grid.innerHTML = scan.targets
    .map((t) => {
      const checked = state.selectedSystemIds.has(t.id);
      const adminBadge = t.admin_required
        ? t.accessible
          ? `<span class="tag tag-emerald">ADMIN OK</span>`
          : `<span class="tag tag-amber">ADMIN REQUIRED</span>`
        : `<span class="tag">STANDARD</span>`;
      const batBadge = `<span class="tag tag-cyan">${esc(t.bat_ref)}</span>`;
      const pathsHtml = (t.paths || []).map((p) => esc(p)).join("<br/>");
      const samplesHtml =
        t.samples && t.samples.length
          ? `<div style="margin-top:6px;color:#93c5fd">Samples:</div>` + t.samples.map((s) => esc(s)).join("<br/>")
          : "";

      return `
        <div class="target-card ${checked ? "selected" : ""}">
          <div class="target-top">
            <label class="target-check-wrap">
              <input type="checkbox" class="sys-chk" data-id="${esc(t.id)}" ${checked ? "checked" : ""} />
              <div>
                <div class="target-title">${esc(t.name)}</div>
                <div class="target-desc">${esc(t.description)}</div>
              </div>
            </label>
            <div class="size-pill ${t.access_denied && t.file_count === 0 ? "amber" : ""}">${esc(t.size_human)}</div>
          </div>
          <div class="target-meta">
            ${batBadge}
            ${adminBadge}
            <span class="tag">${t.file_count.toLocaleString()} item(s)</span>
            <button class="btn btn-ghost btn-sm toggle-paths-btn" style="margin-left:auto;padding:2px 8px;font-size:11px">Inspect Paths</button>
          </div>
          <div class="paths-drawer hidden">${pathsHtml}${samplesHtml}</div>
        </div>`;
    })
    .join("");

  grid.querySelectorAll(".sys-chk").forEach((chk) => {
    chk.addEventListener("change", () => {
      playTone(720, 0.03);
      if (chk.checked) state.selectedSystemIds.add(chk.dataset.id);
      else state.selectedSystemIds.delete(chk.dataset.id);
      chk.closest(".target-card").classList.toggle("selected", chk.checked);
      updateSystemFooterSummary();
    });
  });

  grid.querySelectorAll(".toggle-paths-btn").forEach((btn) => {
    btn.addEventListener("click", () => {
      const drawer = btn.closest(".target-card").querySelector(".paths-drawer");
      drawer.classList.toggle("hidden");
    });
  });

  updateSystemFooterSummary();
}

function updateSystemFooterSummary() {
  const targets = state.systemScan?.targets || [];
  let count = 0;
  let bytes = 0;
  for (const t of targets) {
    if (state.selectedSystemIds.has(t.id)) {
      count++;
      bytes += t.size_bytes;
    }
  }
  document.getElementById("sys-selected-summary").textContent = `${count} target(s) (${formatBytes(bytes)})`;
}

/* ---------------- Browser Cleaner ---------------- */
function ensureDefaultBrowserSelections() {
  const browsers = state.browserScan?.browsers || [];
  for (const b of browsers) {
    if (!state.browserSelections[b.id]) {
      state.browserSelections[b.id] = new Set(["history", "cache", "leftovers"]);
    }
  }
}

function renderBrowsers() {
  const scan = state.browserScan;
  if (!scan) return;
  ensureDefaultBrowserSelections();

  document.getElementById("badge-browsers").textContent = `${scan.browser_count} • ${scan.total_human}`;

  const banner = document.getElementById("running-browsers-banner");
  const running = scan.running_browsers || [];
  if (running.length > 0) {
    const names = [...new Set(running.map((r) => r.name))].join(", ");
    document.getElementById("running-browsers-names").textContent = names;
    banner.classList.remove("hidden");
  } else {
    banner.classList.add("hidden");
  }

  const grid = document.getElementById("browsers-grid");
  if (!scan.browsers.length) {
    grid.innerHTML = `<div class="empty-state">No browsers or leftover browser artifacts found.</div>`;
    updateBrowserFooterSummary();
    return;
  }

  grid.innerHTML = scan.browsers
    .map((b) => {
      const sel = state.browserSelections[b.id] || new Set();
      const statusTag =
        b.status === "running"
          ? `<span class="tag tag-amber">RUNNING</span>`
          : b.status === "installed"
          ? `<span class="tag tag-emerald">INSTALLED</span>`
          : `<span class="tag tag-rose">LEFTOVER (UNINSTALLED)</span>`;

      const profilesTxt = b.profiles.length ? b.profiles.join(", ") : "Root / Default";
      const catOrder = ["history", "cache", "leftovers", "cookies"];
      const catRows = catOrder
        .map((ck) => {
          const cat = b.categories[ck];
          if (!cat) return "";
          const isChecked = sel.has(ck);
          const extraNote = cat.notes?.length ? `<span class="tag tag-emerald">${esc(cat.notes[0])}</span>` : "";
          const optWarn = ck === "cookies" ? `<span class="tag tag-amber">Signs out</span>` : "";
          return `
            <div class="browser-cat-row">
              <label>
                <input type="checkbox" class="br-cat-chk" data-bid="${esc(b.id)}" data-cat="${ck}" ${isChecked ? "checked" : ""} />
                <span>${esc(cat.label)}</span>
                ${extraNote}${optWarn}
              </label>
              <span class="size-pill" style="font-size:12px">${esc(cat.size_human)}</span>
            </div>`;
        })
        .join("");

      const allItemLines = catOrder
        .flatMap((ck) => (b.categories[ck]?.items || []).map((it) => `[${ck.toUpperCase()}] ${it.path} (${it.size_human})`))
        .slice(0, 60)
        .map((line) => esc(line))
        .join("<br/>");

      return `
        <div class="browser-card" style="border-top-color:${esc(b.color)}">
          <div class="browser-head">
            <div>
              <div style="font-size:16px;font-weight:700">${esc(b.name)}</div>
              <div class="muted">Profiles: ${esc(profilesTxt)}</div>
            </div>
            <div style="text-align:right">
              <div class="size-pill">${esc(b.recommended_human)}</div>
              <div style="margin-top:4px;display:flex;gap:5px;justify-content:flex-end">
                <span class="tag">${esc(b.engine.toUpperCase())}</span>
                ${statusTag}
              </div>
            </div>
          </div>
          ${catRows}
          <div style="display:flex;justify-content:space-between;align-items:center">
            <span class="muted">${b.recommended_files.toLocaleString()} item(s)</span>
            <button class="btn btn-ghost btn-sm br-inspect-btn" style="padding:3px 9px;font-size:11px">Inspect Files</button>
          </div>
          <div class="paths-drawer hidden">${allItemLines || "No files in target categories."}</div>
        </div>`;
    })
    .join("");

  grid.querySelectorAll(".br-cat-chk").forEach((chk) => {
    chk.addEventListener("change", () => {
      playTone(720, 0.03);
      const bid = chk.dataset.bid;
      const cat = chk.dataset.cat;
      if (!state.browserSelections[bid]) state.browserSelections[bid] = new Set();
      if (chk.checked) state.browserSelections[bid].add(cat);
      else state.browserSelections[bid].delete(cat);
      updateBrowserFooterSummary();
    });
  });

  grid.querySelectorAll(".br-inspect-btn").forEach((btn) => {
    btn.addEventListener("click", () => {
      btn.closest(".browser-card").querySelector(".paths-drawer").classList.toggle("hidden");
    });
  });

  updateBrowserFooterSummary();
}

function updateBrowserFooterSummary() {
  const browsers = state.browserScan?.browsers || [];
  let totalBytes = 0;
  let totalCats = 0;
  for (const b of browsers) {
    const sel = state.browserSelections[b.id] || new Set();
    for (const ck of sel) {
      totalCats++;
      totalBytes += b.categories[ck]?.size_bytes || 0;
    }
  }
  document.getElementById("br-selected-summary").textContent = `${totalCats} category group(s) (${formatBytes(totalBytes)})`;
}

/* ---------------- Registry Empty Folders ---------------- */
function getFilteredRegistryKeys() {
  const keys = state.registryScan?.keys || [];
  const q = document.getElementById("reg-search").value.trim().toLowerCase();
  const hive = document.getElementById("reg-hive-filter").value;
  return keys.filter((k) => {
    if (hive !== "ALL" && k.hive !== hive) return false;
    if (q && !k.full_path.toLowerCase().includes(q) && !k.vendor.toLowerCase().includes(q)) return false;
    return true;
  });
}

function renderRegistryTable() {
  const scan = state.registryScan;
  if (!scan) return;
  document.getElementById("badge-registry").textContent = String(scan.total_count);

  const filtered = getFilteredRegistryKeys();
  const tbody = document.getElementById("reg-table-body");
  if (!filtered.length) {
    tbody.innerHTML = `<tr><td colspan="6" class="empty-cell">No empty registry folders match the current filter.</td></tr>`;
    updateRegistryFooterSummary();
    return;
  }

  tbody.innerHTML = filtered
    .map((k) => {
      const checked = state.selectedRegistryIds.has(k.id);
      const disabled = !k.accessible;
      const hiveTag = k.hive === "HKCU" ? `<span class="tag tag-cyan">HKCU</span>` : `<span class="tag tag-violet">HKLM</span>`;
      const kindTag = k.kind === "Empty Subtree" ? `<span class="tag tag-amber">Empty Subtree</span>` : `<span class="tag">Empty Leaf</span>`;
      const accTag = k.accessible ? `<span class="tag tag-emerald">Accessible</span>` : `<span class="tag tag-amber">Admin Req</span>`;
      return `
        <tr>
          <td><input type="checkbox" class="reg-row-chk" data-id="${esc(k.id)}" ${checked ? "checked" : ""} ${disabled ? "disabled" : ""} /></td>
          <td>${hiveTag}</td>
          <td>${kindTag}</td>
          <td>${esc(k.vendor)}</td>
          <td class="mono-path">${esc(k.full_path)}</td>
          <td>${accTag}</td>
        </tr>`;
    })
    .join("");

  tbody.querySelectorAll(".reg-row-chk").forEach((chk) => {
    chk.addEventListener("change", () => {
      playTone(720, 0.03);
      if (chk.checked) state.selectedRegistryIds.add(chk.dataset.id);
      else state.selectedRegistryIds.delete(chk.dataset.id);
      updateRegistryFooterSummary();
    });
  });

  updateRegistryFooterSummary();
}

function updateRegistryFooterSummary() {
  document.getElementById("reg-selected-summary").textContent = `${state.selectedRegistryIds.size} empty key(s)`;
}

/* ---------------- Backups & Recovery ---------------- */
async function refreshBackups() {
  const data = await apiFetch("/api/backups");
  if (!data.ok) return;
  state.backups = data;

  document.getElementById("backup-root-display").textContent = `Vault: ${data.backup_root}`;
  const snaps = data.snapshots || [];
  document.getElementById("badge-backups").textContent = String(snaps.length);
  document.getElementById("snapshots-count-label").textContent = `${snaps.length} snapshot(s)`;

  const list = document.getElementById("snapshots-list");
  if (!snaps.length) {
    list.innerHTML = `<div class="empty-state">No snapshots created yet. Create one before cleaning or run any safe cleanup!</div>`;
  } else {
    list.innerHTML = snaps
      .map((s) => {
        const hasQuick = Boolean(s.registry?.surgical_reg_path);
        const hasFull = Boolean(s.registry?.full_zip_path);
        const hasFiles = (s.archived_files_count || 0) > 0;
        const rpTag = s.restore_point?.created
          ? `<span class="tag tag-emerald">Win Restore Point Created</span>`
          : `<span class="tag">${esc(s.restore_point?.status || "Local Snapshot")}</span>`;

        return `
          <div class="snapshot-card">
            <div class="snapshot-head">
              <div>
                <strong>${esc(s.reason)}</strong>
                <div class="muted">${esc(s.created_at)} • ${esc(s.total_size_human)} • ${s.archived_files_count || 0} archived file(s)</div>
              </div>
              <div style="display:flex;gap:5px;flex-wrap:wrap;justify-content:flex-end">
                ${hasQuick ? `<span class="tag tag-violet">Surgical .reg (${s.registry.surgical_keys_count})</span>` : ""}
                ${hasFull ? `<span class="tag tag-cyan">Full Hive .reg</span>` : ""}
                ${rpTag}
              </div>
            </div>
            <div class="snapshot-actions">
              <button class="btn btn-ghost btn-sm snap-inspect" data-id="${esc(s.snapshot_id)}">🔍 Inspect</button>
              <button class="btn btn-ghost btn-sm snap-verify" data-id="${esc(s.snapshot_id)}">✓ Verify SHA-256</button>
              ${hasQuick ? `<button class="btn btn-secondary btn-sm snap-restore-quick" data-id="${esc(s.snapshot_id)}">↩ Restore Deleted Keys</button>` : ""}
              ${hasFull ? `<button class="btn btn-ghost btn-sm snap-restore-hkcu" data-id="${esc(s.snapshot_id)}">↩ Restore HKCU Hive</button>` : ""}
              ${hasFiles ? `<button class="btn btn-secondary btn-sm snap-restore-files" data-id="${esc(s.snapshot_id)}">↩ Restore Files</button>` : ""}
              <button class="btn btn-ghost btn-sm snap-open" data-id="${esc(s.snapshot_id)}">📂 Folder</button>
              <button class="btn btn-danger btn-sm snap-del" data-id="${esc(s.snapshot_id)}">🗑</button>
            </div>
          </div>`;
      })
      .join("");

    list.querySelectorAll(".snap-inspect").forEach((b) => b.addEventListener("click", () => inspectSnapshot(b.dataset.id)));
    list.querySelectorAll(".snap-verify").forEach((b) => b.addEventListener("click", () => verifySnapshot(b.dataset.id)));
    list.querySelectorAll(".snap-restore-quick").forEach((b) => b.addEventListener("click", () => restoreReg(b.dataset.id, "quick")));
    list.querySelectorAll(".snap-restore-hkcu").forEach((b) => b.addEventListener("click", () => restoreReg(b.dataset.id, "hkcu")));
    list.querySelectorAll(".snap-restore-files").forEach((b) => b.addEventListener("click", () => restoreFiles(b.dataset.id)));
    list.querySelectorAll(".snap-open").forEach((b) => b.addEventListener("click", () => apiFetch(`/api/backups/${b.dataset.id}/open`, { method: "POST" })));
    list.querySelectorAll(".snap-del").forEach((b) => b.addEventListener("click", () => deleteSnapshot(b.dataset.id)));
  }

  const rpWrap = document.getElementById("windows-rp-container");
  const wrp = data.windows_restore || {};
  if (wrp.admin_required) {
    rpWrap.innerHTML = `
      <div class="alert-banner warning" style="flex-direction:column;align-items:flex-start">
        <div><strong>Admin Required:</strong> ${esc(wrp.message)}</div>
        <button class="btn btn-warning btn-sm" onclick="document.getElementById('btn-admin-status').click()">Elevate to Administrator</button>
      </div>`;
  } else if (wrp.restore_points && wrp.restore_points.length) {
    rpWrap.innerHTML = wrp.restore_points
      .slice(0, 8)
      .map(
        (rp) => `
        <div class="activity-row" style="margin-bottom:8px">
          <div>
            <strong>#${esc(rp.SequenceNumber)} • ${esc(rp.Description)}</strong>
            <div class="muted">${esc(rp.CreationTime)}</div>
          </div>
        </div>`
      )
      .join("");
  } else {
    rpWrap.innerHTML = `<div class="empty-state">${esc(wrp.message || "No Windows Restore Points found.")}</div>`;
  }
}

function inspectSnapshot(id) {
  const snap = (state.backups?.snapshots || []).find((x) => x.snapshot_id === id);
  if (!snap) return;
  document.getElementById("inspect-modal-title").textContent = snap.reason;
  document.getElementById("inspect-modal-sub").textContent = `${snap.snapshot_id} • ${snap.created_at}`;
  const filesHtml = (snap.archived_files || [])
    .map((f) => `<div>• ${esc(f.original_path)} (${formatBytes(f.size_bytes)})</div>`)
    .join("");
  const sumsHtml = Object.entries(snap.checksums_sha256 || {})
    .map(([k, v]) => `<div><strong>${esc(k)}</strong>: <span class="mono-path">${esc(v)}</span></div>`)
    .join("");
  document.getElementById("inspect-modal-body").innerHTML = `
    <p><strong>Vault Path:</strong> <span class="mono-path">${esc(snap.path)}</span></p>
    <p style="margin-top:8px"><strong>Restore Point Status:</strong> ${esc(snap.restore_point?.message || "N/A")}</p>
    <h4 style="margin:12px 0 6px">Archived Critical Files (${snap.archived_files_count || 0})</h4>
    <div class="paths-drawer">${filesHtml || "System state manifest + registry backup only."}</div>
    <h4 style="margin:12px 0 6px">SHA-256 Checksums</h4>
    <div class="paths-drawer">${sumsHtml}</div>
  `;
  document.getElementById("inspect-modal").classList.remove("hidden");
}

async function verifySnapshot(id) {
  const res = await apiFetch(`/api/backups/${id}/verify`, { method: "POST" });
  if (res.verified) {
    playSuccessChime();
    appendConsoleLog(`Backup ${id} SHA-256 verification PASSED.`, "success");
    alert(`Integrity Verified! All ${res.files.length} backup file(s) match SHA-256 checksums.`);
  } else {
    appendConsoleLog(`Backup ${id} verification FAILED!`, "error");
    alert("Verification warning: One or more backup artifacts are missing or altered.");
  }
}

async function restoreReg(id, mode) {
  const label = mode === "quick" ? "deleted registry keys (Quick Undo .reg)" : "full HKCU\\Software hive";
  if (!confirm(`Restore ${label} from snapshot ${id}?`)) return;
  const res = await apiFetch(`/api/backups/${id}/restore/registry`, {
    method: "POST",
    body: { mode },
  });
  appendConsoleLog(res.message || "Registry restore executed.", res.ok ? "success" : "error");
  if (res.ok) playSuccessChime();
  alert(res.message);
  await runRegistryScanOnly();
}

async function restoreFiles(id) {
  if (!confirm(`Restore archived browser/user files from snapshot ${id} back to their original paths?`)) return;
  const res = await apiFetch(`/api/backups/${id}/restore/files`, { method: "POST" });
  appendConsoleLog(res.message || "File restore executed.", res.ok ? "success" : "error");
  if (res.ok) playSuccessChime();
  alert(res.message);
}

async function deleteSnapshot(id) {
  if (!confirm(`Permanently delete backup snapshot ${id}?`)) return;
  const res = await apiFetch(`/api/backups/${id}`, { method: "DELETE" });
  if (res.ok) {
    appendConsoleLog(`Deleted backup ${id}`, "info");
    refreshBackups();
  }
}

/* ---------------- Job Polling & Pipeline Modal ---------------- */
async function watchJob(jobTitle, jobId, onComplete) {
  const modal = document.getElementById("job-modal");
  const titleEl = document.getElementById("job-modal-title");
  const phaseEl = document.getElementById("job-modal-phase");
  const pctEl = document.getElementById("job-modal-percent");
  const barEl = document.getElementById("job-modal-bar");
  const logsEl = document.getElementById("job-modal-logs");
  const summaryEl = document.getElementById("job-modal-summary");
  const closeBtn = document.getElementById("btn-job-close");
  const steps = modal.querySelectorAll(".pipe-step");

  titleEl.textContent = jobTitle;
  phaseEl.textContent = "Starting...";
  pctEl.textContent = "5%";
  barEl.style.width = "5%";
  logsEl.innerHTML = "";
  summaryEl.classList.add("hidden");
  closeBtn.classList.add("hidden");
  steps.forEach((s) => { s.className = "pipe-step"; });
  modal.classList.remove("hidden");

  let lastLogIdx = 0;
  while (true) {
    const data = await apiFetch(`/api/jobs/${jobId}`);
    if (!data.ok) break;
    const job = data.job;

    phaseEl.textContent = job.phase || "";
    pctEl.textContent = `${job.progress}%`;
    barEl.style.width = `${job.progress}%`;

    // Animate 4 pipeline pills based on progress
    const activeStage = job.progress < 30 ? 1 : job.progress < 60 ? 2 : job.progress < 85 ? 3 : 4;
    steps.forEach((s) => {
      const n = Number(s.dataset.step);
      s.classList.toggle("done", job.status === "completed" || n < activeStage);
      s.classList.toggle("active", job.status === "running" && n === activeStage);
    });

    const newLogs = (job.logs || []).slice(lastLogIdx);
    for (const lg of newLogs) {
      const div = document.createElement("div");
      div.className = `log-line log-${lg.level || "info"}`;
      div.textContent = `[${lg.ts}] ${lg.message}`;
      logsEl.appendChild(div);
      appendConsoleLog(lg.message, lg.level || "info");
    }
    lastLogIdx = (job.logs || []).length;
    logsEl.scrollTop = logsEl.scrollHeight;

    if (job.status === "completed" || job.status === "failed") {
      closeBtn.classList.remove("hidden");
      if (job.status === "completed") {
        playSuccessChime();
        if (onComplete) await onComplete(job.result, summaryEl);
      } else {
        summaryEl.classList.remove("hidden");
        summaryEl.style.borderColor = "#f43f5e";
        summaryEl.innerHTML = `<strong>Error:</strong> ${esc(job.error || "Unknown error")}`;
      }
      break;
    }
    await new Promise((r) => setTimeout(r, 320));
  }
}

/* ---------------- Actions ---------------- */
async function runFullScan() {
  playTone(620, 0.06);
  const res = await apiFetch("/api/scan/all", { method: "POST" });
  if (!res.ok) return;
  await watchJob("Smart Deep System, Browser & Registry Scan", res.job_id, async (result, summaryEl) => {
    state.systemScan = result.system;
    state.browserScan = result.browsers;
    state.registryScan = result.registry;

    // Auto-select all accessible empty registry keys on fresh scan
    state.selectedRegistryIds = new Set(
      (result.registry?.keys || []).filter((k) => k.accessible).map((k) => k.id)
    );

    renderSystemTargets();
    renderBrowsers();
    renderRegistryTable();
    updateHeroGauge();

    summaryEl.classList.remove("hidden");
    summaryEl.innerHTML = `
      <strong>Deep Scan Complete!</strong><br/>
      • System &amp; Game Junk: <strong>${esc(result.system.total_human)}</strong> (${result.system.total_files} items)<br/>
      • Browsers (${result.browsers.browser_count} found): <strong>${esc(result.browsers.total_human)}</strong> (${result.browsers.total_files} items)<br/>
      • Empty Registry Folders: <strong>${result.registry.total_count}</strong> key(s)
    `;
  });
}

async function runSystemScanOnly() {
  const res = await apiFetch("/api/scan/system", { method: "POST" });
  if (res.ok) {
    state.systemScan = res;
    renderSystemTargets();
    updateHeroGauge();
  }
}

async function runBrowserScanOnly() {
  const res = await apiFetch("/api/scan/browsers", { method: "POST" });
  if (res.ok) {
    state.browserScan = res;
    renderBrowsers();
    updateHeroGauge();
  }
}

async function runRegistryScanOnly() {
  const res = await apiFetch("/api/scan/registry", { method: "POST" });
  if (res.ok) {
    state.registryScan = res;
    state.selectedRegistryIds = new Set(res.keys.filter((k) => k.accessible).map((k) => k.id));
    renderRegistryTable();
    updateHeroGauge();
  }
}

async function executeCleanupPipeline({ systemIds = [], browserSelections = {}, registryKeys = [], reason = "Safe Cleanup" }) {
  playTone(760, 0.06);
  const res = await apiFetch("/api/clean/execute", {
    method: "POST",
    body: {
      system_ids: systemIds,
      browser_selections: browserSelections,
      registry_keys: registryKeys,
      dry_run: state.dryRun,
      create_backup: true,
      include_restore_point: true,
      reason,
    },
  });
  if (!res.ok) return;

  const prefix = state.dryRun ? "[SIMULATION] " : "";
  await watchJob(`${prefix}${reason}`, res.job_id, async (result, summaryEl) => {
    summaryEl.classList.remove("hidden");
    const bkpLine = result.backup
      ? `<br/>• Backup Created: <strong>${esc(result.backup.snapshot_id)}</strong> (${esc(result.backup.total_size_human)})`
      : "";
    summaryEl.innerHTML = `
      <strong>${result.dry_run ? "Simulation Complete (Nothing Deleted)" : "Optimization Complete!"}</strong><br/>
      • Space Freed: <strong>${esc(result.total_freed_human)}</strong> (${result.total_deleted_files} file(s))<br/>
      • Empty Registry Folders Removed: <strong>${result.total_registry_cleaned}</strong><br/>
      • Free Space on C:: <strong>${esc(result.c_drive_free_after_human)}</strong>
      ${bkpLine}
    `;
    await refreshTelemetry();
    await refreshBackups();
    // Refresh scan tabs silently
    await Promise.all([runSystemScanOnly(), runBrowserScanOnly(), runRegistryScanOnly()]);
  });
}

/* ---------------- Wire Event Listeners ---------------- */
document.getElementById("btn-smart-scan").addEventListener("click", runFullScan);

document.getElementById("toggle-dry-run").addEventListener("change", (e) => {
  state.dryRun = e.target.checked;
  document.getElementById("dry-run-label").textContent = state.dryRun ? "SIMULATION MODE" : "LIVE MODE";
  playTone(state.dryRun ? 480 : 680, 0.05);
  appendConsoleLog(`Switched mode to ${state.dryRun ? "SIMULATION (DRY-RUN)" : "LIVE MODE"}`, "info");
});

document.getElementById("btn-sound-toggle").addEventListener("click", async () => {
  state.soundEnabled = !state.soundEnabled;
  document.getElementById("btn-sound-toggle").textContent = state.soundEnabled ? "🔊" : "🔇";
  if (state.soundEnabled) playTone(700, 0.05);
  await apiFetch("/api/settings", { method: "POST", body: { sound_enabled: state.soundEnabled } });
});

document.getElementById("btn-admin-status").addEventListener("click", async () => {
  if (state.isAdmin) {
    alert("Already running with full Administrator privileges.");
    return;
  }
  if (!confirm("Relaunch Kenny PC Optimizer Studio as Administrator for full System Temp, Windows Update, HKLM Registry, and System Restore Point access?")) return;
  const res = await apiFetch("/api/system/elevate", { method: "POST" });
  if (res.ok) {
    setTimeout(() => { try { window.close(); } catch (_) {} }, 500);
  } else {
    alert(res.message || "Elevation declined.");
  }
});

document.getElementById("btn-sidebar-open-vault").addEventListener("click", () => apiFetch("/api/backups/open_root", { method: "POST" }));
document.getElementById("btn-vault-open").addEventListener("click", () => apiFetch("/api/backups/open_root", { method: "POST" }));
document.getElementById("btn-launch-rstrui").addEventListener("click", () => apiFetch("/api/backups/launch_rstrui", { method: "POST" }));
document.getElementById("btn-backups-refresh").addEventListener("click", refreshBackups);

document.getElementById("btn-toggle-console").addEventListener("click", () => {
  document.getElementById("console-drawer").classList.toggle("open");
});

document.getElementById("btn-job-close").addEventListener("click", () => {
  document.getElementById("job-modal").classList.add("hidden");
});
document.getElementById("btn-inspect-close").addEventListener("click", () => {
  document.getElementById("inspect-modal").classList.add("hidden");
});

// Dashboard quick tiles
document.getElementById("btn-quick-bat").addEventListener("click", () => {
  executeCleanupPipeline({
    systemIds: ["user_temp", "system_temp", "nvidia_dxcache", "recycle_bin", "windows_update", "game_dumps"],
    reason: "Master_Delete.bat Recommended Suite",
  });
});

document.getElementById("btn-quick-all").addEventListener("click", async () => {
  if (!state.browserScan || !state.registryScan || !state.systemScan) {
    await runFullScan();
  }
  const sysIds = (state.systemScan?.targets || []).filter((t) => t.recommended).map((t) => t.id);
  const brSel = {};
  for (const b of state.browserScan?.browsers || []) {
    brSel[b.id] = ["history", "cache", "leftovers"];
  }
  const regKeys = (state.registryScan?.keys || []).filter((k) => k.accessible);
  executeCleanupPipeline({
    systemIds: sysIds,
    browserSelections: brSel,
    registryKeys: regKeys,
    reason: "Full PC Optimization Suite",
  });
});

document.getElementById("btn-quick-backup").addEventListener("click", () => {
  switchTab("backups");
  document.getElementById("btn-create-backup-now").click();
});

/* ---------------- RAM & Memory Optimizer ---------------- */
async function refreshMemoryStatus() {
  const data = await apiFetch("/api/memory/status");
  if (!data.ok) return;
  state.memoryStatus = data;

  const phys = data.physical || {};
  const brk = data.breakdown || {};
  const commit = data.commit || {};
  const total = Math.max(1, Number(phys.total_bytes || 1));

  const standby = Number(brk.standby_bytes || 0);
  const modified = Number(brk.modified_bytes || 0);
  const freeZero = Number(brk.free_zero_bytes || 0);
  const activeWorking = Math.max(0, total - standby - modified - freeZero);

  const pct = (v) => ((v / total) * 100).toFixed(1) + "%";
  document.getElementById("mem-seg-active").style.width = pct(activeWorking);
  document.getElementById("mem-seg-standby").style.width = pct(standby);
  document.getElementById("mem-seg-modified").style.width = pct(modified);
  document.getElementById("mem-seg-free").style.width = pct(freeZero);

  document.getElementById("mem-summary-pill").textContent =
    `${phys.used_human || "0 B"} / ${phys.total_human || "0 B"} (${phys.percent || 0}%)`;

  document.getElementById("mem-legend-grid").innerHTML = `
    <div class="mem-legend-chip"><span><span class="dot-swatch" style="background:#ec4899"></span>Active Working</span><strong>${formatBytes(activeWorking)}</strong></div>
    <div class="mem-legend-chip"><span><span class="dot-swatch" style="background:#00e5ff"></span>Standby Cache</span><strong>${esc(brk.standby_human)}</strong></div>
    <div class="mem-legend-chip"><span><span class="dot-swatch" style="background:#f59e0b"></span>Modified Pages</span><strong>${esc(brk.modified_human)}</strong></div>
    <div class="mem-legend-chip"><span><span class="dot-swatch" style="background:#10b981"></span>Free / Zeroed</span><strong>${esc(brk.free_zero_human)}</strong></div>
    <div class="mem-legend-chip"><span>System File Cache</span><strong>${esc(brk.system_cache_human)}</strong></div>
    <div class="mem-legend-chip"><span>Pagefile / Commit</span><strong>${esc(commit.total_human)} / ${esc(commit.limit_human)}</strong></div>
  `;

  const areasGrid = document.getElementById("mem-areas-grid");
  areasGrid.innerHTML = (data.areas || [])
    .map((a) => {
      const checked = state.selectedMemAreas.has(a.id);
      const adminTag = a.admin_required
        ? (data.is_admin ? `<span class="tag tag-emerald">ADMIN OK</span>` : `<span class="tag tag-amber">ADMIN REQ</span>`)
        : `<span class="tag tag-cyan">USER + ADMIN</span>`;
      const modeTag = a.safe_default ? `<span class="tag tag-emerald">SAFE</span>` : `<span class="tag tag-amber">AGGRESSIVE</span>`;
      return `
        <div class="target-card ${checked ? "selected" : ""}">
          <div class="target-top">
            <label class="target-check-wrap">
              <input type="checkbox" class="mem-area-chk" data-id="${esc(a.id)}" ${checked ? "checked" : ""} />
              <div>
                <div class="target-title">${esc(a.name)}</div>
                <div class="target-desc">${esc(a.description)}</div>
              </div>
            </label>
            <div class="size-pill">${esc(a.size_human)}</div>
          </div>
          <div class="target-meta">${modeTag}${adminTag}</div>
        </div>`;
    })
    .join("");

  areasGrid.querySelectorAll(".mem-area-chk").forEach((chk) => {
    chk.addEventListener("change", () => {
      playTone(720, 0.03);
      if (chk.checked) state.selectedMemAreas.add(chk.dataset.id);
      else state.selectedMemAreas.delete(chk.dataset.id);
      chk.closest(".target-card").classList.toggle("selected", chk.checked);
      document.getElementById("mem-selected-summary").textContent = `${state.selectedMemAreas.size} area(s)`;
    });
  });
  document.getElementById("mem-selected-summary").textContent = `${state.selectedMemAreas.size} area(s)`;

  const procsBody = document.getElementById("mem-procs-body");
  const procs = data.top_processes || [];
  procsBody.innerHTML = procs.length
    ? procs.map((p) => `
      <tr>
        <td><strong>${esc(p.name)}</strong></td>
        <td class="mono-path">${p.pid}</td>
        <td class="size-pill" style="font-size:12px">${esc(p.working_set_human)}</td>
        <td class="muted">${esc(p.private_human)}</td>
        <td><button class="btn btn-ghost btn-sm mem-trim-btn" data-pid="${p.pid}">✂ Trim</button></td>
      </tr>`).join("")
    : `<tr><td colspan="5" class="empty-cell">No processes found.</td></tr>`;

  procsBody.querySelectorAll(".mem-trim-btn").forEach((btn) => {
    btn.addEventListener("click", async () => {
      playTone(680, 0.05);
      const res = await apiFetch("/api/memory/trim_process", {
        method: "POST",
        body: { pid: Number(btn.dataset.pid) },
      });
      appendConsoleLog(res.message || "Trim completed.", res.ok ? "success" : "warn");
      if (res.ok) playSuccessChime();
      await refreshMemoryStatus();
    });
  });

  const s = data.settings || {};
  document.getElementById("chk-ram-auto-thresh").checked = Boolean(s.ram_auto_threshold_enabled);
  document.getElementById("num-ram-thresh").value = Number(s.ram_auto_threshold_percent || 80);
  document.getElementById("chk-ram-auto-int").checked = Boolean(s.ram_auto_interval_enabled);
  document.getElementById("num-ram-int").value = Number(s.ram_auto_interval_minutes || 30);

  const histEl = document.getElementById("mem-history-list");
  const hist = data.history || [];
  if (hist.length) {
    histEl.innerHTML = hist.map((h) => `
      <div class="activity-row">
        <span><strong>${esc(h.timestamp)}</strong> • ${esc(h.used_before_human)} → ${esc(h.used_after_human)}</span>
        <span class="tag tag-emerald">${esc(h.ram_freed_human)} RAM + ${esc(h.standby_freed_human)} Standby</span>
      </div>`).join("");
  }
}

async function runMemoryBoost(areas) {
  const chosen = areas && areas.length ? areas : [...state.selectedMemAreas];
  if (!chosen.length) return alert("Select at least one memory area.");
  playTone(760, 0.06);
  const res = await apiFetch("/api/memory/clean", {
    method: "POST",
    body: { areas: chosen, dry_run: state.dryRun },
  });
  if (res.ok) {
    playSuccessChime();
    const msg = res.dry_run
      ? "[DRY-RUN] Simulated RAM optimization (nothing purged)."
      : `RAM Boost Complete: Freed ${res.ram_freed_human} Active RAM + ${res.standby_freed_human} Standby (${res.combined_human} combined)`;
    appendConsoleLog(msg, "success");
    await Promise.all([refreshMemoryStatus(), refreshTelemetry()]);
  }
}

document.getElementById("btn-quick-ram").addEventListener("click", () => {
  runMemoryBoost(["working_set", "low_standby", "system_file_cache", "combine_memory"]);
});
document.getElementById("btn-mem-boost").addEventListener("click", () => runMemoryBoost());
document.getElementById("btn-mem-refresh").addEventListener("click", refreshMemoryStatus);
document.getElementById("btn-mem-preset-safe").addEventListener("click", () => {
  state.selectedMemAreas = new Set(["working_set", "low_standby", "system_file_cache", "combine_memory"]);
  refreshMemoryStatus();
});
document.getElementById("btn-mem-preset-deep").addEventListener("click", () => {
  if (!confirm("Enabling Full Standby List & Modified Page List may briefly pause disk-cached apps for a few seconds. Select all aggressive memory areas?")) return;
  state.selectedMemAreas = new Set(["working_set", "low_standby", "system_file_cache", "combine_memory", "registry_cache", "standby_list", "modified_list"]);
  refreshMemoryStatus();
});
document.getElementById("btn-save-ram-rules").addEventListener("click", async () => {
  const res = await apiFetch("/api/settings", {
    method: "POST",
    body: {
      ram_auto_threshold_enabled: document.getElementById("chk-ram-auto-thresh").checked,
      ram_auto_threshold_percent: Number(document.getElementById("num-ram-thresh").value || 80),
      ram_auto_interval_enabled: document.getElementById("chk-ram-auto-int").checked,
      ram_auto_interval_minutes: Number(document.getElementById("num-ram-int").value || 30),
      ram_auto_areas: [...state.selectedMemAreas],
    },
  });
  if (res.ok) {
    playSuccessChime();
    appendConsoleLog("Saved Auto-Boost RAM rules.", "success");
  }
});

/* ---------------- Drivers, App Updates & Essentials Advisor ---------------- */
function renderAdvisorDrivers() {
  const data = state.advisorData;
  if (!data) return;
  const q = (document.getElementById("adv-driver-search").value || "").trim().toLowerCase();
  const list = (data.drivers || []).filter((d) =>
    !q ||
    d.name.toLowerCase().includes(q) ||
    d.device_class.toLowerCase().includes(q) ||
    d.manufacturer.toLowerCase().includes(q)
  );
  const tbody = document.getElementById("adv-drivers-body");
  tbody.innerHTML = list.length
    ? list.map((d) => {
        const stTag = d.status_flag === "Current"
          ? `<span class="tag tag-emerald">Current</span>`
          : `<span class="tag tag-amber">${esc(d.status_flag)}</span>`;
        return `
          <tr>
            <td><span class="tag tag-cyan">${esc(d.device_class)}</span></td>
            <td><strong>${esc(d.name)}</strong></td>
            <td class="mono-path">${esc(d.version)}</td>
            <td class="muted">${esc(d.date || "--")}</td>
            <td class="muted">${esc(d.manufacturer)}</td>
            <td>${stTag}</td>
          </tr>`;
      }).join("")
    : `<tr><td colspan="6" class="empty-cell">No matching drivers.</td></tr>`;
}

function renderAdvisorData() {
  const d = state.advisorData;
  if (!d) return;

  const updCount = Number(d.app_updates_count || 0);
  const badge = document.getElementById("badge-advisor");
  if (badge) badge.textContent = updCount > 0 ? `${updCount} upd` : "OK";

  const hw = d.hardware || {};
  const wu = d.windows_update || {};
  const disksTxt = (hw.disks || []).map((dk) => `${dk.model} (${dk.size_human}, ${dk.status})`).join(" • ");
  document.getElementById("adv-hw-summary").innerHTML = `
    <div style="display:flex;justify-content:space-between;flex-wrap:wrap;gap:12px;align-items:center">
      <div>
        <strong>${esc(hw.form_factor || "PC")}</strong> • ${esc(hw.cpu || "CPU")} • ${esc((hw.gpus || []).join(", "))}<br/>
        <span class="muted">Board: ${esc(hw.motherboard || "--")} (BIOS ${esc(hw.bios_version || "--")} ${esc(hw.bios_date || "")}) • Storage: ${esc(disksTxt || "--")}</span>
      </div>
      <div style="text-align:right">
        <span class="tag ${(wu.pending_drivers || []).length === 0 ? "tag-emerald" : "tag-amber"}">${esc(wu.message || "Windows Update checked")}</span>
      </div>
    </div>`;

  const updates = d.app_updates || [];
  const updBody = document.getElementById("adv-updates-body");
  const updAllBtn = document.getElementById("btn-adv-update-all");
  if (updates.length > 0) {
    updAllBtn.classList.remove("hidden");
    updBody.innerHTML = updates.map((u) => `
      <tr>
        <td><strong>${esc(u.name)}</strong><div class="muted mono-path">${esc(u.id)}</div></td>
        <td class="muted">${esc(u.version)}</td>
        <td class="cyan" style="font-weight:700">${esc(u.available)}</td>
        <td><button class="btn btn-secondary btn-sm adv-upd-btn" data-id="${esc(u.id)}">⬆ Update</button></td>
      </tr>`).join("");
    updBody.querySelectorAll(".adv-upd-btn").forEach((b) => {
      b.addEventListener("click", () => runWingetAction("upgrade", [b.dataset.id]));
    });
  } else {
    updAllBtn.classList.add("hidden");
    updBody.innerHTML = `<tr><td colspan="4" class="empty-cell">${esc(d.winget_note || "All winget-tracked apps are up to date!")}</td></tr>`;
  }

  const essWrap = document.getElementById("adv-essentials-list");
  essWrap.innerHTML = (d.essentials || []).map((e) => {
    const tag = e.installed
      ? `<span class="tag tag-emerald">INSTALLED</span>`
      : `<span class="tag tag-amber">RECOMMENDED</span>`;
    const btn = (!e.installed && e.winget_id)
      ? `<button class="btn btn-secondary btn-sm adv-inst-btn" data-id="${esc(e.winget_id)}">⬇ Install</button>`
      : "";
    return `
      <div class="snapshot-card">
        <div class="snapshot-head">
          <div>
            <strong>${esc(e.name)}</strong>
            <div class="muted">${esc(e.reason)}</div>
          </div>
          <div style="display:flex;gap:6px;align-items:center">${tag}${btn}</div>
        </div>
      </div>`;
  }).join("");

  essWrap.querySelectorAll(".adv-inst-btn").forEach((b) => {
    b.addEventListener("click", () => runWingetAction("install", [b.dataset.id]));
  });

  renderAdvisorDrivers();
}

async function runAdvisorScan(offlineFast = false) {
  playTone(620, 0.05);
  const res = await apiFetch("/api/advisor/scan", { method: "POST", body: { offline: offlineFast } });
  if (!res.ok) return;
  await watchJob(offlineFast ? "Hardware & Essentials Quick Scan" : "Full Drivers, Windows Update & App Updates Scan", res.job_id, async (result, summaryEl) => {
    state.advisorData = result;
    renderAdvisorData();
    summaryEl.classList.remove("hidden");
    summaryEl.innerHTML = `
      <strong>Drivers &amp; Apps Scan Complete!</strong><br/>
      • Key Signed Drivers Inspected: <strong>${result.driver_count}</strong><br/>
      • Windows Update: <strong>${esc(result.windows_update?.message || "Checked")}</strong><br/>
      • Available Winget App Updates: <strong>${result.app_updates_count}</strong>
    `;
  });
}

async function runWingetAction(action, packageIds) {
  if (!packageIds || !packageIds.length) return;
  const verb = action === "install" ? "Install" : "Update";
  if (!confirm(`${verb} ${packageIds.join(", ")} using Windows Package Manager (winget)?`)) return;
  const res = await apiFetch("/api/advisor/winget", {
    method: "POST",
    body: { action, package_ids: packageIds },
  });
  if (!res.ok) return;
  await watchJob(`${verb} Package(s) via Winget`, res.job_id, async (result, summaryEl) => {
    summaryEl.classList.remove("hidden");
    summaryEl.innerHTML = `<strong>Winget ${verb} Finished:</strong> ${result.succeeded} / ${result.total} succeeded.`;
    await runAdvisorScan(false);
  });
}

document.getElementById("btn-adv-scan").addEventListener("click", () => runAdvisorScan(false));
document.getElementById("btn-adv-wu").addEventListener("click", () => apiFetch("/api/advisor/open_tool", { method: "POST", body: { tool: "windowsupdate" } }));
document.getElementById("btn-adv-devmgmt").addEventListener("click", () => apiFetch("/api/advisor/open_tool", { method: "POST", body: { tool: "devmgmt" } }));
document.getElementById("btn-adv-update-all").addEventListener("click", () => {
  const ids = (state.advisorData?.app_updates || []).map((u) => u.id);
  runWingetAction("upgrade", ids);
});
document.getElementById("adv-driver-search").addEventListener("input", renderAdvisorDrivers);

// System buttons
document.getElementById("btn-sys-select-bat").addEventListener("click", () => {
  state.selectedSystemIds = new Set(["user_temp", "system_temp", "nvidia_dxcache", "recycle_bin", "windows_update", "game_dumps"]);
  renderSystemTargets();
});
document.getElementById("btn-sys-select-all").addEventListener("click", () => {
  state.selectedSystemIds = new Set((state.systemScan?.targets || []).map((t) => t.id));
  renderSystemTargets();
});
document.getElementById("btn-sys-select-none").addEventListener("click", () => {
  state.selectedSystemIds.clear();
  renderSystemTargets();
});
document.getElementById("btn-sys-rescan").addEventListener("click", runSystemScanOnly);
document.getElementById("btn-sys-clean").addEventListener("click", () => {
  const ids = [...state.selectedSystemIds];
  if (!ids.length) return alert("Select at least one System target.");
  executeCleanupPipeline({ systemIds: ids, reason: "System & Game Cleanup" });
});

// Browser buttons
function toggleGlobalBrowserCat(cat) {
  const browsers = state.browserScan?.browsers || [];
  ensureDefaultBrowserSelections();
  const anyMissing = browsers.some((b) => !state.browserSelections[b.id]?.has(cat));
  for (const b of browsers) {
    if (anyMissing) state.browserSelections[b.id].add(cat);
    else state.browserSelections[b.id].delete(cat);
  }
  renderBrowsers();
}
document.getElementById("btn-br-toggle-history").addEventListener("click", () => toggleGlobalBrowserCat("history"));
document.getElementById("btn-br-toggle-cache").addEventListener("click", () => toggleGlobalBrowserCat("cache"));
document.getElementById("btn-br-toggle-leftovers").addEventListener("click", () => toggleGlobalBrowserCat("leftovers"));
document.getElementById("btn-br-rescan").addEventListener("click", runBrowserScanOnly);

document.getElementById("btn-close-browsers").addEventListener("click", async () => {
  if (!confirm("Close running user browsers now? (Make sure unsaved browser tabs are saved!)")) return;
  const res = await apiFetch("/api/browsers/close", { method: "POST" });
  appendConsoleLog(`Closed ${res.closed_count} running browser process(es).`, "info");
  await runBrowserScanOnly();
});

document.getElementById("btn-br-clean").addEventListener("click", () => {
  const brSel = {};
  for (const [bid, setVal] of Object.entries(state.browserSelections)) {
    if (setVal.size > 0) brSel[bid] = [...setVal];
  }
  if (!Object.keys(brSel).length) return alert("Select at least one browser category to clean.");
  executeCleanupPipeline({ browserSelections: brSel, reason: "Multi-Browser Deep Clean" });
});

// Registry buttons
document.getElementById("reg-search").addEventListener("input", renderRegistryTable);
document.getElementById("reg-hive-filter").addEventListener("change", renderRegistryTable);
document.getElementById("btn-reg-rescan").addEventListener("click", runRegistryScanOnly);
document.getElementById("btn-reg-select-all").addEventListener("click", () => {
  for (const k of getFilteredRegistryKeys()) {
    if (k.accessible) state.selectedRegistryIds.add(k.id);
  }
  renderRegistryTable();
});
document.getElementById("btn-reg-select-none").addEventListener("click", () => {
  state.selectedRegistryIds.clear();
  renderRegistryTable();
});
document.getElementById("reg-master-chk").addEventListener("change", (e) => {
  for (const k of getFilteredRegistryKeys()) {
    if (!k.accessible) continue;
    if (e.target.checked) state.selectedRegistryIds.add(k.id);
    else state.selectedRegistryIds.delete(k.id);
  }
  renderRegistryTable();
});

document.getElementById("btn-reg-clean").addEventListener("click", () => {
  const selectedKeys = (state.registryScan?.keys || []).filter((k) => state.selectedRegistryIds.has(k.id));
  if (!selectedKeys.length) return alert("Select at least one empty registry key.");
  executeCleanupPipeline({
    registryKeys: selectedKeys,
    reason: "Registry Empty Folders Cleanup",
  });
});

// Manual backup button
document.getElementById("btn-create-backup-now").addEventListener("click", async () => {
  const reason = document.getElementById("backup-reason-input").value.trim() || "Manual Full Safety Checkpoint";
  const res = await apiFetch("/api/backups/create", {
    method: "POST",
    body: {
      reason,
      include_registry: document.getElementById("chk-bkp-reg").checked,
      include_browser_dbs: document.getElementById("chk-bkp-files").checked,
      include_restore_point: document.getElementById("chk-bkp-rp").checked,
    },
  });
  if (!res.ok) return;
  await watchJob("Creating Full Registry & PC Safety Backup", res.job_id, async (result, summaryEl) => {
    const snap = result.snapshot;
    summaryEl.classList.remove("hidden");
    summaryEl.innerHTML = `
      <strong>Backup Checkpoint Secured!</strong><br/>
      • Snapshot ID: <strong>${esc(snap.snapshot_id)}</strong> (${esc(snap.total_size_human)})<br/>
      • Archived Files: <strong>${snap.archived_files_count}</strong><br/>
      • Windows Restore Point: <strong>${esc(snap.restore_point?.message || "N/A")}</strong>
    `;
    await refreshBackups();
  });
});

// Bootstrap on load
(async function init() {
  appendConsoleLog("Kenny PC Cleaner & Optimization Studio initialized.", "success");
  await refreshTelemetry();
  await refreshBackups();
  await refreshMemoryStatus();
  // Kick off silent initial discovery scan for instant rich dashboard
  const [sysRes, brRes, regRes] = await Promise.all([
    apiFetch("/api/scan/system", { method: "POST" }),
    apiFetch("/api/scan/browsers", { method: "POST" }),
    apiFetch("/api/scan/registry", { method: "POST" }),
  ]);
  if (sysRes.ok) { state.systemScan = sysRes; renderSystemTargets(); }
  if (brRes.ok) { state.browserScan = brRes; renderBrowsers(); }
  if (regRes.ok) {
    state.registryScan = regRes;
    state.selectedRegistryIds = new Set((regRes.keys || []).filter((k) => k.accessible).map((k) => k.id));
    renderRegistryTable();
  }
  updateHeroGauge();
  setInterval(refreshTelemetry, 3500);
})();
