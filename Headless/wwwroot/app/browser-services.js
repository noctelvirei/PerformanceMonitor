export function createBrowserServices(toastRegion) {
  return {
    requestNotifications: async () => {
      if (!("Notification" in window)) {
        showToast(toastRegion, "Browser notifications are not supported here.", "yellow");
        return "unsupported";
      }

      const permission = await Notification.requestPermission();
      showToast(
        toastRegion,
        permission === "granted" ? "Browser notifications enabled." : "Browser notifications not enabled.",
        permission === "granted" ? "green" : "yellow");
      return permission;
    },

    updateNotifyButton(button) {
      if (!("Notification" in window)) {
        button.textContent = "Notifications Unavailable";
        button.disabled = true;
        return;
      }

      button.textContent = Notification.permission === "granted"
        ? "Notifications Enabled"
        : "Enable Notifications";
    },

    handleHealthNotifications(servers, loadedOnce) {
      const priorSnapshot = JSON.parse(localStorage.getItem("pm-headless-health") || "{}");
      const nextSnapshot = {};

      for (const server of servers) {
        const health = server.healthState || "yellow";
        nextSnapshot[server.serverId] = health;

        const isAlert = server.isAttentionState === true;
        const prior = priorSnapshot[server.serverId];
        const isNewOrChanged = prior !== health;
        const shouldNotifyInitial = !loadedOnce && health === "red";

        if (isAlert && (isNewOrChanged || shouldNotifyInitial)) {
          const title = `${server.displayName || server.serverId} is ${health.toUpperCase()}`;
          const body = server.healthReason || "Server needs attention";
          showToast(toastRegion, `${title}: ${body}`, health);
          if ("Notification" in window && Notification.permission === "granted") {
            new Notification(title, { body });
          }
        }
      }

      localStorage.setItem("pm-headless-health", JSON.stringify(nextSnapshot));
    }
  };
}

function showToast(region, message, health) {
  const toast = document.createElement("div");
  toast.className = `toast ${health}`;
  toast.textContent = message;
  region.appendChild(toast);
  window.setTimeout(() => toast.remove(), 9000);
}
