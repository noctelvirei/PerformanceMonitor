import { escapeAttr, escapeHtml } from "../html.js";

export function createSettingsView(els, callbacks) {
  wireSettingsNavigation();

  return {
    render(settings) {
      setFormValue(els.settingsForm, "urls", settings.urls);
      setFormValue(els.settingsForm, "storageProvider", settings.storageProvider || "DuckDb");
      setFormValue(els.settingsForm, "ingestApiKey", settings.ingestApiKey || "");
      renderMcpAccess(els.settingsForm, settings.mcpAccess || {});
      setFormValue(els.settingsForm, "storagePath", settings.storagePath);
      setFormValue(els.settingsForm, "archiveDirectory", settings.archiveDirectory);
      renderRepositorySettings(els.settingsForm, settings.repository || {});
      setFormValue(els.settingsForm, "collectionIntervalSeconds", settings.collectionIntervalSeconds);
      setFormValue(els.settingsForm, "maxConcurrentServers", settings.maxConcurrentServers);
      setFormValue(els.settingsForm, "commandTimeoutSeconds", settings.commandTimeoutSeconds);
      setFormValue(els.settingsForm, "archiveIntervalMinutes", settings.archiveIntervalMinutes);
      setFormValue(els.settingsForm, "hotDataDays", settings.hotDataDays);
      renderAlertRules(els.settingsForm, settings.alertRules || {});

      els.settingsServerList.innerHTML = "";
      for (const server of settings.servers || []) {
        els.settingsServerList.appendChild(createServerSettingsCard(server, callbacks.onTestServer));
      }

      els.settingsCollectorList.innerHTML = "";
      for (const collector of settings.collectors || []) {
        els.settingsCollectorList.appendChild(createCollectorSettingsRow(collector));
      }
    },

    collect() {
      return {
        urls: getFormValue(els.settingsForm, "urls"),
        storageProvider: getFormValue(els.settingsForm, "storageProvider"),
        ingestApiKey: getFormValue(els.settingsForm, "ingestApiKey"),
        mcpAccess: collectMcpAccess(els.settingsForm),
        storagePath: getFormValue(els.settingsForm, "storagePath"),
        archiveDirectory: getFormValue(els.settingsForm, "archiveDirectory"),
        repository: collectRepositorySettings(els.settingsForm),
        collectionIntervalSeconds: getNumberFormValue(els.settingsForm, "collectionIntervalSeconds"),
        maxConcurrentServers: getNumberFormValue(els.settingsForm, "maxConcurrentServers"),
        commandTimeoutSeconds: getNumberFormValue(els.settingsForm, "commandTimeoutSeconds"),
        archiveIntervalMinutes: getNumberFormValue(els.settingsForm, "archiveIntervalMinutes"),
        hotDataDays: getNumberFormValue(els.settingsForm, "hotDataDays"),
        alertRules: collectAlertRules(els.settingsForm),
        servers: [...els.settingsServerList.querySelectorAll(".settings-card")].map(collectServerCard),
        collectors: [...els.settingsCollectorList.querySelectorAll(".settings-card")].map(collectCollectorCard)
      };
    },

    addServer(server = null) {
      els.settingsServerList.appendChild(createServerSettingsCard(server || {
        id: "",
        displayName: "",
        purpose: "Development",
        connectionMode: "Windows",
        dataSource: "",
        initialCatalog: "master",
        encrypt: "Optional",
        trustServerCertificate: true,
        enabled: true
      }, callbacks.onTestServer));
    },

    renderDiscoveryResults(instances) {
      renderDiscoveryResults(els.discoveryResults, instances, callbacks.onAddDiscoveredServer);
    }
  };
}

function wireSettingsNavigation() {
  const buttons = [...document.querySelectorAll("[data-settings-target]")];
  const sections = [...document.querySelectorAll("[data-settings-section]")];
  const nav = document.querySelector(".settings-nav");

  if (!buttons.length || !sections.length || nav?.dataset.settingsWired === "true") {
    return;
  }

  if (nav) {
    nav.dataset.settingsWired = "true";
  }

  const activate = (targetId) => {
    const target = document.getElementById(targetId);
    if (!target) return;

    for (const item of buttons) {
      const selected = item.dataset.settingsTarget === targetId;
      item.classList.toggle("active", selected);
      item.setAttribute("aria-selected", String(selected));
    }

    for (const section of sections) {
      const selected = section.id === targetId;
      section.classList.toggle("active", selected);
      section.hidden = !selected;
    }
  };

  for (const button of buttons) {
    button.addEventListener("click", () => {
      activate(button.dataset.settingsTarget || "");
    });

    button.addEventListener("keydown", (event) => {
      const current = buttons.indexOf(button);
      let next = current;

      if (event.key === "ArrowDown" || event.key === "ArrowRight") {
        next = (current + 1) % buttons.length;
      } else if (event.key === "ArrowUp" || event.key === "ArrowLeft") {
        next = (current - 1 + buttons.length) % buttons.length;
      } else if (event.key === "Home") {
        next = 0;
      } else if (event.key === "End") {
        next = buttons.length - 1;
      } else {
        return;
      }

      event.preventDefault();
      buttons[next].focus();
      activate(buttons[next].dataset.settingsTarget || "");
    });
  }

  activate(buttons.find((button) => button.classList.contains("active"))?.dataset.settingsTarget || buttons[0].dataset.settingsTarget || "");
}

function renderMcpAccess(form, access) {
  setFormValue(form, "mcpEnabled", String(access.enabled !== false));
  setFormValue(form, "mcpAuthMode", access.authMode || "None");
  setFormValue(form, "mcpPublicBaseUrl", access.publicBaseUrl || window.location.origin);
  setFormValue(form, "mcpAllowLocalWithoutApiKey", String(access.allowLocalWithoutApiKey === true));
  setFormValue(form, "mcpApiKey", "");

  const apiKeyField = form.elements.mcpApiKey;
  if (apiKeyField) {
    apiKeyField.placeholder = access.hasApiKey ? "Saved" : "";
  }

  wireMcpConnectionGuide(form);
  renderMcpConnectionGuide(form);
}

function wireMcpConnectionGuide(form) {
  if (form.dataset.mcpGuideWired === "true") {
    return;
  }

  form.dataset.mcpGuideWired = "true";
  for (const name of ["mcpPublicBaseUrl", "mcpAuthMode"]) {
    const field = form.elements[name];
    if (field) {
      field.addEventListener("input", () => renderMcpConnectionGuide(form));
      field.addEventListener("change", () => renderMcpConnectionGuide(form));
    }
  }

  for (const button of form.querySelectorAll("[data-copy-mcp]")) {
    button.addEventListener("click", () => copyMcpValue(form, button.dataset.copyMcp || "url"));
  }
}

function renderMcpConnectionGuide(form) {
  const serverUrl = getMcpServerUrl(form);
  const authMode = getFormValue(form, "mcpAuthMode") || "None";
  const authNote = authMode === "BearerToken"
      ? "This monitor expects a bearer token or MCP API key. Add that endpoint token to the client only if prompted."
      : "This monitor is not enforcing endpoint auth. Use this only on a trusted network.";

  setFormValue(form, "mcpServerUrl", serverUrl);
  setFormValue(form, "mcpCodexCommand", `codex mcp add performance-monitor --url ${serverUrl}\n\n${authNote}`);

  for (const guide of form.querySelectorAll("[data-mcp-guide]")) {
    guide.hidden = false;
  }

  const status = document.getElementById("mcp-connect-status");
  if (status && !status.textContent) {
    status.textContent = "PerformanceMonitor hosts this endpoint. Add it from the client machine.";
  }
}

function getMcpServerUrl(form) {
  const baseUrl = (getFormValue(form, "mcpPublicBaseUrl") || window.location.origin).replace(/\/+$/, "");
  return `${baseUrl}/mcp`;
}

async function copyMcpValue(form, target) {
  const status = document.getElementById("mcp-connect-status");
  const field = target === "codex"
    ? form.elements.mcpCodexCommand
    : form.elements.mcpServerUrl;
  const copied = await copyText(field?.value || "");

  setMcpConnectStatus(
    status,
    copied ? "Copied." : "Select the field and copy it from the browser.",
    copied);
}

async function copyText(text) {
  if (!navigator.clipboard) {
    return false;
  }

  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

function setMcpConnectStatus(status, message, success) {
  if (!status) return;
  status.textContent = message;
  status.className = `settings-card-status ${success ? "green" : ""}`;
}

function renderRepositorySettings(form, repository) {
  setFormValue(form, "repositoryConnectionMode", repository.connectionMode || "Windows");
  setFormValue(form, "repositoryDataSource", repository.dataSource || "");
  setFormValue(form, "repositoryInitialCatalog", repository.initialCatalog || "PerformanceMonitorRepository");
  setFormValue(form, "repositoryUserId", repository.userId || "");
  setFormValue(form, "repositoryPassword", "");
  setFormValue(form, "repositoryEncrypt", repository.encrypt || "Optional");
  setFormValue(form, "repositoryTrustServerCertificate", String(repository.trustServerCertificate !== false));
  setFormValue(form, "repositoryConnectionString", repository.connectionString || "");
  setFormValue(form, "repositoryConnectionStringEnvironmentVariable", repository.connectionStringEnvironmentVariable || "");

  const passwordField = form.elements.repositoryPassword;
  if (passwordField) {
    passwordField.placeholder = repository.hasPassword ? "Saved" : "";
  }
}

function renderAlertRules(form, rules) {
  setFormValue(form, "alertRulesEnabled", String(rules.enabled !== false));
  setFormValue(form, "alertCpuEnabled", String(rules.cpuEnabled !== false));
  setFormValue(form, "alertCpuWarningThreshold", rules.cpuWarningThreshold ?? 80);
  setFormValue(form, "alertCpuCriticalThreshold", rules.cpuCriticalThreshold ?? 90);
  setFormValue(form, "alertLongRunningQueryEnabled", String(rules.longRunningQueryEnabled !== false));
  setFormValue(form, "alertLongRunningQueryWarningMinutes", rules.longRunningQueryWarningMinutes ?? 15);
  setFormValue(form, "alertLongRunningQueryCriticalMinutes", rules.longRunningQueryCriticalMinutes ?? 30);
  setFormValue(form, "alertBlockingEnabled", String(rules.blockingEnabled !== false));
  setFormValue(form, "alertDeadlockEnabled", String(rules.deadlockEnabled !== false));
  setFormValue(form, "alertMemoryGrantEnabled", String(rules.memoryGrantEnabled !== false));
  setFormValue(form, "alertMemoryGrantWarningSeconds", rules.memoryGrantWarningSeconds ?? 5);
  setFormValue(form, "alertMemoryGrantCriticalSeconds", rules.memoryGrantCriticalSeconds ?? 30);
  setFormValue(form, "alertFileLatencyEnabled", String(rules.fileLatencyEnabled !== false));
  setFormValue(form, "alertFileLatencyWarningMs", rules.fileLatencyWarningMs ?? 50);
  setFormValue(form, "alertFileLatencyCriticalMs", rules.fileLatencyCriticalMs ?? 200);
  setFormValue(form, "alertLongRunningJobEnabled", String(rules.longRunningJobEnabled !== false));
  setFormValue(form, "alertLongRunningJobWarningMinutes", rules.longRunningJobWarningMinutes ?? 60);
  setFormValue(form, "alertLongRunningJobCriticalMinutes", rules.longRunningJobCriticalMinutes ?? 240);
}

function createServerSettingsCard(server, onTestServer) {
  const card = document.createElement("section");
  card.className = "settings-card";
  card.innerHTML = `
    <div class="settings-card-header">
      <strong>${escapeHtml(server.displayName || server.id || "New server")}</strong>
      <div>
        <button type="button" data-action="test">Test</button>
        <button type="button" data-action="remove">Remove</button>
      </div>
    </div>
    <div class="settings-grid">
      <label>Enabled<select name="enabled"><option value="true">Yes</option><option value="false">No</option></select></label>
      <label>Purpose<input name="purpose" type="text" value="${escapeAttr(server.purpose || "Development")}"><span class="field-help">Used to group and filter dashboard cards.</span></label>
      <label>ID<input name="id" type="text" value="${escapeAttr(server.id || "")}"><span class="field-help">Stable storage key. Avoid renaming after data has collected.</span></label>
      <label>Display Name<input name="displayName" type="text" value="${escapeAttr(server.displayName || "")}"></label>
      <label>Auth<select name="connectionMode">
        <option value="Windows">Windows</option>
        <option value="Sql">SQL Login</option>
        <option value="ConnectionString">Connection String</option>
        <option value="EnvironmentVariable">Environment Variable</option>
      </select><span class="field-help">Windows uses the service account running the central monitor.</span></label>
      <label>SQL Server<input name="dataSource" type="text" value="${escapeAttr(server.dataSource || "")}"><span class="field-help">Server or instance name to connect to remotely.</span></label>
      <label>Database<input name="initialCatalog" type="text" value="${escapeAttr(server.initialCatalog || "master")}"></label>
      <label>Encrypt<select name="encrypt"><option value="Optional">Optional</option><option value="Mandatory">Mandatory</option><option value="Strict">Strict</option></select></label>
      <label>User<input name="userId" type="text" value="${escapeAttr(server.userId || "")}"></label>
      <label>Password<input name="password" type="password" placeholder="${server.hasPassword ? "Saved" : ""}"></label>
      <label>Trust Cert<select name="trustServerCertificate"><option value="true">Yes</option><option value="false">No</option></select></label>
      <label>Env Var<input name="connectionStringEnvironmentVariable" type="text" value="${escapeAttr(server.connectionStringEnvironmentVariable || "")}"></label>
      <label class="wide">Connection String<input name="connectionString" type="text" value="${escapeAttr(server.connectionString || "")}"></label>
    </div>
    <div class="settings-card-status"></div>
  `;

  card.querySelector('[name="enabled"]').value = String(server.enabled !== false);
  card.querySelector('[name="connectionMode"]').value = server.connectionMode || "Windows";
  card.querySelector('[name="encrypt"]').value = server.encrypt || "Optional";
  card.querySelector('[name="trustServerCertificate"]').value = String(server.trustServerCertificate !== false);

  card.querySelector('[data-action="remove"]').addEventListener("click", () => card.remove());
  card.querySelector('[data-action="test"]').addEventListener("click", () => onTestServer(card));
  return card;
}

function createCollectorSettingsRow(collector) {
  const row = document.createElement("section");
  row.className = "settings-card compact";
  row.innerHTML = `
    <div class="settings-grid compact">
      <label>Name<input name="name" type="text" value="${escapeAttr(collector.name || "")}" readonly></label>
      <label>Enabled<select name="enabled"><option value="true">Yes</option><option value="false">No</option></select></label>
      <label>Frequency Seconds<input name="frequencySeconds" type="number" min="0" value="${collector.frequencySeconds ?? 60}"></label>
    </div>
  `;
  row.querySelector('[name="enabled"]').value = String(collector.enabled !== false);
  return row;
}

function renderDiscoveryResults(container, instances, onAddDiscoveredServer) {
  container.innerHTML = "";

  if (!instances.length) {
    container.innerHTML = `<div class="empty-state">No instances found.</div>`;
    return;
  }

  for (const instance of instances) {
    const row = document.createElement("section");
    row.className = "settings-card compact discovery-result";
    row.innerHTML = `
      <div>
        <strong>${escapeHtml(instance.dataSource || instance.displayName || "SQL Server")}</strong>
        <span>${escapeHtml([instance.confidence, instance.availability].filter(Boolean).join(" / ") || "Discovered")}</span>
      </div>
      <div>
        <span>${escapeHtml(instance.machineName || "")}</span>
        <button type="button" ${instance.isAlreadyConfigured ? "disabled" : ""}>${instance.isAlreadyConfigured ? "Added" : "Add"}</button>
      </div>
    `;

    row.querySelector("button").addEventListener("click", () => {
      onAddDiscoveredServer(instance);
      row.querySelector("button").disabled = true;
      row.querySelector("button").textContent = "Added";
    });
    container.appendChild(row);
  }
}

export function collectServerCard(card) {
  return {
    id: getCardValue(card, "id"),
    displayName: getCardValue(card, "displayName"),
    purpose: getCardValue(card, "purpose"),
    connectionMode: getCardValue(card, "connectionMode"),
    dataSource: getCardValue(card, "dataSource"),
    initialCatalog: getCardValue(card, "initialCatalog"),
    userId: getCardValue(card, "userId"),
    password: getCardValue(card, "password"),
    encrypt: getCardValue(card, "encrypt"),
    trustServerCertificate: getCardValue(card, "trustServerCertificate") === "true",
    connectionString: getCardValue(card, "connectionString"),
    connectionStringEnvironmentVariable: getCardValue(card, "connectionStringEnvironmentVariable"),
    enabled: getCardValue(card, "enabled") === "true"
  };
}

export function collectRepositorySettings(form) {
  return {
    connectionMode: getFormValue(form, "repositoryConnectionMode"),
    dataSource: getFormValue(form, "repositoryDataSource"),
    initialCatalog: getFormValue(form, "repositoryInitialCatalog"),
    userId: getFormValue(form, "repositoryUserId"),
    password: getFormValue(form, "repositoryPassword"),
    encrypt: getFormValue(form, "repositoryEncrypt"),
    trustServerCertificate: getFormValue(form, "repositoryTrustServerCertificate") === "true",
    connectionString: getFormValue(form, "repositoryConnectionString"),
    connectionStringEnvironmentVariable: getFormValue(form, "repositoryConnectionStringEnvironmentVariable")
  };
}

function collectMcpAccess(form) {
  return {
    enabled: getFormValue(form, "mcpEnabled") !== "false",
    authMode: getFormValue(form, "mcpAuthMode") || "None",
    publicBaseUrl: getFormValue(form, "mcpPublicBaseUrl"),
    apiKey: getFormValue(form, "mcpApiKey"),
    allowLocalWithoutApiKey: getFormValue(form, "mcpAllowLocalWithoutApiKey") !== "false"
  };
}

function collectAlertRules(form) {
  return {
    enabled: getFormValue(form, "alertRulesEnabled") !== "false",
    cpuEnabled: getFormValue(form, "alertCpuEnabled") !== "false",
    cpuWarningThreshold: getNumberFormValue(form, "alertCpuWarningThreshold"),
    cpuCriticalThreshold: getNumberFormValue(form, "alertCpuCriticalThreshold"),
    longRunningQueryEnabled: getFormValue(form, "alertLongRunningQueryEnabled") !== "false",
    longRunningQueryWarningMinutes: getNumberFormValue(form, "alertLongRunningQueryWarningMinutes"),
    longRunningQueryCriticalMinutes: getNumberFormValue(form, "alertLongRunningQueryCriticalMinutes"),
    blockingEnabled: getFormValue(form, "alertBlockingEnabled") !== "false",
    deadlockEnabled: getFormValue(form, "alertDeadlockEnabled") !== "false",
    memoryGrantEnabled: getFormValue(form, "alertMemoryGrantEnabled") !== "false",
    memoryGrantWarningSeconds: getNumberFormValue(form, "alertMemoryGrantWarningSeconds"),
    memoryGrantCriticalSeconds: getNumberFormValue(form, "alertMemoryGrantCriticalSeconds"),
    fileLatencyEnabled: getFormValue(form, "alertFileLatencyEnabled") !== "false",
    fileLatencyWarningMs: getNumberFormValue(form, "alertFileLatencyWarningMs"),
    fileLatencyCriticalMs: getNumberFormValue(form, "alertFileLatencyCriticalMs"),
    longRunningJobEnabled: getFormValue(form, "alertLongRunningJobEnabled") !== "false",
    longRunningJobWarningMinutes: getNumberFormValue(form, "alertLongRunningJobWarningMinutes"),
    longRunningJobCriticalMinutes: getNumberFormValue(form, "alertLongRunningJobCriticalMinutes")
  };
}

export function collectDiscoverySettings(form) {
  return {
    targets: getFormValue(form, "discoveryTargets"),
    discoveryTypes: getFormValue(form, "discoveryTypes") || "DomainSPN,DataSourceEnumeration",
    scanTypes: getFormValue(form, "discoveryScanTypes") || "Browser,TCPPort",
    ipAddresses: getFormValue(form, "discoveryIpAddresses"),
    tcpPorts: getFormValue(form, "discoveryTcpPorts") || "1433",
    domainController: getFormValue(form, "discoveryDomainController"),
    minimumConfidence: getFormValue(form, "discoveryMinimumConfidence") || "Medium",
    purpose: getFormValue(form, "discoveryPurpose") || "Development",
    timeoutSeconds: getNumberFormValue(form, "discoveryTimeoutSeconds") || 120
  };
}

function collectCollectorCard(card) {
  return {
    name: getCardValue(card, "name"),
    enabled: getCardValue(card, "enabled") === "true",
    frequencySeconds: Number(getCardValue(card, "frequencySeconds")) || 60
  };
}

function setFormValue(form, name, value) {
  const field = form.elements[name];
  if (field) field.value = value ?? "";
}

function getFormValue(form, name) {
  const field = form.elements[name];
  return field ? field.value.trim() : "";
}

function getNumberFormValue(form, name) {
  return Number(getFormValue(form, name)) || 0;
}

function getCardValue(card, name) {
  const field = card.querySelector(`[name="${name}"]`);
  return field ? field.value.trim() : "";
}
