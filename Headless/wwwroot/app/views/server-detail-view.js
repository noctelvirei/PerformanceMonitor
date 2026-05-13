import { escapeHtml } from "../html.js";
import { cssVar, formatDate, formatMs, formatTime } from "../format.js";
import { compactWait } from "../format.js";
import { normalizePurpose } from "../dashboard-model.js";

export function createServerDetailView(els) {
  return {
    show(activeTab) {
      els.overviewView.classList.add("hidden");
      els.serverView.classList.remove("hidden");
      els.settingsView.classList.add("hidden");

      document.querySelectorAll(".server-menu button").forEach(button => {
        button.classList.toggle("active", button.dataset.tab === activeTab);
      });

      document.querySelectorAll(".server-tab").forEach(tab => tab.classList.add("hidden"));
      const activePanel = document.getElementById(`tab-${activeTab}`);
      (activePanel || document.getElementById("tab-stats")).classList.remove("hidden");
    },

    render(server, logs, detail, activeTab) {
      if (!server) {
        els.selectedTitle.textContent = "Server Detail";
        els.selectedSubtitle.textContent = "Select a server";
        return;
      }

      els.selectedTitle.textContent = server.displayName || server.serverId;
      els.selectedSubtitle.textContent = `${normalizePurpose(server.purpose)} / ${(server.healthState || "yellow").toUpperCase()} / ${server.healthReason || ""}`;
      renderServerStats(els.serverStatsGrid, server);
      renderServerLog(els.collectorLog, logs.filter(log => log.serverId === server.serverId).slice(0, 50));

      if (activeTab === "stats") {
        renderWaits(els.waitList, detail.waits || []);
      }

      if (activeTab === "cpu") {
        drawCpuChart(els.cpuCanvas, detail.cpu || []);
      }
    }
  };
}

function renderServerStats(container, server) {
  container.innerHTML = `
    <article class="stat-tile"><span>Status</span><strong>${escapeHtml((server.healthState || "yellow").toUpperCase())}</strong><small>${escapeHtml(server.healthReason || "")}</small></article>
    <article class="stat-tile"><span>SQL CPU</span><strong>${server.latestSqlCpuUtilization ?? "--"}${server.latestSqlCpuUtilization == null ? "" : "%"}</strong><small>Latest sample</small></article>
    <article class="stat-tile"><span>Top Wait</span><strong>${escapeHtml(compactWait(server.topWaitType))}</strong><small>Latest snapshot</small></article>
    <article class="stat-tile"><span>Alerts</span><strong>${server.activeAlertCount ?? 0}</strong><small>Last 15 minutes</small></article>
    <article class="stat-tile wide"><span>Edition</span><strong>${escapeHtml(server.edition || "Unknown")}</strong><small>${escapeHtml(server.productVersion || "No version collected")}</small></article>
    <article class="stat-tile wide"><span>Last Contact</span><strong>${formatDate(server.lastSeenTime) || "Never"}</strong><small>${escapeHtml(normalizePurpose(server.purpose))}</small></article>
  `;
}

function renderServerLog(container, rows) {
  container.innerHTML = "";

  if (!rows.length) {
    container.innerHTML = `<div class="empty-state">No log entries.</div>`;
    return;
  }

  for (const log of rows) {
    const row = document.createElement("div");
    const statusClass = (log.status || "").toLowerCase();
    row.className = "log-row";
    row.title = log.errorMessage || "";
    row.innerHTML = `
      <span>${formatTime(log.collectionTime)}</span>
      <span>${escapeHtml(log.collectorName)}</span>
      <span class="status ${statusClass}">${escapeHtml(log.status)}</span>
      <span>${log.rowsCollected ?? 0}</span>
    `;
    container.appendChild(row);
  }
}

function renderWaits(container, waits) {
  container.innerHTML = "";
  if (!waits.length) {
    container.innerHTML = `<div class="empty-state">No waits.</div>`;
    return;
  }

  const max = Math.max(...waits.map(w => w.waitTimeDeltaMs), 1);
  for (const wait of waits) {
    const row = document.createElement("div");
    row.className = "wait-row";
    const width = Math.max(2, Math.round(wait.waitTimeDeltaMs / max * 100));
    row.innerHTML = `
      <strong title="${escapeHtml(wait.waitType)}">${escapeHtml(wait.waitType)}</strong>
      <span class="bar-track"><span class="bar-fill" style="width:${width}%"></span></span>
      <span>${formatMs(wait.waitTimeDeltaMs)}</span>
    `;
    container.appendChild(row);
  }
}

function drawCpuChart(canvas, samples) {
  const rect = canvas.getBoundingClientRect();
  const ratio = window.devicePixelRatio || 1;
  canvas.width = Math.max(1, Math.round(rect.width * ratio));
  canvas.height = Math.max(1, Math.round(rect.height * ratio));

  const ctx = canvas.getContext("2d");
  ctx.scale(ratio, ratio);
  ctx.clearRect(0, 0, rect.width, rect.height);

  ctx.strokeStyle = cssVar("--border", "#2a313c");
  ctx.lineWidth = 1;
  for (let i = 0; i <= 4; i++) {
    const y = 12 + (rect.height - 24) * i / 4;
    ctx.beginPath();
    ctx.moveTo(0, y);
    ctx.lineTo(rect.width, y);
    ctx.stroke();
  }

  if (!samples.length) {
    ctx.fillStyle = cssVar("--muted", "#9ca8b8");
    ctx.font = "13px Segoe UI, sans-serif";
    ctx.fillText("No CPU samples", 12, 28);
    return;
  }

  const points = samples.map((sample, index) => ({
    x: samples.length === 1 ? rect.width - 10 : 10 + index * (rect.width - 20) / (samples.length - 1),
    y: 10 + (100 - Math.min(100, Math.max(0, sample.sqlServerCpuUtilization))) * (rect.height - 20) / 100
  }));

  ctx.strokeStyle = cssVar("--accent", "#62b6ff");
  ctx.lineWidth = 2;
  ctx.beginPath();
  points.forEach((point, index) => {
    if (index === 0) ctx.moveTo(point.x, point.y);
    else ctx.lineTo(point.x, point.y);
  });
  ctx.stroke();

  const latest = samples[samples.length - 1];
  ctx.fillStyle = cssVar("--text", "#edf2f7");
  ctx.font = "700 13px Segoe UI, sans-serif";
  ctx.fillText(`${latest.sqlServerCpuUtilization}% SQL CPU`, 12, 22);
}
