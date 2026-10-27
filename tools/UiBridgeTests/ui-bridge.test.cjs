"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const { JSDOM, VirtualConsole } = require("jsdom");

const repo = path.resolve(__dirname, "../..");
const html = fs.readFileSync(path.join(repo, "src/RemoteCli.Desktop/UI/longshell-ui.html"), "utf8");
const { relayBaseUrl } = JSON.parse(fs.readFileSync(path.join(repo, "NewServer/server.json"), "utf8"));
const expiresAt = "2099-01-01T00:00:00Z";

// Execute the shipped page, stubbing only the WebView2 host boundary. No desktop UI is opened.
function page(t, initialize = true) {
  const messages = [];
  const listeners = new Map();
  const errors = [];
  const virtualConsole = new VirtualConsole();
  virtualConsole.on("jsdomError", error => errors.push(error));
  const dom = new JSDOM(html, {
    url: relayBaseUrl,
    runScripts: "dangerously",
    virtualConsole,
    beforeParse(window) {
      window.chrome = { webview: {
        postMessage(message) { messages.push(JSON.parse(JSON.stringify(message))); },
        addEventListener(type, listener) { listeners.set(type, listener); }
      } };
    }
  });
  t.after(() => {
    dom.window.close();
    assert.deepEqual(errors, [], "the embedded page must run without script errors");
  });
  const query = selector => {
    const element = dom.window.document.querySelector(selector);
    assert.ok(element, `missing page element: ${selector}`);
    return element;
  };
  const receive = message => {
    assert.ok(listeners.has("message"), "page must subscribe to host messages");
    listeners.get("message")({ data: message });
  };
  if (initialize) receive({ type: "init", userName: "controller", machineName: "test-host", relay: relayBaseUrl });
  return { query, receive, messages, document: dom.window.document };
}

function fillConnection(ui) {
  ui.query('.nav[data-role="local"]').click();
  ui.query("#localIdentity").value = "  Test controller  ";
  ui.query("#relay").value = `  ${relayBaseUrl}  `;
  ui.query("#joinUserName").value = "  remote-user  ";
  ui.query("#joinSecret").value = "  fixed password stays exact  ";
}

function requestApproval(ui) {
  ui.receive({ type: "approval-request", identity: "Remote computer", expiresAt, requested: ["session", "execute"] });
}

test("host initialization gates connection and keeps the NewServer default", t => {
  const ui = page(t, false);
  assert.deepEqual(ui.messages, [{ type: "ready" }]);
  assert.equal(ui.query("#pairBtn").disabled, true);
  assert.equal(ui.query("#relay").value, relayBaseUrl);
  ui.receive({ type: "init", userName: "controller", relay: relayBaseUrl });
  assert.equal(ui.query("#pairBtn").disabled, false);
});

test("connect click submits credentials exactly once to the host", t => {
  const ui = page(t);
  fillConnection(ui);
  ui.query("#pairBtn").click();
  ui.query("#pairBtn").click();
  assert.deepEqual(ui.messages.filter(message => message.type === "join"), [{
    type: "join", identity: "Test controller", relay: relayBaseUrl,
    userName: "remote-user", secret: "  fixed password stays exact  "
  }]);
  assert.match(ui.query("#localStatus").textContent, /正在连接/);
  assert.equal(ui.query("#pairBtn").disabled, true);
  assert.equal(ui.query("#stopLocalBtn").disabled, false);
  assert.equal(ui.query("#remoteText").textContent, "未进行连接");
  assert.equal(ui.query("#sessionCard").hidden, true);
});

test("invalid connection inputs stay editable and do not submit", t => {
  const ui = page(t);
  for (const [selector, value] of [["#localIdentity", " "], ["#joinUserName", "ab"], ["#joinSecret", "short"]]) {
    fillConnection(ui);
    ui.query(selector).value = value;
    ui.query("#pairBtn").click();
    assert.equal(ui.query("#pairBtn").disabled, false);
    assert.equal(ui.query("#stopLocalBtn").disabled, true);
    assert.equal(ui.messages.some(message => message.type === "join"), false);
    assert.equal(ui.query("#localStatus").classList.contains("is-warn"), true);
  }
});

test("host failure ends the pending state and allows a fresh connect request", t => {
  const ui = page(t);
  fillConnection(ui);
  ui.query("#pairBtn").click();
  ui.receive({ type: "error", side: "local", text: "连接超时，请重试。" });
  ui.receive({ type: "ended", side: "local" });
  assert.match(ui.query("#localStatus").textContent, /连接超时/);
  assert.equal(ui.query("#pairBtn").disabled, false);
  assert.equal(ui.query("#stopLocalBtn").disabled, true);
  ui.query("#pairBtn").click();
  assert.equal(ui.messages.filter(message => message.type === "join").length, 2);
});

test("password connection needs no controller approval and follows remote policy updates", t => {
  const ui = page(t);
  fillConnection(ui);
  ui.query("#pairBtn").click();
  requestApproval(ui);
  assert.equal(ui.document.querySelector("#scrim"), null);
  assert.equal(ui.query("#remoteText").textContent, "未进行连接");
  ui.receive({ type: "connected", side: "local", peer: "Remote computer", expiresAt, granted: ["session"] });
  assert.equal(ui.query("#remoteText").textContent, "已进行连接");
  assert.equal(ui.query('.nav[data-role="local"]').classList.contains("is-live"), true);
  assert.equal(ui.query('.nav[data-role="remote"]').classList.contains("is-live"), true);
  assert.equal(ui.query("#sessionPeer").textContent, "Remote computer");
  assert.equal(ui.query("#sessionCard").hidden, false);
  assert.equal(ui.query("#mcpText").textContent, "MCP 未连接");
  ui.receive({ type: "remote-policy", granted: ["session", "execute", "cancel"], workspace: "C:\\WorkspaceB", workspaceRestricted: true });
  assert.match(ui.query("#sessionPerms").textContent, /执行与取消命令/);
  assert.equal(ui.query("#sessionWorkspace").textContent, "C:\\WorkspaceB");
  assert.equal(ui.query("#sessionRestriction").textContent, "已开启");
  ui.receive({ type: "ended", side: "local" });
  assert.equal(ui.query("#remoteText").textContent, "未进行连接");
  assert.equal(ui.query("#sessionCard").hidden, true);
});

test("remote permissions, isolation, and automatic approval publish immediate updates", t => {
  const ui = page(t);
  ui.query('input[name="perm"][value="execute"]').click();
  assert.deepEqual(ui.messages.at(-1), { type: "update-access", permissions: ["session"], workspaceRestricted: true, autoApproveTools: false });
  ui.query("#workspaceRestricted").click();
  assert.deepEqual(ui.messages.at(-1), { type: "update-access", permissions: ["session"], workspaceRestricted: false, autoApproveTools: false });
  ui.query("#autoApproveTools").click();
  assert.deepEqual(ui.messages.at(-1), { type: "update-access", permissions: ["session"], workspaceRestricted: false, autoApproveTools: true });
  ui.query('input[name="perm"][value="execute"]').click();
  assert.deepEqual(ui.messages.at(-1), { type: "update-access", permissions: ["session", "execute", "cancel"], workspaceRestricted: false, autoApproveTools: true });
  ui.receive({ type: "tool-settings", autoApproveTools: true });
  assert.equal(ui.query("#autoApproveTools").checked, true);
  ui.receive({ type: "init", version: "20261027-123456" });
  assert.equal(ui.query("#appVersion").textContent, "20261027-123456");
});

test("stop forwards the active role and waits for host completion", t => {
  const ui = page(t);
  fillConnection(ui);
  ui.query("#pairBtn").click();
  ui.query("#stopLocalBtn").click();
  assert.deepEqual(ui.messages.at(-1), { type: "stop", role: "local" });
  assert.equal(ui.query("#pairBtn").disabled, true);
  ui.receive({ type: "ended", side: "local" });
  assert.equal(ui.query("#pairBtn").disabled, false);
  assert.equal(ui.query("#stopLocalBtn").disabled, true);
});

test("fixed and random authorization clicks forward their settings and stop remotely", t => {
  const ui = page(t);
  for (const mode of ["fixed", "random"]) {
    ui.query("#remoteIdentity").value = "Remote computer";
    ui.query("#authorizationUserName").value = "remote-user";
    ui.query("#credentialMode").value = mode;
    ui.query("#fixedPassword").value = "long fixed password";
    ui.query("#rotationMinutes").value = "30";
    ui.query("#createBtn").click();
    assert.deepEqual(ui.messages.at(-1), {
      type: "create", identity: "Remote computer", userName: "remote-user", credentialMode: mode,
      fixedPassword: "long fixed password", rotationMinutes: 30, relay: relayBaseUrl,
      permissions: ["session", "execute", "cancel"], workspaceRestricted: true, autoApproveTools: false
    });
    ui.query("#stopRemoteBtn").click();
    assert.deepEqual(ui.messages.at(-1), { type: "stop", role: "remote" });
    ui.receive({ type: "ended", side: "remote" });
    assert.equal(ui.query("#createBtn").disabled, false);
  }
});

test("tool setup and copy buttons reach the host", t => {
  const ui = page(t);
  ui.query("#selectWorkspaceBtn").click();
  ui.query("#installToolBtn").click();
  assert.deepEqual(ui.messages.slice(-2), [{ type: "select-workspace" }, { type: "install-tool" }]);
  ui.receive({ type: "authorization-ready", userName: "remote-user", password: "random test password", expiresAt, credentialMode: "random" });
  ui.query('[data-copy="id"]').click();
  assert.deepEqual(ui.messages.at(-1), { type: "copy", text: "remote-user" });
  ui.query('[data-copy="secret"]').click();
  assert.deepEqual(ui.messages.at(-1), { type: "copy", text: "random test password" });
});
