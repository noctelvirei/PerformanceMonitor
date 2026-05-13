async function fetchJson(url, options = {}) {
  const response = await fetch(url, {
    ...options,
    headers: {
      "Accept": "application/json",
      ...(options.headers || {})
    }
  });

  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`);
  }

  return response.json();
}

export function createHeadlessApi() {
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
        fetchJson(`/api/servers/${id}/cpu?hours=1`)
      ]).then(([waits, cpu]) => ({ waits, cpu }));
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
