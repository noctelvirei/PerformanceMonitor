async function fetchJson(url, options = {}) {
  const response = await fetch(url, {
    ...options,
    headers: {
      "Accept": "application/json",
      ...(options.headers || {})
    }
  });

  if (!response.ok) {
    const body = await response.json().catch(() => null);
    throw new Error(body?.message || `${response.status} ${response.statusText}`);
  }

  return response.json();
}

export function createCentralRepositoryApi() {
  return {
    loadDashboard() {
      return Promise.all([
        fetchJson("/api/summary"),
        fetchJson("/api/collection-log?limit=50")
      ]).then(([summary, logs]) => ({ summary, logs }));
    },

    loadServerDetail(serverId) {
      const id = encodeURIComponent(serverId);
      return Promise.all([
        fetchJson(`/api/servers/${id}/waits?hours=1&limit=12`),
        fetchJson(`/api/servers/${id}/cpu?hours=1`),
        fetchJson(`/api/servers/${id}/waiting-tasks?hours=1&limit=50`)
      ]).then(([waits, cpu, waitingTasks]) => ({ waits, cpu, waitingTasks }));
    },

    loadCollectorGroup(serverId, collectorNames, hours = 1, limit = 50) {
      const id = encodeURIComponent(serverId);
      return Promise.all(collectorNames.map(name =>
        fetchJson(`/api/servers/${id}/collectors/${encodeURIComponent(name)}/samples?hours=${hours}&limit=${limit}`)
          .then(rows => [name, rows])
      )).then(entries => Object.fromEntries(entries));
    },

    loadServerExperience(serverId, hours = 1) {
      const id = encodeURIComponent(serverId);
      return fetchJson(`/api/servers/${id}/experience?hours=${hours}`);
    },

    getSettings() {
      return fetchJson("/api/settings");
    },

    async saveSettings(settings) {
      const response = await fetch("/api/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json", "Accept": "application/json" },
        body: JSON.stringify(settings)
      });

      if (!response.ok) {
        throw new Error(await response.text());
      }

      return response.json();
    },

    testServer(server) {
      return postTest("/api/settings/test-connection", { server });
    },

    testRepository(repository) {
      return postTest("/api/settings/test-repository", { repository });
    },

    discoverServers(request) {
      return fetchJson("/api/settings/discover-servers", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request)
      });
    },

    startDiscoveryJob(request) {
      return fetchJson("/api/settings/discovery-jobs", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request)
      });
    },

    loadDiscoveryJob(jobId) {
      return fetchJson(`/api/settings/discovery-jobs/${encodeURIComponent(jobId)}`);
    }
  };
}

async function postTest(url, body) {
  const response = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json", "Accept": "application/json" },
    body: JSON.stringify(body)
  });

  const result = await response.json().catch(() => ({ message: response.statusText }));
  return { ok: response.ok, message: result.message || response.statusText };
}
