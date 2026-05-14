export function normalizePurpose(value) {
  const purpose = String(value || "").trim();
  if (!purpose) return "Unassigned";

  switch (purpose.toLowerCase()) {
    case "prod":
      return "Production";
    case "stage":
      return "Staging";
    case "dev":
      return "Development";
    default:
      return purpose;
  }
}

export function sortPurposes(left, right) {
  const leftRank = purposeRank(left);
  const rightRank = purposeRank(right);
  if (leftRank !== rightRank) return leftRank - rightRank;
  return left.localeCompare(right);
}

export function healthRank(health) {
  switch ((health || "").toLowerCase()) {
    case "red":
      return 1;
    case "yellow":
      return 2;
    case "green":
      return 3;
    case "disabled":
      return 4;
    default:
      return 5;
  }
}

export function buildDashboardModel(summary, logs, purposeFilter, searchQuery = "") {
  const servers = summary.servers || [];
  const activeAlerts = summary.activeAlerts || [];
  const purposes = [...new Set(servers.map(server => normalizePurpose(server.purpose)))].sort(sortPurposes);
  const validFilters = ["all", ...purposes];
  const filter = validFilters.includes(purposeFilter) ? purposeFilter : "all";
  const query = String(searchQuery || "").trim().toLowerCase();
  const visibleServers = servers.filter(server =>
    (filter === "all" || normalizePurpose(server.purpose) === filter)
      && (!query || matchesServerSearch(server, query)));

  return {
    generatedAt: summary.generatedAt,
    servers,
    logs: logs || [],
    alerts: buildAlerts(activeAlerts),
    purposes,
    purposeFilter: filter,
    searchQuery: query,
    visibleServerCount: visibleServers.length,
    groups: groupServersByPurpose(visibleServers)
  };
}

function matchesServerSearch(server, query) {
  const values = [
    server.displayName,
    server.serverId,
    server.dataSource,
    server.edition,
    server.productVersion,
    server.healthState,
    server.healthReason,
    normalizePurpose(server.purpose),
    server.topWaitType
  ];

  return values
    .filter(Boolean)
    .some(value => String(value).toLowerCase().includes(query));
}

function groupServersByPurpose(servers) {
  const groups = new Map();
  for (const server of servers) {
    const purpose = normalizePurpose(server.purpose);
    if (!groups.has(purpose)) {
      groups.set(purpose, []);
    }

    groups.get(purpose).push(server);
  }

  return [...groups.entries()]
    .sort(([left], [right]) => sortPurposes(left, right))
    .map(([purpose, groupedServers]) => ({
      purpose,
      servers: groupedServers.sort((left, right) => {
        const leftHealth = healthRank(left.healthState);
        const rightHealth = healthRank(right.healthState);
        if (leftHealth !== rightHealth) return leftHealth - rightHealth;
        return (left.displayName || left.serverId).localeCompare(right.displayName || right.serverId);
      })
    }));
}

function buildAlerts(activeAlerts) {
  return activeAlerts.map(alert => ({
      serverId: alert.serverId,
      serverName: alert.serverName,
      state: alert.severity || "red",
      title: `${alert.serverName} / ${formatAlertSource(alert.source)}`,
      body: alert.message || "Needs attention",
      targetTab: alert.targetTab || "logs",
      time: alert.raisedAt
    }))
    .sort((left, right) => {
    const severityDelta = healthRank(left.state) - healthRank(right.state);
    if (severityDelta !== 0) return severityDelta;
    return new Date(right.time || 0).getTime() - new Date(left.time || 0).getTime();
  }).slice(0, 30);
}

function formatAlertSource(source) {
  const value = String(source || "").trim();
  if (!value) return "Alert";
  if (value.toLowerCase() === "server_connection") return "Connection";
  return value
    .replaceAll("_", " ")
    .replace(/\b\w/g, letter => letter.toUpperCase());
}

function purposeRank(purpose) {
  switch (purpose.toLowerCase()) {
    case "production":
      return 1;
    case "staging":
      return 2;
    case "development":
      return 3;
    case "test":
      return 4;
    case "unassigned":
      return 99;
    default:
      return 20;
  }
}
