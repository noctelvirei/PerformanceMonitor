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
        renderWaitingTasks(els.waitingTaskList, detail.waitingTasks || []);
      }

      if (activeTab === "cpu") {
        drawCpuChart(els.cpuCanvas, detail.cpu || []);
      }

      if (["queries", "resources", "memory", "jobs", "config"].includes(activeTab)) {
        const collectorContainer = document.querySelector(`#tab-${activeTab} .collector-sample-list`);
        if (collectorContainer) {
          if (detail.experience) {
            renderExperienceArea(collectorContainer, detail.experience, activeTab);
          } else {
            renderCollectorSamples(collectorContainer, detail.collectorSamples || {});
          }
        }
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

function renderWaitingTasks(container, tasks) {
  container.innerHTML = "";
  if (!tasks.length) {
    container.innerHTML = `<div class="empty-state">No active waits.</div>`;
    return;
  }

  for (const task of tasks) {
    const row = document.createElement("div");
    const blocked = Number(task.blockingSessionId || 0) > 0;
    row.className = `waiting-task-row ${blocked ? "blocked" : ""}`;
    row.innerHTML = `
      <strong>${escapeHtml(task.waitType || "wait")}</strong>
      <span>Session ${task.sessionId ?? "--"}${blocked ? ` blocked by ${task.blockingSessionId}` : ""}</span>
      <span>${escapeHtml(task.databaseName || "")}</span>
      <b>${formatMs(task.waitDurationMs || 0)}</b>
    `;
    container.appendChild(row);
  }
}

function renderCollectorSamples(container, sampleGroups) {
  container.innerHTML = "";
  const entries = Object.entries(sampleGroups);
  if (!entries.length) {
    container.innerHTML = `<div class="empty-state">No collector samples.</div>`;
    return;
  }

  for (const [collectorName, rows] of entries) {
    const panel = document.createElement("article");
    panel.className = "panel collector-sample-panel";
    panel.innerHTML = `
      <div class="panel-header">
        <h2>${escapeHtml(formatCollectorName(collectorName))}</h2>
        <span>${rows.length} rows</span>
      </div>
      <div class="collector-sample-table"></div>
    `;

    const tableHost = panel.querySelector(".collector-sample-table");
    if (!rows.length) {
      tableHost.innerHTML = `<div class="empty-state">No recent data.</div>`;
    } else {
      renderSampleTable(tableHost, rows);
    }
    container.appendChild(panel);
  }
}

function renderExperienceArea(container, experience, activeTab) {
  container.innerHTML = "";
  const panels = experience?.[activeTab] || [];
  if (!panels.length) {
    container.innerHTML = `<div class="empty-state">No recent data.</div>`;
    return;
  }

  for (const panel of panels) {
    const article = document.createElement("article");
    article.className = `panel experience-panel severity-${panel.severity || "green"}`;
    article.innerHTML = `
      <div class="panel-header">
        <h2>${escapeHtml(panel.title)}</h2>
        <span>${escapeHtml(panel.summary || "")}</span>
      </div>
      <div class="experience-metrics">
        ${(panel.metrics || []).map(renderExperienceMetric).join("")}
      </div>
      <div class="experience-rows"></div>
    `;

    const rowsHost = article.querySelector(".experience-rows");
    const rows = panel.rows || [];
    if (!rows.length) {
      rowsHost.innerHTML = `<div class="empty-state">No row detail.</div>`;
    } else {
      for (const row of rows) {
        rowsHost.appendChild(renderExperienceRow(row));
      }
    }

    container.appendChild(article);
  }
}

function renderExperienceMetric(metric) {
  const severity = metric.severity || "green";
  return `
    <span class="experience-metric severity-${escapeHtml(severity)}">
      <small>${escapeHtml(metric.label)}</small>
      <strong title="${escapeHtml(metric.value)}">${escapeHtml(metric.value)}</strong>
    </span>
  `;
}

function renderExperienceRow(row) {
  const element = document.createElement("div");
  const severity = row.severity || "green";
  element.className = `experience-row severity-${severity}`;
  element.innerHTML = `
    <div class="experience-row-main">
      <strong title="${escapeHtml(row.label)}">${escapeHtml(row.label)}</strong>
      <span title="${escapeHtml(row.description || "")}">${escapeHtml(row.description || "")}</span>
    </div>
    <div class="experience-row-metrics">
      ${(row.metrics || []).map(renderExperienceMetric).join("")}
    </div>
  `;
  return element;
}

function renderSampleTable(container, rows) {
  const parsedRows = rows.map(row => ({
    collectionTime: row.collectionTime,
    values: safeParseJson(row.payloadJson)
  }));
  const columns = pickColumns(parsedRows);
  const table = document.createElement("table");
  table.className = "sample-table";
  table.innerHTML = `
    <thead><tr><th>Collected</th>${columns.map(column => `<th>${escapeHtml(formatCollectorName(column))}</th>`).join("")}</tr></thead>
    <tbody></tbody>
  `;
  const body = table.querySelector("tbody");
  for (const row of parsedRows.slice(0, 50)) {
    const tr = document.createElement("tr");
    tr.innerHTML = `<td>${formatTime(row.collectionTime)}</td>${columns.map(column => `<td title="${escapeHtml(formatCell(row.values[column]))}">${escapeHtml(formatCell(row.values[column]))}</td>`).join("")}`;
    body.appendChild(tr);
  }
  container.appendChild(table);
}

function pickColumns(rows) {
  const priority = [
    "database_name", "session_id", "wait_type", "query_hash", "procedure_name",
    "counter_name", "cntr_value", "file_name", "size_mb", "memory_mb",
    "total_elapsed_time_ms", "total_worker_time_ms", "job_name", "state_desc",
    "name", "value_in_use"
  ];
  const keys = [...new Set(rows.flatMap(row => Object.keys(row.values)))];
  return keys
    .sort((left, right) => priorityIndex(left, priority) - priorityIndex(right, priority))
    .slice(0, 7);
}

function priorityIndex(key, priority) {
  const index = priority.indexOf(key);
  return index === -1 ? priority.length + key.localeCompare("zzzz") : index;
}

function safeParseJson(json) {
  try {
    return JSON.parse(json || "{}");
  } catch {
    return {};
  }
}

function formatCell(value) {
  if (value == null || value === "") return "--";
  if (typeof value === "number") return Number.isInteger(value) ? String(value) : value.toFixed(2);
  const text = String(value);
  return text.length > 160 ? `${text.slice(0, 157)}...` : text;
}

function formatCollectorName(name) {
  return String(name || "")
    .replaceAll("_", " ")
    .replace(/\b\w/g, letter => letter.toUpperCase());
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
