export function createRouter() {
  const subscribers = new Set();

  window.addEventListener("hashchange", () => {
    for (const subscriber of subscribers) {
      subscriber(current());
    }
  });

  return {
    current,
    subscribe(callback) {
      subscribers.add(callback);
      return () => subscribers.delete(callback);
    },
    goOverview() {
      window.location.hash = "#/overview";
    },
    goSettings() {
      window.location.hash = "#/settings";
    },
    goServer(serverId, tab) {
      window.location.hash = `#/servers/${encodeURIComponent(serverId)}/${tab}`;
    }
  };
}

function current() {
  const hash = window.location.hash || "#/overview";
  const serverMatch = hash.match(/^#\/servers\/([^/]+)(?:\/([^/]+))?/);

  if (hash === "#/settings") {
    return { name: "settings" };
  }

  if (serverMatch) {
    return {
      name: "server",
      serverId: decodeURIComponent(serverMatch[1]),
      tab: serverMatch[2] || "stats"
    };
  }

  return { name: "overview" };
}
