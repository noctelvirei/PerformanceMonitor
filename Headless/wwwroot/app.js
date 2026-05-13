import { createHeadlessApi } from "./app/api.js";
import { createBrowserServices } from "./app/browser-services.js";
import { buildDashboardModel } from "./app/dashboard-model.js";
import { createRouter } from "./app/router.js";
import { createOverviewView } from "./app/views/overview-view.js";
import { createServerDetailView } from "./app/views/server-detail-view.js";
import { collectRepositorySettings, collectServerCard, createSettingsView } from "./app/views/settings-view.js";

const state = {
  selectedServerId: null,
  activeTab: "stats",
  purposeFilter: "all",
  dashboard: null,
  summary: null,
  logs: [],
  settings: null,
  loadedOnce: false
};

const els = {
  overviewView: document.getElementById("overview-view"),
  serverView: document.getElementById("server-view"),
  settingsView: document.getElementById("settings-view"),
  generatedAt: document.getElementById("generated-at"),
  alertCount: document.getElementById("alert-count"),
  purposeFilter: document.getElementById("purpose-filter"),
  serverCardGrid: document.getElementById("server-card-grid"),
  alertList: document.getElementById("alert-list"),
  collectorLog: document.getElementById("collector-log"),
  selectedTitle: document.getElementById("selected-server-title"),
  selectedSubtitle: document.getElementById("selected-server-subtitle"),
  serverStatsGrid: document.getElementById("server-stats-grid"),
  waitList: document.getElementById("wait-list"),
  cpuCanvas: document.getElementById("cpu-chart"),
  settingsButton: document.getElementById("settings-button"),
  settingsBack: document.getElementById("settings-back-button"),
  settingsForm: document.getElementById("settings-form"),
  settingsStatus: document.getElementById("settings-status"),
  settingsServerList: document.getElementById("settings-server-list"),
  settingsCollectorList: document.getElementById("settings-collector-list"),
  testRepository: document.getElementById("test-repository-button"),
  repositoryStatus: document.getElementById("repository-status"),
  addServer: document.getElementById("add-server-button"),
  refresh: document.getElementById("refresh-button"),
  notify: document.getElementById("notify-button"),
  back: document.getElementById("back-button"),
  toastRegion: document.getElementById("toast-region")
};

const api = createHeadlessApi();
const router = createRouter();
const browser = createBrowserServices(els.toastRegion);
const overviewView = createOverviewView(els, { onServer: router.goServer });
const serverDetailView = createServerDetailView(els);
const settingsView = createSettingsView(els, { onTestServer: testSettingsServer });

els.refresh.addEventListener("click", () => loadAll());
els.back.addEventListener("click", () => router.goOverview());
els.settingsButton.addEventListener("click", () => router.goSettings());
els.settingsBack.addEventListener("click", () => router.goOverview());
els.addServer.addEventListener("click", () => settingsView.addServer());
els.testRepository.addEventListener("click", () => testRepository());
els.settingsForm.addEventListener("submit", event => {
  event.preventDefault();
  saveSettings();
});
els.purposeFilter.addEventListener("change", () => {
  state.purposeFilter = els.purposeFilter.value;
  renderOverview();
});
els.notify.addEventListener("click", async () => {
  await browser.requestNotifications();
  browser.updateNotifyButton(els.notify);
});
document.querySelectorAll(".server-menu button").forEach(button => {
  button.addEventListener("click", () => {
    if (!state.selectedServerId) return;
    router.goServer(state.selectedServerId, button.dataset.tab || "stats");
  });
});
window.addEventListener("resize", () => {
  if (state.activeTab === "cpu") {
    loadSelectedServer();
  }
});
router.subscribe(applyRoute);

async function loadAll() {
  const { summary, logs } = await api.loadDashboard();
  state.summary = summary;
  state.logs = logs;
  state.dashboard = buildDashboardModel(summary, logs, state.purposeFilter);
  state.purposeFilter = state.dashboard.purposeFilter;

  if (!state.selectedServerId && state.dashboard.servers.length > 0) {
    const firstActive = state.dashboard.servers.find(server => server.isEnabled) || state.dashboard.servers[0];
    state.selectedServerId = firstActive.serverId;
  }

  browser.handleHealthNotifications(state.dashboard.servers, state.loadedOnce);
  state.loadedOnce = true;
  browser.updateNotifyButton(els.notify);
  applyRoute(router.current());
}

function renderOverview() {
  if (!state.summary) return;
  state.dashboard = buildDashboardModel(state.summary, state.logs, state.purposeFilter);
  state.purposeFilter = state.dashboard.purposeFilter;
  overviewView.render(state.dashboard, state.selectedServerId);
}

async function applyRoute(route) {
  if (!route) {
    route = router.current();
  }

  if (route.name === "settings") {
    await showSettingsView();
    return;
  }

  if (route.name === "server") {
    state.selectedServerId = route.serverId;
    state.activeTab = route.tab || "stats";
    serverDetailView.show(state.activeTab);
    await loadSelectedServer();
    return;
  }

  state.activeTab = "overview";
  els.overviewView.classList.remove("hidden");
  els.serverView.classList.add("hidden");
  els.settingsView.classList.add("hidden");
  renderOverview();
}

async function showSettingsView() {
  state.activeTab = "settings";
  els.overviewView.classList.add("hidden");
  els.serverView.classList.add("hidden");
  els.settingsView.classList.remove("hidden");

  if (!state.settings) {
    state.settings = await api.getSettings();
  }

  settingsView.render(state.settings);
}

async function saveSettings() {
  els.settingsStatus.textContent = "Saving...";
  try {
    state.settings = await api.saveSettings(settingsView.collect());
    els.settingsStatus.textContent = "Saved.";
    settingsView.render(state.settings);
    await loadAll();
  } catch (error) {
    els.settingsStatus.textContent = error.message;
  }
}

async function testSettingsServer(card) {
  const status = card.querySelector(".settings-card-status");
  status.textContent = "Testing...";
  const result = await api.testServer(collectServerCard(card));
  status.textContent = result.message;
  status.className = `settings-card-status ${result.ok ? "green" : "red"}`;
}

async function testRepository() {
  els.repositoryStatus.textContent = "Testing...";
  const result = await api.testRepository(collectRepositorySettings(els.settingsForm));
  els.repositoryStatus.textContent = result.message;
  els.repositoryStatus.className = `settings-card-status ${result.ok ? "green" : "red"}`;
}

async function loadSelectedServer() {
  const server = state.dashboard?.servers.find(item => item.serverId === state.selectedServerId);
  if (!server) {
    serverDetailView.render(null, [], {}, state.activeTab);
    return;
  }

  let detail = { waits: [], cpu: [] };
  if (state.activeTab === "stats" || state.activeTab === "cpu") {
    detail = await api.loadServerDetail(server.serverId);
  }

  serverDetailView.render(server, state.dashboard.logs, detail, state.activeTab);
}

loadAll().catch(error => {
  els.generatedAt.textContent = error.message;
});
