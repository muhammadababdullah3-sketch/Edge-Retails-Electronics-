/**
 * Central portal uses a server-validated cookie session.
 * Support commands use persisted server outcomes; notifications convey no authority.
 */

const AppState = {
  currentUser: null,
  sessionExpiryTimer: null,
  loginChallengeId: null,
  activeView: "dashboard",
  activeShopId: null,
  theme: "dark",
  sessionVersion: 0,
  metricsRequest: 0,
  customerRequest: 0,
  directoryMutationBusy: false,
  directory: { request: 0, filter: "", cursor: null, version: null },
  selectedQueueItem: null,
  queues: {
    dashboard: { type: "PAYMENT_REVIEW", page: 1, pageSize: 20, cursorStack: [null], nextCursor: null, asOfVersion: null, authorizedCount: 0, request: 0 },
    approvals: { type: "DUAL_APPROVAL", page: 1, pageSize: 20, cursorStack: [null], nextCursor: null, asOfVersion: null, authorizedCount: 0, request: 0 }
  },
  audit: { page: 1, pageSize: 25, cursorStack: [null], nextCursor: null, asOfVersion: null, request: 0 },
  pii: { auditRecordId: null, resource: null, resourceId: null, request: 0, busy: false, opener: null },
  support: { page: 1, pageSize: 20, cursorStack: [null], nextCursor: null, asOfVersion: null, authorizedCount: 0, request: 0, items: [], selectedId: null, busy: false, operations: new Map() },
  notifications: { page: 1, pageSize: 20, totalCount: 0, request: 0 },
  recovery: { request: 0 }
};

const METRIC_FIELDS = [
  ["metric-active-shops", "activeShopsCount"],
  ["metric-lifetime-bases", "lifetimeBaseCount"],
  ["metric-fixed-term-bases", "fixedTermBaseCount"],
  ["metric-expiring-addons", "expiringAddonsCount"],
  ["metric-expiring-fixed-bases", "expiringFixedTermBasesCount"],
  ["metric-pending-approvals", "pendingApprovalsCount"]
];

const QUEUE_LABELS = {
  PAYMENT_REVIEW: "Payment reviews",
  HARDWARE_REVIEW: "Hardware reviews",
  DUAL_APPROVAL: "Dual approvals",
  SUPPORT_INBOX: "Support inbox"
};

const VIEW_TITLES = {
  dashboard: "Enterprise Dashboard",
  customer360: "Customer 360",
  commercial: "Commercial & Orders",
  licensing: "Licensing & Hardware",
  approvals: "Dual-Control Approvals",
  backups: "Encrypted Backups",
  support: "Support Tickets",
  notifications: "Notifications",
  audit: "Central Audit Trail",
  recovery: "Recovery & Attestation"
};

// Visibility improves navigation only. The server remains the authority for every request.
const VIEW_CAPABILITIES = {
  dashboard: ["dashboard.view", "queues.view"],
  customer360: ["dashboard.view", "search.masked", "customer.create", "shop.register"],
  commercial: ["licensing.read", "licensing.manage", "license.view", "customer.create", "shop.register", "order.create", "payment.review"],
  licensing: ["license.view", "activation.review", "hardware.review", "primary.transfer"],
  approvals: ["queues.view"],
  backups: ["backup.download", "backup.upload.init"],
  support: ["support.view", "support.create", "support.reply"],
  notifications: ["notifications.view"],
  audit: ["audit.view"],
  recovery: ["security.manage"]
};

function hasCapability(capability) {
  return AppState.currentUser?.capabilities.some(value => value.toLowerCase() === capability.toLowerCase()) === true;
}

function canView(viewName) {
  return Object.hasOwn(VIEW_CAPABILITIES, viewName) && VIEW_CAPABILITIES[viewName].some(hasCapability);
}

function applyCapabilityNavigation() {
  document.querySelectorAll(".nav-link, [data-go-view]").forEach(link => {
    const allowed = canView(link.dataset.view || link.dataset.goView);
    link.hidden = !allowed;
    if (link.classList.contains("nav-link")) link.closest("li").hidden = !allowed;
  });
  applySupportControls();
  applyDirectoryControls();
  document.getElementById("directory-card").hidden = !hasCapability("search.masked");
}

function syncMobileMenu() {
  const sidebar = document.getElementById("sidebar");
  sidebar.inert = window.matchMedia("(max-width: 768px)").matches && !sidebar.classList.contains("open");
}

const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";

function isGuid(value) {
  return typeof value === "string" && GUID_PATTERN.test(value) && value.toLowerCase() !== EMPTY_GUID;
}

function node(tag, text, className) {
  const element = document.createElement(tag);
  if (text !== undefined) element.textContent = text;
  if (className) element.className = className;
  return element;
}

function setMessage(id, message) {
  const element = document.getElementById(id);
  element.textContent = message;
  element.style.display = message ? "block" : "none";
}

function textValue(value) {
  return typeof value === "string" && value.trim() ? value : "Not provided";
}

function formatUtc(value) {
  if (typeof value !== "string" || !value.trim()) return "Unavailable";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "Unavailable" : date.toISOString().replace("T", " ").replace("Z", " UTC");
}

function tableMessage(tbodyId, columnCount, message) {
  const row = node("tr");
  const cell = node("td", message);
  cell.colSpan = columnCount;
  cell.style.textAlign = "center";
  cell.style.padding = "1.5rem";
  row.appendChild(cell);
  document.getElementById(tbodyId).replaceChildren(row);
}

function appendCells(row, values) {
  values.forEach(value => row.appendChild(node("td", value)));
}

function requestIsCurrent(version) {
  return AppState.currentUser !== null && AppState.sessionVersion === version;
}

// Cookies are supplied by the browser. Never copy a session token into storage or headers.
function apiFetch(url, options = {}) {
  const headers = new Headers(options.headers);
  headers.set("Accept", "application/json");
  if (options.body !== undefined) headers.set("Content-Type", "application/json");
  return fetch(url, { ...options, headers, credentials: "same-origin", cache: "no-store" });
}

async function responseError(response, fallback) {
  let code = "";
  try {
    const data = await response.json();
    if (typeof data?.code === "string" && /^[A-Z0-9_]{1,80}$/.test(data.code)) code = data.code;
  } catch (_) {
    // Empty/non-JSON failures must still be visible.
  }
  const statuses = {
    401: "Authentication is required or the session has expired.",
    403: "The server denied access to this operation.",
    404: "The requested resource is unavailable.",
    429: "Too many requests. Please retry later."
  };
  return new Error(`${statuses[response.status] || fallback} (HTTP ${response.status}${code ? `; ${code}` : ""})`);
}

async function readJson(url, version) {
  const response = await apiFetch(url);
  if (!response.ok) {
    const error = await responseError(response, "The server could not complete this read.");
    if (response.status === 401 && requestIsCurrent(version)) {
      clearIdentity();
      showAuthView("Session expired. Sign in again.");
    }
    throw error;
  }
  return response.json();
}

// Theme storage is optional and holds no authentication data.
function initTheme() {
  try {
    // Remove the obsolete bearer-token value left by earlier portal versions.
    localStorage.removeItem("central_session_id");
    const saved = localStorage.getItem("central_portal_theme");
    if (saved === "light" || saved === "dark") AppState.theme = saved;
  } catch (_) {
    // The portal remains usable when browser storage is unavailable.
  }
  const button = document.getElementById("btn-theme-toggle");
  const apply = () => {
    document.documentElement.setAttribute("data-theme", AppState.theme);
    button.textContent = AppState.theme === "dark" ? "Light theme" : "Dark theme";
  };
  button.addEventListener("click", () => {
    AppState.theme = AppState.theme === "dark" ? "light" : "dark";
    try { localStorage.setItem("central_portal_theme", AppState.theme); } catch (_) {}
    apply();
  });
  apply();
}

function setAuthBusy(busy) {
  document.querySelectorAll("#login-form button, #mfa-form button, #btn-check-session, #btn-back-login")
    .forEach(button => { button.disabled = busy; });
}

function clearIdentity() {
  clearSensitiveDisclosure();
  if (AppState.sessionExpiryTimer !== null) clearTimeout(AppState.sessionExpiryTimer);
  AppState.sessionExpiryTimer = null;
  AppState.currentUser = null;
  AppState.loginChallengeId = null;
  AppState.activeShopId = null;
  AppState.selectedQueueItem = null;
  AppState.sessionVersion += 1;
  document.getElementById("queue-inspect-modal").classList.remove("active");
  document.querySelectorAll("[data-queue-detail]").forEach(element => { element.textContent = ""; });
  document.getElementById("user-display-name").textContent = "";
  document.getElementById("user-avatar-text").textContent = "";
  document.getElementById("user-capabilities").textContent = "";
  document.getElementById("user-capabilities").removeAttribute("title");
  document.getElementById("session-capabilities").textContent = "";
  document.getElementById("customer-search-input").value = "";
  document.getElementById("customer-360-details").replaceChildren();
  document.getElementById("directory-results").replaceChildren();
  document.getElementById("customer-create-form").reset();
  document.getElementById("shop-create-form").reset();
  setMessage("customer-create-status", "");
  setMessage("shop-create-status", "");
  AppState.directoryMutationBusy = false;
  applyDirectoryControls();
  document.getElementById("directory-card").hidden = true;
  setMessage("directory-status", "");
  AppState.directory.request += 1;
  AppState.directory.cursor = null;
  AppState.directory.version = null;
  AppState.directory.filter = "";
  document.getElementById("directory-filter").value = "";
  document.getElementById("directory-next").disabled = true;
  METRIC_FIELDS.forEach(([id]) => { document.getElementById(id).textContent = "Unavailable"; });
  tableMessage("dashboard-queue-tbody", 5, "Sign in to load queue items.");
  tableMessage("approvals-table-tbody", 5, "Sign in to load approval queue items.");
  tableMessage("audit-tbody", 7, "Sign in to load audit records.");
  Object.values(AppState.queues).forEach(state => { state.page = 1; });
  Object.values(AppState.queues).forEach(state => {
    state.cursorStack = [null];
    state.nextCursor = null;
    state.asOfVersion = null;
    state.authorizedCount = 0;
  });
  AppState.audit.page = 1;
  AppState.audit.cursorStack = [null];
  AppState.audit.nextCursor = null;
  AppState.audit.asOfVersion = null;
  AppState.support.page = 1;
  AppState.support.cursorStack = [null];
  AppState.support.nextCursor = null;
  AppState.support.asOfVersion = null;
  AppState.support.authorizedCount = 0;
  AppState.support.items = [];
  AppState.support.selectedId = null;
  AppState.support.busy = false;
  AppState.support.operations.clear();
  AppState.notifications.page = 1;
  document.getElementById("support-create-form").reset();
  document.getElementById("support-reply-form").reset();
  document.getElementById("support-status-form").reset();
  document.getElementById("support-ticket-metadata").replaceChildren();
  document.getElementById("support-ticket-summary").textContent = "";
  document.getElementById("support-ticket-attachments").replaceChildren();
  document.getElementById("support-history").replaceChildren();
  ["support-create-status", "support-reply-status", "support-change-status", "support-detail-status", "support-access-status"].forEach(id => setMessage(id, ""));
  tableMessage("support-tbody", 6, "Sign in to load support tickets.");
  tableMessage("notifications-tbody", 4, "Sign in to load notifications.");
  setMessage("support-list-status", "");
  setMessage("notifications-status", "");
  setMessage("notifications-worker-status", "Worker activity is unknown. Sign in and refresh to check the server.");
  applyCapabilityNavigation();
  setMessage("shell-status", "");
}

function showAuthView(message = "") {
  document.getElementById("app-shell").style.display = "none";
  document.getElementById("auth-wrapper").style.display = "flex";
  document.getElementById("login-step").style.display = "block";
  document.getElementById("mfa-step").style.display = "none";
  document.getElementById("login-password").value = "";
  document.getElementById("mfa-totp").value = "";
  setMessage("auth-status", message);
}

async function checkCurrentSession(afterMfa = false) {
  // Always ask the server, including a fresh visit with no browser storage.
  setAuthBusy(true);
  try {
    const response = await apiFetch("/api/admin/v1/portal/session");
    if (!response.ok) {
      const error = await responseError(response, "Session verification failed.");
      clearIdentity();
      showAuthView(response.status === 401 && !afterMfa ? "Sign in to continue." : error.message);
      return false;
    }
    const context = await response.json();
    if (!context || !isGuid(context.adminUserId) || typeof context.username !== "string" || !context.username.trim() ||
        typeof context.expiresAtUtc !== "string" || !Number.isFinite(Date.parse(context.expiresAtUtc)) ||
        !Array.isArray(context.capabilities) || !context.capabilities.every(value => typeof value === "string")) {
      throw new Error("The server returned an incomplete session context. Portal access is unavailable.");
    }
    if (AppState.currentUser && AppState.currentUser.id !== context.adminUserId) clearIdentity();
    AppState.sessionVersion += 1;
    AppState.loginChallengeId = null;
    // The session identifier is deliberately not retained by the client.
    AppState.currentUser = { id: context.adminUserId, username: context.username, capabilities: context.capabilities };
    if (AppState.sessionExpiryTimer !== null) clearTimeout(AppState.sessionExpiryTimer);
    AppState.sessionExpiryTimer = setTimeout(() => {
      AppState.sessionExpiryTimer = null;
      clearSensitiveDisclosure();
    }, Math.max(0, Date.parse(context.expiresAtUtc) - Date.now()));
    document.getElementById("user-display-name").textContent = context.username;
    document.getElementById("user-avatar-text").textContent = context.username.substring(0, 2).toUpperCase();
    const capabilities = document.getElementById("user-capabilities");
    capabilities.textContent = `${context.capabilities.length} capabilities`;
    capabilities.title = context.capabilities.join(", ") || "No capabilities returned";
    document.getElementById("session-capabilities").textContent = context.capabilities.length
      ? `Session capabilities: ${context.capabilities.join(", ")}` : "Session capabilities: none returned by the server.";
    document.getElementById("auth-wrapper").style.display = "none";
    document.getElementById("app-shell").style.display = "flex";
    setMessage("shell-status", "");
    applyCapabilityNavigation();
    const requestedView = afterMfa ? "dashboard" : AppState.activeView;
    const availableView = canView(requestedView) ? requestedView : Object.keys(VIEW_TITLES).find(canView);
    if (availableView) navigateTo(availableView);
    else {
      document.querySelectorAll(".view-container").forEach(view => view.classList.remove("active"));
      document.getElementById("current-page-title").textContent = "Administration";
      setMessage("shell-status", "Your session has no capabilities for the available portal views.");
    }
    return true;
  } catch (error) {
    clearIdentity();
    showAuthView(afterMfa
      ? `MFA response received, but the authenticated session could not be verified. Retry session check. ${error.message}`
      : `Unable to verify your session. Retry session check or sign in. ${error.message}`);
    return false;
  } finally {
    setAuthBusy(false);
  }
}

function bindAuthHandlers() {
  document.getElementById("btn-check-session").addEventListener("click", () => checkCurrentSession());
  document.getElementById("btn-back-login").addEventListener("click", () => {
    AppState.loginChallengeId = null;
    showAuthView("Sign in to request a new MFA challenge.");
  });
  document.getElementById("login-form").addEventListener("submit", async event => {
    event.preventDefault();
    setAuthBusy(true);
    setMessage("login-error", "");
    setMessage("auth-status", "");
    AppState.loginChallengeId = null;
    const username = document.getElementById("login-username").value.trim();
    const passwordOrProviderAssertion = document.getElementById("login-password").value;
    try {
      const response = await apiFetch("/api/admin/v1/auth/login", {
        method: "POST", body: JSON.stringify({ username, passwordOrProviderAssertion })
      });
      if (!response.ok) throw await responseError(response, "Sign-in failed.");
      const challenge = await response.json();
      if (!challenge || !isGuid(challenge.challengeId)) throw new Error("The server did not return a valid MFA challenge.");
      AppState.loginChallengeId = challenge.challengeId;
      document.getElementById("login-step").style.display = "none";
      document.getElementById("mfa-step").style.display = "block";
      setMessage("mfa-error", "");
      document.getElementById("mfa-totp").value = "";
      document.getElementById("mfa-totp").focus();
    } catch (error) {
      setMessage("login-error", `Unable to sign in. ${error.message}`);
    } finally {
      document.getElementById("login-password").value = "";
      setAuthBusy(false);
    }
  });

  document.getElementById("mfa-form").addEventListener("submit", async event => {
    event.preventDefault();
    setAuthBusy(true);
    setMessage("mfa-error", "");
    try {
      if (!isGuid(AppState.loginChallengeId)) throw new Error("Login challenge unavailable. Return to sign-in.");
      const proof = document.getElementById("mfa-totp").value.trim();
      const response = await apiFetch("/api/admin/v1/auth/mfa", {
        method: "POST", body: JSON.stringify({ loginChallengeId: AppState.loginChallengeId, proof })
      });
      if (!response.ok) throw await responseError(response, "MFA verification failed.");
      // MFA supplies a Secure, HttpOnly, SameSite cookie; identity comes only from portal/session.
      await checkCurrentSession(true);
    } catch (error) {
      setMessage("mfa-error", `Unable to complete MFA. ${error.message}`);
    } finally {
      document.getElementById("mfa-totp").value = "";
      setAuthBusy(false);
    }
  });

  document.getElementById("btn-logout").addEventListener("click", async () => {
    const button = document.getElementById("btn-logout");
    button.disabled = true;
    setMessage("shell-status", "");
    try {
      const response = await apiFetch("/api/admin/v1/auth/logout", { method: "POST" });
      if (!response.ok) throw await responseError(response, "Logout failed.");
      const result = await response.json();
      if (result?.success !== true) throw new Error("The server did not confirm logout.");
      clearIdentity();
      showAuthView("Logout acknowledged by the server.");
    } catch (error) {
      // Do not hide an active session or claim success when revocation was not confirmed.
      setMessage("shell-status", `Logout not confirmed; your session may still be active. Retry logout. ${error.message}`);
    } finally {
      button.disabled = false;
    }
  });
}

function bindNavigation() {
  document.getElementById("btn-menu").addEventListener("click", () => {
    const open = document.getElementById("sidebar").classList.toggle("open");
    document.getElementById("btn-menu").setAttribute("aria-expanded", String(open));
    syncMobileMenu();
  });
  document.querySelectorAll(".nav-link, [data-go-view]").forEach(link => {
    link.addEventListener("click", event => {
      event.preventDefault();
      navigateTo(link.dataset.view || link.dataset.goView);
    });
  });
  document.querySelectorAll("[data-queue]").forEach(button => {
    button.addEventListener("click", () => loadQueue(button.dataset.queue, 1, "dashboard", true));
  });
  Object.keys(AppState.queues).forEach(scope => {
    document.getElementById(`${scope}-queue-prev`).addEventListener("click", () => {
      const state = AppState.queues[scope];
      if (state.page > 1) loadQueue(state.type, state.page - 1, scope);
    });
    document.getElementById(`${scope}-queue-next`).addEventListener("click", () => {
      const state = AppState.queues[scope];
      if (state.nextCursor === null) return;
      state.cursorStack[state.page] = state.nextCursor;
      loadQueue(state.type, state.page + 1, scope);
    });
  });
  document.getElementById("customer-create-form").addEventListener("submit", event => { event.preventDefault(); createDirectoryRecord(false); });
  document.getElementById("shop-create-form").addEventListener("submit", event => { event.preventDefault(); createDirectoryRecord(true); });
  document.getElementById("directory-search-form").addEventListener("submit", event => {
    event.preventDefault();
    AppState.directory.filter = document.getElementById("directory-filter").value.trim();
    AppState.directory.cursor = null;
    AppState.directory.version = null;
    searchDirectory(false);
  });
  document.getElementById("directory-next").addEventListener("click", () => searchDirectory(true));
  document.getElementById("customer-search-form").addEventListener("submit", event => {
    event.preventDefault();
    fetchCustomer360Data(document.getElementById("customer-search-input").value.trim());
  });
  document.getElementById("audit-prev").addEventListener("click", () => {
    if (AppState.audit.page > 1) loadAudit(AppState.audit.page - 1);
  });
  document.getElementById("audit-next").addEventListener("click", () => {
    if (AppState.audit.nextCursor) loadAudit(AppState.audit.page + 1);
  });
  document.getElementById("btn-close-queue-modal").addEventListener("click", closeQueueModal);
  document.getElementById("btn-queue-shop").addEventListener("click", () => {
    const item = AppState.selectedQueueItem;
    if (!item || !isGuid(item.shopId)) return;
    AppState.activeShopId = item.shopId;
    document.getElementById("customer-search-input").value = item.shopId;
    closeQueueModal();
    navigateTo("customer360");
  });
  document.addEventListener("keydown", event => {
    if (event.key === "Escape") {
      closeQueueModal();
      const sidebar = document.getElementById("sidebar");
      if (sidebar.classList.contains("open")) {
        sidebar.classList.remove("open");
        document.getElementById("btn-menu").setAttribute("aria-expanded", "false");
        syncMobileMenu();
        document.getElementById("btn-menu").focus();
      }
    }
  });
  window.matchMedia("(max-width: 768px)").addEventListener("change", syncMobileMenu);
  syncMobileMenu();
}

function navigateTo(viewName) {
  if (!AppState.currentUser || !Object.hasOwn(VIEW_TITLES, viewName)) return;
  if (!canView(viewName)) {
    setMessage("shell-status", "Your session does not have access to this view.");
    return;
  }
  clearSensitiveDisclosure();
  AppState.activeView = viewName;
  document.querySelectorAll(".nav-link").forEach(link => {
    const active = link.dataset.view === viewName;
    link.classList.toggle("active", active);
    if (active) link.setAttribute("aria-current", "page"); else link.removeAttribute("aria-current");
  });
  document.querySelectorAll(".view-container").forEach(view => view.classList.toggle("active", view.id === `${viewName}-view`));
  document.getElementById("current-page-title").textContent = VIEW_TITLES[viewName];
  document.getElementById("sidebar").classList.remove("open");
  document.getElementById("btn-menu").setAttribute("aria-expanded", "false");
  syncMobileMenu();
  const handlers = {
    dashboard: loadDashboard, customer360: loadCustomer360, commercial: loadCommercial,
    licensing: loadLicensing, approvals: loadApprovals, backups: loadBackups,
    audit: () => loadAudit(AppState.audit.page), recovery: loadRecovery,
    support: () => loadSupport(AppState.support.page), notifications: () => loadNotifications(AppState.notifications.page)
  };
  handlers[viewName]();
}

async function loadDashboard() {
  if (hasCapability("queues.view")) loadQueue(AppState.queues.dashboard.type, AppState.queues.dashboard.page);
  else {
    tableMessage("dashboard-queue-tbody", 5, "Your session cannot view operational queues.");
    setMessage("dashboard-queue-status", "Queue access is unavailable for this session.");
  }
  if (!hasCapability("dashboard.view")) {
    METRIC_FIELDS.forEach(([id]) => { document.getElementById(id).textContent = "Unavailable"; });
    setMessage("metrics-status", "Your session cannot view dashboard metrics.");
    return;
  }
  const version = AppState.sessionVersion;
  const request = ++AppState.metricsRequest;
  METRIC_FIELDS.forEach(([id]) => { document.getElementById(id).textContent = "Loading"; });
  setMessage("metrics-status", "Loading server metrics.");
  try {
    const data = await readJson("/api/admin/v1/dashboard/metrics", version);
    if (!requestIsCurrent(version) || request !== AppState.metricsRequest) return;
    if (!data || typeof data !== "object" || Array.isArray(data)) throw new Error("Unexpected metrics response.");
    const missing = [];
    METRIC_FIELDS.forEach(([id, field]) => {
      const value = data[field];
      const valid = Number.isSafeInteger(value) && value >= 0;
      document.getElementById(id).textContent = valid ? value.toLocaleString() : "Unavailable";
      if (!valid) missing.push(field);
    });
    setMessage("metrics-status", missing.length ? `Incomplete metrics response. Unavailable fields: ${missing.join(", ")}.` : "Current counts returned by the server; not a health attestation.");
  } catch (error) {
    if (!requestIsCurrent(version) || request !== AppState.metricsRequest) return;
    METRIC_FIELDS.forEach(([id]) => { document.getElementById(id).textContent = "Unavailable"; });
    setMessage("metrics-status", `Metrics unavailable. ${error.message}`);
  }
}

async function loadQueue(queueType, page = 1, scope = "dashboard", resetSnapshot = false) {
  if (!AppState.currentUser || !Object.hasOwn(AppState.queues, scope) || !Object.hasOwn(QUEUE_LABELS, queueType) ||
      !Number.isSafeInteger(page) || page < 1) return;
  const state = AppState.queues[scope];
  if (resetSnapshot || state.type !== queueType) {
    state.cursorStack = [null];
    state.nextCursor = null;
    state.asOfVersion = null;
  }
  state.type = queueType;
  state.page = page;
  const request = ++state.request;
  const version = AppState.sessionVersion;
  const tbodyId = scope === "dashboard" ? "dashboard-queue-tbody" : "approvals-table-tbody";
  const previous = document.getElementById(`${scope}-queue-prev`);
  const next = document.getElementById(`${scope}-queue-next`);
  previous.disabled = true;
  next.disabled = true;
  tableMessage(tbodyId, 5, `Loading ${QUEUE_LABELS[queueType]}...`);
  setMessage(`${scope}-queue-status`, `Loading page ${page}.`);
  if (scope === "dashboard") {
    document.querySelectorAll("[data-queue]").forEach(button => button.setAttribute("aria-pressed", String(button.dataset.queue === queueType)));
  }
  try {
    const query = new URLSearchParams({ pageSize: String(state.pageSize) });
    const cursor = state.cursorStack[page - 1];
    if (typeof cursor === "string") query.set("cursor", cursor);
    if (Number.isSafeInteger(state.asOfVersion)) query.set("asOfVersion", String(state.asOfVersion));
    const data = await readJson(`/api/admin/v1/queues/${encodeURIComponent(queueType)}?${query}`, version);
    if (!requestIsCurrent(version) || request !== state.request) return;
    if (!data || !Array.isArray(data.items) || !data.items.every(item => item && isGuid(item.entityId) && item.queueType === queueType) ||
        !(data.nextCursor === null || typeof data.nextCursor === "string") ||
        !Number.isSafeInteger(data.asOfVersion) || data.asOfVersion < 0 ||
        !Number.isSafeInteger(data.authorizedCount) || data.authorizedCount < 0 ||
        !Number.isSafeInteger(data.projectionRevision) || data.projectionRevision < 0) {
      throw new Error("The server returned an incomplete queue snapshot.");
    }
    const items = data.items;
    state.nextCursor = data.nextCursor;
    state.asOfVersion = data.asOfVersion;
    state.authorizedCount = data.authorizedCount;
    const tbody = document.getElementById(tbodyId);
    tbody.replaceChildren();
    if (!items.length) tableMessage(tbodyId, 5, `No ${QUEUE_LABELS[queueType]} items returned for this page.`);
    items.forEach(item => {
      const row = node("tr");
      appendCells(row, [item.entityId, textValue(item.title), isGuid(item.shopId) ? item.shopId : "Not supplied", textValue(item.status)]);
      const cell = node("td");
      const inspect = node("button", "Inspect (read only)", "btn btn-secondary btn-sm");
      inspect.type = "button";
      inspect.addEventListener("click", () => handleQueueAction(item));
      cell.appendChild(inspect);
      row.appendChild(cell);
      tbody.appendChild(row);
    });
    setMessage(`${scope}-queue-status`, `${QUEUE_LABELS[queueType]} — ${data.authorizedCount} records in this snapshot; page ${page}.`);
    previous.disabled = page <= 1;
    next.disabled = data.nextCursor === null;
  } catch (error) {
    if (!requestIsCurrent(version) || request !== state.request) return;
    if (String(error?.message || "").includes("PROJECTION_VERSION_CHANGED") && page > 1) {
      state.cursorStack = [null];
      state.nextCursor = null;
      state.asOfVersion = null;
      await loadQueue(queueType, 1, scope, true);
      return;
    }
    tableMessage(tbodyId, 5, "Queue unavailable; no review or decision has been performed.");
    setMessage(`${scope}-queue-status`, error.message);
    previous.disabled = page <= 1;
  }
}

function handleQueueAction(item) {
  if (!AppState.currentUser || !isGuid(item.entityId) || !Object.hasOwn(QUEUE_LABELS, item.queueType)) return;
  AppState.selectedQueueItem = item;
  const fields = {
    "queue-item-entity": item.entityId,
    "queue-item-type": item.queueType,
    "queue-item-title": textValue(item.title),
    "queue-item-status": textValue(item.status),
    "queue-item-created": formatUtc(item.createdAtUtc),
    "queue-item-summary": textValue(item.reasonOrSummary)
  };
  Object.entries(fields).forEach(([id, value]) => { document.getElementById(id).textContent = value; });
  document.getElementById("queue-item-workflow").textContent = item.queueType === "SUPPORT_INBOX"
    ? "Open Support Tickets to view the persisted conversation or save a permitted staff reply. Inspecting this item makes no change."
    : `The ${QUEUE_LABELS[item.queueType]} decision workflow is unavailable in this view. No mutation has been submitted.`;
  document.getElementById("btn-queue-shop").disabled = !isGuid(item.shopId);
  document.getElementById("queue-inspect-modal").classList.add("active");
  document.getElementById("btn-close-queue-modal").focus();
}

function closeQueueModal() {
  document.getElementById("queue-inspect-modal").classList.remove("active");
  AppState.selectedQueueItem = null;
}

function clearSensitiveDisclosure() {
  const state = AppState.pii;
  state.request += 1;
  state.auditRecordId = null;
  state.resource = null;
  state.resourceId = null;
  state.busy = false;
  const opener = state.opener;
  state.opener = null;
  const modal = document.getElementById("pii-unmask-modal");
  modal.classList.remove("active");
  modal.hidden = true;
  document.getElementById("pii-unmask-form").reset();
  document.getElementById("pii-unmask-record").textContent = "";
  setMessage("pii-unmask-status", "");
  const result = document.getElementById("pii-unmask-result");
  result.replaceChildren();
  result.hidden = true;
  document.getElementById("btn-submit-pii-unmask").disabled = false;
  if (opener?.isConnected && AppState.currentUser) opener.focus();
}

function canRequestPiiReveal(record) {
  const resource = typeof record?.resource === "string" ? record.resource.toLowerCase() : "";
  return hasCapability("customer.view_sensitive") && hasCapability("audit.read") && isGuid(record?.id) &&
    isGuid(record?.resourceId) && ["customer", "shop"].includes(resource);
}

function openPiiUnmask(record, opener) {
  if (!canRequestPiiReveal(record)) return;
  const state = AppState.pii;
  state.auditRecordId = record.id;
  state.resource = record.resource;
  state.resourceId = record.resourceId;
  state.opener = opener;
  const result = document.getElementById("pii-unmask-result");
  result.replaceChildren();
  result.hidden = true;
  document.getElementById("pii-unmask-record").textContent = `${record.resource} · ${record.resourceId} · Audit ${record.id}`;
  setMessage("pii-unmask-status", "Enter the operational reason for this access.");
  const modal = document.getElementById("pii-unmask-modal");
  modal.hidden = false;
  modal.classList.add("active");
  document.getElementById("pii-unmask-reason").focus();
}

function renderPiiReveal(data) {
  const result = document.getElementById("pii-unmask-result");
  const details = JSON.parse(data.unmaskedData);
  if (!details || typeof details !== "object" || Array.isArray(details)) throw new Error("The server returned an invalid contact response.");
  const fields = [
    ["Customer ID", "customerId"], ["Legal name", "legalName"], ["Contact reference", "contactReference"],
    ["Contact email", "contactEmail"], ["Contact phone", "contactPhone"]
  ];
  const list = document.createElement("dl");
  list.className = "pii-result-fields";
  for (const [label, key] of fields) {
    if (!Object.hasOwn(details, key)) continue;
    const term = node("dt", label, "form-label");
    const value = node("dd", textValue(details[key]));
    list.append(term, value);
  }
  const loggedAt = formatUtc(data.accessLoggedAtUtc);
  result.replaceChildren(node("p", `Central recorded this access at ${loggedAt}.`), list);
  result.hidden = false;
}

async function submitPiiUnmask(event) {
  event.preventDefault();
  const state = AppState.pii;
  const reason = document.getElementById("pii-unmask-reason").value;
  if (!AppState.currentUser || state.busy || !isGuid(state.auditRecordId) || !reason.trim() ||
      !hasCapability("customer.view_sensitive") || !hasCapability("audit.read")) {
    setMessage("pii-unmask-status", "A valid audit record, both required capabilities and an operational reason are required.");
    return;
  }
  const session = AppState.sessionVersion;
  const request = ++state.request;
  state.busy = true;
  document.getElementById("btn-submit-pii-unmask").disabled = true;
  setMessage("pii-unmask-status", "Requesting audited access from Central...");
  try {
    const response = await apiFetch("/api/admin/v1/sensitive-approvals/unmask-pii", {
      method: "POST", body: JSON.stringify({ auditRecordId: state.auditRecordId, reason })
    });
    if (!response.ok) {
      if (response.status === 401 && requestIsCurrent(session)) {
        clearIdentity();
        showAuthView("Session expired. Sign in again before requesting sensitive data.");
      }
      throw await responseError(response, "Central did not authorize this request.");
    }
    const data = await response.json();
    if (!requestIsCurrent(session) || request !== state.request) return;
    if (!data || data.auditRecordId !== state.auditRecordId || typeof data.unmaskedData !== "string" ||
        typeof data.accessLoggedAtUtc !== "string") throw new Error("The server did not confirm the audited reveal.");
    renderPiiReveal(data);
    setMessage("pii-unmask-status", "Contact details are visible only in this open dialog. Close it or navigate away to clear them.");
  } catch (error) {
    if (requestIsCurrent(session) && request === state.request) setMessage("pii-unmask-status", `Contact access was not completed. ${error.message}`);
  } finally {
    if (request === state.request) {
      state.busy = false;
      document.getElementById("btn-submit-pii-unmask").disabled = false;
    }
  }
}

function loadCustomer360() {
  if (AppState.activeShopId) fetchCustomer360Data(AppState.activeShopId);
}

async function fetchCustomer360Data(shopId) {
  if (!AppState.currentUser) return;
  const container = document.getElementById("customer-360-details");
  const request = ++AppState.customerRequest;
  if (!isGuid(shopId)) {
    AppState.activeShopId = null;
    container.replaceChildren(node("div", "Enter a valid non-empty Shop GUID. No request was sent.", "card card-body"));
    return;
  }
  AppState.activeShopId = shopId;
  const version = AppState.sessionVersion;
  container.replaceChildren(node("div", "Loading Customer 360...", "card card-body"));
  try {
    const data = await readJson(`/api/admin/v1/dashboard/customer360/${encodeURIComponent(shopId)}`, version);
    if (!requestIsCurrent(version) || request !== AppState.customerRequest) return;
    if (!data || !isGuid(data.shopId) || data.shopId.toLowerCase() !== shopId.toLowerCase() ||
        !Array.isArray(data.installations) || !Array.isArray(data.addons) ||
        !Array.isArray(data.recentOrders) || !Array.isArray(data.recentTickets) ||
        !(data.enforcementPolicy === null || data.enforcementPolicy === undefined ||
          data.enforcementPolicy && typeof data.enforcementPolicy === "object" && !Array.isArray(data.enforcementPolicy))) {
      throw new Error("Unexpected Customer 360 response; matching shopId and installations are required.");
    }
    renderCustomer360(data);
  } catch (error) {
    if (!requestIsCurrent(version) || request !== AppState.customerRequest) return;
    container.replaceChildren(node("div", `Customer 360 unavailable. ${error.message}`, "card card-body"));
  }
}

function detailField(container, label, value) {
  const field = node("div");
  field.append(node("div", label, "form-label"), node("div", value));
  container.appendChild(field);
}

function detailCard(title) {
  const card = node("section", undefined, "card");
  const header = node("div", undefined, "card-header");
  header.appendChild(node("h3", title, "card-title"));
  const body = node("div", undefined, "card-body");
  body.style.display = "grid";
  body.style.gridTemplateColumns = "repeat(auto-fit, minmax(200px, 1fr))";
  body.style.gap = "1rem";
  card.append(header, body);
  return { card, body };
}

function detailTable(title, headers, rows, emptyMessage) {
  const card = detailCard(title);
  card.body.style.display = "block";
  const wrapper = node("div", undefined, "table-responsive");
  const table = node("table", undefined, "data-table");
  const head = node("thead");
  const headingRow = node("tr");
  headers.forEach(label => headingRow.appendChild(node("th", label)));
  head.appendChild(headingRow);
  const body = node("tbody");
  if (rows.length) {
    rows.forEach(values => {
      const row = node("tr");
      appendCells(row, values);
      body.appendChild(row);
    });
  }
  if (!rows.length) {
    const row = node("tr");
    const cell = node("td", emptyMessage);
    cell.colSpan = headers.length;
    row.appendChild(cell);
    body.appendChild(row);
  }
  table.append(head, body);
  wrapper.appendChild(table);
  card.body.appendChild(wrapper);
  return card.card;
}

function renderCustomer360(data) {
  const container = document.getElementById("customer-360-details");
  const identity = detailCard("Shop Identity");
  detailField(identity.body, "Shop ID", data.shopId);
  detailField(identity.body, "Shop Code", textValue(data.shopCode));
  detailField(identity.body, "Shop Name", textValue(data.shopName));
  detailField(identity.body, "Shop Status", textValue(data.status));
  detailField(identity.body, "Created (UTC)", formatUtc(data.createdAtUtc));

  const commercial = detailCard("Commercial Base License");
  const license = data.commercialLicense;
  if (license && typeof license === "object" && !Array.isArray(license)) {
    detailField(commercial.body, "License ID", textValue(license.id));
    detailField(commercial.body, "Term Kind", textValue(license.termKind));
    detailField(commercial.body, "Status", textValue(license.status));
    const lifetimeWithoutExpiry = license.termKind === "LIFETIME" && license.validUntilUtc === null;
    detailField(commercial.body, "Valid Until (UTC)", lifetimeWithoutExpiry ? "No expiry (lifetime base)" : formatUtc(license.validUntilUtc));
    detailField(commercial.body, "Authority Revision", Number.isSafeInteger(license.authorityRevision) ? String(license.authorityRevision) : "Unavailable");
    if (license.termKind === "LIFETIME" && license.validUntilUtc !== null) {
      commercial.body.appendChild(node("p", "Unexpected lifetime expiry value; review the server record."));
    }
  } else {
    commercial.body.appendChild(node("p", license === null ? "No active commercial base license returned." : "Commercial license data unavailable in this response."));
  }

  const installations = detailCard(`Installations (${data.installations.length})`);
  installations.body.style.display = "block";
  const tableWrapper = node("div", undefined, "table-responsive");
  const table = node("table", undefined, "data-table");
  const head = node("thead");
  const headings = node("tr");
  ["Installation ID", "Machine Fingerprint", "Hostname", "OS Platform", "Status", "Registered (UTC)"].forEach(label => headings.appendChild(node("th", label)));
  head.appendChild(headings);
  const tbody = node("tbody");
  data.installations.forEach(installation => {
    const row = node("tr");
    appendCells(row, [textValue(installation?.id), textValue(installation?.machineFingerprint), textValue(installation?.hostname),
      textValue(installation?.osPlatform), textValue(installation?.status), formatUtc(installation?.registeredAtUtc)]);
    tbody.appendChild(row);
  });
  if (!data.installations.length) {
    const row = node("tr");
    const cell = node("td", "No installations returned for this shop.");
    cell.colSpan = 6;
    row.appendChild(cell);
    tbody.appendChild(row);
  }
  table.append(head, tbody);
  tableWrapper.appendChild(table);
  installations.body.appendChild(tableWrapper);

  const addonRows = data.addons.map(addon => [
    textValue(addon?.id), textValue(addon?.kind), textValue(addon?.status), formatUtc(addon?.validUntilUtc),
    addon?.isExpiringSoon === true ? "Expiring soon" : addon?.isExpiringSoon === false ? "No" : "Unavailable"
  ]);
  const addons = detailTable("Add-on Entitlements", ["Entitlement ID", "Kind", "Status", "Valid Until (UTC)", "Expiring Soon"],
    addonRows, "No add-on entitlements returned for this shop.");

  const orderRows = data.recentOrders.map(order => [
    textValue(order?.id), textValue(order?.purchaseTarget), textValue(order?.termKind), textValue(order?.status),
    Number.isFinite(order?.totalAmount) ? `${order.totalAmount.toLocaleString()} ${textValue(order?.currency)}` : "Unavailable",
    formatUtc(order?.createdAtUtc)
  ]);
  const orders = detailTable("Recent Orders", ["Order ID", "Purchase Target", "Term", "Status", "Total", "Created (UTC)"],
    orderRows, "No recent orders returned for this shop.");

  const ticketRows = data.recentTickets.map(ticket => [
    textValue(ticket?.id), textValue(ticket?.subject), textValue(ticket?.status), formatUtc(ticket?.createdAtUtc)
  ]);
  const tickets = detailTable("Recent Support Tickets", ["Ticket ID", "Subject", "Status", "Created (UTC)"],
    ticketRows, "No recent support tickets returned for this shop.");

  const policy = detailCard("Enforcement Policy");
  if (data.enforcementPolicy) {
    detailField(policy.body, "Mode", textValue(data.enforcementPolicy.enforcementMode));
    detailField(policy.body, "Policy Version", Number.isSafeInteger(data.enforcementPolicy.policyVersion)
      ? String(data.enforcementPolicy.policyVersion) : "Unavailable");
    detailField(policy.body, "Extension End (UTC)", formatUtc(data.enforcementPolicy.extensionEndUtc));
  } else {
    policy.body.appendChild(node("p", "No enforcement policy returned for this shop."));
  }

  const unavailable = node("div", "License/hardware mutations remain unavailable here.", "card card-body");
  container.replaceChildren(identity.card, commercial.card, addons, installations.card, orders, tickets, policy.card, unavailable);
}

// These collection/workflow endpoints do not exist in the current controllers.
function loadCommercial() {
  tableMessage("commercial-licenses-tbody", 7, "OPEN — A commercial-license collection read is not available in the current API. Use Customer 360 for a shop's commercial base. Order/license mutations are not connected in this portal.");
}

function loadLicensing() {
  tableMessage("licensing-activations-tbody", 5, "OPEN — A global installation collection read is not available in the current API. Use Customer 360 for a shop's installations. Hardware/activation mutations are not connected in this portal.");
}

function loadApprovals() {
  loadQueue("DUAL_APPROVAL", AppState.queues.approvals.page, "approvals");
}

function loadBackups() {
  tableMessage("backups-tbody", 6, "OPEN — A backup metadata collection read is not available in the current API. No repository rows, integrity verification or custody status can be reported. Upload/download/restore workflows are not connected here.");
}

async function loadAudit(page = 1) {
  if (!AppState.currentUser || !Number.isSafeInteger(page) || page < 1) return;
  const state = AppState.audit;
  if (page > state.page + 1 || (page <= state.page && page > state.cursorStack.length)) return;
  const cursor = page === state.page + 1 ? state.nextCursor : state.cursorStack[page - 1];
  if (page > 1 && !cursor) return;
  const request = ++state.request;
  const version = AppState.sessionVersion;
  document.getElementById("audit-prev").disabled = true;
  document.getElementById("audit-next").disabled = true;
  tableMessage("audit-tbody", 7, "Loading audit records...");
  setMessage("audit-status", `Loading audit page ${page}.`);
  try {
    const query = new URLSearchParams({ pageSize: String(state.pageSize) });
    if (cursor) query.set("cursor", cursor);
    if (state.asOfVersion !== null) query.set("asOfVersion", String(state.asOfVersion));
    const data = await readJson(`/api/admin/v1/audit?${query}`, version);
    if (!requestIsCurrent(version) || request !== state.request) return;
    if (!data || !Array.isArray(data.items) || !data.items.every(record => record && typeof record === "object") ||
        !(data.nextCursor === null || typeof data.nextCursor === "string") ||
        !Number.isSafeInteger(data.asOfVersion) || data.asOfVersion < 0 ||
        !Number.isSafeInteger(data.authorizedCount) || data.authorizedCount < 0 ||
        !Number.isSafeInteger(data.projectionRevision) || data.projectionRevision !== data.asOfVersion) {
      throw new Error("Unexpected audit response; cursor and snapshot metadata are required.");
    }
    if (state.asOfVersion !== null && state.asOfVersion !== data.asOfVersion) {
      throw new Error("Audit snapshot changed while paging.");
    }
    const tbody = document.getElementById("audit-tbody");
    tbody.replaceChildren();
    if (!data.items.length) tableMessage("audit-tbody", 7, "No audit records returned for this page.");
    data.items.forEach(record => {
      const row = node("tr");
      appendCells(row, [textValue(record.id), textValue(record.action), textValue(record.actorUsername), textValue(record.reason),
        formatUtc(record.timestampUtc), record.isSuccess === true ? "Succeeded" : record.isSuccess === false ? "Failed" : "Unavailable"]);
      const accessCell = node("td");
      if (canRequestPiiReveal(record)) {
        const reveal = node("button", "Request access", "btn btn-secondary btn-sm");
        reveal.type = "button";
        reveal.addEventListener("click", () => openPiiUnmask(record, reveal));
        accessCell.appendChild(reveal);
      } else {
        accessCell.textContent = "Unavailable";
      }
      row.appendChild(accessCell);
      tbody.appendChild(row);
    });
    state.page = page;
    state.cursorStack[page - 1] = cursor ?? null;
    state.cursorStack.length = page;
    state.nextCursor = data.nextCursor;
    state.asOfVersion = data.asOfVersion;
    setMessage("audit-status", `${data.authorizedCount} records in this snapshot; page ${page}; page size ${state.pageSize}. Eligible Customer and Shop records can request audited contact access when both required capabilities are present.`);
    document.getElementById("audit-prev").disabled = page <= 1;
    document.getElementById("audit-next").disabled = !data.nextCursor;
  } catch (error) {
    if (!requestIsCurrent(version) || request !== state.request) return;
    if (error.message.includes("PROJECTION_VERSION_CHANGED") || error.message === "Audit snapshot changed while paging.") {
      state.page = 1;
      state.cursorStack = [null];
      state.nextCursor = null;
      state.asOfVersion = null;
      setMessage("audit-status", "Audit records changed. Refreshing the snapshot.");
      await loadAudit(1);
      return;
    }
    tableMessage("audit-tbody", 7, "Audit records unavailable.");
    setMessage("audit-status", error.message);
    document.getElementById("audit-prev").disabled = state.page <= 1;
    document.getElementById("audit-next").disabled = true;
  }
}

function applySupportControls() {
  const state = AppState.support;
  const selected = state.items.some(ticket => ticket.id === state.selectedId);
  document.getElementById("support-create-panel").hidden = !hasCapability("support.create");
  document.getElementById("support-list-panel").hidden = !hasCapability("support.view");
  document.getElementById("support-detail-panel").hidden = !hasCapability("support.view") || !selected;
  document.getElementById("support-reply-form").hidden = !hasCapability("support.reply") || !selected;
  document.getElementById("support-status-form").hidden = !hasCapability("support.reply") || !selected;
  document.querySelectorAll("#support-create-form input, #support-create-form textarea, #support-create-form button, #support-reply-form textarea, #support-reply-form button, #support-status-form select, #support-status-form button")
    .forEach(control => { control.disabled = state.busy; });
}

function validatePagedResponse(data, requestedPage, itemValidator) {
  if (!data || !Array.isArray(data.items) || !data.items.every(itemValidator) ||
      !Number.isSafeInteger(data.page) || data.page !== requestedPage ||
      !Number.isSafeInteger(data.pageSize) || data.pageSize < 1 ||
      !Number.isSafeInteger(data.totalCount) || data.totalCount < 0) {
    throw new Error("The server returned an incomplete page of records.");
  }
  return data;
}

async function loadSupport(page = 1) {
  if (!AppState.currentUser || !Number.isSafeInteger(page) || page < 1) return;
  const state = AppState.support;
  applySupportControls();

  if (!hasCapability("support.view")) {
    state.items = [];
    state.selectedId = null;
    state.cursorStack = [null];
    state.nextCursor = null;
    state.asOfVersion = null;
    applySupportControls();

    setMessage("support-access-status", "Your session cannot read the ticket list. Any permitted ticket creation is still authorized by the server.");
    return;
  }
  setMessage("support-access-status", "");
  const version = AppState.sessionVersion;
  const request = ++state.request;
  document.getElementById("support-prev").disabled = true;
  document.getElementById("support-next").disabled = true;
  document.getElementById("support-detail-panel").hidden = true;
  tableMessage("support-tbody", 6, "Loading support tickets...");
  setMessage("support-list-status", `Loading page ${page}.`);
  try {
    const query = new URLSearchParams({ pageSize: String(state.pageSize) });
    const cursor = state.cursorStack[page - 1];
    if (typeof cursor === "string") query.set("cursor", cursor);
    if (Number.isSafeInteger(state.asOfVersion)) query.set("asOfVersion", String(state.asOfVersion));
    const data = await readJson(`/api/admin/v1/support/tickets?${query}`, version);
    if (!data || !Array.isArray(data.items) || !data.items.every(item => item && isGuid(item.id) && isGuid(item.shopId) &&
        typeof item.subject === "string" && typeof item.summary === "string" && typeof item.status === "string" &&
        typeof item.updatedAtUtc === "string" && Number.isSafeInteger(item.rowVersion) && item.rowVersion >= 1) ||
        !(data.nextCursor === null || typeof data.nextCursor === "string") ||
        !Number.isSafeInteger(data.asOfVersion) || data.asOfVersion < 0 ||
        !Number.isSafeInteger(data.authorizedCount) || data.authorizedCount < 0 ||
        !Number.isSafeInteger(data.projectionRevision) || data.projectionRevision < 0) {
      throw new Error("The server returned an incomplete support ticket snapshot.");
    }
    if (!requestIsCurrent(version) || request !== state.request) return;
    state.items = data.items;
    state.page = page;
    state.nextCursor = data.nextCursor;
    state.asOfVersion = data.asOfVersion;
    state.authorizedCount = data.authorizedCount;
    const tbody = document.getElementById("support-tbody");
    tbody.replaceChildren();
    if (!data.items.length) tableMessage("support-tbody", 6, "No support tickets returned for this page.");
    data.items.forEach(ticket => {
      const row = node("tr");
      appendCells(row, [ticket.id, ticket.shopId, ticket.subject, ticket.status, formatUtc(ticket.updatedAtUtc)]);
      const actionCell = node("td");
      const open = node("button", "Open Ticket", "btn btn-secondary btn-sm");
      open.type = "button";
      open.addEventListener("click", () => selectSupportTicket(ticket.id));
      actionCell.appendChild(open);
      row.appendChild(actionCell);
      tbody.appendChild(row);
    });
    setMessage("support-list-status", `${data.authorizedCount} tickets in this snapshot; page ${page}.`);
    document.getElementById("support-prev").disabled = page <= 1;
    document.getElementById("support-next").disabled = data.nextCursor === null;
    const selected = data.items.find(ticket => ticket.id === state.selectedId);
    if (selected) renderSupportTicket(selected);
    else state.selectedId = null;
    applySupportControls();

  } catch (error) {
    if (!requestIsCurrent(version) || request !== state.request) return;
    if (String(error?.message || "").includes("PROJECTION_VERSION_CHANGED") && page > 1) {
      state.cursorStack = [null];
      state.nextCursor = null;
      state.asOfVersion = null;
      await loadSupport(1);
      return;
    }
    state.items = [];
    state.selectedId = null;
    tableMessage("support-tbody", 6, "Support tickets unavailable.");
    setMessage("support-list-status", error.message);
    document.getElementById("support-prev").disabled = page <= 1;
    applySupportControls();

  }
}

function selectSupportTicket(ticketId) {
  if (!hasCapability("support.view") || !isGuid(ticketId) || AppState.support.busy) return;
  const ticket = AppState.support.items.find(item => item.id === ticketId);
  if (!ticket) return;
  if (AppState.support.selectedId !== ticketId) {
    document.getElementById("support-reply-form").reset();
    ["support-reply-status", "support-change-status"].forEach(id => setMessage(id, ""));
  }
  AppState.support.selectedId = ticketId;
  renderSupportTicket(ticket);
  applySupportControls();

  document.getElementById("support-detail-title").focus();
}

function renderSupportTicket(ticket) {
  document.getElementById("support-detail-title").textContent = ticket.subject;
  const metadata = document.getElementById("support-ticket-metadata");
  metadata.replaceChildren();
  [["Ticket ID", ticket.id], ["Shop ID", ticket.shopId], ["Status", ticket.status], ["Updated (UTC)", formatUtc(ticket.updatedAtUtc)]]
    .forEach(([label, value]) => metadata.append(node("dt", label, "form-label"), node("dd", value)));
  document.getElementById("support-ticket-summary").textContent = ticket.summary;
  const attachments = document.getElementById("support-ticket-attachments");
  const history = document.getElementById("support-history");
  attachments.replaceChildren();
  history.replaceChildren();
  setMessage("support-detail-status", "");
  try {
    if (typeof ticket.attachmentObjectIdsJson !== "string") throw new Error("Attachment metadata is unavailable.");
    const ids = JSON.parse(ticket.attachmentObjectIdsJson);
    if (!Array.isArray(ids) || !ids.every(isGuid)) throw new Error("Attachment metadata is invalid.");
    if (!ids.length) attachments.appendChild(node("li", "No attachment objects."));
    ids.forEach(id => {
      const item = node("li");
      item.append(node("span", id));
      const download = node("a", "Download encrypted attachment", "button button-secondary");
      download.href = `/api/admin/v1/support/tickets/${encodeURIComponent(ticket.id)}/attachments/${encodeURIComponent(id)}/download`;
      download.setAttribute("download", `${id}.bin`);
      item.append(document.createTextNode(" "), download);
      attachments.appendChild(item);
    });
  } catch (error) {
    attachments.appendChild(node("li", "Attachment metadata could not be read."));
    setMessage("support-detail-status", error.message);
  }
  try {
    if (typeof ticket.repliesJson !== "string") throw new Error("Conversation history is unavailable.");
    const replies = JSON.parse(ticket.repliesJson);
    if (!Array.isArray(replies)) throw new Error("Conversation history is invalid.");
    if (!replies.length) history.appendChild(node("li", "No replies or status changes recorded."));
    replies.forEach(reply => {
      if (!reply || typeof reply !== "object") throw new Error("Conversation history contains an invalid record.");
      // The JSON string is persisted by .NET's default serializer; both field casings
      // are accepted as data, and every value is rendered through textContent.
      const author = reply.author ?? reply.Author;
      const text = reply.text ?? reply.Text;
      const kind = reply.kind ?? reply.Kind;
      const occurred = reply.timestampUtc ?? reply.TimestampUtc;
      if (typeof author !== "string" || typeof text !== "string" || typeof kind !== "string" || typeof occurred !== "string") {
        throw new Error("Conversation history contains an incomplete record.");
      }
      const entry = node("li");
      entry.append(node("div", `${kind === "STATUS_CHANGED" ? "Status change" : "Staff reply"} · ${author} · ${formatUtc(occurred)}`, "form-help"), node("p", text, "preserve-lines"));
      history.appendChild(entry);
    });
  } catch (error) {
    history.replaceChildren(node("li", "Conversation history could not be read."));
    setMessage("support-detail-status", error.message);
  }
  document.getElementById("support-new-status").value = ticket.status;
}

async function supportOperation(scope, payload, adminUserId) {
  const body = JSON.stringify(payload);
  const cached = AppState.support.operations.get(scope);
  if (cached && cached.body === body && cached.adminUserId === adminUserId) return cached;
  if (!globalThis.crypto?.randomUUID || !globalThis.crypto?.subtle) throw new Error("A secure browser connection is required to preserve the request identity.");
  const bytes = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(body));
  const digest = Array.from(new Uint8Array(bytes), value => value.toString(16).padStart(2, "0")).join("");
  // Only a non-authoritative operation UUID and body digest enter session storage.
  // Ticket text, attachments and authentication credentials are never stored here.
  const storageKey = `central_support_operation:${adminUserId}:${scope}:${digest}`;
  let id;
  try {
    id = sessionStorage.getItem(storageKey);
    if (!isGuid(id)) id = crypto.randomUUID();
    sessionStorage.setItem(storageKey, id);
    if (sessionStorage.getItem(storageKey) !== id) throw new Error("Retry identity was not preserved.");
  } catch (_) {
    throw new Error("This browser could not preserve a safe retry identity. No ticket change was submitted. Allow session storage before retrying.");
  }
  const operation = { id, body, storageKey, adminUserId };
  AppState.support.operations.set(scope, operation);
  return operation;
}

async function performSupportCommand(scope, url, payload, capability, statusId, isConfirmed, expectedVersion = 0, operationType = null) {
  if (!AppState.currentUser || AppState.support.busy) return null;
  if (!hasCapability(capability)) {
    setMessage(statusId, "Your session cannot perform this ticket operation.");
    return null;
  }
  const version = AppState.sessionVersion;
  const actor = AppState.currentUser.id;
  AppState.support.busy = true;
  applySupportControls();

  setMessage(statusId, "Saving; waiting for server confirmation...");
  try {
    const operation = operationType
      ? await supportCommandBody(scope, payload, operationType, expectedVersion, actor)
      : await supportOperation(scope, payload, actor);
    if (!requestIsCurrent(version)) return null;
    const response = await apiFetch(url, { method: "POST", headers: { "X-Client-Operation-Id": operation.id }, body: operation.body });
    if (!response.ok) {
      const error = await responseError(response, "The server could not save this ticket operation.");
      if (response.status === 401 && requestIsCurrent(version)) {
        clearIdentity();
        showAuthView("Session expired. Sign in again before retrying.");
      }
      throw error;
    }
    const result = await response.json();
    if (!requestIsCurrent(version)) return null;
    const outcome = result?.outcome ?? result;
    if (outcome?.state !== "SUCCEEDED" || !isConfirmed(result, outcome)) throw new Error("The server did not confirm a persisted successful outcome.");
    try { sessionStorage.removeItem(operation.storageKey); } catch (_) {}
    AppState.support.operations.delete(scope);
    return result;
  } catch (error) {
    if (requestIsCurrent(version)) setMessage(statusId, `Save not confirmed. ${error.message} To retry the same operation, keep the fields unchanged.`);
    return null;
  } finally {
    if (requestIsCurrent(version)) {
      AppState.support.busy = false;
      applySupportControls();

    }
  }
}

async function supportCommandBody(scope, payload, operationType, expectedVersion, actor) {
  const operation = await supportOperation(scope, payload, actor);
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(directoryCanonicalJson(payload)));
  const payloadSha256 = Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, "0")).join("");
  const meta = {
    clientOperationId: operation.id,
    operationType,
    fingerprintVersion: 1,
    payloadSha256,
    expectedVersion,
    policyVersion: 0,
    reason: operationType === "support.create" ? "Support ticket created in admin portal" : "Staff support action recorded in admin portal"
  };
  return { ...operation, body: JSON.stringify({ meta, payload }) };
}

function bindSupportHandlers() {
  document.getElementById("support-refresh").addEventListener("click", () => {
    AppState.support.cursorStack = [null];
    AppState.support.nextCursor = null;
    AppState.support.asOfVersion = null;
    loadSupport(1);
  });
  document.getElementById("support-prev").addEventListener("click", () => {
    if (AppState.support.page > 1) loadSupport(AppState.support.page - 1);
  });
  document.getElementById("support-next").addEventListener("click", () => {
    const state = AppState.support;
    if (state.nextCursor === null) return;
    state.cursorStack[state.page] = state.nextCursor;
    loadSupport(state.page + 1);
  });
  document.getElementById("support-create-form").addEventListener("submit", async event => {
    event.preventDefault();
    const shopId = document.getElementById("support-shop-id").value.trim();
    const subject = document.getElementById("support-subject").value.trim();
    const summary = document.getElementById("support-summary").value.trim();
    const redactionProfileVersion = Number(document.getElementById("support-redaction-version").value);
    const attachmentObjectIds = document.getElementById("support-attachments").value.split(/[\s,]+/).filter(Boolean);
    if (!isGuid(shopId) || !subject || !summary || !Number.isSafeInteger(redactionProfileVersion) || redactionProfileVersion < 1 || !attachmentObjectIds.every(isGuid)) {
      setMessage("support-create-status", "Enter a valid shop ID, subject, summary, positive redaction version and valid attachment IDs.");
      return;
    }
    const payload = { shopId, subject, summary, attachmentObjectIds, redactionProfileVersion };
    const result = await performSupportCommand("create", "/api/admin/v1/support/tickets", payload, "support.create", "support-create-status", (data, outcome) =>
      outcome.operationType === "support.create" && typeof outcome.resultReference === "string" && /^support\/tickets\/[0-9a-f-]{36}$/i.test(outcome.resultReference), 0, "support.create");
    if (!result) return;
    const ticketId = result.resultReference.slice("support/tickets/".length);
    document.getElementById("support-create-form").reset();
    setMessage("support-create-status", `Ticket ${ticketId} was saved by the server.`);
    AppState.support.selectedId = ticketId;
    if (hasCapability("support.view")) {
      AppState.support.cursorStack = [null];
      AppState.support.nextCursor = null;
      AppState.support.asOfVersion = null;
      await loadSupport(1);
    }
  });
  document.getElementById("support-reply-form").addEventListener("submit", async event => {
    event.preventDefault();
    const ticketId = AppState.support.selectedId;
    const replyText = document.getElementById("support-reply-text").value.trim();
    if (!isGuid(ticketId) || !replyText) { setMessage("support-reply-status", "Select a ticket and enter a staff reply."); return; }
    const ticket = AppState.support.items.find(item => item.id === ticketId);
    if (!ticket || !Number.isSafeInteger(ticket.rowVersion) || ticket.rowVersion < 1) { setMessage("support-reply-status", "Reload the selected ticket before replying."); return; }
    const payload = { ticketId, action: "REPLY", replyText };
    const result = await performSupportCommand(`reply:${ticketId}`, `/api/admin/v1/support/tickets/${encodeURIComponent(ticketId)}/commands`, payload, "support.reply", "support-reply-status", (data, outcome) =>
      outcome.operationType === "support.reply" && outcome.resultReference === ticketId, ticket.rowVersion, "support.reply");
    if (!result) return;
    document.getElementById("support-reply-text").value = "";
    setMessage("support-reply-status", "Reply saved in the portal. No external message was sent.");
    AppState.support.cursorStack = [null];
    AppState.support.nextCursor = null;
    AppState.support.asOfVersion = null;
    await loadSupport(1);
  });
  document.getElementById("support-status-form").addEventListener("submit", async event => {
    event.preventDefault();
    const ticketId = AppState.support.selectedId;
    const status = document.getElementById("support-new-status").value;
    if (!isGuid(ticketId) || !["OPEN", "IN_PROGRESS", "WAITING", "CLOSED"].includes(status)) { setMessage("support-change-status", "Select a ticket and a supported status."); return; }
    if (AppState.support.items.find(ticket => ticket.id === ticketId)?.status === status) { setMessage("support-change-status", "The ticket already has this status. No change was submitted."); return; }
    const ticket = AppState.support.items.find(item => item.id === ticketId);
    if (!ticket || !Number.isSafeInteger(ticket.rowVersion) || ticket.rowVersion < 1) { setMessage("support-change-status", "Reload the selected ticket before changing its status."); return; }
    const payload = { ticketId, action: "SET_STATUS", status };
    const result = await performSupportCommand(`status:${ticketId}`, `/api/admin/v1/support/tickets/${encodeURIComponent(ticketId)}/commands`, payload, "support.reply", "support-change-status", (data, outcome) =>
      outcome.operationType === "support.reply" && outcome.resultReference === ticketId, ticket.rowVersion, "support.reply");
    if (!result) return;
    setMessage("support-change-status", "Ticket status and history saved by the server.");
    AppState.support.cursorStack = [null];
    AppState.support.nextCursor = null;
    AppState.support.asOfVersion = null;
    await loadSupport(1);
  });
}

async function loadNotifications(page = 1) {
  if (!AppState.currentUser || !Number.isSafeInteger(page) || page < 1) return;
  if (!hasCapability("notifications.view")) {
    tableMessage("notifications-tbody", 4, "Your session cannot view notifications.");
    setMessage("notifications-status", "Notification access is unavailable for this session.");
    setMessage("notifications-worker-status", "Worker activity is unknown for this session.");
    return;
  }
  const state = AppState.notifications;
  const version = AppState.sessionVersion;
  const request = ++state.request;
  loadNotificationWorkerStatus(version, request);
  document.getElementById("notifications-prev").disabled = true;
  document.getElementById("notifications-next").disabled = true;
  tableMessage("notifications-tbody", 4, "Loading notifications...");
  setMessage("notifications-status", `Loading page ${page}.`);
  try {
    const query = new URLSearchParams({ page: String(page), pageSize: String(state.pageSize) });
    const data = validatePagedResponse(await readJson(`/api/admin/v1/notifications?${query}`, version), page,
      item => item && isGuid(item.id) && typeof item.title === "string" && typeof item.category === "string" &&
        typeof item.targetType === "string" && typeof item.targetId === "string" && typeof item.occurredAtUtc === "string");
    if (!requestIsCurrent(version) || request !== state.request) return;
    const tbody = document.getElementById("notifications-tbody");
    tbody.replaceChildren();
    if (!data.items.length) tableMessage("notifications-tbody", 4, "No notifications returned for this page.");
    data.items.forEach(item => {
      const row = node("tr");
      appendCells(row, [item.title, item.category, `${item.targetType}: ${item.targetId}`, formatUtc(item.occurredAtUtc)]);
      tbody.appendChild(row);
    });
    state.page = data.page;
    state.totalCount = data.totalCount;
    setMessage("notifications-status", `${data.totalCount} notifications; page ${data.page}. Viewing does not change the related record.`);
    document.getElementById("notifications-prev").disabled = data.page <= 1;
    document.getElementById("notifications-next").disabled = data.page * data.pageSize >= data.totalCount;
  } catch (error) {
    if (!requestIsCurrent(version) || request !== state.request) return;
    tableMessage("notifications-tbody", 4, "Notifications unavailable.");
    setMessage("notifications-status", error.message);
    document.getElementById("notifications-prev").disabled = page <= 1;
  }
}

async function loadNotificationWorkerStatus(version, request) {
  setMessage("notifications-worker-status", "Worker activity is unknown while a fresh server observation is requested...");
  try {
    const data = await readJson("/api/admin/v1/notifications/status", version);
    if (!requestIsCurrent(version) || request !== AppState.notifications.request) return;
    if (!data || !["unavailable", "unknown", "degraded", "healthy"].includes(data.status) ||
        typeof data.observedAtUtc !== "string" || Number.isNaN(Date.parse(data.observedAtUtc))) {
      throw new Error("The server did not return a valid worker observation.");
    }
    const observed = formatUtc(data.observedAtUtc);
    const lastSuccess = typeof data.lastSucceededAtUtc === "string" && !Number.isNaN(Date.parse(data.lastSucceededAtUtc))
      ? formatUtc(data.lastSucceededAtUtc) : null;
    if (data.status === "healthy" && !lastSuccess) throw new Error("No successful worker cycle was supplied with the observation.");
    const observation = data.status === "healthy" ? "The worker reported healthy activity" : data.status === "unavailable"
      ? "Worker activity was unavailable" : data.status === "degraded"
      ? "The worker reported delivery failures requiring review" : "Worker activity was unknown";
    setMessage("notifications-worker-status", `${observation} at ${observed}. ${lastSuccess ? `Last successful cycle: ${lastSuccess}.` : "No successful cycle was reported."} Refresh to obtain a new observation.`);
  } catch (_) {
    if (!requestIsCurrent(version) || request !== AppState.notifications.request) return;
    setMessage("notifications-worker-status", "Worker activity is unknown because a fresh server observation could not be obtained. Refresh to retry.");
  }
}

function bindNotificationHandlers() {
  document.getElementById("notifications-refresh").addEventListener("click", () => loadNotifications(AppState.notifications.page));
  document.getElementById("recovery-refresh").addEventListener("click", loadRecovery);
  document.getElementById("notifications-prev").addEventListener("click", () => { if (AppState.notifications.page > 1) loadNotifications(AppState.notifications.page - 1); });
  document.getElementById("notifications-next").addEventListener("click", () => loadNotifications(AppState.notifications.page + 1));
}

async function loadRecovery() {
  const version = AppState.sessionVersion;
  const request = ++AppState.recovery.request;
  setMessage("recovery-status", "Recovery and restore attestation are unavailable in this portal. A worker observation does not prove a backup, restore, host identity, or database target.");
  setMessage("recovery-worker-status", "Worker health is unknown while a fresh server observation is requested...");
  if (!AppState.currentUser) {
    setMessage("recovery-worker-status", "Worker health is unknown until an authorized admin session is available.");
    return;
  }
  if (!hasCapability("notifications.view")) {
    setMessage("recovery-worker-status", "Worker health is unknown for this session; notifications.view is required for the existing live worker-status endpoint.");
    return;
  }
  try {
    const data = await readJson("/api/admin/v1/notifications/status", version);
    if (!requestIsCurrent(version) || request !== AppState.recovery.request) return;
    if (!data || !["unavailable", "unknown", "degraded", "healthy"].includes(data.status) ||
        typeof data.observedAtUtc !== "string" || Number.isNaN(Date.parse(data.observedAtUtc))) {
      throw new Error("The server did not return a valid worker observation.");
    }
    const lastSuccess = typeof data.lastSucceededAtUtc === "string" && !Number.isNaN(Date.parse(data.lastSucceededAtUtc))
      ? formatUtc(data.lastSucceededAtUtc) : null;
    if (data.status === "healthy" && !lastSuccess) throw new Error("No successful worker cycle was supplied with the observation.");
    const labels = { healthy: "Healthy", degraded: "Degraded", unknown: "Unknown", unavailable: "Unavailable" };
    setMessage("recovery-worker-status", `Outbox worker: ${labels[data.status]}. Observed ${formatUtc(data.observedAtUtc)}. ${lastSuccess ? `Last successful cycle: ${lastSuccess}.` : "No successful cycle was reported."}`);
  } catch (_) {
    if (!requestIsCurrent(version) || request !== AppState.recovery.request) return;
    setMessage("recovery-worker-status", "Worker health is unknown because a fresh server observation could not be obtained.");
  }
}

// No view is authenticated until the server confirms its session context.
document.addEventListener("DOMContentLoaded", () => {
  initTheme();
  bindNavigation();
  bindAuthHandlers();
  bindSupportHandlers();
  bindNotificationHandlers();
  document.getElementById("pii-unmask-form").addEventListener("submit", submitPiiUnmask);
  document.getElementById("btn-close-pii-modal").addEventListener("click", clearSensitiveDisclosure);
  document.getElementById("pii-unmask-modal").addEventListener("click", event => {
    if (event.target.id === "pii-unmask-modal") clearSensitiveDisclosure();
  });
  document.addEventListener("keydown", event => {
    if (event.key === "Escape" && !document.getElementById("pii-unmask-modal").hidden) clearSensitiveDisclosure();
  });
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) clearSensitiveDisclosure();
  });
  window.addEventListener("pagehide", clearSensitiveDisclosure);
  checkCurrentSession();
});

async function searchDirectory(append) {
  if (!hasCapability("search.masked")) return;
  const state = AppState.directory;
  const request = ++state.request;
  const session = AppState.sessionVersion;
  const next = document.getElementById("directory-next");
  next.disabled = true;
  if (!append) document.getElementById("directory-results").replaceChildren();
  setMessage("directory-status", "Searching shops...");
  try {
    const query = new URLSearchParams({ filter: state.filter, pageSize: "20" });
    if (append && state.cursor) {
      query.set("cursor", state.cursor);
      query.set("asOfVersion", String(state.version));
    }
    const data = await readJson(`/api/admin/v1/search?${query}`, session);
    if (!requestIsCurrent(session) || request !== state.request) return;
    if (!data || !Array.isArray(data.items) || !Number.isSafeInteger(data.authorizedCount) || data.authorizedCount < 0 ||
        !Number.isSafeInteger(data.asOfVersion) || (data.nextCursor !== null && !/^\d+$/.test(data.nextCursor)) ||
        !data.items.every(item => item && isGuid(item.shopId) && typeof item.shopName === "string" && typeof item.customerDisplayName === "string")) {
      throw new Error("The server returned an invalid shop search response.");
    }
    for (const item of data.items) {
      const card = node("div", undefined, "card card-body");
      card.append(node("strong", item.shopName), node("p", `${item.shopCode} · ${item.customerDisplayName} · ${item.status}`));
      if (hasCapability("dashboard.view")) {
        const open = node("button", "Open Customer 360", "btn btn-secondary btn-sm");
        open.type = "button";
        open.addEventListener("click", () => {
          AppState.activeShopId = item.shopId;
          document.getElementById("customer-search-input").value = item.shopId;
          fetchCustomer360Data(item.shopId);
        });
        card.append(open);
      }
      document.getElementById("directory-results").append(card);
    }
    state.cursor = data.nextCursor;
    state.version = data.asOfVersion;
    next.disabled = !state.cursor;
    setMessage("directory-status", data.authorizedCount ? `${data.authorizedCount} matching shops. Customer names are masked.` : "No matching shops.");
  } catch (error) {
    if (!requestIsCurrent(session) || request !== state.request) return;
    state.cursor = null;
    setMessage("directory-status", `Search could not be completed. ${error.message} Search again to refresh the results.`);
  }
}

function applyDirectoryControls() {
  for (const [kind, capability] of [["customer", "customer.create"], ["shop", "shop.register"]]) {
    document.getElementById(`${kind}-create-card`).hidden = !hasCapability(capability);
    document.querySelectorAll(`#${kind}-create-form input, #${kind}-create-form button`).forEach(element => {
      element.disabled = AppState.directoryMutationBusy || !hasCapability(capability);
    });
  }
}

// Version 1 matches the existing System.Text.Json canonical writer, including
// its Basic Latin encoder. No Unicode normalization or whitespace trimming is
// applied to payload values after the user confirms them.
function directoryCanonicalJson(payload) {
  const encode = value => '"' + value.split("").map(character => {
    const escapes = { "\b": "\\b", "\t": "\\t", "\n": "\\n", "\f": "\\f", "\r": "\\r", "\\": "\\\\" };
    if (Object.hasOwn(escapes, character)) return escapes[character];
    const code = character.charCodeAt(0);
    if (code < 32 || code > 126 || "\"<>&'+`".includes(character)) return `\\u${code.toString(16).toUpperCase().padStart(4, "0")}`;
    return character;
  }).join("") + '"';
  return "{" + Object.keys(payload).sort().map(key => `${encode(key)}:${typeof payload[key] === "string" ? encode(payload[key]) : JSON.stringify(payload[key])}`).join(",") + "}";
}
async function createDirectoryRecord(shop) {
  const kind = shop ? "shop" : "customer";
  const operationType = shop ? "shop.register" : "customer.create";
  const statusId = `${kind}-create-status`;
  if (!AppState.currentUser || AppState.directoryMutationBusy || !hasCapability(operationType)) return;
  const session = AppState.sessionVersion;
  const actor = AppState.currentUser.id;
  const name = document.getElementById(shop ? "shop-display-name" : "customer-display-name").value;
  const reason = document.getElementById(`${kind}-create-reason`).value;
  const contactReference = document.getElementById(`${kind}-contact-reference`).value;
  const payload = shop ? { customerId: document.getElementById("shop-customer-id").value.trim(), shopName: name, installationCapacity: 1 } : { displayName: name };
  if (!name.trim() || !reason.trim() || shop && !isGuid(payload.customerId)) {
    setMessage(statusId, "Enter a name, reason and valid customer ID where required.");
    return;
  }
  if (contactReference) payload.contactReference = contactReference;
  AppState.directoryMutationBusy = true;
  applyDirectoryControls();
  setMessage(statusId, "Saving; waiting for server confirmation...");
  try {
    const identity = await supportOperation(`directory:${operationType}`, { payload, reason }, actor);
    const canonical = directoryCanonicalJson(payload);
    const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(canonical));
    const payloadSha256 = Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, "0")).join("");
    if (!requestIsCurrent(session)) return;
    const meta = { clientOperationId: identity.id, operationType, fingerprintVersion: 1, payloadSha256, expectedVersion: 0, policyVersion: 0, reason };
    const response = await apiFetch(`/api/admin/v1/${shop ? "shops" : "customers"}`, { method: "POST", body: JSON.stringify({ meta, payload }) });
    if (!response.ok) {
      if (response.status === 401 && requestIsCurrent(session)) {
        clearIdentity();
        showAuthView("Session expired. Sign in again before retrying.");
      }
      throw await responseError(response, "The server did not confirm this creation.");
    }
    const outcome = await response.json();
    if (!requestIsCurrent(session)) return;
    const prefix = shop ? "shops/" : "customers/";
    if (outcome.clientOperationId !== identity.id || outcome.operationType !== operationType || outcome.payloadSha256 !== payloadSha256 ||
        outcome.state !== "SUCCEEDED" || typeof outcome.resultReference !== "string" ||
        !outcome.resultReference.startsWith(prefix) || !isGuid(outcome.resultReference.slice(prefix.length))) {
      throw new Error("The response did not confirm the requested operation. Keep the fields unchanged and retry.");
    }
    const id = outcome.resultReference.slice(prefix.length);
    setMessage(statusId, `${shop ? "Shop registered" : "Customer created"}. ID: ${id}`);
    if (!shop) document.getElementById("shop-customer-id").value = id;
    else {
      AppState.activeShopId = id;
      document.getElementById("customer-search-input").value = id;
    }
  } catch (error) {
    if (requestIsCurrent(session)) setMessage(statusId, `Creation not confirmed. ${error.message} Keep the same fields to retry this operation.`);
  } finally {
    if (requestIsCurrent(session)) {
      AppState.directoryMutationBusy = false;
      applyDirectoryControls();
    }
  }
}
