import { escapeAttr, escapeHtml } from "../html.js";

export function createSettingsView(els, callbacks) {
  return {
    render(settings) {
      setFormValue(els.settingsForm, "urls", settings.urls);
      setFormValue(els.settingsForm, "storageProvider", settings.storageProvider || "DuckDb");
      setFormValue(els.settingsForm, "ingestApiKey", settings.ingestApiKey || "");
      setFormValue(els.settingsForm, "storagePath", settings.storagePath);
      setFormValue(els.settingsForm, "archiveDirectory", settings.archiveDirectory);
      renderRepositorySettings(els.settingsForm, settings.repository || {});
      setFormValue(els.settingsForm, "collectionIntervalSeconds", settings.collectionIntervalSeconds);
      setFormValue(els.settingsForm, "maxConcurrentServers", settings.maxConcurrentServers);
      setFormValue(els.settingsForm, "commandTimeoutSeconds", settings.commandTimeoutSeconds);
      setFormValue(els.settingsForm, "archiveIntervalMinutes", settings.archiveIntervalMinutes);
      setFormValue(els.settingsForm, "hotDataDays", settings.hotDataDays);

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
        storagePath: getFormValue(els.settingsForm, "storagePath"),
        archiveDirectory: getFormValue(els.settingsForm, "archiveDirectory"),
        repository: collectRepositorySettings(els.settingsForm),
        collectionIntervalSeconds: getNumberFormValue(els.settingsForm, "collectionIntervalSeconds"),
        maxConcurrentServers: getNumberFormValue(els.settingsForm, "maxConcurrentServers"),
        commandTimeoutSeconds: getNumberFormValue(els.settingsForm, "commandTimeoutSeconds"),
        archiveIntervalMinutes: getNumberFormValue(els.settingsForm, "archiveIntervalMinutes"),
        hotDataDays: getNumberFormValue(els.settingsForm, "hotDataDays"),
        servers: [...els.settingsServerList.querySelectorAll(".settings-card")].map(collectServerCard),
        collectors: [...els.settingsCollectorList.querySelectorAll(".settings-card")].map(collectCollectorCard)
      };
    },

    addServer() {
      els.settingsServerList.appendChild(createServerSettingsCard({
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
    }
  };
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
      <label>Purpose<input name="purpose" type="text" value="${escapeAttr(server.purpose || "Development")}"></label>
      <label>ID<input name="id" type="text" value="${escapeAttr(server.id || "")}"></label>
      <label>Display Name<input name="displayName" type="text" value="${escapeAttr(server.displayName || "")}"></label>
      <label>Auth<select name="connectionMode">
        <option value="Windows">Windows</option>
        <option value="Sql">SQL Login</option>
        <option value="ConnectionString">Connection String</option>
        <option value="EnvironmentVariable">Environment Variable</option>
      </select></label>
      <label>SQL Server<input name="dataSource" type="text" value="${escapeAttr(server.dataSource || "")}"></label>
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
