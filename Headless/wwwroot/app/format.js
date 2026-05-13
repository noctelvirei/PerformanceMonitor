export function formatDate(value) {
  if (!value) return "";
  return new Date(value).toLocaleString();
}

export function formatTime(value) {
  if (!value) return "";
  return new Date(value).toLocaleTimeString();
}

export function formatMs(ms) {
  if (ms > 3600000) return `${(ms / 3600000).toFixed(1)}h`;
  if (ms > 60000) return `${(ms / 60000).toFixed(1)}m`;
  return `${(ms / 1000).toFixed(1)}s`;
}

export function compactWait(waitType) {
  if (!waitType) return "--";
  return waitType.length > 8 ? `${waitType.slice(0, 8)}...` : waitType;
}

export function compactSeen(value) {
  if (!value) return "--";
  const seconds = Math.max(0, (Date.now() - new Date(value).getTime()) / 1000);
  if (seconds < 60) return `${Math.floor(seconds)}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h`;
  return `${Math.floor(seconds / 86400)}d`;
}

export function cssVar(name, fallback) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}
