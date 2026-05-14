import { escapeHtml } from "../html.js";
import { compactSeen, compactWait, formatDate } from "../format.js";

export function createOverviewView(els, callbacks) {
  return {
    render(model, selectedServerId) {
      els.generatedAt.textContent = `Updated ${formatDate(model.generatedAt)}`;
      renderHealthSummary(els.healthSummary, model.servers);
      renderPurposeOptions(els.purposeFilter, model.purposes, model.purposeFilter);
      renderServerCards(els.serverCardGrid, model.groups, selectedServerId, callbacks.onServer);
      renderAlerts(els.alertCount, els.alertList, model.alerts, callbacks.onServer);
      renderOverviewFooter(els.overviewFooter, model);
    }
  };
}

function renderHealthSummary(container, servers) {
  const counts = {
    green: 0,
    yellow: 0,
    red: 0,
    disabled: 0
  };

  for (const server of servers || []) {
    const state = String(server.healthState || "yellow").toLowerCase();
    if (state in counts) counts[state] += 1;
    else counts.yellow += 1;
  }

  container.innerHTML = [
    summaryTile("green", "Green", counts.green, "All clear"),
    summaryTile("yellow", "Yellow", counts.yellow, "Warning"),
    summaryTile("red", "Red", counts.red, "Needs attention"),
    summaryTile("disabled", "Disabled", counts.disabled, "Not monitored")
  ].join("");
}

function summaryTile(state, label, count, caption) {
  return `
    <article class="health-tile ${state}">
      <span>${escapeHtml(label)}</span>
      <strong>${count}</strong>
      <small>${escapeHtml(caption)}</small>
    </article>
  `;
}

function renderPurposeOptions(select, purposes, current) {
  const options = ["all", ...purposes];
  select.innerHTML = options
    .map(value => `<option value="${escapeHtml(value)}">${escapeHtml(value === "all" ? "All" : value)}</option>`)
    .join("");
  select.value = current;
}

function renderServerCards(container, groups, selectedServerId, onServer) {
  container.innerHTML = "";
  if (!groups.length) {
    container.innerHTML = `<div class="empty-state">No servers match the current view.</div>`;
    return;
  }

  for (const group of groups) {
    const section = document.createElement("section");
    section.className = "purpose-section";
    section.innerHTML = `
      <div class="purpose-heading">
        <h3>${escapeHtml(group.purpose)}</h3>
        <span>${group.servers.length}</span>
      </div>
      <div class="server-card-row"></div>
    `;

    const row = section.querySelector(".server-card-row");
    for (const server of group.servers) {
      row.appendChild(createServerCard(server, selectedServerId, onServer));
    }

    container.appendChild(section);
  }
}

function renderOverviewFooter(container, model) {
  if (!container) return;

  const enabled = model.servers.filter(server => server.isEnabled !== false).length;
  const disabled = model.servers.length - enabled;
  const filtered = model.visibleServerCount ?? model.servers.length;
  container.innerHTML = `
    <span>Total: ${model.servers.length} server${model.servers.length === 1 ? "" : "s"}</span>
    <span>${enabled} monitoring</span>
    <span>${disabled} disabled</span>
    <span>${filtered} shown</span>
    <span>Last updated: ${escapeHtml(formatDate(model.generatedAt) || "pending")}</span>
  `;
}

function createServerCard(server, selectedServerId, onServer) {
  const card = document.createElement("button");
  card.type = "button";
  card.className = `server-card health-${server.healthState || "yellow"} ${server.serverId === selectedServerId ? "selected" : ""}`;
  card.addEventListener("click", () => onServer(server.serverId, "stats"));

  const title = server.displayName || server.serverId;
  const platform = "SQL Server";
  const os = server.productVersion ? `v${server.productVersion}` : "Windows";
  const health = server.healthState || "yellow";

  card.innerHTML = `
    <div class="server-card-top">
      <span class="server-icon" aria-hidden="true"></span>
      <div>
        <strong>${escapeHtml(title)}</strong>
        <small>${escapeHtml(platform)} / ${escapeHtml(os)}</small>
      </div>
      <span class="card-menu" aria-hidden="true">
        <svg viewBox="0 0 24 24"><path d="M12 7.2v.1M12 12v.1M12 16.8v.1" /></svg>
      </span>
    </div>
    <div class="mini-stats">
      <span><b>${server.latestSqlCpuUtilization ?? "--"}${server.latestSqlCpuUtilization == null ? "" : "%"}</b><small>CPU</small></span>
      <span><b>${escapeHtml(compactWait(server.topWaitType))}</b><small>Wait</small></span>
      <span><b>${server.activeAlertCount ?? 0}</b><small>Alerts</small></span>
      <span><b>${compactSeen(server.lastSeenTime)}</b><small>Seen</small></span>
    </div>
    <div class="server-card-ribbon ${health}">
      <span class="ribbon-dot"></span>
      <span>${escapeHtml(server.healthReason || "All good")}</span>
    </div>
  `;

  return card;
}

function renderAlerts(count, list, alerts, onServer) {
  count.textContent = alerts.length === 1 ? "1 active" : `${alerts.length} active`;
  list.innerHTML = "";

  if (!alerts.length) {
    list.innerHTML = `<div class="alert-empty"><strong>No alerts</strong></div>`;
    return;
  }

  for (const alert of alerts) {
    const item = document.createElement("button");
    item.type = "button";
    item.className = `alert-item ${alert.state}`;
    item.addEventListener("click", () => onServer(alert.serverId, alert.targetTab || "stats"));
    item.innerHTML = `
      <span class="alert-severity">${escapeHtml(alert.state.toUpperCase())}</span>
      <strong>${escapeHtml(alert.title)}</strong>
      <span>${escapeHtml(alert.body)}</span>
      <small>${formatDate(alert.time) || "Needs attention"}</small>
    `;
    list.appendChild(item);
  }
}
