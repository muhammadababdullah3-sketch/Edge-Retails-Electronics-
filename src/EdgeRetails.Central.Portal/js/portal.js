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
  lifecycle: { request: 0, busy: false, licenseId: null, shopId: null, details: null, preview: null, stage: null, opener: null },
  directory: { request: 0, filter: "", cursor: null, version: null },
  selectedQueueItem: null,
  queues: {
    dashboard: { type: "PAYMENT_REVIEW", page: 1, pageSize: 20, cursorStack: [null], nextCursor: null, asOfVersion: null, authorizedCount: 0, request: 0 },
    approvals: { type: "DUAL_APPROVAL", page: 1, pageSize: 20, cursorStack: [null], nextCursor: null, asOfVersion: null, authorizedCount: 0, request: 0 },
    licensing: { type: "ACTIVATION_REVIEW", page: 1, pageSize: 20, cursorStack: [null], nextCursor: null, asOfVersion: null, authorizedCount: 0, request: 0 }
  },
  audit: { page: 1, pageSize: 25, cursorStack: [null], nextCursor: null, asOfVersion: null, request: 0 },
  backups: { page: 1, pageSize: 25, cursorStack: [null], nextCursor: null, asOfVersion: null, authorizedCount: null, projectionRevision: null, request: 0, busy: false },
  pii: { auditRecordId: null, resource: null, resourceId: null, request: 0, busy: false, opener: null },
  support: { page: 1, pageSize: 20, cursorStack: [null], nextCursor: null, asOfVersion: null, authorizedCount: 0, request: 0, items: [], selectedId: null, busy: false, operations: new Map() },
  commercial: { request: 0, plans: [], busy: false },
  notifications: { page: 1, pageSize: 20, totalCount: 0, request: 0 },
  recovery: { request: 0 },
  sessions: { page: 1, pageSize: 25, cursorStack: [null], nextCursor: null, asOfVersion: null, authorizedCount: 0, request: 0, confirmTarget: null, busy: false }
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
  ACTIVATION_REVIEW: "Activation reviews",
  PAYMENT_REVIEW: "Payment reviews",
  HARDWARE_REVIEW: "Hardware reviews",
  PRIMARY_TRANSFER: "Primary transfers",
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
  recovery: "Recovery & Attestation",
  sessions: "Administrator Sessions"
};

// Visibility improves navigation only. The server remains the authority for every request.
const VIEW_CAPABILITIES = {
  dashboard: ["dashboard.view", "queues.view"],
  customer360: ["dashboard.view", "search.masked", "customer.create", "shop.register"],
  commercial: ["licensing.read", "licensing.manage", "license.view", "customer.create", "shop.register", "price.snapshot", "order.create", "payment.review", "license.issue", "license.renew", "license.extend", "addon.renew", "evidence.upload.init", "evidence.upload.finalize"],
  licensing: ["license.view", "activation.review", "hardware.review", "primary.transfer"],
  approvals: ["queues.view"],
  backups: ["backup.download"],
  support: ["support.view", "support.create", "support.reply"],
  notifications: ["notifications.view"],
  audit: ["audit.view"],
  recovery: ["security.manage"],
  sessions: ["sessions.view"]
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
  applyCommercialControls();
  applySessionControls();
  document.getElementById("directory-card").hidden = !hasCapability("search.masked");
}

function applySessionControls() {
  const canRevoke = hasCapability("sessions.revoke");
  document.querySelectorAll("[data-session-revoke]").forEach(button => {
    button.disabled = !canRevoke || AppState.sessions.busy;
  });
  document.getElementById("sessions-refresh").disabled = !hasCapability("sessions.view") || AppState.sessions.busy;
  document.getElementById("sessions-filter").disabled = !hasCapability("sessions.view") || AppState.sessions.busy;
  document.querySelector("#sessions-filter-form button[type=submit]").disabled = !hasCapability("sessions.view") || AppState.sessions.busy;
  document.getElementById("sessions-prev").disabled = !hasCapability("sessions.view") || AppState.sessions.busy || AppState.sessions.page <= 1;
  document.getElementById("sessions-next").disabled = !hasCapability("sessions.view") || AppState.sessions.busy || !AppState.sessions.nextCursor;
  document.getElementById("sessions-revoke-confirm").disabled = !canRevoke || AppState.sessions.busy || !AppState.sessions.confirmTarget;
}

function clearSessionsData(message = "Sign in to load administrator sessions.") {
  const state = AppState.sessions;
  state.request += 1;
  state.page = 1;
  state.cursorStack = [null];
  state.nextCursor = null;
  state.asOfVersion = null;
  state.authorizedCount = 0;
  state.confirmTarget = null;
  state.busy = false;
  const dialog = document.getElementById("sessions-revoke-dialog");
  if (dialog.open) dialog.close();
  document.getElementById("sessions-filter").value = "";
  tableMessage("sessions-table-tbody", 8, message);
  setMessage("sessions-status", "");
  setMessage("sessions-page-status", "");
  setMessage("sessions-revoke-status", "");
  setMessage("sessions-revoke-target", "");
  applySessionControls();
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
  const error = new Error(`${statuses[response.status] || fallback} (HTTP ${response.status}${code ? `; ${code}` : ""})`);
  error.code = code;
  error.status = response.status;
  return error;
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
  const lifecycleDialog = document.getElementById("license-lifecycle-dialog");
  if (lifecycleDialog?.open) lifecycleDialog.close();
  AppState.lifecycle = { request: AppState.lifecycle.request + 1, busy: false, licenseId: null, shopId: null, details: null, preview: null, stage: null, opener: null };
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
  clearBackupsData();
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
  AppState.commercial.request += 1;
  AppState.commercial.plans = [];
  AppState.commercial.busy = false;
  AppState.notifications.page = 1;
  document.getElementById("support-create-form").reset();
  document.getElementById("support-reply-form").reset();
  document.getElementById("support-status-form").reset();
  document.getElementById("commercial-order-form").reset();
  renderCommercialPlans();
  setMessage("commercial-order-status", "");
  setMessage("commercial-catalog-status", "");
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
  clearSessionsData();
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
  clearSessionsData("Session context is being revalidated.");
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
      // Idle expiry is sliding on the server. Revalidate at the last known
      // expiry instead of leaving cached authenticated data visible or
      // logging out a session whose activity extended the server-side lease.
      void checkCurrentSession();
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
  document.getElementById("backups-refresh").addEventListener("click", () => loadBackups(1, true));
  document.getElementById("backups-prev").addEventListener("click", () => {
    if (AppState.backups.page > 1) loadBackups(AppState.backups.page - 1);
  });
  document.getElementById("backups-next").addEventListener("click", () => {
    if (AppState.backups.nextCursor) loadBackups(AppState.backups.page + 1);
  });
  document.getElementById("btn-close-queue-modal").addEventListener("click", closeQueueModal);
  bindLicenseLifecycleHandlers();
  document.getElementById("activation-review-form").addEventListener("submit", submitActivationReview);
  document.getElementById("sensitive-approval-form").addEventListener("submit", submitSensitiveApprovalDecision);
  document.getElementById("payment-review-form").addEventListener("submit", submitPaymentReview);
  document.getElementById("licensing-activation-queue").addEventListener("click", () => loadLicensingQueue("ACTIVATION_REVIEW"));
  document.getElementById("licensing-hardware-queue").addEventListener("click", () => loadLicensingQueue("HARDWARE_REVIEW"));
  document.getElementById("licensing-transfer-queue").addEventListener("click", () => loadLicensingQueue("PRIMARY_TRANSFER"));
  document.getElementById("licensing-queue-refresh").addEventListener("click", () => loadLicensingQueue(AppState.queues.licensing.type));
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
    support: () => loadSupport(AppState.support.page), notifications: () => loadNotifications(AppState.notifications.page),
    sessions: () => loadSessions(AppState.sessions.page)
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
  const columnCount = scope === "licensing" ? 6 : 5;
  if (resetSnapshot || state.type !== queueType) {
    state.cursorStack = [null];
    state.nextCursor = null;
    state.asOfVersion = null;
  }
  state.type = queueType;
  state.page = page;
  const request = ++state.request;
  const version = AppState.sessionVersion;
  const tbodyId = scope === "dashboard" ? "dashboard-queue-tbody" : scope === "licensing" ? "licensing-activations-tbody" : "approvals-table-tbody";
  const previous = document.getElementById(`${scope}-queue-prev`);
  const next = document.getElementById(`${scope}-queue-next`);
  previous.disabled = true;
  next.disabled = true;
  tableMessage(tbodyId, columnCount, `Loading ${QUEUE_LABELS[queueType]}...`);
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
    if (!data || !Array.isArray(data.items) || !data.items.every(item => item && isGuid(item.entityId) && item.queueType === queueType &&
        (queueType !== "PAYMENT_REVIEW" || !hasCapability("payment.review") || isGuid(item.orderId) && Number.isSafeInteger(item.resourceVersion) && item.resourceVersion > 0) &&
        (queueType !== "ACTIVATION_REVIEW" || Number.isSafeInteger(item.resourceVersion) && item.resourceVersion > 0 &&
          (item.validationSnapshotVersion === null || Number.isSafeInteger(item.validationSnapshotVersion) && item.validationSnapshotVersion > 0) && isGuid(item.shopId))) ||
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
    if (!items.length) tableMessage(tbodyId, columnCount, `No ${QUEUE_LABELS[queueType]} items returned for this page.`);
    items.forEach(item => {
      const row = node("tr");
      if (scope === "licensing") {
        appendCells(row, [item.entityId, textValue(item.title), isGuid(item.shopId) ? item.shopId : "Not supplied", textValue(item.status), formatUtc(item.createdAtUtc)]);
      } else {
        appendCells(row, [item.entityId, textValue(item.title), isGuid(item.shopId) ? item.shopId : "Not supplied", textValue(item.status)]);
      }
      const cell = node("td");
      const activationReviewable = queueType === "ACTIVATION_REVIEW" && hasCapability("activation.review") && Number.isSafeInteger(item.validationSnapshotVersion) && item.validationSnapshotVersion > 0;
      const approvalReviewable = queueType === "DUAL_APPROVAL" && hasCapability("sensitive.approve") && isGuid(item.approvalRequesterAdminId) &&
        typeof item.approvalTargetActionType === "string" && item.approvalTargetActionType.length > 0 && isGuid(item.approvalTargetId) &&
        typeof item.approvalTargetPayloadSha256 === "string" && /^[a-f0-9]{64}$/i.test(item.approvalTargetPayloadSha256) &&
        typeof item.approvalExpiresAtUtc === "string" && Number.isSafeInteger(item.resourceVersion) && item.resourceVersion > 0;
      const paymentReviewable = queueType === "PAYMENT_REVIEW" && hasCapability("payment.review") && isGuid(item.orderId) && Number.isSafeInteger(item.resourceVersion) && item.resourceVersion > 0;
      const inspect = node("button", paymentReviewable ? "Review payment" : activationReviewable ? "Review activation" : approvalReviewable ? "Review approval" : "Inspect (read only)", "btn btn-secondary btn-sm");
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
    tableMessage(tbodyId, columnCount, "Queue unavailable; no review or decision has been performed.");
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
  const activationReview = item.queueType === "ACTIVATION_REVIEW";
  const hardwareReview = item.queueType === "HARDWARE_REVIEW";
  const activationReviewable = activationReview && hasCapability("activation.review") && Number.isSafeInteger(item.validationSnapshotVersion) && item.validationSnapshotVersion > 0;
  const paymentReviewable = item.queueType === "PAYMENT_REVIEW" && hasCapability("payment.review") && isGuid(item.orderId) && Number.isSafeInteger(item.resourceVersion) && item.resourceVersion > 0;
  const approvalReviewable = item.queueType === "DUAL_APPROVAL" && hasCapability("sensitive.approve") && isGuid(item.approvalRequesterAdminId) &&
    typeof item.approvalTargetActionType === "string" && item.approvalTargetActionType.length > 0 && isGuid(item.approvalTargetId) &&
    typeof item.approvalTargetPayloadSha256 === "string" && /^[a-f0-9]{64}$/i.test(item.approvalTargetPayloadSha256) &&
    typeof item.approvalExpiresAtUtc === "string" && Number.isSafeInteger(item.resourceVersion) && item.resourceVersion > 0;
  document.getElementById("queue-inspect-title").textContent = paymentReviewable ? "Payment Evidence Review" : activationReview ? "Activation Review" : hardwareReview ? "Hardware Change Review (Read Only)" : approvalReviewable ? "Independent Approval Review" : "Queue Item (Read Only)";
  document.getElementById("activation-review-controls").hidden = !activationReviewable;
  document.getElementById("sensitive-approval-controls").hidden = !approvalReviewable;
  document.getElementById("payment-review-controls").hidden = !paymentReviewable;
  document.getElementById("activation-review-version").textContent = activationReview
    ? `Persisted request version ${item.resourceVersion}; validation snapshot ${item.validationSnapshotVersion}. Central rechecks both before saving.`
    : "";
  document.getElementById("activation-review-form").reset();
  document.getElementById("sensitive-approval-form").reset();
  document.getElementById("payment-review-form").reset();
  setMessage("activation-review-status", "");
  setMessage("sensitive-approval-status", "");
  setMessage("payment-review-status", "");
  document.getElementById("sensitive-approval-details").textContent = approvalReviewable
    ? `Request from ${item.approvalRequesterAdminId}. Requester reason: ${textValue(item.reasonOrSummary)}. Expires ${formatUtc(item.approvalExpiresAtUtc)}. Persisted version ${item.resourceVersion}.`
    : "";
  document.getElementById("payment-review-details").textContent = paymentReviewable
    ? `Order ${item.orderId}; proof ${item.entityId}; persisted proof revision ${item.resourceVersion}. Central rechecks the order, shop and revision before saving.`
    : "";
  document.getElementById("queue-item-workflow").textContent = paymentReviewable
    ? "Choose a decision and reason code. Central records the review, order effect, audit and outbox outcome together."
    : hardwareReview
    ? hasCapability("hardware.review")
      ? "This persisted hardware case can be inspected, but no decision is submitted. The contract requires POST /api/admin/v1/hardware-reviews/{id}/decide with versioned HardwareCommand evidence; that route is not implemented by a controller, and the legacy decide endpoint accepts a different boolean-only DTO."
      : "Your session can inspect this queue item but lacks hardware.review. No decision control is available."
    : activationReview
    ? activationReviewable
      ? "Choose a decision and reason code. Central will verify the persisted activation snapshot and request version before recording it."
      : hasCapability("activation.review")
        ? "This request has no current validation snapshot. Refresh eligibility before deciding; no decision control is available."
        : "Your session can inspect this queue item but cannot decide it."
    : approvalReviewable
      ? `Review ${item.approvalTargetActionType} for target ${item.approvalTargetId}. Central rechecks the saved requester, digest, expiry and request version. Approval records independent authorization; it does not execute the business action.`
    : item.queueType === "DUAL_APPROVAL" && hasCapability("sensitive.approve")
      ? "Central did not provide a complete persisted approval snapshot. Refresh the queue before deciding; no action is available."
    : item.queueType === "SUPPORT_INBOX"
      ? "Open Support Tickets to view the persisted conversation or save a permitted staff reply. Inspecting this item makes no change."
    : item.queueType === "PRIMARY_TRANSFER"
      ? "This persisted transfer can be inspected, but no decision is submitted. The canonical primary.transfer decision route is not implemented yet; approval and completion remain separate persisted actions."
      : item.queueType === "HARDWARE_REVIEW"
        ? "Hardware review decisions are unavailable until the canonical versioned decision route and persisted case version/evidence are exposed."
        : `The ${QUEUE_LABELS[item.queueType]} decision workflow is unavailable in this view. No mutation has been submitted.`;
  document.getElementById("btn-queue-shop").disabled = !isGuid(item.shopId);
  document.getElementById("queue-inspect-modal").classList.add("active");
  document.getElementById("btn-close-queue-modal").focus();
}

async function submitPaymentReview(event) {
  event.preventDefault();
  const item = AppState.selectedQueueItem;
  if (!AppState.currentUser || !item || item.queueType !== "PAYMENT_REVIEW" || !hasCapability("payment.review")) {
    setMessage("payment-review-status", "Your session cannot decide this payment review.");
    return;
  }
  if (!isGuid(item.orderId) || !Number.isSafeInteger(item.resourceVersion) || item.resourceVersion < 1) {
    setMessage("payment-review-status", "This queue item has no valid persisted order or proof revision. Reload the queue before deciding.");
    return;
  }
  const decision = document.getElementById("payment-review-decision").value;
  const reasonCode = document.getElementById("payment-review-reason").value.trim();
  const internalNote = document.getElementById("payment-review-note").value.trim();
  if (!["APPROVE", "REJECT", "CLARIFICATION"].includes(decision) || !reasonCode || reasonCode.length > 120 || internalNote.length > 1000) {
    setMessage("payment-review-status", "Choose a supported decision, enter a reason code of at most 120 characters, and keep the optional note within 1000 characters.");
    return;
  }
  const payload = { orderId: item.orderId, proofId: item.entityId, decision, reasonCode };
  if (internalNote) payload.internalNote = internalNote;
  const reason = `Payment evidence review: ${decision} (${reasonCode})`;
  const operationScope = `payment-review:${item.entityId}`;
  const session = AppState.sessionVersion;
  const submit = document.getElementById("payment-review-submit");
  submit.disabled = true;
  setMessage("payment-review-status", "Saving review; waiting for Central's persisted outcome...");
  try {
    const operation = await supportOperation(operationScope, { payload, reason }, AppState.currentUser.id);
    const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(directoryCanonicalJson(payload)));
    const payloadSha256 = Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, "0")).join("");
    if (!requestIsCurrent(session)) return;
    const meta = { clientOperationId: operation.id, operationType: "payment.review", fingerprintVersion: 1,
      payloadSha256, expectedVersion: item.resourceVersion, policyVersion: 0, reason };
    const response = await apiFetch(`/api/admin/v1/payments/${encodeURIComponent(item.entityId)}/review`, {
      method: "POST", body: JSON.stringify({ meta, payload })
    });
    if (!response.ok) {
      if (response.status === 401 && requestIsCurrent(session)) {
        clearIdentity();
        showAuthView("Session expired. Sign in again before deciding.");
      }
      throw await responseError(response, "Central could not confirm this payment review.");
    }
    const outcome = await response.json();
    if (!requestIsCurrent(session) || outcome?.clientOperationId !== operation.id || outcome.operationType !== "payment.review" ||
        outcome.payloadSha256 !== payloadSha256 || outcome.state !== "SUCCEEDED" || typeof outcome.resultReference !== "string" ||
        !outcome.resultReference.startsWith("payment_reviews/") || !isGuid(outcome.resultReference.slice("payment_reviews/".length)) ||
        !Number.isSafeInteger(outcome.resourceVersion) || outcome.resourceVersion <= item.resourceVersion) {
      throw new Error("Central did not return the persisted payment review outcome expected for this request.");
    }
    try { sessionStorage.removeItem(operation.storageKey); } catch (_) {}
    AppState.support.operations.delete(operationScope);
    closeQueueModal();
    const state = AppState.queues.dashboard;
    state.cursorStack = [null];
    state.nextCursor = null;
    state.asOfVersion = null;
    await loadQueue("PAYMENT_REVIEW", 1, "dashboard", true);
    setMessage("dashboard-queue-status", `Central recorded ${decision.toLowerCase()} for payment proof ${item.entityId}.`);
  } catch (error) {
    if (requestIsCurrent(session)) setMessage("payment-review-status", `Decision not confirmed. ${error.message} Keep the same decision and reason to retry the same operation.`);
  } finally {
    if (requestIsCurrent(session)) submit.disabled = !hasCapability("payment.review");
  }
}

function closeQueueModal() {
  document.getElementById("queue-inspect-modal").classList.remove("active");
  document.getElementById("activation-review-controls").hidden = true;
  document.getElementById("sensitive-approval-controls").hidden = true;
  document.getElementById("payment-review-controls").hidden = true;
  document.getElementById("queue-inspect-title").textContent = "Queue Item (Read Only)";
  AppState.selectedQueueItem = null;
}

async function submitSensitiveApprovalDecision(event) {
  event.preventDefault();
  const item = AppState.selectedQueueItem;
  if (!AppState.currentUser || !item || item.queueType !== "DUAL_APPROVAL" || !hasCapability("sensitive.approve")) {
    setMessage("sensitive-approval-status", "Your session cannot decide this approval request.");
    return;
  }
  if (!isGuid(item.approvalRequesterAdminId) || !isGuid(item.approvalTargetId) || !isGuid(AppState.currentUser.id) ||
      item.approvalRequesterAdminId === AppState.currentUser.id || typeof item.approvalTargetActionType !== "string" ||
      !item.approvalTargetActionType || typeof item.approvalTargetPayloadSha256 !== "string" ||
      !/^[a-f0-9]{64}$/i.test(item.approvalTargetPayloadSha256) || typeof item.approvalExpiresAtUtc !== "string" ||
      !Number.isSafeInteger(item.resourceVersion) || item.resourceVersion < 1) {
    setMessage("sensitive-approval-status", "The persisted approval snapshot is incomplete or this session is the requester. Reload the queue or use a different administrator.");
    return;
  }
  const decision = document.getElementById("sensitive-approval-decision").value;
  const reason = document.getElementById("sensitive-approval-reason").value.trim();
  if (!["APPROVE", "REJECT"].includes(decision) || !reason || reason.length > 500) {
    setMessage("sensitive-approval-status", "Choose approve or reject and enter a decision reason of at most 500 characters.");
    return;
  }

  const sessionVersion = AppState.sessionVersion;
  const payload = {
    targetActionType: item.approvalTargetActionType,
    targetId: item.approvalTargetId,
    targetPayloadSha256: item.approvalTargetPayloadSha256,
    requesterAdminId: item.approvalRequesterAdminId,
    approverAdminId: AppState.currentUser.id,
    expiresAtUtc: item.approvalExpiresAtUtc,
    decision
  };
  const operationScope = `sensitive-approval:${item.entityId}:${decision}`;
  const submit = document.getElementById("sensitive-approval-submit");
  submit.disabled = true;
  setMessage("sensitive-approval-status", "Saving independent decision; waiting for Central's persisted outcome...");
  try {
    const operation = await supportOperation(operationScope, { payload, reason }, AppState.currentUser.id);
    const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(directoryCanonicalJson(payload)));
    const payloadSha256 = Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, "0")).join("");
    if (!requestIsCurrent(sessionVersion)) return;
    const meta = { clientOperationId: operation.id, operationType: "sensitive.approve", fingerprintVersion: 1,
      payloadSha256, expectedVersion: item.resourceVersion, policyVersion: 0, reason, sensitiveApprovalId: item.entityId };
    const response = await apiFetch(`/api/admin/v1/sensitive-approvals/${encodeURIComponent(item.entityId)}/decide`, {
      method: "POST", body: JSON.stringify({ meta, payload })
    });
    if (!response.ok) {
      if (response.status === 401 && requestIsCurrent(sessionVersion)) {
        clearIdentity();
        showAuthView("Session expired. Sign in again before deciding.");
      }
      throw await responseError(response, "Central could not confirm this approval decision.");
    }
    const outcome = await response.json();
    if (!requestIsCurrent(sessionVersion) || outcome?.clientOperationId !== operation.id || outcome.operationType !== "sensitive.approve" ||
        outcome.payloadSha256 !== payloadSha256 || outcome.state !== "SUCCEEDED" || outcome.resultReference !== `sensitive-approvals/${item.entityId}` ||
        !Number.isSafeInteger(outcome.resourceVersion) || outcome.resourceVersion <= item.resourceVersion) {
      throw new Error("Central did not return the persisted approval outcome expected for this request.");
    }
    try { sessionStorage.removeItem(operation.storageKey); } catch (_) {}
    AppState.support.operations.delete(operationScope);
    setMessage("sensitive-approval-status", `Central recorded ${decision.toLowerCase()} for approval ${item.entityId}. The approved action has not been executed.`);
    closeQueueModal();
    const state = AppState.queues.approvals;
    state.cursorStack = [null];
    state.nextCursor = null;
    state.asOfVersion = null;
    await loadQueue("DUAL_APPROVAL", 1, "approvals", true);
    setMessage("approvals-queue-status", `Central saved ${decision.toLowerCase()} for approval ${item.entityId}. The approved action has not been executed.`);
  } catch (error) {
    if (requestIsCurrent(sessionVersion)) setMessage("sensitive-approval-status", `Decision not confirmed. ${error.message} Keep the decision and reason unchanged to retry the same operation.`);
  } finally {
    if (requestIsCurrent(sessionVersion)) submit.disabled = !hasCapability("sensitive.approve");
  }
}

async function submitActivationReview(event) {
  event.preventDefault();
  const item = AppState.selectedQueueItem;
  if (!AppState.currentUser || !item || item.queueType !== "ACTIVATION_REVIEW" || !hasCapability("activation.review")) {
    setMessage("activation-review-status", "Your session cannot decide this activation review.");
    return;
  }
  if (!Number.isSafeInteger(item.resourceVersion) || item.resourceVersion < 1 ||
      !Number.isSafeInteger(item.validationSnapshotVersion) || item.validationSnapshotVersion < 1) {
    setMessage("activation-review-status", "This queue item has no valid persisted review versions. Reload the queue before deciding.");
    return;
  }
  const decision = document.getElementById("activation-review-decision").value;
  const reasonCode = document.getElementById("activation-review-reason").value.trim();
  if (!["APPROVE", "REJECT", "REQUEST_INFORMATION", "OPEN_HARDWARE_REVIEW"].includes(decision) || !reasonCode || reasonCode.length > 120) {
    setMessage("activation-review-status", "Choose a supported decision and enter a reason code of at most 120 characters.");
    return;
  }
  const payload = { activationRequestId: item.entityId, decision, reasonCode, validationSnapshotVersion: item.validationSnapshotVersion };
  const reason = `Activation review: ${decision} (${reasonCode})`;
  const operationScope = `activation-review:${item.entityId}`;
  const session = AppState.sessionVersion;
  const submit = document.getElementById("activation-review-submit");
  submit.disabled = true;
  setMessage("activation-review-status", "Saving decision; waiting for Central's persisted outcome...");
  try {
    const operation = await supportOperation(operationScope, { payload, reason }, AppState.currentUser.id);
    const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(directoryCanonicalJson(payload)));
    const payloadSha256 = Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, "0")).join("");
    const meta = { clientOperationId: operation.id, operationType: "activation.review", fingerprintVersion: 1,
      payloadSha256, expectedVersion: item.resourceVersion, policyVersion: 0, reason };
    const response = await apiFetch(`/api/admin/v1/activations/${encodeURIComponent(item.entityId)}/review`, {
      method: "POST", body: JSON.stringify({ meta, payload })
    });
    if (!response.ok) {
      if (response.status === 401 && requestIsCurrent(session)) {
        clearIdentity();
        showAuthView("Session expired. Sign in again before retrying.");
      }
      throw await responseError(response, "Central could not confirm this activation decision.");
    }
    const outcome = await response.json();
    if (!requestIsCurrent(session) || outcome?.clientOperationId !== operation.id || outcome.operationType !== "activation.review" ||
        outcome.payloadSha256 !== payloadSha256 || outcome.state !== "SUCCEEDED" || outcome.resultReference !== `activation_requests/${item.entityId}` ||
        !Number.isSafeInteger(outcome.resourceVersion) || outcome.resourceVersion <= item.resourceVersion) {
      throw new Error("Central did not return the persisted activation decision outcome expected for this request.");
    }
    try { sessionStorage.removeItem(operation.storageKey); } catch (_) {}
    AppState.support.operations.delete(operationScope);
    setMessage("activation-review-status", `Central saved ${decision} for activation request ${item.entityId}.`);
    closeQueueModal();
    const state = AppState.queues.licensing;
    state.cursorStack = [null];
    state.nextCursor = null;
    state.asOfVersion = null;
    await loadQueue("ACTIVATION_REVIEW", 1, "licensing", true);
  } catch (error) {
    if (requestIsCurrent(session)) setMessage("activation-review-status", `Decision not confirmed. ${error.message} Keep the same decision and reason to retry the same operation.`);
  } finally {
    if (requestIsCurrent(session)) submit.disabled = !hasCapability("activation.review");
  }
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

function renderRecentOrders(orders, customer360) {
  const card = detailCard("Recent Orders");
  card.body.style.display = "block";
  const wrapper = node("div", undefined, "table-responsive");
  const table = node("table", undefined, "data-table");
  const head = node("thead");
  const heading = node("tr");
  ["Order ID", "Purchase Target", "Term", "Status", "Total", "Created (UTC)", "License Action"].forEach(label => heading.appendChild(node("th", label)));
  head.appendChild(heading);
  const body = node("tbody");
  if (!orders.length) {
    const row = node("tr");
    const cell = node("td", "No recent orders returned for this shop.");
    cell.colSpan = 7;
    row.appendChild(cell);
    body.appendChild(row);
  }
  orders.forEach(order => {
    const row = node("tr");
    [textValue(order?.id), textValue(order?.purchaseTarget), textValue(order?.termKind), textValue(order?.status),
      Number.isFinite(order?.totalAmount) ? `${order.totalAmount.toLocaleString()} ${textValue(order?.currency)}` : "Unavailable",
      formatUtc(order?.createdAtUtc)].forEach(value => row.appendChild(node("td", value)));
    const actionCell = node("td");
    if (order?.issuedLicenseId && isGuid(order.issuedLicenseId)) {
      actionCell.appendChild(node("span", `Issued: ${order.issuedLicenseId}`));
    } else if (order?.purchaseTarget !== "BASE_LICENSE") {
      const license = customer360?.commercialLicense;
      const entitlement = customer360?.addons?.find(item => item?.kind === order?.purchaseTarget);
      const targetMatches = (!order?.baseLicenseId || order.baseLicenseId.toLowerCase() === license?.id?.toLowerCase()) &&
        (!order?.existingEntitlementId || order.existingEntitlementId.toLowerCase() === entitlement?.id?.toLowerCase());
      if (order?.status !== "PAID_APPROVED") {
        actionCell.appendChild(node("span", "Manual payment approval is required before add-on renewal."));
      } else if (!hasCapability("addon.renew")) {
        actionCell.appendChild(node("span", "Your session lacks addon.renew permission."));
      } else if (!isGuid(order?.id) || !Number.isSafeInteger(order?.rowVersion) || order.rowVersion < 1 ||
          !isGuid(license?.id) || license.status !== "ACTIVE" || !targetMatches ||
          !["SUPPORT", "PREMIUM_UPGRADE"].includes(order?.purchaseTarget) ||
          (entitlement && (!isGuid(entitlement.id) || !Number.isSafeInteger(entitlement.rowVersion) || entitlement.rowVersion < 1))) {
        actionCell.appendChild(node("span", "This paid order is not bound to the current license and entitlement."));
      } else {
        const open = node("button", "Review add-on renewal", "btn btn-secondary btn-sm");
        open.type = "button";
        open.dataset.addonRenewOrderId = order.id;
        const form = document.createElement("form");
        form.className = "card-body";
        form.hidden = true;
        form.setAttribute("aria-label", `Manual add-on renewal for order ${order.id}`);
        form.dataset.addonRenewOrderId = order.id;
        const explanation = node("p", "Payment was manually approved. Enter the expiry specified by the approved order and provide an audit reason; this action does not approve payment.", "form-help");
        const expiryLabel = node("label", "New add-on expiry (local time)", "form-label");
        const expiry = node("input", undefined, "form-input");
        expiry.type = "datetime-local";
        expiry.required = true;
        expiry.dataset.addonRenewExpiry = "true";
        expiryLabel.appendChild(expiry);
        const reasonLabel = node("label", "Manual renewal reason", "form-label");
        const reason = node("textarea", undefined, "form-input");
        reason.required = true;
        reason.maxLength = 500;
        reason.rows = 2;
        reason.dataset.addonRenewReason = "true";
        reasonLabel.appendChild(reason);
        const status = node("p", "", "workflow-status");
        status.setAttribute("role", "status");
        status.setAttribute("aria-live", "polite");
        const submit = node("button", "Renew add-on manually", "btn btn-primary");
        submit.type = "submit";
        submit.dataset.addonRenewSubmit = order.id;
        open.addEventListener("click", () => {
          open.hidden = true;
          form.hidden = false;
          expiry.focus();
        });
        form.addEventListener("submit", event => {
          event.preventDefault();
          submitManualAddonRenewal(form, order, license, entitlement, status);
        });
        form.append(explanation, expiryLabel, reasonLabel, status, submit);
        actionCell.append(open, form);
      }
    } else if (order?.status !== "PAID_APPROVED") {
      actionCell.appendChild(node("span", "Manual payment approval is required first."));
    } else if (!hasCapability("license.issue")) {
      actionCell.appendChild(node("span", "Your session lacks license.issue permission."));
    } else if (!isGuid(order.id) || !Number.isSafeInteger(order.rowVersion) || order.rowVersion < 1 ||
        !["LIFETIME", "FIXED_TERM"].includes(order.termKind)) {
      actionCell.appendChild(node("span", "Order data is incomplete; reload before issuing."));
    } else {
      const open = node("button", "Review license issue", "btn btn-secondary btn-sm");
      open.type = "button";
      open.dataset.licenseIssueOrderId = order.id;
      const form = document.createElement("form");
      form.className = "card-body";
      form.hidden = true;
      form.setAttribute("aria-label", `Manual license issue for order ${order.id}`);
      const explanation = node("p", "Payment was manually approved. This separate action will issue one license; review the dates and enter a reason.", "form-help");
      const validFromLabel = node("label", "Valid from (local time)", "form-label");
      const validFrom = node("input", undefined, "form-input");
      validFrom.type = "datetime-local";
      validFrom.required = true;
      validFrom.dataset.licenseValidFrom = "true";
      validFromLabel.appendChild(validFrom);
      form.append(explanation, validFromLabel);
      if (order.termKind === "FIXED_TERM") {
        const validUntilLabel = node("label", "Valid until (local time)", "form-label");
        const validUntil = node("input", undefined, "form-input");
        validUntil.type = "datetime-local";
        validUntil.required = true;
        validUntil.dataset.licenseValidUntil = "true";
        validUntilLabel.appendChild(validUntil);
        form.appendChild(validUntilLabel);
      } else {
        form.appendChild(node("p", "Lifetime term has no expiry."));
      }
      const reasonLabel = node("label", "Manual issuance reason", "form-label");
      const reason = node("textarea", undefined, "form-input");
      reason.required = true;
      reason.maxLength = 500;
      reason.rows = 2;
      reason.dataset.licenseIssueReason = "true";
      reasonLabel.appendChild(reason);
      const status = node("p", "", "workflow-status");
      status.setAttribute("role", "status");
      status.setAttribute("aria-live", "polite");
      const submit = node("button", "Issue license manually", "btn btn-primary");
      submit.type = "submit";
      submit.dataset.licenseIssueSubmit = order.id;
      form.append(reasonLabel, submit, status);
      open.addEventListener("click", () => {
        open.hidden = true;
        form.hidden = false;
        validFrom.focus();
      });
      form.addEventListener("submit", event => {
        event.preventDefault();
        submitManualLicenseIssue(form, order, status);
      });
      actionCell.append(open, form);
    }
    row.appendChild(actionCell);
    body.appendChild(row);
  });
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

  const commercial = detailCard("Commercial Base Licenses");
  const legacyLicense = data.commercialLicense;
  const licenses = Array.isArray(data.commercialLicenses)
    ? data.commercialLicenses
    : legacyLicense && typeof legacyLicense === "object" && !Array.isArray(legacyLicense) ? [legacyLicense] : [];
  if (licenses.length > 0) {
    licenses.forEach(license => {
      const licenseCard = node("section", undefined, "detail-card");
      licenseCard.dataset.licenseId = textValue(license.id);
      detailField(licenseCard, "License ID", textValue(license.id));
      detailField(licenseCard, "Term Kind", textValue(license.termKind));
      detailField(licenseCard, "Status", textValue(license.status));
      const lifetimeWithoutExpiry = license.termKind === "LIFETIME" && license.validUntilUtc === null;
      detailField(licenseCard, "Valid Until (UTC)", lifetimeWithoutExpiry ? "No expiry (lifetime base)" : formatUtc(license.validUntilUtc));
      detailField(licenseCard, "Authority Revision", Number.isSafeInteger(license.authorityRevision) ? String(license.authorityRevision) : "Unavailable");
      if (license.status === "ACTIVE" && hasCapability("license.view") && hasCapability("license.revoke") &&
          hasCapability("sensitive.approve") && hasCapability("auth.stepup")) {
        const review = node("button", "Review revocation impact", "btn btn-secondary");
        review.type = "button";
        review.addEventListener("click", () => openLicenseRevocation(review, license.id, data.shopId));
        licenseCard.appendChild(review);
      }
      if (license.termKind === "LIFETIME" && license.validUntilUtc !== null) {
        licenseCard.appendChild(node("p", "Unexpected lifetime expiry value; review the server record."));
      }
      commercial.body.appendChild(licenseCard);
    });
  } else {
    commercial.body.appendChild(node("p", legacyLicense === null ? "No active commercial base license returned." : "Commercial license data unavailable in this response."));
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

  const orders = renderRecentOrders(data.recentOrders, data);

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

  const unavailable = node("div", "Hardware and primary-transfer decisions remain unavailable until their source-defined evidence and outcomes are persisted.", "card card-body");
  container.replaceChildren(identity.card, commercial.card, addons, installations.card, orders, tickets, policy.card, unavailable);
}

function lifecycleStageKey(adminId, licenseId) {
  return `central_license_lifecycle:${adminId}:${licenseId}`;
}

function readLicenseLifecycleStage(adminId, licenseId, shopId) {
  try {
    const raw = sessionStorage.getItem(lifecycleStageKey(adminId, licenseId));
    if (!raw) return null;
    const value = JSON.parse(raw);
    if (!value || value.licenseId?.toLowerCase() !== licenseId.toLowerCase() || value.shopId?.toLowerCase() !== shopId.toLowerCase() ||
        !isGuid(value.capabilityPolicyId) || !Number.isSafeInteger(value.resourceVersion) || value.resourceVersion < 1 ||
        !Number.isSafeInteger(value.policyVersion) || value.policyVersion < 1 || !Number.isSafeInteger(value.authorityRevision) ||
        !Number.isFinite(Date.parse(value.effectiveAtUtc)) || typeof value.payloadSha256 !== "string" || !/^[a-f0-9]{64}$/i.test(value.payloadSha256) ||
        value.requestGrantId !== null && !isGuid(value.requestGrantId) ||
        value.requestGrantExpiresAtUtc !== null && !Number.isFinite(Date.parse(value.requestGrantExpiresAtUtc)) ||
        value.approvalRequestId !== null && !isGuid(value.approvalRequestId) ||
        value.approvalExpiresAtUtc !== null && !Number.isFinite(Date.parse(value.approvalExpiresAtUtc))) return null;
    return value;
  } catch (_) {
    return null;
  }
}

function saveLicenseLifecycleStage(stage) {
  try {
    const key = lifecycleStageKey(AppState.currentUser.id, stage.licenseId);
    sessionStorage.setItem(key, JSON.stringify(stage));
    if (sessionStorage.getItem(key) !== JSON.stringify(stage)) throw new Error("Retry context could not be verified.");
  } catch (_) {
    throw new Error("This browser could not preserve the non-secret approval reference. No business action was submitted.");
  }
}

function lifecycleHasCapabilities() {
  return ["license.view", "license.revoke", "sensitive.approve", "auth.stepup"].every(hasCapability);
}

function setLifecycleBusy(busy) {
  AppState.lifecycle.busy = busy;
  ["license-lifecycle-close", "license-lifecycle-request-approval", "license-lifecycle-execute", "license-lifecycle-preview"].forEach(id => {
    const control = document.getElementById(id);
    if (control) control.disabled = busy;
  });
}

async function readPersistedLifecycleLicense(licenseId, shopId, version) {
  const response = await apiFetch(`/api/admin/v1/licenses/${encodeURIComponent(licenseId)}`);
  if (!response.ok) {
    if (response.status === 401 && requestIsCurrent(version)) {
      clearIdentity();
      showAuthView("Session expired. Sign in again.");
    }
    throw await responseError(response, "Central could not read the persisted license and policy.");
  }
  const page = await response.json();
  if (!requestIsCurrent(version) || !page || page.authorizedCount !== 1 || !Array.isArray(page.items) || page.items.length !== 1 ||
      page.nextCursor !== null || !Number.isSafeInteger(page.asOfVersion)) throw new Error("Central returned an invalid single-license page.");
  const item = page.items[0];
  if (!isGuid(item?.id) || item.id.toLowerCase() !== licenseId.toLowerCase() || !isGuid(item.shopId) || item.shopId.toLowerCase() !== shopId.toLowerCase() ||
      !Number.isSafeInteger(item.resourceVersion) || item.resourceVersion !== page.asOfVersion || !Number.isSafeInteger(item.authorityRevision) ||
      !isGuid(item.capabilityPolicyId) || !Number.isSafeInteger(item.policyVersion) || item.policyVersion < 1 || typeof item.status !== "string") {
    throw new Error("Central did not return a matching persisted license, shop and current policy. Lifecycle actions remain unavailable.");
  }
  return item;
}

async function previewLicenseRevocation(details, version) {
  const proposed = {
    shopId: details.shopId,
    targetEntityId: details.id,
    expectedVersion: details.resourceVersion,
    authorityRevision: details.authorityRevision,
    policyVersion: details.policyVersion
  };
  const response = await apiFetch("/api/admin/v1/action-preview", {
    method: "POST",
    body: JSON.stringify({ actionType: "REVOKE_LICENSE", targetEntityId: details.id, shopId: details.shopId,
      proposedParametersJson: directoryCanonicalJson(proposed) })
  });
  if (!response.ok) throw await responseError(response, "Central could not preview this license revocation.");
  const preview = await response.json();
  if (!requestIsCurrent(version) || preview?.actionType !== "REVOKE_LICENSE" || preview.isDestructive !== true ||
      preview.requiresStepUp !== true || preview.requiresDualApproval !== true || !Number.isSafeInteger(preview.affectedInstallationsCount) ||
      typeof preview.consequenceSummary !== "string" || !preview.consequenceSummary.trim() || !Array.isArray(preview.warnings) ||
      !preview.warnings.every(item => typeof item === "string")) throw new Error("Central returned an incomplete revocation preview.");
  return preview;
}

async function openLicenseRevocation(opener, licenseId, shopId) {
  if (!AppState.currentUser || !lifecycleHasCapabilities() || !isGuid(licenseId) || !isGuid(shopId)) return;
  const state = AppState.lifecycle;
  state.request += 1;
  const request = state.request;
  const version = AppState.sessionVersion;
  state.licenseId = licenseId;
  state.shopId = shopId;
  state.opener = opener;
  state.busy = false;
  state.details = null;
  state.preview = null;
  state.stage = readLicenseLifecycleStage(AppState.currentUser.id, licenseId, shopId);
  let expiredStepUp = false;
  const savedGrantExpiry = Date.parse(state.stage?.requestGrantExpiresAtUtc ?? "");
  if (state.stage?.requestGrantId && (!Number.isFinite(savedGrantExpiry) || savedGrantExpiry <= Date.now())) {
    state.stage.requestGrantId = null;
    state.stage.requestGrantExpiresAtUtc = null;
    saveLicenseLifecycleStage(state.stage);
    expiredStepUp = true;
  }
  const savedApprovalExpiry = Date.parse(state.stage?.approvalExpiresAtUtc ?? "");
  const expiredApproval = Boolean(state.stage?.approvalRequestId) &&
    (!Number.isFinite(savedApprovalExpiry) || savedApprovalExpiry <= Date.now());
  if (expiredApproval) {
    try { sessionStorage.removeItem(lifecycleStageKey(AppState.currentUser.id, licenseId)); } catch (_) {}
    state.stage = null;
  }
  const dialog = document.getElementById("license-lifecycle-dialog");
  document.getElementById("license-lifecycle-target").textContent = `License ${licenseId} · Shop ${shopId}`;
  document.getElementById("license-lifecycle-preview-result").hidden = true;
  document.getElementById("license-lifecycle-request-section").hidden = true;
  document.getElementById("license-lifecycle-final-section").hidden = true;
  document.getElementById("license-lifecycle-reason").value = "";
  document.getElementById("license-lifecycle-request-totp").value = "";
  document.getElementById("license-lifecycle-final-reason").value = "";
  document.getElementById("license-lifecycle-final-totp").value = "";
  document.getElementById("license-lifecycle-approval-id").value = "";
  ["license-lifecycle-preview-status", "license-lifecycle-request-status", "license-lifecycle-final-status"].forEach(id => setMessage(id, ""));
  dialog.showModal();
  document.getElementById("license-lifecycle-close").focus();
  setMessage("license-lifecycle-preview-status", "Reading the persisted license and asking Central for a current consequence preview...");
  setLifecycleBusy(true);
  try {
    const details = await readPersistedLifecycleLicense(licenseId, shopId, version);
    if (!requestIsCurrent(version) || request !== state.request) return;
    if (details.status !== "ACTIVE") throw new Error("Only an active persisted license can be previewed for revocation.");
    if (state.stage && (state.stage.resourceVersion !== details.resourceVersion || state.stage.authorityRevision !== details.authorityRevision ||
        state.stage.policyVersion !== details.policyVersion || state.stage.capabilityPolicyId.toLowerCase() !== details.capabilityPolicyId.toLowerCase())) {
      throw new Error("The persisted license or policy changed after the approval request. Start a new preview and request a new approval.");
    }
    state.details = details;
    const preview = await previewLicenseRevocation(details, version);
    if (!requestIsCurrent(version) || request !== state.request) return;
    state.preview = preview;
    document.getElementById("license-lifecycle-consequence").textContent = preview.consequenceSummary;
    const warnings = document.getElementById("license-lifecycle-warnings");
    warnings.replaceChildren(...preview.warnings.map(value => node("li", value)));
    document.getElementById("license-lifecycle-policy").textContent = `Current license version ${details.resourceVersion}; authority revision ${details.authorityRevision}; policy version ${details.policyVersion}.`;
    document.getElementById("license-lifecycle-preview-result").hidden = false;
    setMessage("license-lifecycle-preview-status", "Central validated the current persisted target and policy. This preview has not changed the license.");
    if (expiredApproval) {
      setMessage("license-lifecycle-request-status", "The previous approval expired. Enter a reason and fresh MFA code to create a new independent approval request.");
      document.getElementById("license-lifecycle-request-section").hidden = false;
    } else if (state.stage?.approvalRequestId) {
      document.getElementById("license-lifecycle-final-section").hidden = false;
      document.getElementById("license-lifecycle-approval-state").textContent = `Request ${state.stage.approvalRequestId} is saved. A different administrator must approve it in Dual-Control Approvals before execution.`;
      document.getElementById("license-lifecycle-approval-id").value = state.stage.approvalRequestId;
    } else {
      document.getElementById("license-lifecycle-request-section").hidden = false;
      if (expiredStepUp) setMessage("license-lifecycle-request-status", "The previous step-up grant expired. Enter fresh MFA to create the independent approval request.");
      else if (state.stage?.requestGrantId) setMessage("license-lifecycle-request-status", "A step-up grant was issued for this exact command. Retry the same approval request or start a new preview.");
    }
  } catch (error) {
    if (requestIsCurrent(version) && request === state.request) setMessage("license-lifecycle-preview-status", `No action is available. ${error.message}`);
  } finally {
    if (requestIsCurrent(version) && request === state.request) setLifecycleBusy(false);
  }
}

function buildLifecyclePayload(details) {
  return { licenseId: details.id, effectiveAtUtc: new Date(Date.now() - 1000).toISOString(), capabilityPolicyId: details.capabilityPolicyId };
}

async function requestLicenseRevocationApproval() {
  const state = AppState.lifecycle;
  if (!AppState.currentUser || !lifecycleHasCapabilities() || state.busy || !state.details || !state.preview || !state.preview.requiresDualApproval) return;
  const reason = document.getElementById("license-lifecycle-reason").value.trim();
  const proofInput = document.getElementById("license-lifecycle-request-totp");
  const proof = proofInput.value.trim();
  if (!reason || reason.length > 500 || !/^[0-9]{6}$/.test(proof)) {
    setMessage("license-lifecycle-request-status", "Enter a reason and the current six-digit MFA code.");
    return;
  }
  const version = AppState.sessionVersion;
  setLifecycleBusy(true);
  setMessage("license-lifecycle-request-status", "Verifying fresh MFA and saving the independent approval request...");
  try {
    if (!state.stage?.requestGrantId) {
      const payload = buildLifecyclePayload(state.details);
      const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(directoryCanonicalJson(payload)));
      const payloadSha256 = Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, "0")).join("");
      const stepUpResponse = await apiFetch("/api/admin/v1/auth/step-up", { method: "POST", body: JSON.stringify({
        sessionId: EMPTY_GUID, actionScope: "license.revoke", targetPayloadSha256: payloadSha256,
        proof, challengeId: crypto.randomUUID()
      }) });
      if (!stepUpResponse.ok) throw await responseError(stepUpResponse, "Central could not confirm fresh MFA.");
      const grant = await stepUpResponse.json();
      if (!requestIsCurrent(version) || grant?.actionScope !== "license.revoke" || grant.targetPayloadSha256 !== payloadSha256 || !isGuid(grant.stepUpGrantId) ||
          typeof grant.expiresAtUtc !== "string" || !Number.isFinite(Date.parse(grant.expiresAtUtc)) || Date.parse(grant.expiresAtUtc) <= Date.now()) {
        throw new Error("Central returned a step-up grant for a different action or payload.");
      }
      state.stage = { licenseId: state.licenseId, shopId: state.shopId, capabilityPolicyId: state.details.capabilityPolicyId,
        resourceVersion: state.details.resourceVersion, authorityRevision: state.details.authorityRevision,
        policyVersion: state.details.policyVersion, effectiveAtUtc: payload.effectiveAtUtc, payloadSha256,
        requestGrantId: grant.stepUpGrantId, requestGrantExpiresAtUtc: grant.expiresAtUtc, approvalRequestId: null, approvalExpiresAtUtc: null };
      saveLicenseLifecycleStage(state.stage);
    }
    const requestBody = { operationType: "LICENSE_REVOKE", targetEntityId: state.licenseId,
      targetPayloadSha256: state.stage.payloadSha256, actionScope: "license.revoke", stepUpGrantId: state.stage.requestGrantId, reason };
    const response = await apiFetch("/api/lifecycle/v1/sensitive-approvals", { method: "POST", body: JSON.stringify(requestBody) });
    if (!response.ok) {
      if (response.status === 401 && requestIsCurrent(version)) { clearIdentity(); showAuthView("Session expired. Sign in again."); }
      throw await responseError(response, "Central could not confirm the saved approval request.");
    }
    const approval = await response.json();
    if (!requestIsCurrent(version) || !isGuid(approval?.id) || approval.targetEntityId?.toLowerCase() !== state.licenseId.toLowerCase() ||
        approval.targetPayloadSha256 !== state.stage.payloadSha256 || approval.operationType !== "LICENSE_REVOKE" ||
        approval.actionScope !== "license.revoke" || approval.status !== "PENDING" || approval.requesterAdminId?.toLowerCase() !== AppState.currentUser.id.toLowerCase() ||
        typeof approval.expiresAtUtc !== "string" || !Number.isFinite(Date.parse(approval.expiresAtUtc))) {
      throw new Error("Central did not return the persisted pending approval for this exact command.");
    }
    state.stage.approvalRequestId = approval.id;
    state.stage.approvalExpiresAtUtc = approval.expiresAtUtc;
    saveLicenseLifecycleStage(state.stage);
    proofInput.value = "";
    document.getElementById("license-lifecycle-request-section").hidden = true;
    document.getElementById("license-lifecycle-final-section").hidden = false;
    document.getElementById("license-lifecycle-approval-id").value = approval.id;
    document.getElementById("license-lifecycle-approval-state").textContent = `Pending independent approval ${approval.id}. The license has not been changed.`;
    setMessage("license-lifecycle-request-status", "");
  } catch (error) {
    if (error.code === "STEP_UP_REQUIRED" && state.stage?.requestGrantId) {
      state.stage.requestGrantId = null;
      state.stage.requestGrantExpiresAtUtc = null;
      try { saveLicenseLifecycleStage(state.stage); } catch (_) {}
      setMessage("license-lifecycle-request-status", "That step-up grant is no longer valid for this session. Enter fresh MFA and retry the approval request.");
      return;
    }
    if (requestIsCurrent(version)) setMessage("license-lifecycle-request-status", `Approval request not confirmed. ${error.message} Retry with the same target and reason.`);
  } finally {
    if (requestIsCurrent(version)) setLifecycleBusy(false);
  }
}

async function executeApprovedLicenseRevocation() {
  const state = AppState.lifecycle;
  if (!AppState.currentUser || !lifecycleHasCapabilities() || state.busy || !state.details || !state.preview || !state.stage?.approvalRequestId) return;
  const approvalId = document.getElementById("license-lifecycle-approval-id").value.trim();
  const reason = document.getElementById("license-lifecycle-final-reason").value.trim();
  const proofInput = document.getElementById("license-lifecycle-final-totp");
  const proof = proofInput.value.trim();
  if (approvalId.toLowerCase() !== state.stage.approvalRequestId.toLowerCase() || !reason || reason.length > 500 || !/^[0-9]{6}$/.test(proof)) {
    setMessage("license-lifecycle-final-status", "Use the persisted approval ID, enter a reason, and provide a fresh six-digit MFA code.");
    return;
  }
  if (!state.stage.approvalExpiresAtUtc || !Number.isFinite(Date.parse(state.stage.approvalExpiresAtUtc)) ||
      Date.parse(state.stage.approvalExpiresAtUtc) <= Date.now()) {
    setMessage("license-lifecycle-final-status", "This approval is expired or its expiry is unavailable. Refresh the server preview and request a new independent approval.");
    return;
  }
  const version = AppState.sessionVersion;
  setLifecycleBusy(true);
  setMessage("license-lifecycle-final-status", "Rechecking current policy and approval before the final server command...");
  try {
    const details = await readPersistedLifecycleLicense(state.licenseId, state.shopId, version);
    if (details.status !== "ACTIVE" || details.resourceVersion !== state.stage.resourceVersion || details.authorityRevision !== state.stage.authorityRevision ||
        details.policyVersion !== state.stage.policyVersion || details.capabilityPolicyId.toLowerCase() !== state.stage.capabilityPolicyId.toLowerCase()) {
      throw new Error("The license or policy changed after preview. No mutation was submitted; refresh and create a new approval request.");
    }
    const preview = await previewLicenseRevocation(details, version);
    if (!preview.requiresStepUp || !preview.requiresDualApproval) throw new Error("The server's current policy no longer matches this sensitive-action request.");
    const payload = { licenseId: state.stage.licenseId, effectiveAtUtc: state.stage.effectiveAtUtc, capabilityPolicyId: state.stage.capabilityPolicyId };
    const payloadSha256 = Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(directoryCanonicalJson(payload)))), value => value.toString(16).padStart(2, "0")).join("");
    if (payloadSha256 !== state.stage.payloadSha256) throw new Error("The saved lifecycle command digest no longer matches its persisted approval.");
    const stepUpResponse = await apiFetch("/api/admin/v1/auth/step-up", { method: "POST", body: JSON.stringify({
      sessionId: EMPTY_GUID, actionScope: "license.revoke", targetPayloadSha256: payloadSha256, proof, challengeId: crypto.randomUUID()
    }) });
    if (!stepUpResponse.ok) throw await responseError(stepUpResponse, "Central could not confirm fresh MFA for execution.");
    const grant = await stepUpResponse.json();
    if (!requestIsCurrent(version) || grant?.actionScope !== "license.revoke" || grant.targetPayloadSha256 !== payloadSha256 || !isGuid(grant.stepUpGrantId)) {
      throw new Error("Central returned an execution grant for a different action or payload.");
    }
    const commandIdentity = await supportOperation(`license-revoke:${state.licenseId}:${state.stage.approvalRequestId}`, { payload, approvalId, reason }, AppState.currentUser.id);
    const meta = { clientOperationId: commandIdentity.id, operationType: "license.revoke", fingerprintVersion: 1,
      payloadSha256, expectedVersion: details.resourceVersion, policyVersion: details.policyVersion, reason, sensitiveApprovalId: approvalId };
    const response = await apiFetch(`/api/admin/v1/licenses/${encodeURIComponent(state.licenseId)}/revoke`, {
      method: "POST", body: JSON.stringify({ meta, payload })
    });
    if (!response.ok) {
      if (response.status === 401 && requestIsCurrent(version)) { clearIdentity(); showAuthView("Session expired. Sign in again."); }
      throw await responseError(response, "Central did not confirm the license revocation.");
    }
    const outcome = await response.json();
    if (!requestIsCurrent(version) || outcome?.clientOperationId !== commandIdentity.id || outcome.operationType !== "license.revoke" ||
        outcome.payloadSha256 !== payloadSha256 || outcome.state !== "SUCCEEDED" || outcome.resultReference !== `licenses/${state.licenseId}` ||
        !Number.isSafeInteger(outcome.resourceVersion) || outcome.resourceVersion <= details.resourceVersion) {
      throw new Error("Central did not return the persisted revocation outcome expected for this command.");
    }
    try { sessionStorage.removeItem(state.stage ? lifecycleStageKey(AppState.currentUser.id, state.licenseId) : ""); } catch (_) {}
    try { sessionStorage.removeItem(commandIdentity.storageKey); } catch (_) {}
    AppState.support.operations.delete(`license-revoke:${state.licenseId}:${state.stage.approvalRequestId}`);
    proofInput.value = "";
    setMessage("license-lifecycle-final-status", `Central confirmed revocation of license ${state.licenseId}. Authority revision ${outcome.authorityRevision}.`);
    state.stage = null;
    await fetchCustomer360Data(state.shopId);
  } catch (error) {
    if (requestIsCurrent(version)) setMessage("license-lifecycle-final-status", `Execution not confirmed. ${error.message} Keep the approval, payload and reason unchanged to retry.`);
  } finally {
    if (requestIsCurrent(version)) setLifecycleBusy(false);
  }
}

function bindLicenseLifecycleHandlers() {
  const dialog = document.getElementById("license-lifecycle-dialog");
  document.getElementById("license-lifecycle-close").addEventListener("click", () => dialog.close());
  document.getElementById("license-lifecycle-preview").addEventListener("click", () => {
    const state = AppState.lifecycle;
    if (state.licenseId && state.shopId && !state.busy) void openLicenseRevocation(state.opener, state.licenseId, state.shopId);
  });
  document.getElementById("license-lifecycle-request-approval").addEventListener("click", requestLicenseRevocationApproval);
  document.getElementById("license-lifecycle-execute").addEventListener("click", executeApprovedLicenseRevocation);
  dialog.addEventListener("close", () => {
    const opener = AppState.lifecycle.opener;
    if (opener?.isConnected && AppState.currentUser) opener.focus();
    AppState.lifecycle.request += 1;
    AppState.lifecycle.busy = false;
  });
}

async function submitManualLicenseIssue(form, order, status) {
  if (!AppState.currentUser || AppState.commercial.busy) return;
  if (!hasCapability("license.issue")) {
    status.textContent = "Your session cannot issue licenses.";
    return;
  }
  const validFromInput = form.querySelector("[data-license-valid-from]");
  const validUntilInput = form.querySelector("[data-license-valid-until]");
  const reason = form.querySelector("[data-license-issue-reason]").value.trim();
  const validFrom = new Date(validFromInput.value);
  const validUntil = validUntilInput ? new Date(validUntilInput.value) : null;
  if (!form.reportValidity() || !Number.isFinite(validFrom.getTime()) ||
      (order.termKind === "FIXED_TERM" && (!validUntil || !Number.isFinite(validUntil.getTime()) || validUntil <= validFrom)) ||
      (order.termKind === "LIFETIME" && validUntilInput) || !reason || reason.length > 500) {
    status.textContent = "Enter valid dates for the approved term and a reason before issuing.";
    return;
  }
  const payload = {
    approvedOrderId: order.id,
    validFromUtc: validFrom.toISOString().replace(/\.000Z$/, "+00:00"),
    validUntilUtc: order.termKind === "FIXED_TERM" ? validUntil.toISOString().replace(/\.000Z$/, "+00:00") : null
  };
  const version = AppState.sessionVersion;
  const actor = AppState.currentUser.id;
  const shopId = AppState.activeShopId;
  const scope = `commercial:license.issue:${order.id}`;
  AppState.commercial.busy = true;
  form.querySelectorAll("input, textarea, button").forEach(control => { control.disabled = true; });
  status.textContent = "Waiting for your separate license issue command to be recorded by Central...";
  try {
    const identity = await supportOperation(scope, { payload, reason }, actor);
    const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(directoryCanonicalJson(payload)));
    const payloadSha256 = Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, "0")).join("");
    const commandMeta = {
      clientOperationId: identity.id,
      operationType: "license.issue",
      fingerprintVersion: 1,
      payloadSha256,
      expectedVersion: order.rowVersion,
      policyVersion: 0,
      reason
    };
    const response = await apiFetch("/api/admin/v1/licenses/issue", {
      method: "POST",
      body: JSON.stringify({ ...payload, commandMeta })
    });
    if (!response.ok) {
      const error = await responseError(response, "Central did not confirm license issuance.");
      if (response.status === 401 && requestIsCurrent(version)) {
        clearIdentity();
        showAuthView("Session expired. Sign in again before issuing a license.");
      }
      throw error;
    }
    const license = await response.json();
    if (!requestIsCurrent(version) || !isGuid(license?.id) || license.initialOrderId?.toLowerCase() !== order.id.toLowerCase() ||
        license.status !== "ACTIVE" || license.termKind !== order.termKind ||
        Date.parse(license.validFromUtc) !== validFrom.getTime() ||
        (order.termKind === "LIFETIME" ? license.validUntilUtc !== null : Date.parse(license.validUntilUtc) !== validUntil.getTime())) {
      throw new Error("Central's license response did not match this approved order and the dates you confirmed.");
    }
    try { sessionStorage.removeItem(identity.storageKey); } catch (_) {}
    AppState.support.operations.delete(scope);
    status.textContent = `Central recorded the manually issued license ${license.id}. Refreshing Customer 360...`;
    try {
      await fetchCustomer360Data(shopId);
    } catch (refreshError) {
      if (requestIsCurrent(version)) status.textContent = `License ${license.id} was recorded, but Customer 360 could not refresh. ${refreshError.message}`;
    }
  } catch (error) {
    if (requestIsCurrent(version)) status.textContent = `Issue not confirmed. ${error.message} Keep the dates and reason unchanged to retry safely.`;
  } finally {
    if (requestIsCurrent(version)) {
      AppState.commercial.busy = false;
      form.querySelectorAll("input, textarea, button").forEach(control => { control.disabled = false; });
    }
  }
}

async function submitManualAddonRenewal(form, order, license, entitlement, status) {
  if (!AppState.currentUser || AppState.commercial.busy) return;
  if (!hasCapability("addon.renew")) {
    status.textContent = "Your session cannot renew add-ons.";
    return;
  }
  const expiryInput = form.querySelector("[data-addon-renew-expiry]");
  const reasonInput = form.querySelector("[data-addon-renew-reason]");
  const localExpiry = expiryInput?.value ? new Date(expiryInput.value) : null;
  const reason = reasonInput?.value?.trim() ?? "";
  if (!isGuid(order?.id) || !isGuid(license?.id) || order.status !== "PAID_APPROVED" ||
      !localExpiry || !Number.isFinite(localExpiry.getTime()) || !reason) {
    status.textContent = "Enter a valid expiry from the approved order and an audit reason.";
    return;
  }
  const payload = {
    approvedOrderId: order.id,
    newValidUntilUtc: localExpiry.toISOString().replace(/\.000Z$/, "+00:00")
  };
  const scope = `commercial:addon.renew:${order.id}`;
  const version = AppState.sessionVersion;
  const shopId = AppState.activeShopId;
  AppState.commercial.busy = true;
  form.querySelectorAll("input, textarea, button").forEach(control => { control.disabled = true; });
  status.textContent = "Waiting for Central to record this separate add-on renewal...";
  try {
    const identity = await supportOperation(scope, { payload, reason }, AppState.currentUser.id);
    const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(directoryCanonicalJson(payload)));
    const payloadSha256 = Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, "0")).join("");
    const commandMeta = {
      clientOperationId: identity.id,
      operationType: "addon.renew",
      fingerprintVersion: 1,
      payloadSha256,
      expectedVersion: entitlement?.rowVersion ?? 0,
      policyVersion: 0,
      reason
    };
    const path = entitlement
      ? `/api/admin/v1/licenses/${encodeURIComponent(license.id)}/addons/${encodeURIComponent(entitlement.id)}/renew`
      : `/api/commercial/v1/licenses/${encodeURIComponent(license.id)}/addons/renew`;
    const response = await apiFetch(path, { method: "POST", body: JSON.stringify({ ...payload, commandMeta }) });
    if (!response.ok) {
      const error = await responseError(response, "Central did not confirm add-on renewal.");
      if (response.status === 401 && requestIsCurrent(version)) {
        clearIdentity();
        showAuthView("Session expired. Sign in again before renewing an add-on.");
      }
      throw error;
    }
    const updated = await response.json();
    const returnedExpiry = Date.parse(updated?.validUntilUtc);
    if (!requestIsCurrent(version) || !isGuid(updated?.id) || updated.kind !== order.purchaseTarget ||
        updated.status !== "ACTIVE" || returnedExpiry !== localExpiry.getTime() ||
        (entitlement && updated.id.toLowerCase() !== entitlement.id.toLowerCase())) {
      throw new Error("Central's entitlement response did not match this approved order and requested expiry.");
    }
    try { sessionStorage.removeItem(identity.storageKey); } catch (_) {}
    AppState.support.operations.delete(scope);
    status.textContent = `Central recorded the add-on renewal ${updated.id}. Refreshing Customer 360...`;
    try {
      await fetchCustomer360Data(shopId);
    } catch (refreshError) {
      if (requestIsCurrent(version)) status.textContent = `Renewal ${updated.id} was recorded, but Customer 360 could not refresh. ${refreshError.message}`;
    }
  } catch (error) {
    if (requestIsCurrent(version)) status.textContent = `Renewal not confirmed. ${error.message} Keep the expiry and reason unchanged to retry safely.`;
  } finally {
    if (requestIsCurrent(version)) {
      AppState.commercial.busy = false;
      form.querySelectorAll("input, textarea, button").forEach(control => { control.disabled = false; });
    }
  }
}

// These collection/workflow endpoints do not exist in the current controllers.
function loadCommercial() {
  const version = AppState.sessionVersion;
  const request = ++AppState.commercial.request;
  const body = document.getElementById("commercial-licenses-tbody");
  if (!hasCapability("licensing.read")) {
    AppState.commercial.plans = [];
    tableMessage("commercial-licenses-tbody", 7, "Plan catalog access requires licensing.read. Per-shop license details remain available in Customer 360 when permitted.");
    setMessage("commercial-catalog-status", "Your session cannot read the plan catalog.");
    applyCommercialControls();
    return;
  }
  tableMessage("commercial-licenses-tbody", 7, "The server does not expose a global license collection. Per-shop license details are available in Customer 360.");
  setMessage("commercial-catalog-status", "Loading active catalog plans from Central...");
  readJson("/api/commercial/v1/catalog-items", version).then(items => {
    if (!requestIsCurrent(version) || request !== AppState.commercial.request) return;
    if (!Array.isArray(items) || !items.every(plan => plan && isGuid(plan.id) && typeof plan.code === "string" &&
        typeof plan.name === "string" && ["BASE_LICENSE", "SUPPORT", "PREMIUM_UPGRADE"].includes(plan.purchaseTarget) &&
        ["FIXED_TERM", "LIFETIME"].includes(plan.termKind) && (plan.billingPeriodId === null || typeof plan.billingPeriodId === "string") &&
        Number.isFinite(plan.unitPrice) && plan.unitPrice >= 0 && typeof plan.currency === "string" &&
        typeof plan.isActive === "boolean" && Number.isSafeInteger(plan.rowVersion) && plan.rowVersion > 0)) {
      throw new Error("The server returned an incomplete plan catalog.");
    }
    AppState.commercial.plans = items.filter(plan => plan.isActive);
    renderCommercialPlans();
    setMessage("commercial-catalog-status", `${AppState.commercial.plans.length} active plans returned by Central. Prices are server-owned and snapshotted when an order is created.`);
  }).catch(error => {
    if (!requestIsCurrent(version) || request !== AppState.commercial.request) return;
    AppState.commercial.plans = [];
    renderCommercialPlans();
    setMessage("commercial-catalog-status", `The plan catalog is unavailable. ${error.message}`);
  });
  applyCommercialControls();
}

function applyCommercialControls() {
  const visible = hasCapability("order.create") || hasCapability("price.snapshot");
  const allowed = hasCapability("licensing.read") && hasCapability("order.create") && hasCapability("price.snapshot");
  const panel = document.getElementById("commercial-order-panel");
  if (!panel) return;
  panel.hidden = !visible;
  panel.querySelectorAll("input, select, button").forEach(control => { control.disabled = AppState.commercial.busy || !allowed; });
  if (visible && !allowed && !AppState.commercial.busy) {
    setMessage("commercial-order-status", "Order creation requires licensing.read, price.snapshot and order.create. The server rechecks every capability.");
  } else if (allowed && !AppState.commercial.plans.length && !AppState.commercial.busy) {
    setMessage("commercial-order-status", "Load the plan catalog before creating an order.");
  }
}

function renderCommercialPlans() {
  const select = document.getElementById("commercial-plan-id");
  if (!select) return;
  const previous = select.value;
  select.replaceChildren(new Option("Select an active server plan", ""));
  AppState.commercial.plans.forEach(plan => {
    const label = `${plan.code} — ${plan.name} · ${plan.purchaseTarget.replaceAll("_", " ")} · ${plan.termKind.replaceAll("_", " ")} · ${plan.currency} ${plan.unitPrice}`;
    select.add(new Option(label, plan.id));
  });
  if (AppState.commercial.plans.some(plan => plan.id === previous)) select.value = previous;
  updateCommercialOrderFields();
}

function updateCommercialOrderFields() {
  const plan = AppState.commercial.plans.find(item => item.id === document.getElementById("commercial-plan-id").value);
  const type = document.getElementById("commercial-order-type");
  const previous = type.value;
  type.replaceChildren();
  const orderTypes = plan?.purchaseTarget === "BASE_LICENSE"
    ? [["NEW_LICENSE", "New license"], ["RENEWAL", "Renewal"], ["PLAN_CHANGE", "Plan change"]]
    : plan?.purchaseTarget === "SUPPORT" ? [["SUPPORT_RENEWAL", "Support renewal"]]
      : plan?.purchaseTarget === "PREMIUM_UPGRADE" ? [["UPGRADE", "Upgrade"]] : [];
  orderTypes.forEach(([value, label]) => type.add(new Option(label, value)));
  if (orderTypes.some(([value]) => value === previous)) type.value = previous;
  const addOn = plan && plan.purchaseTarget !== "BASE_LICENSE";
  const needsBaseLicense = addOn || ["RENEWAL", "PLAN_CHANGE"].includes(type.value);
  document.getElementById("commercial-base-license-group").hidden = !needsBaseLicense;
  document.getElementById("commercial-base-license-id").required = needsBaseLicense;
  document.getElementById("commercial-entitlement-group").hidden = !addOn;
  document.getElementById("commercial-plan-summary").textContent = plan
    ? `${plan.termKind === "LIFETIME" ? "Lifetime; no expiry" : `Fixed term${plan.billingPeriodId ? ` (${plan.billingPeriodId})` : ""}`} · ${plan.currency} ${plan.unitPrice} per server catalog. Central will revalidate current plan version and calculate the immutable pricing snapshot.`
    : "Select a server plan. The server will calculate and persist the price snapshot.";
}

function bindCommercialHandlers() {
  document.getElementById("commercial-plan-id").addEventListener("change", updateCommercialOrderFields);
  document.getElementById("commercial-order-type").addEventListener("change", updateCommercialOrderFields);
  document.getElementById("commercial-order-form").addEventListener("submit", async event => {
    event.preventDefault();
    await createCommercialOrder();
  });
}

async function createCommercialOrder() {
  const state = AppState.commercial;
  if (!AppState.currentUser || state.busy || !hasCapability("licensing.read") ||
      !hasCapability("order.create") || !hasCapability("price.snapshot")) return;
  const version = AppState.sessionVersion;
  const shopId = document.getElementById("commercial-shop-id").value.trim();
  const plan = state.plans.find(item => item.id === document.getElementById("commercial-plan-id").value);
  const orderType = document.getElementById("commercial-order-type").value;
  const baseLicenseId = document.getElementById("commercial-base-license-id").value.trim();
  const existingEntitlementId = document.getElementById("commercial-entitlement-id").value.trim();
  const reason = document.getElementById("commercial-order-reason").value;
  if (!isGuid(shopId) || !plan || !reason.trim() ||
      (baseLicenseId && !isGuid(baseLicenseId)) || (existingEntitlementId && !isGuid(existingEntitlementId)) ||
      (plan.purchaseTarget !== "BASE_LICENSE" && !isGuid(baseLicenseId)) ||
      (["RENEWAL", "PLAN_CHANGE"].includes(orderType) && !isGuid(baseLicenseId)) ||
      (plan.purchaseTarget === "BASE_LICENSE" && !["NEW_LICENSE", "RENEWAL", "PLAN_CHANGE"].includes(orderType)) ||
      (plan.purchaseTarget === "SUPPORT" && orderType !== "SUPPORT_RENEWAL") ||
      (plan.purchaseTarget === "PREMIUM_UPGRADE" && orderType !== "UPGRADE")) {
    setMessage("commercial-order-status", "Enter a valid shop, active plan, compatible order type, required existing license IDs, and an audit reason.");
    return;
  }
  const purchaseTarget = { ...(baseLicenseId ? { baseLicenseId } : {}), ...(existingEntitlementId ? { existingEntitlementId } : {}), kind: plan.purchaseTarget };
  const snapshotPayload = {
    billingPeriodId: plan.billingPeriodId,
    currency: plan.currency,
    orderType,
    planId: plan.id,
    purchaseTarget,
    requestedPrimaryCapacity: 1,
    shopId,
    termKind: plan.termKind
  };
  const reasonText = reason.trim();
  state.busy = true;
  applyCommercialControls();
  setMessage("commercial-order-status", "Saving a server-validated price snapshot...");
  try {
    const snapshotOutcome = await executeCommercialOperation(
      "price.snapshot", "/api/admin/v1/pricing-snapshots", "price.snapshot", snapshotPayload,
      plan.rowVersion, reasonText, "pricing-snapshots/");
    if (!requestIsCurrent(version)) return;
    const snapshotId = snapshotOutcome.resultReference.slice("pricing-snapshots/".length);
    const orderPayload = {
      orderType,
      planId: plan.id,
      pricingSnapshotId: snapshotId,
      purchaseTarget,
      requestedPrimaryCapacity: 1,
      shopId,
      termKind: plan.termKind
    };
    setMessage("commercial-order-status", `Price snapshot ${snapshotId} persisted. Creating the order with the same plan authority...`);
    const orderOutcome = await executeCommercialOperation(
      "order.create", "/api/admin/v1/orders", "order.create", orderPayload,
      snapshotOutcome.authorityRevision, reasonText, "orders/");
    if (!requestIsCurrent(version)) return;
    const orderId = orderOutcome.resultReference.slice("orders/".length);
    setMessage("commercial-order-status", `Order ${orderId} was persisted by Central. Payment evidence must be uploaded and reviewed before license issuance.`);
    try {
      await loadCustomer360ForCommercial(shopId, version);
    } catch (refreshError) {
      if (requestIsCurrent(version)) setMessage("commercial-order-status", `Order ${orderId} was persisted by Central, but Customer 360 could not refresh. ${refreshError.message}`);
    }
  } catch (error) {
    if (requestIsCurrent(version)) setMessage("commercial-order-status", `Order was not confirmed. ${error.message} Keep the same fields and reason to safely retry the same operation.`);
  } finally {
    if (requestIsCurrent(version)) {
      state.busy = false;
      applyCommercialControls();
    }
  }
}

async function executeCommercialOperation(scope, url, operationType, payload, expectedVersion, reason, resultPrefix) {
  const actor = AppState.currentUser.id;
  const identity = await supportOperation(`commercial:${scope}`, { payload, reason }, actor);
  const canonical = directoryCanonicalJson(payload);
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(canonical));
  const payloadSha256 = Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, "0")).join("");
  const meta = { clientOperationId: identity.id, operationType, fingerprintVersion: 1, payloadSha256,
    expectedVersion, policyVersion: 0, reason };
  const version = AppState.sessionVersion;
  const response = await apiFetch(url, { method: "POST", body: JSON.stringify({ meta, payload }) });
  if (!response.ok) {
    if (response.status === 401 && requestIsCurrent(version)) {
      clearIdentity();
      showAuthView("Session expired. Sign in again before retrying.");
    }
    throw await responseError(response, "The server could not confirm this commercial operation.");
  }
  const outcome = await response.json();
  if (!requestIsCurrent(version) || outcome?.clientOperationId !== identity.id || outcome.operationType !== operationType ||
      outcome.payloadSha256 !== payloadSha256 || outcome.state !== "SUCCEEDED" ||
      typeof outcome.resultReference !== "string" || !outcome.resultReference.startsWith(resultPrefix) ||
      !isGuid(outcome.resultReference.slice(resultPrefix.length)) || !Number.isSafeInteger(outcome.resourceVersion) ||
      !Number.isSafeInteger(outcome.authorityRevision)) {
    throw new Error("Central did not return the persisted operation outcome expected for this request.");
  }
  try { sessionStorage.removeItem(identity.storageKey); } catch (_) {}
  AppState.support.operations.delete(`commercial:${scope}`);
  return outcome;
}

async function loadCustomer360ForCommercial(shopId, version) {
  if (!hasCapability("dashboard.view") || !requestIsCurrent(version)) return;
  AppState.activeShopId = shopId;
  document.getElementById("customer-search-input").value = shopId;
  await fetchCustomer360Data(shopId);
}

function loadLicensing() {
  if (!hasCapability("queues.view")) {
    tableMessage("licensing-activations-tbody", 6, "Your session cannot view licensing review queues.");
    setMessage("licensing-queue-status", "Queue access is unavailable for this session; queues.view is required to read these snapshots.");
    document.getElementById("licensing-queue-prev").disabled = true;
    document.getElementById("licensing-queue-next").disabled = true;
    return;
  }
  loadLicensingQueue(AppState.queues.licensing.type || "ACTIVATION_REVIEW");
}

function loadLicensingQueue(queueType) {
  if (!["ACTIVATION_REVIEW", "HARDWARE_REVIEW", "PRIMARY_TRANSFER"].includes(queueType)) return;
  if (!hasCapability("queues.view")) {
    tableMessage("licensing-activations-tbody", 6, "Your session cannot view licensing review queues.");
    setMessage("licensing-queue-status", "Queue access is unavailable for this session; queues.view is required.");
    return;
  }
  const state = AppState.queues.licensing;
  state.page = 1;
  state.cursorStack = [null];
  state.nextCursor = null;
  state.asOfVersion = null;
  loadQueue(queueType, 1, "licensing", true);
}

function loadApprovals() {
  loadQueue("DUAL_APPROVAL", AppState.queues.approvals.page, "approvals");
}

function applyBackupControls() {
  const state = AppState.backups;
  const allowed = hasCapability("backup.download");
  document.getElementById("backups-refresh").disabled = !allowed || state.busy;
  document.getElementById("backups-prev").disabled = !allowed || state.busy || state.page <= 1;
  document.getElementById("backups-next").disabled = !allowed || state.busy || !state.nextCursor;
}

function clearBackupsData(message = "Sign in to load backup metadata.") {
  const state = AppState.backups;
  state.request += 1;
  state.page = 1;
  state.cursorStack = [null];
  state.nextCursor = null;
  state.asOfVersion = null;
  state.authorizedCount = null;
  state.projectionRevision = null;
  state.busy = false;
  tableMessage("backups-tbody", 9, message);
  setMessage("backups-status", "");
  setMessage("backups-page-status", "");
  applyBackupControls();
}

function isBackupMetadataRecord(record) {
  return record && typeof record === "object" &&
    typeof record.id === "string" && isGuid(record.id) &&
    typeof record.shopId === "string" && isGuid(record.shopId) &&
    Number.isSafeInteger(record.contentLength) && record.contentLength > 0 &&
    typeof record.ciphertextSha256 === "string" && /^[a-f0-9]{64}$/i.test(record.ciphertextSha256) &&
    typeof record.createdAtUtc === "string" && !Number.isNaN(Date.parse(record.createdAtUtc)) &&
    (record.retentionUntilUtc === null || typeof record.retentionUntilUtc === "string" && !Number.isNaN(Date.parse(record.retentionUntilUtc))) &&
    typeof record.isUnderLegalHold === "boolean" && typeof record.isTombstoned === "boolean" &&
    (record.tombstonedAtUtc === null || typeof record.tombstonedAtUtc === "string" && !Number.isNaN(Date.parse(record.tombstonedAtUtc))) &&
    Number.isSafeInteger(record.resourceVersion) && record.resourceVersion >= 1;
}

function isCanonicalBackupCursor(cursor) {
  if (typeof cursor !== "string") return false;
  const offset = Number(cursor);
  return Number.isSafeInteger(offset) && offset >= 0 && offset <= 100000 && String(offset) === cursor;
}

function formatBytes(value) {
  if (!Number.isSafeInteger(value) || value < 0) return "Unavailable";
  return `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 }).format(value)} bytes`;
}

function renderBackupRows(items) {
  const tbody = document.getElementById("backups-tbody");
  tbody.replaceChildren();
  if (items.length === 0) {
    tableMessage("backups-tbody", 9, "No persisted backup metadata was returned for this page.");
    return;
  }
  items.forEach(record => {
    const row = node("tr");
    appendCells(row, [record.id, record.shopId, formatBytes(record.contentLength), record.ciphertextSha256.toLowerCase(),
      formatUtc(record.createdAtUtc), record.retentionUntilUtc ? formatUtc(record.retentionUntilUtc) : "No recorded deadline",
      record.isUnderLegalHold ? "Yes" : "No", record.isTombstoned ? "Tombstoned" : "Not tombstoned",
      record.tombstonedAtUtc ? formatUtc(record.tombstonedAtUtc) : "Not recorded"]);
    tbody.appendChild(row);
  });
}

async function loadBackups(page = 1, refresh = false) {
  const state = AppState.backups;
  if (!AppState.currentUser || !hasCapability("backup.download") || !Number.isSafeInteger(page) || page < 1) return;
  if (refresh) {
    state.page = 1;
    state.cursorStack = [null];
    state.nextCursor = null;
    state.asOfVersion = null;
  }
  if (page > state.page + 1 || (page <= state.page && page > state.cursorStack.length)) return;
  const cursor = page === state.page + 1 ? state.nextCursor : state.cursorStack[page - 1];
  if (page > 1 && !cursor) return;
  const request = ++state.request;
  const version = AppState.sessionVersion;
  state.busy = true;
  applyBackupControls();
  tableMessage("backups-tbody", 9, "Loading persisted backup metadata...");
  setMessage("backups-status", "Loading backup metadata from Central.");
  try {
    const query = new URLSearchParams({ pageSize: String(state.pageSize) });
    if (cursor) query.set("cursor", cursor);
    if (state.asOfVersion !== null) query.set("asOfVersion", String(state.asOfVersion));
    const data = await readJson(`/api/admin/v1/backups?${query}`, version);
    if (!requestIsCurrent(version) || request !== state.request) return;
    if (!data || !Array.isArray(data.items) || !data.items.every(isBackupMetadataRecord) ||
        !(data.nextCursor === null || isCanonicalBackupCursor(data.nextCursor)) ||
        !Number.isSafeInteger(data.asOfVersion) || data.asOfVersion < 0 ||
        !Number.isSafeInteger(data.authorizedCount) || data.authorizedCount < 0 ||
        !Number.isSafeInteger(data.projectionRevision) || data.projectionRevision !== data.asOfVersion ||
        typeof data.isTruncated !== "boolean") {
      throw new Error("Central returned an invalid backup metadata page; no rows were displayed.");
    }
    if (state.asOfVersion !== null && state.asOfVersion !== data.asOfVersion) {
      throw new Error("Backup metadata snapshot changed while paging.");
    }
    renderBackupRows(data.items);
    state.page = page;
    state.cursorStack[page - 1] = cursor ?? null;
    state.cursorStack.length = page;
    state.nextCursor = data.nextCursor;
    state.asOfVersion = data.asOfVersion;
    state.authorizedCount = data.authorizedCount;
    state.projectionRevision = data.projectionRevision;
    setMessage("backups-status", data.items.length === 0 && page === 1
      ? "Central returned no persisted backup metadata records."
      : "Metadata comes from the persisted Central backup object records. No file, hash, custody, or restore verification was performed.");
    const truncationNotice = data.isTruncated ? " Listing is truncated by the server pagination limit." : "";
    setMessage("backups-page-status", `${data.authorizedCount} records in snapshot ${data.projectionRevision}; page ${page}; ${data.items.length} shown.${truncationNotice}`);
  } catch (error) {
    if (!requestIsCurrent(version) || request !== state.request) return;
    if (error.message.includes("PROJECTION_VERSION_CHANGED") || error.message === "Backup metadata snapshot changed while paging.") {
      state.page = 1;
      state.cursorStack = [null];
      state.nextCursor = null;
      state.asOfVersion = null;
      state.authorizedCount = null;
      state.projectionRevision = null;
      setMessage("backups-status", "Backup metadata changed. Reloading the current snapshot.");
      state.busy = false;
      await loadBackups(1);
      return;
    }
    tableMessage("backups-tbody", 9, error.message.includes("HTTP 403")
      ? "Central denied backup metadata access for this session."
      : "Backup metadata could not be loaded.");
    setMessage("backups-status", error.message);
    setMessage("backups-page-status", "Use Refresh metadata to retry.");
    state.nextCursor = null;
  } finally {
    if (requestIsCurrent(version) && request === state.request) {
      state.busy = false;
      applyBackupControls();
    }
  }
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

function validSessionItem(item) {
  return item && isGuid(item.sessionId) && typeof item.adminDisplayName === "string" &&
    ["createdAtUtc", "lastActivityAtUtc", "expiresAtUtc", "absoluteExpiresAtUtc"].every(key =>
      typeof item[key] === "string" && Number.isFinite(Date.parse(item[key]))) &&
    typeof item.mfaSatisfied === "boolean" && typeof item.isRevoked === "boolean" &&
    (item.revokedAtUtc === null || typeof item.revokedAtUtc === "string" && Number.isFinite(Date.parse(item.revokedAtUtc))) &&
    typeof item.isCurrentSession === "boolean";
}

function renderSessions(items) {
  const body = document.getElementById("sessions-table-tbody");
  body.replaceChildren();
  if (items.length === 0) {
    tableMessage("sessions-table-tbody", 8, "No administrator sessions match this filter.");
    return;
  }
  for (const item of items) {
    const row = node("tr");
    appendCells(row, [
      item.adminDisplayName,
      formatUtc(item.createdAtUtc),
      formatUtc(item.lastActivityAtUtc),
      formatUtc(item.expiresAtUtc),
      formatUtc(item.absoluteExpiresAtUtc),
      item.mfaSatisfied ? "Satisfied" : "Not satisfied",
      item.isRevoked ? `Revoked${item.revokedAtUtc ? ` · ${formatUtc(item.revokedAtUtc)}` : ""}` : item.isCurrentSession ? "Current session" : "Active"
    ]);
    const actionCell = node("td");
    if (!item.isRevoked && !item.isCurrentSession) {
      const revoke = node("button", "Revoke", "btn btn-danger btn-sm");
      revoke.type = "button";
      revoke.dataset.sessionRevoke = item.sessionId;
      revoke.setAttribute("aria-label", `Revoke session for ${item.adminDisplayName}`);
      revoke.disabled = !hasCapability("sessions.revoke") || AppState.sessions.busy;
      revoke.addEventListener("click", () => openSessionRevokeDialog(item));
      actionCell.appendChild(revoke);
    } else {
      actionCell.textContent = item.isCurrentSession ? "Current session" : "Already revoked";
    }
    row.appendChild(actionCell);
    body.appendChild(row);
  }
}

async function loadSessions(page = 1, reset = false) {
  if (!AppState.currentUser || !hasCapability("sessions.view")) {
    clearSessionsData("Your session does not have sessions.view; session data is unavailable.");
    return;
  }
  const state = AppState.sessions;
  if (reset) {
    state.page = 1;
    state.cursorStack = [null];
    state.asOfVersion = null;
    state.nextCursor = null;
  }
  const cursor = state.cursorStack[page - 1];
  if (page < 1 || cursor === undefined) return;
  const version = AppState.sessionVersion;
  const request = ++state.request;
  const query = new URLSearchParams({ pageSize: String(state.pageSize) });
  const filter = document.getElementById("sessions-filter").value.trim();
  if (filter) query.set("filter", filter);
  if (cursor !== null) {
    query.set("cursor", cursor);
    query.set("asOfVersion", String(state.asOfVersion));
  }
  tableMessage("sessions-table-tbody", 8, "Loading administrator sessions...");
  setMessage("sessions-status", "Loading sessions from Central.");
  setMessage("sessions-page-status", "");
  applySessionControls();
  try {
    const response = await apiFetch(`/api/admin/v1/sessions?${query}`);
    if (!response.ok) {
      const error = await responseError(response, "The server could not load administrator sessions.");
      if (response.status === 401 && requestIsCurrent(version)) {
        clearIdentity();
        showAuthView("Session expired. Sign in again.");
      }
      throw error;
    }
    const data = await response.json();
    if (!requestIsCurrent(version) || request !== state.request) return;
    if (!data || !Array.isArray(data.items) || !data.items.every(validSessionItem) ||
        !Number.isSafeInteger(data.authorizedCount) || data.authorizedCount < 0 ||
        !Number.isSafeInteger(data.asOfVersion) || data.asOfVersion < 0 ||
        !(data.nextCursor === null || typeof data.nextCursor === "string" && /^\d+$/.test(data.nextCursor))) {
      throw new Error("The server returned an invalid administrator session page.");
    }
    state.page = page;
    state.asOfVersion = data.asOfVersion;
    state.nextCursor = data.nextCursor;
    state.authorizedCount = data.authorizedCount;
    if (data.nextCursor !== null) state.cursorStack[page] = data.nextCursor;
    renderSessions(data.items);
    setMessage("sessions-status", data.authorizedCount ? `${data.authorizedCount} administrator session${data.authorizedCount === 1 ? "" : "s"} in this authorized view.` : "No administrator sessions are available.");
    setMessage("sessions-page-status", `Page ${page}; ${data.items.length} shown. Current session is identified by Central.`);
  } catch (error) {
    if (!requestIsCurrent(version) || request !== state.request) return;
    tableMessage("sessions-table-tbody", 8, "Administrator sessions could not be loaded.");
    setMessage("sessions-status", `Session list unavailable. ${error.message}${error.message.includes("HTTP 403") ? " Your session needs sessions.view." : ""}`);
    setMessage("sessions-page-status", "Refresh to retry the current authorized view.");
    state.nextCursor = null;
  } finally {
    if (requestIsCurrent(version) && request === state.request) applySessionControls();
  }
}

function openSessionRevokeDialog(item) {
  if (!hasCapability("sessions.revoke") || !item || item.isCurrentSession || item.isRevoked || !isGuid(item.sessionId)) return;
  const state = AppState.sessions;
  state.confirmTarget = item.sessionId;
  document.getElementById("sessions-revoke-target").textContent = `Administrator: ${item.adminDisplayName}. Session ID: ${item.sessionId}`;
  setMessage("sessions-revoke-status", "");
  document.getElementById("sessions-revoke-confirm").disabled = false;
  document.getElementById("sessions-revoke-dialog").showModal();
}

async function confirmSessionRevocation() {
  const state = AppState.sessions;
  const targetId = state.confirmTarget;
  if (!hasCapability("sessions.revoke") || !isGuid(targetId) || state.busy) return;
  const session = AppState.sessionVersion;
  state.busy = true;
  document.getElementById("sessions-revoke-confirm").disabled = true;
  document.getElementById("sessions-revoke-cancel").disabled = true;
  setMessage("sessions-revoke-status", "Revocation is being submitted to Central...");
  applySessionControls();
  try {
    const query = new URLSearchParams({ targetSessionId: targetId });
    const response = await apiFetch(`/api/admin/v1/portal/sessions/revoke?${query}`, { method: "POST" });
    if (!response.ok) {
      const error = await responseError(response, "The server could not revoke this session.");
      if (response.status === 401 && requestIsCurrent(session)) {
        clearIdentity();
        showAuthView("Session expired. Sign in again.");
      }
      throw error;
    }
    const data = await response.json();
    if (!requestIsCurrent(session)) return;
    if (data?.success !== true) throw new Error("Central did not confirm the requested revocation.");
    document.getElementById("sessions-revoke-dialog").close();
    state.confirmTarget = null;
    setMessage("sessions-status", "Central confirmed revocation. Refreshing the persisted session list.");
    await loadSessions(1, true);
  } catch (error) {
    if (!requestIsCurrent(session)) return;
    setMessage("sessions-revoke-status", `Revocation was not confirmed. ${error.message}${error.message.includes("HTTP 403") ? " Your session needs sessions.revoke." : ""}`);
  } finally {
    if (requestIsCurrent(session)) {
      state.busy = false;
      document.getElementById("sessions-revoke-cancel").disabled = false;
      applySessionControls();
    }
  }
}

function bindSessionHandlers() {
  document.getElementById("sessions-refresh").addEventListener("click", () => loadSessions(AppState.sessions.page));
  document.getElementById("sessions-filter-form").addEventListener("submit", event => {
    event.preventDefault();
    loadSessions(1, true);
  });
  document.getElementById("sessions-prev").addEventListener("click", () => {
    if (AppState.sessions.page > 1) loadSessions(AppState.sessions.page - 1);
  });
  document.getElementById("sessions-next").addEventListener("click", () => {
    if (AppState.sessions.nextCursor) loadSessions(AppState.sessions.page + 1);
  });
  document.getElementById("sessions-revoke-confirm").addEventListener("click", confirmSessionRevocation);
  document.getElementById("sessions-revoke-dialog").addEventListener("close", () => {
    if (!AppState.sessions.busy) AppState.sessions.confirmTarget = null;
  });
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
  bindCommercialHandlers();
  bindNotificationHandlers();
  bindSessionHandlers();
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
