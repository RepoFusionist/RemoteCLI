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
function page(t, initialize = true, confirmResponses = [true]) {
  const messages = [];
  const listeners = new Map();
  const errors = [];
  const confirmMessages = [];
  const virtualConsole = new VirtualConsole();
  virtualConsole.on("jsdomError", error => errors.push(error));
  const dom = new JSDOM(html, {
    url: relayBaseUrl,
    runScripts: "dangerously",
    virtualConsole,
    beforeParse(window) {
      window.confirm = message => {
        confirmMessages.push(message);
        return confirmResponses.length ? confirmResponses.shift() : true;
      };
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
  return { query, receive, messages, confirmMessages, document: dom.window.document };
}

function fillConnection(ui) {
  ui.query('.nav[data-role="local"]').click();
  ui.query("#localIdentity").value = "  Test controller  ";
  ui.query("#relay").value = `  ${relayBaseUrl}  `;
  ui.query("#joinUserName").value = "  remote-user  ";
  ui.query("#joinSecret").value = "  fixed password stays exact  ";
}

function addWorkspace(ui, name = "Alpha", path = "C:////Work////Alpha") {
  ui.query("#addWorkspaceBtn").click();
  ui.receive({ type: "workspace-folder", path });
  ui.query("#trustWorkspaceName").value = name;
  ui.query("#trustWorkspaceBtn").click();
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

test("startup splash draws the product name and the greeting has no decorative marker", t => {
  const ui = page(t);
  assert.equal(ui.document.querySelectorAll("#bootSplash .boot-splash__letter").length, 9);
  assert.equal(ui.document.querySelector(".terminal__greet .mark"), null);
  assert.match(ui.query(".approval-menu__warning").textContent, /完全访问权限只适用于自定义网关\s+远程设备仍受远程会话授权保护/);
});

test("startup letters draw together and land on the title within three seconds", async t => {
  const { chromium } = require("playwright-core");
  const { pathToFileURL } = require("node:url");
  const browser = await chromium.launch({ executablePath: process.env.REMOTECLI_TEST_BROWSER || "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", headless: true });
  t.after(async () => browser.close());
  const browserPage = await browser.newPage({ viewport: { width: 1100, height: 760 }, reducedMotion: "no-preference" });
  await browserPage.goto(pathToFileURL(path.join(repo, "src/RemoteCli.Desktop/UI/longshell-ui.html")).href);
  const result = await browserPage.evaluate(() => {
    const logo = document.querySelector(".boot-splash__logo"), brand = document.querySelector("#pageBrand");
    const letters = [...logo.querySelectorAll(".boot-splash__letter")];
    const animations = letters.map(letter => letter.querySelector(".boot-splash__stroke").getAnimations()[0]);
    const timings = animations.map(animation => animation.effect.getComputedTiming());
    const colors = letters.map(letter => getComputedStyle(letter.querySelector(".boot-splash__stroke")).stroke);
    const dock = logo.getAnimations().find(animation => animation.animationName === "boot-splash-dock");
    dock.pause(); dock.currentTime = 1800;
    const target = brand.getBoundingClientRect(), actual = logo.getBoundingClientRect();
    return { timings:timings.map(timing => ({ delay:timing.delay, duration:timing.duration })), colors,
      logoFont:getComputedStyle(logo).fontFamily, brandFont:getComputedStyle(brand).fontFamily,
      target:{ x:target.x, y:target.y, width:target.width, height:target.height },
      actual:{ x:actual.x, y:actual.y, width:actual.width, height:actual.height },
      total:document.querySelector(".app").getAnimations()[0].effect.getComputedTiming().endTime };
  });
  assert.equal(result.timings.length, 9);
  assert.ok(result.timings.every(timing => timing.delay === result.timings[0].delay && timing.duration === result.timings[0].duration));
  assert.equal(new Set(result.colors).size, 1);
  assert.equal(result.logoFont, result.brandFont, "splash uses the same font family as the title");
  for (const key of ["x", "y", "width", "height"]) assert.ok(Math.abs(result.actual[key] - result.target[key]) < 2, `${key}: ${JSON.stringify(result)}`);
  assert.ok(result.total >= 2000 && result.total <= 3000);
});

test("Codex CLI controls request auto-find, native browse, and saved path", t => {
  const ui = page(t);
  ui.receive({ type: "codex-cli-status", path: "", found: false, message: "未找到 Codex CLI。" });
  assert.equal(ui.query("#codexCliExecutablePath").disabled, false);
  assert.match(ui.query("#codexCliStatus").textContent, /未找到/);

  ui.query("#codexCliDetectBtn").click();
  ui.query("#codexCliBrowseBtn").click();
  ui.query("#codexCliExecutablePath").value = "C:\\Tools\\Codex\\codex.exe";
  ui.query("#codexCliSaveBtn").click();
  assert.deepEqual(ui.messages.filter(message => message.type.startsWith("codex-cli-")), [
    { type: "codex-cli-detect" },
    { type: "codex-cli-browse" },
    { type: "codex-cli-save", path: "C:\\Tools\\Codex\\codex.exe" }
  ]);

  ui.receive({
    type: "codex-cli-status",
    path: "C:\\Tools\\Codex\\codex.exe",
    found: true,
    message: "Codex CLI 路径已保存。"
  });
  assert.equal(ui.query("#codexCliExecutablePath").value, "C:\\Tools\\Codex\\codex.exe");
  assert.match(ui.query("#codexCliStatus").textContent, /已保存/);
});

test("enabling controller Codex consent warns about MCP side effects and honors cancellation", t => {
  const ui = page(t, true, [false, true]);
  const consent = ui.query("#allowControllerModelConsent");

  consent.click();
  assert.equal(consent.checked, false, "canceling the warning must leave consent disabled");
  assert.match(ui.confirmMessages[0], /已配置的 MCP 服务器/);
  assert.match(ui.confirmMessages[0], /外部服务或执行操作/);

  consent.click();
  assert.equal(consent.checked, true, "accepting the warning must enable consent");
  assert.equal(ui.confirmMessages.length, 2);
});

test("connect click submits credentials exactly once to the host", t => {
  const ui = page(t);
  fillConnection(ui);
  ui.query("#pairBtn").click();
  ui.query("#pairBtn").click();
  assert.deepEqual(ui.messages.filter(message => message.type === "join"), [{
    type: "join", identity: "Test controller", relay: relayBaseUrl, expectedFingerprint: "",
    userName: "remote-user", secret: "  fixed password stays exact  ", allowControllerModel: false
  }]);
  assert.match(ui.query("#localStatus").textContent, /正在连接/);
  assert.equal(ui.query("#pairBtn").disabled, true);
  assert.equal(ui.query("#stopLocalBtn").disabled, false);
  assert.equal(ui.query("#remoteText").textContent, "未进行连接");
  assert.equal(ui.query("#sessionCard").hidden, true);
});

test("controller Codex consent is included in the connection request after the risk prompt", t => {
  const ui = page(t);
  fillConnection(ui);
  ui.query("#allowControllerModelConsent").click();
  ui.receive({
    type: "controller-model-catalog",
    forGrant: true,
    success: true,
    defaultModel: "gpt-6.1-sol",
    defaultReasoningEffort: "medium",
    models: [
      { id: "gpt-6.1-sol", displayName: "GPT-6.1 Sol", reasoningEfforts: ["low", "medium", "high"], defaultReasoningEffort: "medium" },
      { id: "gpt-5.6-luna", displayName: "GPT-5.6 Luna", reasoningEfforts: ["low", "high"], defaultReasoningEffort: "low" }
    ]
  });
  ui.query("#pairBtn").click();

  assert.deepEqual(ui.messages.filter(message => message.type === "join"), [{
    type: "join", identity: "Test controller", relay: relayBaseUrl, expectedFingerprint: "",
    userName: "remote-user", secret: "  fixed password stays exact  ", allowControllerModel: true,
    controllerModelIds: ["gpt-6.1-sol", "gpt-5.6-luna"], controllerModelMaxEffort: "high"
  }]);
  assert.equal(ui.confirmMessages.length, 1);
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

test("failed local Codex response renders an error in the terminal, clears busy state, and permits retry", t => {
  const ui = page(t);
  addWorkspace(ui);
  ui.receive({ type: "connected", side: "local", peer: "Remote computer", expiresAt, granted: ["session", "controller-model"] });
  ui.query("#terminalInput").value = "Use the configured MCP";
  ui.query("#terminalInput").dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  ui.query("#terminalSendBtn").click();
  assert.deepEqual(ui.messages.at(-1), { type: "local-controller-model-request", prompt: "Use the configured MCP", model:"", reasoningEffort:"high", workspaceRoot:"C:////Work////Alpha" });

  ui.receive({ type: "local-controller-model-started", requestId: "request-1", prompt: "Use the configured MCP" });
  assert.equal(ui.query("#terminalSendBtn").getAttribute("aria-label"), "停止");
  assert.equal(ui.query("#terminalSendBtn").classList.contains("is-stop"), true);
  ui.receive({
    type: "local-controller-model-response", requestId: "request-1", success: false,
    error: "Codex 无法启动 MCP 服务器，请检查主控端 MCP 配置。"
  });

  const error = ui.query("#terminalThread .node--system .node__text.is-error");
  assert.equal(error.textContent, "Codex 无法启动 MCP 服务器，请检查主控端 MCP 配置。");
  ui.query("#terminalInput").value = "Retry";
  ui.query("#terminalInput").dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  assert.equal(ui.query("#terminalSendBtn").disabled, false, "failed requests must release the busy state so the user can retry");
});

test("terminal view requires a workspace and persists conversations through the host", t => {
  const ui = page(t);
  addWorkspace(ui);
  ui.query('[data-page-pill="terminal"]').click();
  assert.equal(ui.query(".main").dataset.page, "terminal");
  assert.match(ui.query("#sessionChip").textContent, /Alpha/);
  ui.query("#newChatBtn").click();
  assert.equal(ui.document.querySelector("#terminalGreeting"), null);
  const beforeSend = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(beforeSend.state.conversations.length, 0, "empty drafts must not become conversations");

  ui.receive({ type: "connected", side: "local", peer: "Remote computer", expiresAt, granted: ["session", "controller-model"] });
  ui.query("#terminalInput").value = "帮我看看服务器状态";
  ui.query("#terminalInput").dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  ui.query("#terminalSendBtn").click();
  assert.deepEqual(ui.messages.at(-1), { type: "local-controller-model-request", prompt: "帮我看看服务器状态", model:"", reasoningEffort:"high", workspaceRoot:"C:////Work////Alpha" });
  ui.receive({ type: "local-controller-model-started", requestId: "request-1", prompt: "帮我看看服务器状态" });

  const saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(saved.state.conversations.length, 1);
  const alpha = saved.state.workspaces.find(workspace => workspace.name === "Alpha");
  assert.equal(saved.state.conversations[0].workspaceId, alpha.id);
  assert.equal(saved.state.conversations[0].messages[0].text, "帮我看看服务器状态");
  assert.equal(ui.document.querySelector("#terminalGreeting"), null);

  const replay = page(t);
  replay.receive({ type: "ui-state", state: saved.state });
  replay.receive({ type: "init", userName: "controller", relay: relayBaseUrl });
  assert.match(replay.query("#workspaceList").textContent, /帮我看看服务器状态/);
  replay.query("[data-conversation]").click();
  assert.match(replay.query("#terminalThread").textContent, /帮我看看服务器状态/);
});

test("account window configures gateway credentials, models, and effort", t => {
  const ui = page(t);
  addWorkspace(ui);
  assert.equal(ui.query("#replySourceChip").textContent, "主控端模型");
  ui.query("#accountBtn").click();
  assert.equal(ui.query("#settingsScrim").hidden, false);
  ui.query('input[name="replySource"][value="gateway"]').click();
  assert.equal(ui.query("#gatewayFields").hidden, false);
  ui.query("#gatewayUrl").value = "https://gw.example.com/v1";
  ui.query("#gatewayApiKey").value = "secret-key";
  ui.query("#gatewayBackend").value = "anthropic";
  ui.query("#relay").value = "https://relay2.example.com/remotecli/";

  ui.query('[data-settings-pane="models"]').click();
  ui.query("#addModelBtn").click();
  const modelInput = ui.query("#modelList input");
  modelInput.value = "test-model";
  modelInput.dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  ui.query("#defaultEffortSelect").value = "max";
  ui.query('[data-settings-pane="tools"]').click();
  ui.query('input[name="chatTool"][value="glob"]').checked = true;
  ui.query('input[name="chatTool"][value="remote_tools"]').checked = true;
  ui.query("#webSearchProvider").value = "exa";
  ui.query("#webSearchApiKey").value = "exa-test-key";
  ui.query("#webSearchBaseUrl").value = "https://search.example.com";
  ui.query("#webSearchTimeoutSeconds").value = "15";
  ui.query("[data-settings-pane='connection']").click();

  ui.query("#settingsSaveBtn").click();
  assert.equal(ui.query("#settingsScrim").hidden, true);
  assert.equal(ui.query("#replySourceChip").textContent, "自定义网关");
  ui.query("#replySourceChip").click();
  assert.equal(ui.query("#replySourceChip").textContent, "主控端模型");
  ui.query("#replySourceChip").click();
  assert.equal(ui.query("#replySourceChip").textContent, "自定义网关");
  assert.equal(ui.query("#modelChip").textContent, "test-model");
  assert.equal(ui.query("#effortChip").textContent, "思考 · Max");

  const saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(saved.state.settings.replySource, "gateway");
  assert.equal(saved.state.settings.gatewayUrl, "https://gw.example.com/v1");
  assert.equal(saved.state.settings.relay, "https://relay2.example.com/remotecli/");
  assert.deepEqual(saved.state.settings.models, ["test-model"]);
  assert.equal(saved.state.settings.defaultModel, "test-model");
  assert.equal(saved.state.settings.effort, "max");
  assert.equal(saved.state.settings.backend, "anthropic");
  assert.equal(saved.state.settings.webSearchProvider, "exa");
  assert.equal(saved.state.settings.webSearchApiKey, "exa-test-key");
  assert.equal(saved.state.settings.webSearchBaseUrl, "https://search.example.com");
  assert.equal(saved.state.settings.webSearchTimeoutSeconds, 15);
  assert.deepEqual(saved.state.settings.globalTools, saved.state.settings.tools);

  ui.query('[data-page-pill="terminal"]').click();
  ui.query("#terminalInput").value = "hello gateway";
  ui.query("#terminalInput").dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  ui.query("#terminalSendBtn").click();
  const request = ui.messages.filter(message => message.type === "gateway-chat-request").at(-1);
  assert.ok(request.conversationId);
  assert.equal(request.baseUrl, "https://gw.example.com/v1");
  assert.equal(request.model, "test-model");
  assert.equal(request.effort, "max");
  assert.equal(request.backend, "anthropic");
  assert.equal(request.webSearchProvider, "exa");
  assert.equal(request.webSearchApiKey, "exa-test-key");
  assert.equal(request.webSearchBaseUrl, "https://search.example.com");
  assert.equal(request.webSearchTimeoutSeconds, 15);
  assert.equal(request.workspaceRoot, "C:////Work////Alpha");
  assert.ok(request.enabledTools.includes("list_files"));
  assert.ok(request.enabledTools.includes("glob"));
  assert.ok(request.enabledTools.includes("remote_tools"));
  ui.receive({ type: "gateway-chat-started", requestId: request.requestId, prompt: "hello gateway", cancellable: true });
  ui.receive({ type: "gateway-chat-response", requestId: request.requestId, success: true, response: "pong" });
  assert.match(ui.query("#terminalThread").textContent, /pong/);
});

test("interface language switches, translates settings, and persists", t => {
  const ui = page(t);
  ui.query("#accountBtn").click();
  ui.query("#languageSelect").value = "en-US";
  ui.query("#settingsSaveBtn").click();

  assert.equal(ui.document.documentElement.lang, "en-US");
  assert.equal(ui.query(".settings-window__title").textContent, "Settings");
  assert.equal(ui.query('[data-settings-pane="general"]').textContent.trim(), "General");
  assert.equal(ui.query("#languageSelect").value, "en-US");
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(saved.state.settings.language, "en-US");

  ui.receive({ type:"ui-state", state:{ settings:{ language:"zh-CN" } } });
  assert.equal(ui.document.documentElement.lang, "zh-CN");
  assert.equal(ui.query(".settings-window__title").textContent, "设置");
});

test("composer chips show only the workspace name and the approval menu uses the product name", t => {
  const ui = page(t);
  ui.receive({ type:"ui-state", state:baseState({ activeConversationId:"c1" }) });
  assert.equal(ui.query("#sessionChip").textContent, "Alpha");
  assert.equal(ui.query("#sessionChip").textContent.includes("服务器问题"), false);
  assert.equal(ui.query(".approval-menu__title").textContent, "如何批准 Remote CLI 操作？");
  assert.equal(ui.document.querySelector("#composerDrawBtn"), null);
});

test("composer options use line icons", t => {
  const ui = page(t);
  for (const id of ["composerTargetBtn", "composerPlanBtn", "composerQaBtn"]) {
    assert.ok(ui.query(`#${id} svg`), `${id} uses a line icon`);
  }
  assert.doesNotMatch(ui.query("#composerTargetBtn").textContent, /🎯/);
  assert.doesNotMatch(ui.query("#composerPlanBtn").textContent, /💡/);
  assert.doesNotMatch(ui.query("#composerQaBtn").textContent, /✦/);
});

test("composer modes are exclusive and Q&A sends read-only tools only", t => {
  const ui = page(t);
  ui.receive({ type:"ui-state", state:baseState({
    activeConversationId:"c1",
    settings:{ relay:relayBaseUrl, replySource:"gateway", gatewayUrl:"https://gateway.example/v1",
      gatewayApiKey:"key", models:["model"], defaultModel:"model",
      globalTools:["list_files","read_file","write_file","apply_patch","web_search","web_fetch"] },
    composer:{ mode:"default", plan:false, target:"stale goal text" }
  }) });
  ui.query('[data-page-pill="terminal"]').click();
  assert.equal(ui.query("#composerModeAnnotation").hidden, true, "the default tool-calling mode does not take up space in the composer");
  assert.equal(ui.document.querySelector("#composerToolModeBtn"), null, "the implicit default is not listed as a mode choice");
  assert.equal(ui.document.querySelectorAll(".turn__mode").item(0).hidden, true, "default conversation turns have no mode badge");

  ui.query("#composerPlanBtn").click();
  assert.equal(ui.query("#composerPlanBtn").getAttribute("aria-pressed"), "true");
  assert.equal(ui.query("#composerQaBtn").getAttribute("aria-pressed"), "false");
  assert.equal(ui.query("#composerModeLabel").textContent, "计划");
  assert.equal(ui.query("#composerModeAnnotation").hidden, false);
  assert.equal(ui.query("#composerModeAnnotation").parentElement.classList.contains("composer__toolbar"), true, "the active mode sits in the bottom composer toolbar");
  assert.ok(ui.query("#composerModeIcon").querySelector("svg"), "the selected mode includes its icon");
  assert.equal(ui.document.activeElement, ui.query("#terminalInput"), "selecting a mode returns focus to the primary dialog");
  assert.equal(ui.query("#composerAddMenu").hidden, true, "selection applies and closes the options menu immediately");
  assert.equal(ui.query("#composerModeClear").hidden, false);
  assert.match(ui.query("#terminalGateHint").textContent, /只读取信息/);

  ui.query("#composerQaBtn").click();
  assert.equal(ui.query("#composerPlanBtn").getAttribute("aria-pressed"), "false");
  assert.equal(ui.query("#composerQaBtn").getAttribute("aria-pressed"), "true");
  assert.equal(ui.query("#composerModeLabel").textContent, "仅问答");
  assert.equal(ui.document.activeElement, ui.query("#terminalInput"));
  assert.match(ui.query("#terminalGateHint").textContent, /只读工具/);

  ui.query("#terminalInput").value = "读取当前状态";
  ui.query("#terminalInput").dispatchEvent(new ui.document.defaultView.Event("input", { bubbles:true }));
  ui.query("#terminalSendBtn").click();
  const request = ui.messages.filter(message => message.type === "gateway-chat-request").at(-1);
  assert.equal(request.composerMode, "qa");
  assert.equal(request.planMode, false);
  assert.deepEqual(request.enabledTools, ["list_files","read_file","web_search","web_fetch"]);

  ui.receive({ type:"gateway-chat-started", requestId:request.requestId, prompt:"读取当前状态", cancellable:true });
  ui.receive({ type:"gateway-chat-response", requestId:request.requestId, success:true, response:"当前状态正常。" });
  assert.equal(ui.document.querySelectorAll(".turn__mode").item(1).textContent, "仅问答", "the sent turn keeps the selected mode annotation");
  assert.equal(ui.document.querySelectorAll(".turn__ask-text").item(1).textContent, "读取当前状态", "mode instructions do not leak into the visible user message");
});

test("selecting a target immediately annotates the composer toolbar and can be cleared", t => {
  const ui = page(t);
  ui.receive({ type:"ui-state", state:baseState({ activeConversationId:"c1" }) });
  ui.query('[data-page-pill="terminal"]').click();
  ui.query("#composerAddBtn").click();
  ui.query("#composerTargetBtn").click();
  assert.equal(ui.query("#composerModeLabel").textContent, "目标");
  assert.equal(ui.query("#composerModeAnnotation").hidden, false);
  assert.equal(ui.query("#composerModeAnnotation").parentElement.classList.contains("composer__toolbar"), true);
  assert.equal(ui.document.activeElement, ui.query("#terminalInput"), "the main conversation field stays primary after selecting a mode");
  assert.equal(ui.document.querySelector("#composerTargetInput"), null, "the goal is written directly in the main conversation field");
  assert.equal(ui.query("#terminalInput").placeholder, "描述你的目标，并设定可衡量的预期成果，以取得最佳效果");
  assert.equal(ui.query("#composerAddMenu").hidden, true);
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(saved.state.composer.mode, "goal", "selecting a mode is saved immediately");
  assert.equal(saved.state.composer.target, "", "stale target text is not carried into the selected mode");
  assert.equal(ui.query("#composerTargetBtn").getAttribute("aria-pressed"), "true");
  assert.equal(ui.query("#composerPlanBtn").getAttribute("aria-pressed"), "false");
  assert.equal(ui.query("#composerQaBtn").getAttribute("aria-pressed"), "false");
  ui.query("#composerModeClear").click();
  assert.equal(ui.query("#composerModeAnnotation").hidden, true, "clearing the selected mode returns to the unlabelled default");
  assert.equal(ui.document.activeElement, ui.query("#terminalInput"));
  assert.equal(ui.messages.filter(message => message.type === "save-state").at(-1).state.composer.target, "");
});

test("controller model requests keep the prompt clean and send the selected mode separately", t => {
  const ui = page(t);
  addWorkspace(ui);
  ui.receive({ type:"connected", side:"local", peer:"Remote computer", expiresAt, granted:["session","controller-model"] });
  ui.query("#composerAddBtn").click();
  ui.query("#composerQaBtn").click();
  ui.query("#terminalInput").value = "解释连接状态";
  ui.query("#terminalInput").dispatchEvent(new ui.document.defaultView.Event("input", { bubbles:true }));
  ui.query("#terminalSendBtn").click();

  const request = ui.messages.filter(message => message.type === "local-controller-model-request").at(-1);
  assert.equal(request.displayPrompt, "解释连接状态");
  assert.equal(request.composerMode, "qa");
  assert.match(request.prompt, /仅问答模式/);
  assert.equal(request.target, "");

  ui.receive({ type:"local-controller-model-started", requestId:"local-q1", prompt:request.displayPrompt, composerMode:request.composerMode });
  assert.equal(ui.document.querySelectorAll(".turn__ask-text").item(0).textContent, "解释连接状态");
  assert.equal(ui.document.querySelectorAll(".turn__mode").item(0).textContent, "仅问答");
});

test("terminal chat routes reverse requests to the controller model when connected as controlled side", t => {
  const ui = page(t);
  addWorkspace(ui);
  ui.receive({ type: "connected", side: "remote", peer: "Remote computer", expiresAt, granted: ["session", "controller-model"] });
  ui.query('[data-page-pill="terminal"]').click();
  ui.query("#terminalInput").value = "问问主控端";
  ui.query("#terminalInput").dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  ui.query("#terminalSendBtn").click();
  assert.deepEqual(ui.messages.at(-1), { type: "controller-model-request", prompt: "问问主控端", model:"", reasoningEffort:"high", workspaceRoot:"C:////Work////Alpha" });
  ui.receive({ type: "controller-model-started", requestId: "r9", prompt: "问问主控端" });
  ui.receive({ type: "controller-model-response", requestId: "r9", success: true, response: "答案" });
  assert.match(ui.query("#terminalThread").textContent, /答案/);
});

test("controller model catalog uses gateway-style menus, display names, and ephemeral selection", async t => {
  const ui = page(t);
  addWorkspace(ui);
  ui.receive({ type: "connected", side: "remote", peer: "Remote computer", expiresAt, granted: ["session", "controller-model"] });
  ui.query('[data-page-pill="terminal"]').click();
  ui.receive({
    type: "controller-model-catalog",
    success: true,
    defaultModel: "gpt-6.1-sol",
    defaultReasoningEffort: "medium",
    models: [
      { id: "gpt-6.1-sol", displayName: "GPT-6.1 Sol", reasoningEfforts: ["low", "medium", "high"], defaultReasoningEffort: "medium" },
      { id: "gpt-5.6-luna", displayName: "GPT-5.6 Luna", reasoningEfforts: ["low", "high"], defaultReasoningEffort: "low" }
    ]
  });
  ui.query("#modelChip").click();
  const modelEntry = [...ui.document.querySelectorAll("#convMenu .context-menu__item")].find(item => item.textContent.includes("GPT-5.6 Luna"));
  assert.ok(modelEntry);
  assert.doesNotMatch(modelEntry.textContent, /gpt-5\.6-luna/);
  modelEntry.click();
  assert.equal(ui.query("#modelChip").textContent, "GPT-5.6 Luna");
  ui.query("#effortChip").click();
  ui.query("#effortRange").value = "2";
  ui.query("#effortRange").dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  assert.match(ui.query("#effortChip").textContent, /高/);

  ui.query("#terminalInput").value = "使用选定模型";
  ui.query("#terminalInput").dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  ui.query("#terminalSendBtn").click();
  assert.deepEqual(ui.messages.at(-1), {
    type: "controller-model-request",
    prompt: "使用选定模型",
    model: "gpt-5.6-luna",
    reasoningEffort: "high",
    workspaceRoot: "C:////Work////Alpha"
  });
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(Object.prototype.hasOwnProperty.call(saved.state.settings, "controllerModel"), false);
  assert.equal(Object.prototype.hasOwnProperty.call(saved.state.settings, "controllerEffort"), false);
});

test("relay fingerprint mismatch asks before allowing a connection", t => {
  const ui = page(t);
  ui.receive({
    type: "relay-fingerprint-warning",
    requestId: "fingerprint-1",
    relay: relayBaseUrl,
    fingerprint: "A".repeat(64),
    previousFingerprint: "B".repeat(64),
    mismatch: true
  });
  assert.equal(ui.query("#relayFingerprintScrim").hidden, false);
  assert.match(ui.query("#relayFingerprintText").textContent, /SHA-256/);
  ui.query("#relayFingerprintAllowBtn").click();
  assert.deepEqual(ui.messages.at(-1), { type: "relay-fingerprint-response", requestId: "fingerprint-1", allow: true });
  assert.equal(ui.query("#relayFingerprintScrim").hidden, true);
});

test("terminal chat explains when no reply channel is available", t => {
  const ui = page(t);
  addWorkspace(ui);
  ui.query('[data-page-pill="terminal"]').click();
  ui.query("#terminalInput").value = "hi";
  ui.query("#terminalInput").dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  ui.query("#terminalSendBtn").click();
  assert.equal(ui.messages.some(message => message.type === "local-controller-model-request"), false);
  assert.match(ui.query("#terminalThread").textContent, /没有可用的回复通道/);
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
  assert.equal(ui.query("#appVersion").textContent, "10.27.123456");
  assert.equal(ui.query("#appVersion").title, "20261027-123456");
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
      fixedPassword: "long fixed password", rotationMinutes: 30, relay: relayBaseUrl, expectedFingerprint: "",
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
  addWorkspace(ui);
  ui.query("#selectWorkspaceBtn").click();
  ui.query("#installToolBtn").click();
  assert.deepEqual(ui.messages.slice(-2), [{ type: "sync-remote-workspace", path: "C:////Work////Alpha" }, { type: "install-tool" }]);
  ui.receive({ type: "authorization-ready", userName: "remote-user", password: "random test password", expiresAt, credentialMode: "random" });
  ui.query('[data-copy="id"]').click();
  assert.deepEqual(ui.messages.at(-1), { type: "copy", text: "remote-user" });
  ui.query('[data-copy="secret"]').click();
  assert.deepEqual(ui.messages.at(-1), { type: "copy", text: "random test password" });
});

test("workspaces start empty and must be created and trusted", t => {
  const ui = page(t);
  assert.match(ui.query("#workspaceList").textContent, /还没有工作区/);
  assert.equal(ui.document.querySelectorAll("[data-workspace]").length, 0);
  ui.query("#addWorkspaceBtn").click();
  assert.deepEqual(ui.messages.at(-1), { type: "select-workspace-folder" });
  ui.receive({ type: "workspace-folder", path: "C:////Work////Alpha" });
  assert.equal(ui.query("#trustScrim").hidden, false);
  assert.equal(ui.query("#trustWorkspaceName").value, "Alpha");
  ui.query("#trustWorkspaceBtn").click();
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  const created = saved.state.workspaces.find(workspace => workspace.name === "Alpha");
  assert.ok(created && created.trusted === true);
  assert.equal(created.path, "C:////Work////Alpha");
  assert.match(ui.query("#sessionChip").textContent, /Alpha/);
});

test("remote workspace follows trusted terminal selection and clears when it is removed", t => {
  const ui = page(t);
  addWorkspace(ui, "Alpha", "C:\\Work\\Alpha");
  addWorkspace(ui, "Beta", "C:\\Work\\Beta");
  assert.deepEqual(ui.messages.filter(message => message.type === "sync-remote-workspace").at(-1),
    { type: "sync-remote-workspace", path: "C:\\Work\\Beta" });
  const alpha = [...ui.document.querySelectorAll("[data-workspace-select]")].find(node => node.textContent.includes("Alpha"));
  alpha.click();
  assert.deepEqual(ui.messages.filter(message => message.type === "sync-remote-workspace").at(-1),
    { type: "sync-remote-workspace", path: "C:\\Work\\Alpha" });
  ui.receive({ type: "ui-state", state: { workspaces: [], conversations: [], activeWorkspaceId: null, activeConversationId: null, draftWorkspaceId: null } });
  assert.deepEqual(ui.messages.filter(message => message.type === "sync-remote-workspace").at(-1),
    { type: "sync-remote-workspace", path: "" });
});

test("home cards, settings and page title expose the revised layout", t => {
  const ui = page(t);
  assert.equal(ui.query("#pageBrand").textContent, "Remote CLI");
  assert.equal(ui.query("#remoteHomeCard").open, true);
  assert.equal(ui.query("#localHomeCard").open, true);
  ui.query("#localHomeCard summary").click();
  assert.equal(ui.query("#localHomeCard").open, false);
  assert.ok(ui.query("#homeWorkspaceList"));
  assert.ok(ui.query("#homeAddWorkspaceBtn"));
  ui.query('[data-page-pill="terminal"]').click();
  assert.equal(ui.query("#pageBrand").textContent, "Remote CLI");
  assert.ok(ui.query('#codexCliSettings').closest('[data-pane="connection"]'));
  assert.ok(ui.query('#discoverModelsBtn').closest('.settings-card__header'));
});

test("sidebar controls and effort marks fit at desktop viewport size", async t => {
  const { chromium } = require("playwright-core");
  const { pathToFileURL } = require("node:url");
  const browser = await chromium.launch({ executablePath: process.env.REMOTECLI_TEST_BROWSER || "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", headless: true });
  t.after(async () => browser.close());
  const page = await browser.newPage({ viewport: { width: 1198, height: 668 } });
  await page.goto(pathToFileURL(path.join(repo, "src/RemoteCli.Desktop/UI/longshell-ui.html")).href);
  const result = await page.evaluate(() => {
    const title = document.querySelector("#sessionListTitle").getBoundingClientRect();
    const buttons = [...document.querySelectorAll(".side-section-head .iconbtn")].map(el => el.getBoundingClientRect());
    const header = document.querySelector(".side-section-head").getBoundingClientRect();
    const sendBorder = getComputedStyle(document.querySelector("#terminalSendBtn")).borderWidth;
    const marks = document.querySelector(".effort-slider__marks");
    return { titleY: title.y + title.height / 2, buttons: buttons.map(r => ({ x:r.x, right:r.right, centerY:r.y+r.height/2 })), headerRight: header.right, sendBorder, marks: marks.children.length };
  });
  assert.equal(result.buttons.length, 2);
  assert.ok(result.buttons.every(r => Math.abs(r.centerY - result.titleY) < 2 && r.right <= result.headerRight));
  assert.ok(result.buttons[0].x < result.buttons[1].x);
  assert.equal(result.sendBorder, "0px");
  assert.equal(result.marks, 5);
  const widthBefore = await page.locator(".main").evaluate(el => el.getBoundingClientRect().width);
  await page.click("#sidebarToggle");
  await page.waitForTimeout(300);
  assert.equal(await page.locator("#sidebarToggle").getAttribute("aria-expanded"), "false");
  assert.ok(await page.locator(".main").evaluate(el => el.getBoundingClientRect().width) > widthBefore + 100);
  await page.click("#sidebarToggle");
  assert.equal(await page.locator("#sidebarToggle").getAttribute("aria-expanded"), "true");
});

test("conversation context menu pins, renames, archives, forks, and deletes", t => {
  const ui = page(t);
  ui.receive({
    type: "ui-state",
    state: {
      workspaces: [{ id: "ws-chat", name: "Chat", path: "", trusted: true, builtin: true }],
      conversations: [{
        id: "c1", workspaceId: "ws-chat", title: "旧标题", pinned: false, unread: false, archived: false,
        createdAt: Date.now(), updatedAt: Date.now(),
        messages: [{ role: "user", who: "你", text: "你好", at: Date.now() }]
      }],
      activeWorkspaceId: "ws-chat", activeConversationId: null, draftWorkspaceId: "ws-chat",
      view: { archiveFilter: "hide", grouping: "workspace" },
      settings: { relay: relayBaseUrl, replySource: "master", gatewayUrl: "", gatewayApiKey: "", gatewayModel: "" }
    }
  });
  const openMenu = id => ui.query('[data-conversation="' + id + '"]')
    .dispatchEvent(new ui.document.defaultView.MouseEvent("contextmenu", { bubbles: true, clientX: 120, clientY: 120 }));

  openMenu("c1");
  assert.equal(ui.query("#convMenu").classList.contains("open"), true);
  const actions = [...ui.query("#convMenu").querySelectorAll("[data-action]")].map(node => node.dataset.action);
  assert.deepEqual(actions, ["open-in", "pin", "unread", "rename", "fork", "archive", "delete"]);

  ui.query('#convMenu [data-action="pin"]').click();
  let saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(saved.state.conversations[0].pinned, true);

  openMenu("c1");
  ui.query('#convMenu [data-action="rename"]').click();
  assert.equal(ui.query("#renameScrim").hidden, false);
  ui.query("#renameInput").value = "新标题";
  ui.query("#renameSaveBtn").click();
  saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(saved.state.conversations[0].title, "新标题");

  // 归档后默认筛选隐藏，且归档会话不可打开
  openMenu("c1");
  ui.query('#convMenu [data-action="archive"]').click();
  saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(saved.state.conversations[0].archived, true);
  assert.equal(ui.document.querySelectorAll('[data-conversation="c1"]').length, 0);

  ui.query("#accountBtn").click();
  ui.query('[data-settings-pane="archive"]').click();
  assert.match(ui.query("#archivedConversations").textContent, /新标题/);
  ui.query('#archivedConversations button').click();
  ui.query("#settingsCloseBtn").click();
  saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(saved.state.conversations[0].archived, false);

  openMenu("c1");
  ui.query('#convMenu [data-action="fork"]').click();
  saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(saved.state.conversations.length, 2);
  const forkId = saved.state.activeConversationId;
  assert.match(saved.state.conversations[0].title, /分叉/);

  openMenu(forkId);
  ui.query('#convMenu [data-action="delete"]').click();
  assert.equal(ui.query("#confirmScrim").hidden, false);
  ui.query("#confirmOkBtn").click();
  assert.ok(ui.messages.some(message => message.type === "gateway-session-close" && message.conversationId === forkId));
  saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(saved.state.conversations.length, 1);
});

test("terminal cannot send without a trusted workspace", t => {
  const ui = page(t);
  ui.receive({
    type: "ui-state",
    state: {
      workspaces: [{ id: "ws-raw", name: "Raw", path: "C:////Work////Raw", trusted: false, builtin: false }],
      conversations: [], activeWorkspaceId: "ws-raw", activeConversationId: null, draftWorkspaceId: "ws-raw",
      settings: { relay: relayBaseUrl, replySource: "master", gatewayUrl: "", gatewayApiKey: "", gatewayModel: "" }
    }
  });
  ui.query('[data-page-pill="terminal"]').click();
  ui.query("#terminalInput").value = "hi";
  ui.query("#terminalInput").dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  assert.equal(ui.query("#terminalSendBtn").disabled, true);
  assert.match(ui.query("#sessionChip").textContent, /Raw/);
  assert.match(ui.query("#terminalGateHint").textContent, /未信任/);
});

function baseState(extra = {}) {
  return Object.assign({
    workspaces: [{ id: "ws-a", name: "Alpha", path: "C://Work//Alpha", trusted: true, builtin: false }],
    conversations: [{
      id: "c1", workspaceId: "ws-a", title: "服务器问题", pinned: false, unread: false, archived: false,
      order: 1, createdAt: 1, updatedAt: 1,
      messages: [{ role: "user", who: "你", text: "ssh 登录失败，帮我看看", at: 1 }]
    }],
    activeWorkspaceId: "ws-a", activeConversationId: null, draftWorkspaceId: "ws-a",
    view: { archiveFilter: "hide", grouping: "workspace", sort: "recent" },
    settings: { relay: relayBaseUrl, replySource: "master", gatewayUrl: "", gatewayApiKey: "", gatewayModel: "" }
  }, extra);
}

test("workspace tree groups folders by parent path and collapses", t => {
  const ui = page(t);
  ui.receive({
    type: "ui-state",
    state: baseState({
      workspaces: [
        { id: "ws-a", name: "Alpha", path: "C://Work//Alpha", trusted: true },
        { id: "ws-b", name: "Beta", path: "C://Work//Beta", trusted: true }
      ],
      conversations: [
        { id: "c1", workspaceId: "ws-a", title: "会话A", messages: [{ role: "user", text: "a", at: 1 }], createdAt: 1, updatedAt: 1 },
        { id: "c2", workspaceId: "ws-b", title: "会话B", messages: [{ role: "user", text: "b", at: 1 }], createdAt: 1, updatedAt: 1 }
      ]
    })
  });
  ui.query("#sessionViewBtn").click();
  [...ui.query("#convMenu").querySelectorAll(".context-menu__item")]
    .find(node => node.textContent.includes("按工作区树")).click();
  assert.match(ui.query("#workspaceList").textContent, /Work/);
  assert.match(ui.query("#workspaceList").textContent, /Alpha/);
  assert.match(ui.query("#workspaceList").textContent, /Beta/);
  assert.equal(ui.query(".tree-group__content").hidden, true, "tree folders start collapsed");
  ui.query(".tree-path").click();
  assert.equal(ui.query(".tree-group__content").hidden, false, "expanding the folder shows its workspaces");
  let saved = ui.messages.filter(message => message.type === "save-state").at(-1).state;
  assert.equal(saved.collapsed["C:/Work"], false, "the expanded folder is persisted as an explicit choice");
  ui.query(".tree-path").click();
  assert.equal(ui.query(".tree-group__content").hidden, true, "collapsing the folder hides its workspaces");
  saved = ui.messages.filter(message => message.type === "save-state").at(-1).state;
  assert.equal(saved.collapsed["C:/Work"], true, "the collapsed folder survives a redraw");
});

test("manual sort moves a session and persists order", t => {
  const ui = page(t);
  ui.receive({
    type: "ui-state",
    state: baseState({
      conversations: [
        { id: "c1", workspaceId: "ws-a", title: "第一条", order: 1, messages: [{ role: "user", text: "a", at: 1 }], createdAt: 1, updatedAt: 1 },
        { id: "c2", workspaceId: "ws-a", title: "第二条", order: 2, messages: [{ role: "user", text: "b", at: 2 }], createdAt: 2, updatedAt: 2 }
      ]
    })
  });
  ui.query("#sessionViewBtn").click();
  [...ui.query("#convMenu").querySelectorAll(".context-menu__item")]
    .find(node => node.textContent.includes("手动排序")).click();
  ui.query('[data-conversation="c2"]').dispatchEvent(
    new ui.document.defaultView.MouseEvent("contextmenu", { bubbles: true, clientX: 100, clientY: 100 }));
  const actions = [...ui.query("#convMenu").querySelectorAll("[data-action]")].map(node => node.dataset.action);
  assert.ok(actions.includes("move-up"));
  ui.query('#convMenu [data-action="move-up"]').click();
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  const first = saved.state.conversations.find(item => item.id === "c1");
  const second = saved.state.conversations.find(item => item.id === "c2");
  assert.ok(second.order < first.order);
});

test("session search matches message content", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState() });
  ui.query("#sessionSearchBtn").click();
  const input = ui.query("#sessionSearchInput");
  input.value = "ssh";
  input.dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  assert.match(ui.query("#workspaceList").textContent, /服务器问题/);
  input.value = "zzz";
  input.dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  assert.match(ui.query("#workspaceList").textContent, /无匹配会话/);
});

test("open in system app posts the workspace folder", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState() });
  ui.query('[data-conversation="c1"]').dispatchEvent(
    new ui.document.defaultView.MouseEvent("contextmenu", { bubbles: true, clientX: 100, clientY: 100 }));
  ui.query('#convMenu [data-action="open-in"]').click();
  ui.query('#convMenu [data-action="open-explorer"]').click();
  assert.deepEqual(ui.messages.at(-1), { type: "open-workspace", path: "C://Work//Alpha", mode: "explorer" });
});

test("archiving a running Codex request waits for completion", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.receive({ type: "local-controller-model-started", requestId: "r1", prompt: "ssh 登录失败，帮我看看", cancellable: false });
  ui.query('[data-conversation="c1"]').dispatchEvent(
    new ui.document.defaultView.MouseEvent("contextmenu", { bubbles: true, clientX: 100, clientY: 100 }));
  ui.query('#convMenu [data-action="archive"]').click();
  assert.equal(ui.query("#confirmScrim").hidden, false);
  assert.match(ui.query("#confirmText").textContent, /无法中途取消/);
  ui.query("#confirmOkBtn").click();
  let saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(saved.state.conversations[0].archived, false, "cannot cancel: must wait");
  assert.equal(saved.state.conversations[0].archiveWhenDone, true);

  ui.receive({ type: "local-controller-model-response", requestId: "r1", success: true, response: "looks fine" });
  saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(saved.state.conversations[0].archived, true);
});

test("archiving a running gateway request cancels it immediately", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.receive({ type: "gateway-chat-started", requestId: "g1", prompt: "ssh 登录失败，帮我看看", cancellable: true });
  ui.query('[data-conversation="c1"]').dispatchEvent(
    new ui.document.defaultView.MouseEvent("contextmenu", { bubbles: true, clientX: 100, clientY: 100 }));
  ui.query('#convMenu [data-action="archive"]').click();
  assert.match(ui.query("#confirmText").textContent, /强制取消/);
  ui.query("#confirmOkBtn").click();
  assert.deepEqual(ui.messages.filter(message => message.type === "cancel-model-request").at(-1),
    { type: "cancel-model-request", requestId: "g1" });
  assert.ok(ui.messages.some(message => message.type === "gateway-session-close" && message.conversationId === "c1"));
  let saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(saved.state.conversations[0].archived, true);
  ui.receive({ type: "model-canceled", requestId: "g1" });
  saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.ok(saved.state.conversations[0].messages.some(message => message.text === "已取消。"));
});

test("settings window switches panes and exposes model controls", t => {
  const ui = page(t);
  ui.query("#accountBtn").click();
  assert.ok(ui.query("#settingsWindow"));
  assert.equal(ui.query("#settingsScrim").hidden, false);
  assert.equal(ui.query("#relayFingerprintScrim").hidden, true);
  assert.equal(ui.document.querySelector("#relayFingerprint"), null, "manual fingerprint override is removed");
  assert.equal(ui.query('[data-pane="connection"]').hidden, false);
  ui.query('[data-settings-pane="models"]').click();
  assert.equal(ui.query('[data-pane="models"]').hidden, false);
  assert.equal(ui.query('[data-pane="connection"]').hidden, true);
  assert.equal(ui.query('[data-settings-pane="models"]').getAttribute("aria-selected"), "true");
  ui.query("#addModelBtn").click();
  assert.equal(ui.document.querySelectorAll("#modelList .model-row").length, 1);
  ui.query('[data-settings-pane="workspaces"]').click();
  assert.equal(ui.query('[data-pane="workspaces"]').hidden, false);
  ui.query("#settingsCloseBtn").click();
  assert.equal(ui.query("#settingsScrim").hidden, true);
});

test("settings search matches pane content, selects results, and clears with Escape", t => {
  const ui = page(t);
  ui.query("#accountBtn").click();
  const search = ui.query("#settingsSearchInput");
  search.value = "中继地址";
  search.dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  assert.equal(ui.query('[data-settings-pane="connection"]').hidden, false);
  assert.equal(ui.query('[data-settings-pane="models"]').hidden, true);
  assert.equal(ui.query('[data-pane="connection"]').hidden, false);
  assert.match(ui.query("#settingsSearchStatus").textContent, /找到 1 个设置分组/);

  search.value = "完全不存在的设置";
  search.dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  assert.equal(ui.query("#settingsSearchStatus").textContent, "未找到匹配的设置");
  assert.equal(ui.query('[data-pane="connection"]').hidden, true);

  search.dispatchEvent(new ui.document.defaultView.KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
  assert.equal(search.value, "");
  assert.equal(ui.query("#settingsSearchStatus").hidden, true);
  assert.equal(ui.query('[data-pane="connection"]').hidden, false);
});

test("file panel lists workspace entries and previews text", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.query("#filesToggle").click();
  assert.equal(ui.query("#filesPanel").hidden, false);
  const listRequest = ui.messages.at(-1);
  assert.deepEqual(listRequest, { type: "list-workspace-files", path: "C://Work//Alpha", root: "C://Work//Alpha", requestId: "1" });

  ui.receive({
    type: "workspace-files",
    path: "C://Work//Alpha", requestId: listRequest.requestId,
    entries: [
      { name: "a.txt", path: "C://Work//Alpha//a.txt", isDirectory: false, size: 12 },
      { name: "src", path: "C://Work//Alpha//src", isDirectory: true, size: 0 }
    ]
  });
  const rows = [...ui.query("#filesList").querySelectorAll(".file-row")];
  assert.equal(rows.length, 2);
  rows[0].click();
  assert.deepEqual(ui.messages.at(-1),
    { type: "read-workspace-file", path: "C://Work//Alpha//a.txt", root: "C://Work//Alpha", requestId: "2" });

  ui.receive({ type: "workspace-file", path: "C://Work//Alpha//a.txt", kind: "text", content: "hello world" });
  assert.equal(ui.query("#filesPreview").hidden, false);
  assert.equal(ui.query("#filesPreviewText").textContent, "hello world");
  assert.equal(ui.query("#filesBackBtn").hidden, false);
  ui.query("#filesBackBtn").click();
  assert.equal(ui.query("#filesList").hidden, false);
  assert.equal(ui.query("#filesPreview").hidden, true);

  ui.query("#filesCloseBtn").click();
  assert.equal(ui.query("#filesPanel").hidden, true);
});

test("file rows expose open-with context menus and the folder button opens Explorer", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.query("#filesToggle").click();
  const listRequest = ui.messages.at(-1);
  ui.receive({
    type: "workspace-files",
    path: "C://Work//Alpha", requestId: listRequest.requestId,
    entries: [
      { name: "src", path: "C://Work//Alpha//src", isDirectory: true, size: 0 },
      { name: "a.txt", path: "C://Work//Alpha//a.txt", isDirectory: false, size: 12 }
    ]
  });
  const rows = [...ui.query("#filesList").querySelectorAll(".file-row")];
  assert.equal(ui.query("#filesRevealBtn").hidden, false);

  const menu = ui.query("#convMenu");
  const items = () => [...menu.querySelectorAll(".context-menu__item")];
  const labels = () => items().map(item => item.textContent);
  const pick = label => {
    const item = items().find(candidate => candidate.textContent.startsWith(label));
    assert.ok(item, `missing menu item: ${label} (${labels().join(" / ")})`);
    item.click();
  };
  const rightClick = element => element.dispatchEvent(new ui.document.defaultView.MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: 160, clientY: 120 }));

  assert.equal(rightClick(rows[0]), false, "the browser context menu must be suppressed");
  assert.equal(menu.classList.contains("open"), true);
  assert.ok(labels().some(label => label.startsWith("打开目录")));
  assert.ok(labels().some(label => label.startsWith("打开方式")));
  pick("打开方式");
  assert.deepEqual(labels(), ["文件资源管理器", "终端", "Visual Studio Code"]);
  ui.messages.length = 0;
  pick("文件资源管理器");
  assert.deepEqual(ui.messages.at(-1), { type:"open-path", path:"C://Work//Alpha//src", root:"C://Work//Alpha", mode:"explorer" });

  rightClick(rows[1]);
  assert.ok(labels().some(label => label.startsWith("在文件资源管理器中显示")));
  pick("打开方式");
  assert.deepEqual(labels(), ["默认程序", "Visual Studio Code", "记事本", "选择其他应用…"]);
  ui.messages.length = 0;
  pick("默认程序");
  assert.deepEqual(ui.messages.at(-1), { type:"open-path", path:"C://Work//Alpha//a.txt", root:"C://Work//Alpha", mode:"default" });

  ui.messages.length = 0;
  ui.query("#filesRevealBtn").click();
  assert.deepEqual(ui.messages.at(-1), { type:"open-path", path:"C://Work//Alpha", root:"C://Work//Alpha", mode:"explorer" });
  rows[1].click();
  ui.messages.length = 0;
  ui.query("#filesRevealBtn").click();
  assert.deepEqual(ui.messages.at(-1), { type:"open-path", path:"C://Work//Alpha//a.txt", root:"C://Work//Alpha", mode:"reveal" });
});

test("open-with menus read shell icons once and hide missing programs", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });

  const prefetch = ui.messages.filter(message => message.type === "path-icons").at(-1);
  assert.deepEqual(prefetch.items.map(item => item.key), ["app:explorer", "app:terminal", "app:code", "app:notepad"]);
  ui.receive({ type: "path-icons", requestId: prefetch.requestId, icons: {
    "app:explorer": "data:image/png;base64,AAA",
    "app:terminal": "data:image/png;base64,BBB",
    "app:code": null,
    "app:notepad": "data:image/png;base64,CCC",
    "app:openas": "data:image/png;base64,DDD"
  } });

  ui.query("#filesToggle").click();
  const listRequest = ui.messages.filter(message => message.type === "list-workspace-files").at(-1);
  ui.receive({
    type: "workspace-files",
    path: "C://Work//Alpha", requestId: listRequest.requestId,
    entries: [
      { name: "src", path: "C://Work//Alpha//src", isDirectory: true, size: 0 },
      { name: "a.txt", path: "C://Work//Alpha//a.txt", isDirectory: false, size: 12 }
    ]
  });
  const rows = [...ui.query("#filesList").querySelectorAll(".file-row")];
  const menu = ui.query("#convMenu");
  const items = () => [...menu.querySelectorAll(".context-menu__item")];
  const labels = () => items().map(item => item.textContent);
  const pick = label => {
    const item = items().find(candidate => candidate.textContent.startsWith(label));
    assert.ok(item, `missing menu item: ${label} (${labels().join(" / ")})`);
    item.click();
  };
  const rightClick = element => element.dispatchEvent(new ui.document.defaultView.MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: 160, clientY: 120 }));

  rightClick(rows[0]);
  pick("打开方式");
  assert.deepEqual(labels(), ["文件资源管理器", "终端"], "VS Code is hidden when the host reports no icon for it");
  const directoryIcons = [...menu.querySelectorAll(".context-menu__icon")];
  assert.deepEqual(directoryIcons.map(icon => icon.dataset.iconKey), ["app:explorer", "app:terminal"]);
  assert.equal(directoryIcons[0].src, "data:image/png;base64,AAA");
  assert.equal(directoryIcons[0].hidden, false);
  assert.equal(ui.messages.filter(message => message.type === "path-icons").length, 1, "cached program icons are not requested again");

  rightClick(rows[1]);
  pick("打开方式");
  assert.deepEqual(labels(), ["默认程序", "记事本", "选择其他应用…"]);
  const fileRequest = ui.messages.filter(message => message.type === "path-icons").at(-1);
  assert.deepEqual(fileRequest.items, [{ key:"file:c://work//alpha//a.txt", kind:"path", value:"C://Work//Alpha//a.txt" }]);
  ui.receive({ type: "path-icons", requestId: fileRequest.requestId, icons: { "file:c://work//alpha//a.txt": "data:image/png;base64,EEE" } });
  assert.equal(menu.querySelector(".context-menu__icon[data-icon-key='file:c://work//alpha//a.txt']").src, "data:image/png;base64,EEE");
});

test("presented deliverables render as cards and open the right-side preview", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({
    activeConversationId: "c1",
    conversations: [{
      id: "c1", workspaceId: "ws-a", title: "生成报告", pinned: false, unread: false, archived: false, order: 1,
      createdAt: 1, updatedAt: 3,
      messages: [
        { role: "user", who: "你", text: "生成报告", at: 1 },
        { role: "agent", who: "Remote CLI", text: "报告已经生成。", at: 2, deliverables: [
          { path: "C://Work//Alpha//report.md", description: "本次评估报告" },
          { path: "C://Work//Alpha//sheet.xlsx" }
        ] }
      ]
    }]
  }) });
  ui.query('[data-page-pill="terminal"]').click();

  const cards = [...ui.query("#terminalThread").querySelectorAll(".deliverable")];
  assert.equal(cards.length, 2);
  assert.equal(cards[0].querySelector(".deliverable__name").textContent, "report.md");
  assert.equal(cards[0].querySelector(".deliverable__desc").textContent, "本次评估报告");
  assert.equal(cards[1].querySelector(".deliverable__name").textContent, "sheet.xlsx");
  assert.equal(cards[1].querySelector(".deliverable__desc").textContent, "XLSX 文件");
  assert.equal(cards[0].title, "C://Work//Alpha//report.md");

  cards[0].click();
  const preview = ui.messages.filter(message => message.type === "read-workspace-file").at(-1);
  assert.equal(preview.path, "C://Work//Alpha//report.md");
  assert.equal(ui.query("#filesPreview").hidden, false);
  assert.equal(ui.query("#filesPanel").hidden, false);
});

test("deliverables that arrive mid-request attach to the finished reply", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({
    activeConversationId: "c1",
    settings: {
      relay: relayBaseUrl, replySource: "gateway", gatewayUrl: "https://gw.example.com/v1", gatewayApiKey: "test-key",
      backend: "openai", models: ["test-model"], defaultModel: "test-model",
      globalTools: ["list_files", "read_file", "web_search", "present"], tools: ["list_files", "read_file", "web_search", "present"]
    }
  }) });
  ui.query('[data-page-pill="terminal"]').click();
  ui.query("#terminalInput").value = "生成报告";
  ui.query("#terminalInput").dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  ui.query("#terminalSendBtn").click();
  const request = ui.messages.filter(message => message.type === "gateway-chat-request").at(-1);
  assert.ok(request, "the terminal must send a gateway request");
  assert.ok(request.enabledTools.includes("present"));

  ui.receive({ type: "gateway-chat-started", requestId: request.requestId, prompt: "生成报告", cancellable: true });
  ui.receive({ type: "deliverables", requestId: request.requestId, files: [
    { path: "C://Work//Alpha//report.md", description: "本次报告" }
  ] });
  assert.equal(ui.query("#terminalThread").querySelectorAll(".deliverable").length, 0, "cards wait for the reply");
  ui.receive({ type: "gateway-chat-response", requestId: request.requestId, success: true, response: "报告已生成。" });

  const cards = [...ui.query("#terminalThread").querySelectorAll(".deliverable")];
  assert.equal(cards.length, 1);
  assert.equal(cards[0].querySelector(".deliverable__name").textContent, "report.md");
  assert.equal(cards[0].querySelector(".deliverable__desc").textContent, "本次报告");
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  const stored = saved.state.conversations.find(conversation => conversation.id === "c1").messages
    .find(message => message.role === "agent" && Array.isArray(message.deliverables));
  assert.equal(stored.deliverables[0].path, "C://Work//Alpha//report.md");
});

test("choosing another app lists candidates, opens the file, and closes the picker", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.query("#filesToggle").click();
  const listRequest = ui.messages.filter(message => message.type === "list-workspace-files").at(-1);
  ui.receive({
    type: "workspace-files", path: "C://Work//Alpha", requestId: listRequest.requestId,
    entries: [{ name: "a.txt", path: "C://Work//Alpha//a.txt", isDirectory: false, size: 12 }]
  });
  const row = ui.query("#filesList .file-row");
  row.dispatchEvent(new ui.document.defaultView.MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: 160, clientY: 120 }));
  const menu = ui.query("#convMenu");
  const items = () => [...menu.querySelectorAll(".context-menu__item")];
  const pick = label => {
    const item = items().find(candidate => candidate.textContent.startsWith(label));
    assert.ok(item, `missing menu item: ${label}`);
    item.click();
  };
  pick("打开方式");
  pick("选择其他应用…");

  const openWithRequest = ui.messages.filter(message => message.type === "list-open-with").at(-1);
  assert.equal(openWithRequest.path, "C://Work//Alpha//a.txt");
  assert.equal(openWithRequest.root, "C://Work//Alpha");
  ui.receive({ type: "open-with-list", requestId: openWithRequest.requestId, path: "C://Work//Alpha//a.txt", apps: [
    { name: "Notepad++", path: "C:\\Apps\\notepad++.exe", isDefault: false },
    { name: "记事本", path: "C:\\Windows\\notepad.exe", isDefault: true }
  ] });
  assert.equal(ui.query("#openWithScrim").hidden, false);
  assert.equal(ui.query("#openWithPath").textContent, "C://Work//Alpha//a.txt");
  const candidates = [...ui.query("#openWithList").querySelectorAll(".open-with-item")];
  assert.equal(candidates.length, 2);
  assert.match(candidates[1].querySelector(".open-with-item__name").textContent, /记事本默认/);
  assert.equal(candidates[0].querySelector(".open-with-item__path").textContent, "C:\\Apps\\notepad++.exe");

  ui.messages.length = 0;
  candidates[0].click();
  assert.deepEqual(ui.messages.at(-1), {
    type: "open-path", path: "C://Work//Alpha//a.txt", root: "C://Work//Alpha", mode: "app", app: "C:\\Apps\\notepad++.exe"
  });
  assert.equal(ui.query("#openWithScrim").hidden, true);
});

test("absolute backslash paths are not re-rooted under the workspace", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({
    activeConversationId: "c1",
    conversations: [{
      id: "c1", workspaceId: "ws-a", title: "路径", pinned: false, unread: false, archived: false, order: 1, createdAt: 1, updatedAt: 3,
      messages: [
        { role: "user", who: "你", text: "看下报告", at: 1 },
        { role: "agent", who: "Remote CLI", text: "报告在 `C:\\Work\\Alpha\\docs\\report.md` 里。", at: 2 }
      ]
    }]
  }) });
  ui.query('[data-page-pill="terminal"]').click();

  const link = ui.query("#terminalThread a.file-link");
  assert.equal(link.textContent, "C:\\Work\\Alpha\\docs\\report.md");
  assert.equal(link.title, "预览文件：C:\\Work\\Alpha\\docs\\report.md", "the resolved path must not repeat the workspace root");
  link.click();
  const read = ui.messages.filter(message => message.type === "read-workspace-file").at(-1);
  assert.equal(read.path, "C:\\Work\\Alpha\\docs\\report.md");
  assert.equal(ui.query("#filesPreviewName").textContent, "C:\\Work\\Alpha\\docs\\report.md");
});

test("previewing a directory opens the file list instead of a missing-file note", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({
    activeConversationId: "c1",
    conversations: [{
      id: "c1", workspaceId: "ws-a", title: "目录", pinned: false, unread: false, archived: false, order: 1, createdAt: 1, updatedAt: 3,
      messages: [
        { role: "user", who: "你", text: "看看这个目录", at: 1 },
        { role: "agent", who: "Remote CLI", text: "目录在这里。", at: 2, deliverables: [{ path: "C://Work//Alpha//src" }] }
      ]
    }]
  }) });
  ui.query('[data-page-pill="terminal"]').click();
  ui.query(".deliverable").click();
  const read = ui.messages.filter(message => message.type === "read-workspace-file").at(-1);
  assert.equal(read.path, "C://Work//Alpha//src");
  assert.equal(ui.query("#filesPreview").hidden, false);

  ui.receive({ type: "workspace-file", path: read.path, requestId: read.requestId, kind: "directory" });
  assert.equal(ui.query("#filesPreview").hidden, true);
  assert.equal(ui.query("#filesList").hidden, false);
  const listing = ui.messages.filter(message => message.type === "list-workspace-files").at(-1);
  assert.equal(listing.path, "C://Work//Alpha//src");
  assert.equal(ui.query("#filesTitle").textContent, "C://Work//Alpha//src");
});

test("opening a path from the reply lists its folder and highlights the file", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({
    activeConversationId: "c1",
    conversations: [{
      id: "c1", workspaceId: "ws-a", title: "报告", pinned: false, unread: false, archived: false, order: 1, createdAt: 1, updatedAt: 3,
      messages: [
        { role: "user", who: "你", text: "看报告", at: 1 },
        { role: "agent", who: "Remote CLI", text: "报告在这里。", at: 2, deliverables: [{ path: "C://Work//Alpha//report.md", description: "报告" }] }
      ]
    }]
  }) });
  ui.query('[data-page-pill="terminal"]').click();
  ui.query(".deliverable").click();

  assert.equal(ui.query("#filesList").hidden, false, "the folder list stays visible next to the preview");
  assert.equal(ui.query("#filesPanel").dataset.preview, "true");
  assert.equal(ui.query("#filesTitle").textContent, "report.md");

  const listRequest = ui.messages.filter(message => message.type === "list-workspace-files").at(-1);
  assert.equal(listRequest.path, "C:/Work/Alpha");
  ui.receive({
    type: "workspace-files", path: listRequest.path, root: listRequest.root, requestId: listRequest.requestId,
    entries: [
      { name: "other.md", path: "C://Work//Alpha//other.md", isDirectory: false, size: 10 },
      { name: "report.md", path: "C://Work//Alpha//report.md", isDirectory: false, size: 10 }
    ]
  });
  const rows = [...ui.query("#filesList").querySelectorAll(".file-row")];
  assert.equal(rows.length, 2);
  assert.equal(rows[1].classList.contains("is-current"), true, "the previewed file is marked in the list");
  assert.equal(rows[0].classList.contains("is-current"), false);
  assert.equal(ui.query("#filesTitle").textContent, "report.md", "loading the folder must not replace the file title");
});

test("unsupported files show a centered notice with open buttons", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.query("#filesToggle").click();
  const listing = ui.messages.filter(message => message.type === "list-workspace-files").at(-1);
  ui.receive({
    type: "workspace-files", path: listing.path, root: listing.root, requestId: listing.requestId,
    entries: [{ name: "data.bin", path: "C://Work//Alpha//data.bin", isDirectory: false, size: 10 }]
  });
  ui.query("#filesList .file-row").click();
  const read = ui.messages.filter(message => message.type === "read-workspace-file").at(-1);
  ui.receive({ type: "workspace-file", path: read.path, requestId: read.requestId, kind: "binary", size: 10 });

  assert.equal(ui.query("#filesPreviewUnsupported").hidden, false);
  assert.equal(ui.query("#filesPreviewUnsupportedText").textContent, "这个文件不支持在应用内预览");
  assert.equal(ui.query("#filesPreviewNote").hidden, true);

  ui.messages.length = 0;
  ui.query("#filesPreviewOpenBtn").click();
  assert.deepEqual(ui.messages.at(-1), { type: "open-path", path: "C://Work//Alpha//data.bin", root: "C://Work//Alpha", mode: "default" });

  ui.messages.length = 0;
  ui.query("#filesPreviewOpenWithBtn").click();
  const picker = ui.messages.at(-1);
  assert.equal(picker.type, "list-open-with");
  assert.equal(picker.path, "C://Work//Alpha//data.bin");
});

test("a failed open shows its notice inside the preview panel", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.receive({ type: "open-path-failed", path: "C://Work//Alpha//data.bin", message: "系统里没有登记可以打开这个文件的程序。" });

  assert.equal(ui.query("#filesPanel").hidden, false);
  assert.equal(ui.query("#filesPreview").hidden, false);
  assert.equal(ui.query("#filesTitle").textContent, "data.bin");
  assert.equal(ui.query("#filesPreviewName").textContent, "C://Work//Alpha//data.bin");
  assert.equal(ui.query("#filesPreviewUnsupported").hidden, false);
  assert.equal(ui.query("#filesPreviewUnsupportedText").textContent, "系统里没有登记可以打开这个文件的程序。");
});

test("delivered files stay clickable when the reply mentions them inline", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({
    activeConversationId: "c1",
    conversations: [{
      id: "c1", workspaceId: "ws-a", title: "打包", pinned: false, unread: false, archived: false, order: 1, createdAt: 1, updatedAt: 3,
      messages: [
        { role: "user", who: "你", text: "打包", at: 1 },
        { role: "agent", who: "Remote CLI", text: "打包结果在 `release.zip`，源码见 `src`。", at: 2,
          deliverables: [{ path: "C://Work//Alpha//release.zip", description: "发布包" }] }
      ]
    }]
  }) });
  ui.query('[data-page-pill="terminal"]').click();

  const links = [...ui.query("#terminalThread").querySelectorAll("a.file-link")];
  assert.equal(links.length, 1, "only the delivered file becomes a link");
  assert.equal(links[0].textContent, "release.zip");
  links[0].click();
  const read = ui.messages.filter(message => message.type === "read-workspace-file").at(-1);
  assert.equal(read.path, "C://Work//Alpha//release.zip");
});

test("right panel opens on the home page and previews Word and Excel safely", t => {
  const ui = page(t);
  ui.query("#filesPanelToggle").click();
  assert.equal(ui.query("#filesPanel").hidden, false);
  assert.equal(ui.query(".main").dataset.page, "home");
  assert.match(ui.query("#filesList").textContent, /选择并信任/);
  addWorkspace(ui);
  const listing = ui.messages.filter(m => m.type === "list-workspace-files").at(-1);
  ui.receive({type:"workspace-files",path:listing.path,requestId:listing.requestId,entries:[
    {name:"a.docx",path:listing.path + "/a.docx"}, {name:"a.xlsx",path:listing.path + "/a.xlsx"}
  ]});
  ui.query("#filesList .file-row").click();
  let request = ui.messages.at(-1);
  ui.receive({type:"workspace-file",path:request.path,requestId:request.requestId,kind:"word",document:{blocks:[
    {kind:"paragraph",text:"<img src=x onerror=alert(1)>"}, {kind:"table",rows:[["cell"]]}
  ]}});
  assert.match(ui.query("#filesPreviewDocument").textContent, /<img/);
  assert.equal(ui.query("#filesPreviewDocument").querySelector("img"), null);
  assert.match(ui.query("#filesPreviewDocument table").textContent, /cell/);
  ui.query("#filesBackBtn").click();
  ui.document.querySelectorAll("#filesList .file-row")[1].click();
  request = ui.messages.at(-1);
  ui.receive({type:"workspace-file",path:request.path,requestId:request.requestId,kind:"excel",workbook:{sheets:[
    {name:"一",rows:[{number:1,cells:["first"]}]}, {name:"二",rows:[{number:2,cells:["second"]}]}
  ]}});
  assert.match(ui.query("#filesPreviewDocument").textContent, /first/);
  ui.query("#filesPreviewSheet").value = "1";
  ui.query("#filesPreviewSheet").dispatchEvent(new ui.document.defaultView.Event("change"));
  assert.match(ui.query("#filesPreviewDocument").textContent, /second/);
  ui.query("#filesPanelToggle").click();
  assert.equal(ui.query("#filesPanel").hidden, true);
});

test("PDF preview loads the browser PDF viewer in the right panel", async t => {
  const { chromium } = require("playwright-core");
  const browser = await chromium.launch({ executablePath: process.env.REMOTECLI_TEST_BROWSER || "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", headless: true });
  t.after(async () => browser.close());
  const page = await browser.newPage({ viewport: { width: 1198, height: 668 } });
  await page.addInitScript(() => {
    const listeners = [];
    window.testMessages = [];
    window.testReceive = data => listeners.forEach(listener => listener({ data }));
    window.chrome = { webview: { postMessage(message) { window.testMessages.push(message); }, addEventListener(type, listener) { if(type === "message") listeners.push(listener); } } };
  });
  await page.route("https://longshell.test/", route => route.fulfill({ status:200, contentType:"text/html", body:html }));
  await page.goto("https://longshell.test/");
  await page.evaluate(state => window.testReceive({type:"ui-state", state}), baseState({activeConversationId:"c1"}));
  await page.click("#filesPanelToggle");
  const beforeWidth = await page.locator("#filesPanel").evaluate(el => el.getBoundingClientRect().width);
  const handle = await page.locator("#filesSplit").boundingBox();
  await page.mouse.move(handle.x + handle.width / 2, handle.y + handle.height / 2);
  await page.mouse.down(); await page.mouse.move(handle.x - 80, handle.y + handle.height / 2, { steps: 6 }); await page.mouse.up();
  assert.ok(await page.locator("#filesPanel").evaluate(el => el.getBoundingClientRect().width) > beforeWidth + 50);
  const list = await page.evaluate(() => window.testMessages.filter(m => m.type === "list-workspace-files").at(-1));
  await page.evaluate(m => window.testReceive(m), {type:"workspace-files",path:list.path,requestId:list.requestId,entries:[{name:"test.pdf",path:list.path + "/test.pdf"}]});
  await page.click("#filesList .file-row");
  const read = await page.evaluate(() => window.testMessages.filter(m => m.type === "read-workspace-file").at(-1));
  let pdf = "%PDF-1.4\n", offsets = [0];
  const text = "BT /F1 24 Tf 40 100 Td (PDF Preview) Tj ET\n";
  const objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", `<< /Length ${text.length} >>\nstream\n${text}endstream`];
  for(let i=0;i<objects.length;i++){ offsets.push(Buffer.byteLength(pdf)); pdf += `${i+1} 0 obj\n${objects[i]}\nendobj\n`; }
  const xref = Buffer.byteLength(pdf); pdf += "xref\n0 6\n0000000000 65535 f \n";
  for(let i=1;i<=5;i++) pdf += `${String(offsets[i]).padStart(10,"0")} 00000 n \n`;
  pdf += `trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  await page.evaluate(m => window.testReceive(m), {type:"workspace-file",path:read.path,requestId:read.requestId,kind:"pdf",content:Buffer.from(pdf).toString("base64")});
  await page.waitForFunction(() => document.querySelector("#filesPreviewPdf").src.startsWith("blob:"));
  await page.waitForTimeout(1000);
  assert.ok(page.frames().some(frame => frame.url().startsWith("blob:")), "PDF viewer should navigate to the local PDF blob");
  assert.ok(!page.frames().some(frame => frame.url().startsWith("chrome-error:")), "PDF viewer should not be blocked by iframe sandboxing");
  await page.click("#filesBackBtn");
  assert.equal(await page.locator("#filesPreviewPdf").getAttribute("src"), null);
  await page.click("#filesList .file-row");
  const office = await page.evaluate(() => window.testMessages.filter(m => m.type === "read-workspace-file").at(-1));
  await page.evaluate(m => window.testReceive(m), { type:"workspace-file", path:office.path, requestId:office.requestId, kind:"office", content:Buffer.from(pdf).toString("base64"), workbook:{sheets:[{name:"Sheet", rows:[{number:1,cells:["Cell"]}]}]} });
  assert.equal(await page.locator("#filesPreviewModes").isVisible(), true);
  await page.click("#filesPreviewData");
  assert.match(await page.locator("#filesPreviewDocument").innerText(), /Cell/);
  await page.click("#filesPreviewLayout");
  assert.equal(await page.locator("#filesPreviewPdf").isVisible(), true);
});

test("draft file preview ignores stale list and file responses", t => {
  const ui = page(t);
  addWorkspace(ui);
  ui.query("#filesToggle").click();
  const first = ui.messages.at(-1);
  assert.equal(first.type, "list-workspace-files");
  ui.query("#filesRefreshBtn").click();
  const second = ui.messages.at(-1);
  ui.receive({ type:"workspace-files", path:first.path, requestId:first.requestId, entries:[{ name:"old.txt", path:first.path + "/old.txt" }] });
  assert.equal(ui.document.querySelectorAll("#filesList .file-row").length, 0);
  ui.receive({ type:"workspace-files", path:second.path, requestId:second.requestId, entries:[{ name:"new.txt", path:second.path + "/new.txt", size:3 }] });
  ui.query("#filesList .file-row").click();
  const request = ui.messages.at(-1);
  ui.query("#filesBackBtn").click();
  ui.receive({ type:"workspace-file", path:request.path, requestId:request.requestId, kind:"text", content:"stale" });
  assert.equal(ui.query("#filesPreview").hidden, true);
  assert.equal(ui.query("#filesList").hidden, false);
});

test("open file panel follows the selected workspace and discards old results", t => {
  const ui = page(t);
  addWorkspace(ui, "Alpha", "C:\\Work\\Alpha");
  addWorkspace(ui, "Beta", "C:\\Work\\Beta");
  ui.query('[data-page-pill="terminal"]').click();
  ui.query("#filesToggle").click();
  const beta = ui.messages.filter(message => message.type === "list-workspace-files").at(-1);
  const alpha = [...ui.document.querySelectorAll("[data-workspace-select]")].find(node => node.textContent.includes("Alpha"));
  alpha.click();
  const latest = ui.messages.filter(message => message.type === "list-workspace-files").at(-1);
  assert.equal(latest.root, "C:\\Work\\Alpha");
  ui.receive({ type:"workspace-files", path:beta.path, root:beta.root, requestId:beta.requestId, entries:[{name:"old.txt"}] });
  assert.equal(ui.document.querySelectorAll("#filesList .file-row").length, 0);
  ui.receive({ type:"workspace-files", path:latest.path, root:latest.root, requestId:latest.requestId, entries:[{name:"new.txt",path:"C:\\Work\\Alpha\\new.txt"}] });
  assert.match(ui.query("#filesList").textContent, /new\.txt/);
});

test("conversation sidebar collapses and restores its saved state", t => {
  const ui = page(t);
  ui.query("#sidebarToggle").click();
  assert.equal(ui.query(".body").dataset.sideCollapsed, "true");
  assert.equal(ui.query("#sidebarToggle").getAttribute("aria-expanded"), "false");
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(saved.state.view.sideCollapsed, true);
  const replay = page(t);
  replay.receive({ type:"ui-state", state:saved.state });
  assert.equal(replay.query(".body").dataset.sideCollapsed, "true");
  replay.query("#sidebarToggle").click();
  assert.equal(replay.query(".body").dataset.sideCollapsed, "false");
});

test("model discovery fills the list from the gateway", t => {
  const ui = page(t);
  ui.query("#accountBtn").click();
  ui.query('[data-settings-pane="models"]').click();
  ui.query("#gatewayUrl").value = "https://gw.example.com/v1";
  ui.query("#gatewayApiKey").value = "k";
  ui.query("#gatewayBackend").value = "anthropic";
  ui.query("#discoverModelsBtn").click();
  assert.deepEqual(ui.messages.at(-1),
    { type: "discover-models", baseUrl: "https://gw.example.com/v1", apiKey: "k", backend: "anthropic" });
  ui.receive({ type: "models-discovered", models: ["claude-a", "claude-b"] });
  assert.equal(ui.document.querySelectorAll("#modelList .model-row").length, 2);
  assert.equal(ui.query("#defaultModelSelect").value, "claude-a");
});

test("Office preview plugin installs and uninstalls from global settings", t => {
  const ui = page(t);
  ui.receive({ type: "plugin-settings", plugins: [{ id: "office-preview", installed: false }] });
  ui.query("#accountBtn").click();
  ui.query('[data-settings-pane="tools"]').click();

  const stateEl = ui.query("#pluginState-office-preview");
  const button = ui.query("#pluginToggle-office-preview");
  assert.equal(stateEl.textContent, "未安装");
  assert.equal(button.textContent, "安装");

  button.click();
  assert.deepEqual(ui.messages.at(-1), { type: "install-plugin", id: "office-preview" });
  assert.equal(button.disabled, false);
  assert.equal(button.textContent, "取消");
  assert.equal(button.classList.contains("is-danger"), true);
  assert.equal(stateEl.textContent, "正在准备安装…");

  ui.receive({ type: "plugin-status", id: "office-preview", state: "installing", phase: "download", percent: 42 });
  assert.equal(stateEl.textContent, "正在下载预览组件… 42%");
  assert.equal(button.textContent, "取消");
  ui.receive({ type: "plugin-status", id: "office-preview", state: "installing", phase: "extract" });
  assert.equal(stateEl.textContent, "正在解压预览组件…");

  button.click();
  assert.deepEqual(ui.messages.at(-1), { type: "cancel-plugin", id: "office-preview" });
  assert.equal(button.disabled, true);
  assert.equal(button.textContent, "正在取消…");
  assert.equal(stateEl.textContent, "正在取消…");
  ui.receive({ type: "plugin-settings", plugins: [{ id: "office-preview", installed: false }] });
  assert.equal(stateEl.textContent, "未安装");
  assert.equal(button.textContent, "安装");
  assert.equal(button.disabled, false);
  assert.equal(button.classList.contains("is-danger"), false);

  button.click();
  assert.deepEqual(ui.messages.at(-1), { type: "install-plugin", id: "office-preview" });
  assert.equal(button.textContent, "取消");
  ui.receive({ type: "plugin-settings", plugins: [{ id: "office-preview", installed: true }] });
  assert.equal(stateEl.textContent, "已安装");
  assert.equal(button.textContent, "卸载");
  assert.equal(button.disabled, false);

  button.click();
  assert.deepEqual(ui.messages.at(-1), { type: "uninstall-plugin", id: "office-preview" });
  assert.equal(button.disabled, true);
  assert.equal(stateEl.textContent, "正在卸载预览组件…");

  ui.receive({ type: "plugin-status", id: "office-preview", state: "failed", text: "Office 预览组件正在使用，无法卸载。" });
  assert.equal(button.disabled, false);
  assert.equal(button.textContent, "卸载");
  assert.equal(stateEl.textContent, "Office 预览组件正在使用，无法卸载。");

  ui.receive({ type: "plugin-settings", plugins: [{ id: "office-preview", installed: false }] });
  assert.equal(stateEl.textContent, "未安装");
  assert.equal(button.textContent, "安装");
});

test("terminal renders reasoning, tool calls, and tool results", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.receive({ type: "gateway-chat-started", requestId: "g1", prompt: "ssh 登录失败，帮我看看", cancellable: true });
  ui.receive({ type: "reasoning", requestId: "g1", text: "先列目录，再读日志。" });
  ui.receive({ type: "reasoning-done", requestId: "g1", text: "先列目录，再读日志。", tokens: 128 });
  assert.match(ui.query("#terminalThread .node--think").textContent, /思考/);
  assert.match(ui.query("#terminalThread .node--think .node__meta").textContent, /128 tokens/);
  assert.match(ui.query("#terminalThread .node--think .node__pre").textContent, /先列目录/);
  assert.equal(ui.query("#terminalThread .node--think").classList.contains("node--open"), false, "thinking folds by default");

  ui.receive({ type: "tool-call", requestId: "g1", callId: "t1", name: "list_files", arguments: "path=." });
  ui.receive({ type: "tool-call", requestId: "g1", callId: "t1", name: "list_files", arguments: "path=." });
  assert.equal(ui.document.querySelectorAll('#terminalThread .node--tool').length, 1, "a repeated tool event with the same call id is ignored");
  const card = ui.query('#terminalThread .node--tool');
  const runningProcess = card.closest(".turn__process");
  assert.ok(runningProcess, "running nodes belong to the internal process tree");
  assert.equal(runningProcess.hidden, true, "the process tree starts collapsed even while streaming");
  assert.equal(runningProcess.dataset.running, "true");
  assert.equal(card.classList.contains("is-running"), true);
  assert.match(card.textContent, /list_files/);
  assert.match(card.querySelector(".node__state").textContent, /运行中/);
  assert.equal(card.classList.contains("node--open"), false, "tool folds by default");

  ui.receive({ type: "tool-result", requestId: "g1", callId: "t1", ok: true, result: "a.txt\nb.txt" });
  assert.equal(ui.query(".node--tool .node__state").dataset.status, "ok");
  assert.match(ui.query(".node--tool .node__pre").textContent, /a\.txt/);
  assert.match(ui.query(".node--tool .node__state").textContent, /完成/);
  assert.equal(card.classList.contains("is-running"), false, "finished tools stop the active marker");

  ui.receive({ type: "gateway-chat-response", requestId: "g1", success: true, response: "看完了" });
  assert.match(ui.query("#terminalThread").textContent, /看完了/);
  assert.doesNotMatch(ui.query("#terminalThread").textContent, /网关模型/, "gateway responses do not show an internal provider label");
  const completedTurns = [...ui.document.querySelectorAll("#terminalThread .turn:not(.turn--bare)")];
  const completedTurn = completedTurns[completedTurns.length - 1];
  const process = completedTurn.querySelector(".turn__process");
  assert.equal(process.hidden, true, "completed process stays collapsed when the user never opened it");
  assert.equal(process, runningProcess, "completion retains the same process tree");
  assert.equal(process.dataset.running, "false", "completion stops the internal guide animation");
  const processToggle = completedTurn.querySelector(".turn__toggle");
  assert.match(processToggle.textContent, /用时/);
  assert.equal(processToggle.firstElementChild.className, "turn__toggle-label", "the duration and arrow share one label");
  assert.equal(processToggle.querySelector(".turn__toggle-label-text").nextElementSibling.className, "turn__chevron", "the expand arrow follows the duration");
  assert.equal(processToggle.querySelector(".turn__chevron").getAttribute("aria-hidden"), "true");
  assert.ok(process.querySelector(".node--think"));
  assert.ok(process.querySelector(".node--tool"));
  assert.match(completedTurn.querySelector(".node--agent .node__text").textContent, /看完了/);
  assert.equal(completedTurn.querySelector(".node--agent").hidden, false, "the final answer remains visible");
  assert.match(completedTurn.querySelector(".node--agent .chat-markdown").className, /chat-markdown/);
  completedTurn.querySelector(".turn__toggle").click();
  assert.equal(process.hidden, false, "completed process can be reopened");
  assert.equal(process.querySelector(".node--think").classList.contains("node--open"), false, "opening the process does not force tool panels open");
  assert.equal(process.querySelector(".node--tool").classList.contains("node--open"), false, "tool panels stay independently collapsed");
  assert.equal(completedTurn.querySelector(".node--agent").closest(".turn__process"), null);
});

test("approvals are recorded in the conversation tree and leave the pending queue after one decision", t => {
  const ui = page(t);
  ui.receive({ type:"ui-state", state:baseState({ activeConversationId:"c1" }) });
  const gatewayRequest = { type:"gateway-approval-request", approvalId:"gateway-1", title:"批准工作区补丁", description:"请核对补丁。", content:"*** Begin Patch" };
  ui.receive(gatewayRequest);
  const gatewayCard = ui.query("#terminalThread .approval-card");
  assert.match(gatewayCard.textContent, /批准工作区补丁/);
  assert.ok(gatewayCard.closest(".turn"), "the approval belongs to a turn in the conversation tree");
  assert.equal(ui.query(".approval-menu__title").textContent, "如何批准 Remote CLI 操作？");
  assert.equal(ui.document.querySelector("#composerDrawBtn"), null, "the unsupported drawing option is removed");
  ui.receive(gatewayRequest);
  assert.equal(ui.document.querySelectorAll("#terminalThread .approval-card").length, 1, "redelivering one approval id does not duplicate it");
  const allowGateway = gatewayCard.querySelector(".btn.btn--primary");
  allowGateway.click();
  allowGateway.click();
  assert.deepEqual(ui.messages.filter(message => message.type === "gateway-approval-response").at(-1),
    { type:"gateway-approval-response", approvalId:"gateway-1", allow:true });
  assert.equal(ui.messages.filter(message => message.type === "gateway-approval-response" && message.approvalId === "gateway-1").length, 1,
    "a resolved approval cannot be sent twice");
  assert.equal(gatewayCard.querySelectorAll(".btn").length, 0, "resolved action buttons are destroyed");
  assert.match(gatewayCard.textContent, /已允许/);
  assert.ok(ui.messages.filter(message => message.type === "save-state").at(-1).state.conversations[0].messages
    .some(message => message.role === "approval" && message.id === "gateway-1" && message.status === "allowed"));

  ui.receive({ type:"approval-request", approvalId:"remote-1", name:"读取文件", description:"远程工具请求读取文件。", permission:"查看会话", content:'{"path":"a.txt"}' });
  const remoteCard = [...ui.document.querySelectorAll("#terminalThread .approval-card")].find(card => /读取文件/.test(card.textContent));
  assert.ok(remoteCard);
  [...remoteCard.querySelectorAll(".btn")].find(button => button.textContent.includes("会话允许")).click();
  assert.deepEqual(ui.messages.filter(message => message.type === "approval-response").at(-1),
    { type:"approval-response", approvalId:"remote-1", decision:"allow_session" });

  ui.receive({ type:"approval-request", approvalId:"remote-cancelled", name:"删除文件", description:"远程工具请求删除文件。" });
  ui.receive({ type:"approval-cancelled", approvalId:"remote-cancelled" });
  const cancelledCard = [...ui.document.querySelectorAll("#terminalThread .approval-card")].find(card => /删除文件/.test(card.textContent));
  assert.ok(cancelledCard);
  assert.match(cancelledCard.textContent, /已取消/);
  assert.equal(cancelledCard.querySelectorAll(".btn").length, 0, "cancelled approval actions are destroyed");
});

test("plain answers have no process group", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.receive({ type: "gateway-chat-started", requestId: "no-think", prompt: "你好", cancellable: true });
  ui.receive({ type: "gateway-chat-response", requestId: "no-think", success: true, response: "你好" });
  const turns = [...ui.document.querySelectorAll("#terminalThread .turn:not(.turn--bare)")];
  const turn = turns[turns.length - 1];
  assert.equal(turn.querySelector(".turn__process-group"), null);
  assert.match(turn.querySelector(".node--agent").textContent, /你好/);
});

test("process guides stay inside the tree through streaming, expansion and completion", async t => {
  const { chromium } = require("playwright-core");
  const browser = await chromium.launch({ executablePath: process.env.REMOTECLI_TEST_BROWSER || "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", headless: true });
  const errors = [];
  t.after(async () => { await browser.close(); assert.deepEqual(errors, []); });
  const browserPage = await browser.newPage({ viewport: { width: 1100, height: 760 }, reducedMotion: "no-preference" });
  browserPage.on("pageerror", error => errors.push(error.message));
  await browserPage.addInitScript(() => {
    const listeners = [];
    window.testReceive = data => listeners.forEach(listener => listener({ data }));
    window.chrome = { webview: { postMessage() {}, addEventListener(type, listener) { if(type === "message") listeners.push(listener); } } };
  });
  await browserPage.route("https://longshell.test/", route => route.fulfill({ status: 200, contentType: "text/html", body: html }));
  await browserPage.goto("https://longshell.test/");
  const receive = message => browserPage.evaluate(value => window.testReceive(value), message);
  await receive({ type: "init", userName: "controller", relay: relayBaseUrl });
  await receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  await browserPage.click('[data-page-pill="terminal"]');
  await receive({ type: "gateway-chat-started", requestId: "tree-guide", prompt: "检查工作区", cancellable: true });
  await receive({ type: "tool-call", requestId: "tree-guide", callId: "first", name: "list_files", arguments: "path=." });
  await receive({ type: "tool-result", requestId: "tree-guide", callId: "first", ok: true, result: "a.txt\nb.txt\nc.txt" });
  await receive({ type: "gateway-chat-delta", requestId: "tree-guide", text: "接着读取文件。" });
  await receive({ type: "tool-call", requestId: "tree-guide", callId: "second", name: "read_file", arguments: "path=a.txt" });
  const processTree = browserPage.locator('.turn__process[data-running="true"]');
  assert.equal(await processTree.isVisible(), false, "the process tree stays collapsed by default");
  await browserPage.locator(".turn__toggle").last().click();
  await processTree.waitFor({ state: "visible" });
  assert.deepEqual(await processTree.locator(":scope > .node").evaluateAll(nodes => nodes.map(node =>
    node.classList.contains("node--action-group") ? node.querySelector(".node--tool .node__meta").textContent.includes("list_files") ? "first" : "second" : "comment")),
    ["first", "comment", "second"], "streamed commentary keeps its chronological position inside the process");

  async function checkGeometry() {
    await processTree.evaluate(tree => Promise.allSettled(tree.getAnimations({ subtree:true })
      .filter(animation => Number.isFinite(animation.effect.getComputedTiming().endTime)).map(animation => animation.finished)));
    const geometry = await processTree.evaluate(tree => {
      const group = tree.parentElement.getBoundingClientRect();
      const nodes = [...tree.children];
      const segments = nodes.slice(0, -1).map((node, index) => {
        const box = node.getBoundingClientRect(), next = nodes[index + 1].getBoundingClientRect();
        const line = getComputedStyle(node, "::after"), dot = getComputedStyle(nodes[index + 1], "::before");
        return { left: box.left + parseFloat(line.left), right: box.left + parseFloat(line.left) + parseFloat(line.width),
          end: box.bottom - parseFloat(line.bottom), next: next.top + parseFloat(dot.top) + parseFloat(dot.height) / 2,
          animation: line.animationName };
      });
      return { left: group.left, right: group.right, segments,
        outer: getComputedStyle(tree.parentElement.parentElement, "::before").content,
        last: getComputedStyle(nodes.at(-1), "::after").content };
    });
    assert.equal(geometry.outer, "none", "no guide remains outside the process tree");
    assert.equal(geometry.last, "none", "the guide stops at its last node");
    for(const segment of geometry.segments) {
      assert.ok(segment.left > geometry.left && segment.right < geometry.right, "guide segments are inside the tree boundary");
      assert.ok(Math.abs(segment.end - segment.next) <= 1, `each segment meets the next node even when an earlier node expands: ${JSON.stringify(segment)}`);
      assert.notEqual(segment.animation, "none", "the running guide animates inside the process");
    }
  }
  await checkGeometry();
  await processTree.locator(".action-group__head").first().click();
  await processTree.locator(".node__head").first().click();
  await processTree.locator(".node__body").first().evaluate(body => Promise.all(body.getAnimations().map(animation => animation.finished)));
  await checkGeometry();
  await browserPage.emulateMedia({ reducedMotion: "reduce" });
  assert.equal(await processTree.locator(".node").first().evaluate(node => getComputedStyle(node, "::after").animationName), "none");
  await browserPage.emulateMedia({ reducedMotion: "no-preference" });
  for(const theme of ["light", "dark"]) {
    await browserPage.evaluate(value => { document.documentElement.dataset.theme = value; }, theme);
    await checkGeometry();
  }
  const screenshotDirectory = path.join(repo, "artifacts/ui-verification");
  fs.mkdirSync(screenshotDirectory, { recursive: true });
  await browserPage.screenshot({ path: path.join(screenshotDirectory, "conversation-tree-internal.png") });
  await receive({ type: "tool-result", requestId: "tree-guide", callId: "second", ok: true, result: "文件内容" });
  await receive({ type: "gateway-chat-response", requestId: "tree-guide", success: true, response: "检查完成。" });
  const completed = browserPage.locator('.turn__process[data-running="false"]');
  await completed.waitFor({ state: "visible" });
  assert.equal(await completed.isVisible(), true, "a process the user opened stays open after completion");
  assert.equal(await browserPage.locator(".turn__kids > .node--agent").last().innerText(), "检查完成。");
  const collapseFilters = await completed.locator("..").locator(".turn__toggle").evaluate(button => {
    button.click();
    return button.parentElement.querySelector(".turn__process")
      .getAnimations().flatMap(animation => animation.effect.getKeyframes().map(frame => frame.filter || ""));
  });
  assert.ok(collapseFilters.some(value => value.includes("blur(4px)")), "collapsing by hand fades the process with blur");
  await completed.waitFor({ state: "hidden" });
  assert.equal(await completed.isVisible(), false, "collapsing hides the process");
  assert.equal(await completed.locator(".node").first().evaluate(node => getComputedStyle(node, "::after").animationName), "none");
});

test("tool toggles persist from the settings window", t => {
  const ui = page(t);
  ui.query("#accountBtn").click();
  ui.query('[data-settings-pane="tools"]').click();
  assert.equal(ui.query('[data-pane="tools"]').hidden, false);
  ui.query("#webSearchProvider").value = "deepseek";
  ui.query("#webSearchProvider").dispatchEvent(new ui.document.defaultView.Event("change", { bubbles: true }));
  assert.equal(ui.query("#webSearchBaseUrl").value, "https://api.deepseek.com/v1/tools");
  const boxes = [...ui.document.querySelectorAll('input[name="chatTool"]')];
  assert.equal(boxes.length, 18);
  assert.equal(boxes.find(item => item.value === "present").checked, true);
  for (const name of ["search_text", "edit_file", "web_fetch", "exec_command", "remote_tools", "remote_commands", "glob", "grep", "apply_patch", "exa_search", "subagents"]) {
    const box = boxes.find(item => item.value === name);
    assert.ok(box);
    assert.equal(box.checked, false);
    box.checked = true;
  }
  const search = boxes.find(box => box.value === "web_search");
  assert.equal(search.checked, true);
  search.checked = false;
  ui.query("#settingsSaveBtn").click();
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.ok(saved.state.settings.tools.includes("list_files"));
  assert.deepEqual(saved.state.settings.globalTools, saved.state.settings.tools);
  assert.equal(saved.state.settings.tools.includes("web_search"), false);
  for (const name of ["search_text", "edit_file", "web_fetch", "exec_command", "remote_tools", "remote_commands", "glob", "grep", "apply_patch", "exa_search", "subagents"]) assert.ok(saved.state.settings.tools.includes(name));
});

test("streaming deltas append to the pending reply and reasoning", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.receive({ type: "gateway-chat-started", requestId: "s1", prompt: "列出文件", cancellable: true });
  ui.receive({ type: "gateway-chat-delta", requestId: "s1", kind: "reasoning", text: "先想一下" });
  ui.receive({ type: "gateway-chat-delta", requestId: "s1", kind: "reasoning", text: "，再列目录。" });
  assert.match(ui.query("#terminalThread .node--think .node__pre").textContent, /先想一下，再列目录。/);
  ui.receive({ type: "gateway-chat-delta", requestId: "s1", kind: "text", text: "共 " });
  ui.receive({ type: "gateway-chat-delta", requestId: "s1", kind: "text", text: "3 个文件" });
  assert.match(ui.query("#terminalThread").textContent, /共 3 个文件/);
  ui.receive({ type: "gateway-chat-response", requestId: "s1", success: true, response: "共 3 个文件" });
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  const textMessages = saved.state.conversations[0].messages.filter(m => m.role === "agent");
  assert.ok(textMessages.some(m => m.text === "共 3 个文件"));
  const reasoningMessages = saved.state.conversations[0].messages.filter(m => m.role === "reasoning");
  assert.equal(reasoningMessages.length, 1);
});

test("reasoning displays cumulative request context estimates and preserves them on restore", t => {
  const ui = page(t);
  const previous = baseState().conversations[0];
  previous.messages.push({ role:"reasoning", text:"旧记录", tokens:8 }, { role:"agent", text:"旧回答" });
  ui.receive({ type:"ui-state", state:baseState({ conversations:[previous], activeConversationId:"c1" }) });
  assert.match(ui.query(".node--think .node__meta").textContent, /8 tokens · 预计消耗 —/);

  ui.receive({ type:"gateway-chat-started", requestId:"estimate", prompt:"本轮问题", cancellable:true });
  ui.receive({ type:"gateway-context-estimate", requestId:"other", totalInputTokens:999 });
  ui.receive({ type:"gateway-context-estimate", requestId:"estimate", round:1, inputTokens:120, totalInputTokens:120 });
  ui.receive({ type:"reasoning", requestId:"estimate", text:"第一次思考" });
  ui.receive({ type:"reasoning-done", requestId:"estimate", text:"第一次思考", tokens:30 });
  let nodes = [...ui.document.querySelectorAll(".node--think .node__meta")];
  assert.match(nodes.at(-1).textContent, /30 tokens · 预计消耗 约 120 tokens/);
  assert.match(nodes.at(-1).title, /不含图片、工具定义及子任务/);

  ui.receive({ type:"tool-call", requestId:"estimate", callId:"one", name:"list_files", arguments:"path=." });
  ui.receive({ type:"tool-result", requestId:"estimate", callId:"one", ok:true, result:"a.txt" });
  ui.receive({ type:"gateway-context-estimate", requestId:"estimate", round:2, inputTokens:220, totalInputTokens:340 });
  ui.receive({ type:"reasoning", requestId:"estimate", text:"第二次思考" });
  ui.receive({ type:"reasoning-done", requestId:"estimate", text:"第二次思考", tokens:40 });
  nodes = [...ui.document.querySelectorAll(".node--think .node__meta")];
  assert.match(nodes.at(-2).textContent, /预计消耗 约 120 tokens/);
  assert.match(nodes.at(-1).textContent, /40 tokens · 预计消耗 约 340 tokens/);

  ui.receive({ type:"tool-call", requestId:"estimate", callId:"two", name:"read_file", arguments:"path=a.txt" });
  ui.receive({ type:"tool-result", requestId:"estimate", callId:"two", ok:true, result:"contents" });
  ui.receive({ type:"gateway-context-estimate", requestId:"estimate", round:3, inputTokens:260, totalInputTokens:600 });
  ui.receive({ type:"gateway-chat-response", requestId:"estimate", success:true, response:"完成" });
  assert.match(ui.document.querySelectorAll(".node--think .node__meta")[2].textContent, /40 tokens · 预计消耗 约 600 tokens/,
    "the final request is included even when it has no reasoning block");
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1).state;
  assert.deepEqual(saved.conversations[0].messages.filter(message => message.role === "reasoning").map(message => message.estimatedRequestTokens),
    [undefined, 120, 600]);
  ui.receive({ type:"ui-state", state:saved });
  nodes = [...ui.document.querySelectorAll(".node--think .node__meta")];
  assert.match(nodes.at(-1).textContent, /预计消耗 约 600 tokens/);

  ui.receive({ type:"gateway-chat-started", requestId:"next", prompt:"下一轮", cancellable:true });
  ui.receive({ type:"reasoning", requestId:"next", text:"新一轮" });
  assert.match([...ui.document.querySelectorAll(".node--think .node__meta")].at(-1).textContent, /预计消耗 —/,
    "each user request starts a fresh estimate");
});

test("actual model usage drives the turn label, context window and conversation total", t => {
  const ui = page(t);
  const first = baseState().conversations[0];
  first.messages[0].usage = { inputTokens:1000, totalTokens:520000 };
  const settings = { relay:relayBaseUrl, replySource:"gateway", gatewayUrl:"https://gateway.example/v1", models:["m"], defaultModel:"m", modelOptions:{m:{contextWindow:256000}}, tools:[] };
  ui.receive({ type:"ui-state", state:baseState({ conversations:[first], settings, activeConversationId:"c1" }) });
  ui.query('[data-page-pill="terminal"]').click();
  ui.receive({ type:"gateway-chat-started", requestId:"usage", prompt:"问题", cancellable:true });
  ui.receive({ type:"reasoning", requestId:"usage", text:"思考中" });
  const turn = [...ui.document.querySelectorAll(".turn:not(.turn--bare)")].at(-1);
  assert.equal(turn.querySelector(".turn__usage"), null, "missing provider usage is hidden");
  ui.receive({ type:"gateway-usage", requestId:"usage", round:1, inputTokens:112000, outputTokens:100, totalTurnTokens:1400000 });
  assert.equal(turn.querySelector(".turn__usage").textContent, "本次消耗 1.4m Token");
  assert.equal(ui.query("#contextPercent").textContent, "44% 已用");
  assert.equal(ui.query("#contextTokens").textContent, "112k Token，共 256k Token");
  assert.equal(ui.query("#conversationTokens").textContent, "本对话总共消耗 1.92m Token");
  ui.receive({ type:"gateway-usage", requestId:"usage", round:2, inputTokens:null, outputTokens:null, totalTurnTokens:null });
  assert.equal(turn.querySelector(".turn__usage"), null, "partial usage is not presented as a complete total");
  assert.equal(ui.query("#contextPercent").textContent, "— 已用");
  assert.equal(ui.query("#conversationTokens").textContent, "本对话总共消耗 —");
});

test("prediction uses the configured training folder and updates until actual usage arrives", t => {
  const ui = page(t);
  const settings = { relay:relayBaseUrl, replySource:"gateway", gatewayUrl:"https://gateway.example/v1", models:["m"], defaultModel:"m", tools:[] };
  ui.receive({ type:"ui-state", state:baseState({ settings, activeConversationId:"c1" }) });
  ui.query("#accountBtn").click();
  ui.query('[data-settings-pane="general"]').click();
  ui.query("#tokenTrainingBrowseBtn").click();
  assert.deepEqual(ui.messages.at(-1), { type:"select-token-training-folder" });
  ui.receive({ type:"token-training-folder", path:"D:\\TokenTraining" });
  ui.query("#settingsSaveBtn").click();
  assert.equal(ui.messages.filter(message => message.type === "save-state").at(-1).state.settings.tokenTrainingPath, "D:\\TokenTraining");

  ui.query('[data-page-pill="terminal"]').click();
  ui.query("#terminalInput").value = "问题";
  ui.query("#terminalInput").dispatchEvent(new ui.document.defaultView.Event("input", { bubbles:true }));
  ui.query("#terminalSendBtn").click();
  const request = ui.messages.filter(message => message.type === "gateway-chat-request").at(-1);
  assert.equal(request.tokenTrainingPath, "D:\\TokenTraining");
  ui.receive({ type:"gateway-chat-started", requestId:request.requestId, prompt:"问题", cancellable:true });
  ui.receive({ type:"gateway-token-prediction", requestId:request.requestId, predictedTurnTotalTokens:523, sampleCount:0 });
  let turn = [...ui.document.querySelectorAll(".turn")].at(-1);
  assert.equal(turn.querySelector(".turn__usage").textContent, "预计本次消耗 523 Token");
  assert.equal(turn.querySelector(".turn__usage").title, "冷启动估算");
  ui.receive({ type:"reasoning", requestId:request.requestId, text:"思考中" });
  ui.receive({ type:"gateway-token-prediction", requestId:request.requestId, predictedTurnTotalTokens:850, sampleCount:4 });
  turn = [...ui.document.querySelectorAll(".turn")].at(-1);
  assert.equal(turn.querySelector(".turn__usage").textContent, "预计本次消耗 850 Token");
  assert.match(turn.querySelector(".node--think .node__meta").textContent, /预计本次总消耗 约 850 tokens/);
  ui.receive({ type:"gateway-usage", requestId:request.requestId, round:1, inputTokens:100, outputTokens:836, totalTurnTokens:936 });
  assert.equal(turn.querySelector(".turn__usage").textContent, "预计本次消耗 850 Token");
  ui.receive({ type:"gateway-chat-response", requestId:request.requestId, success:true, response:"完成" });
  assert.equal(turn.querySelector(".turn__usage").textContent, "本次消耗 936 Token");
});

test("models retain their own context and common sampling parameters", t => {
  const ui = page(t);
  const settings = { relay:relayBaseUrl, replySource:"gateway", gatewayUrl:"https://gateway.example/v1", models:["model-a"], defaultModel:"model-a", tools:[] };
  ui.receive({ type:"ui-state", state:baseState({ settings, activeConversationId:"c1" }) });
  ui.query("#accountBtn").click(); ui.query('[data-settings-pane="models"]').click();
  assert.equal(ui.query('[data-param="contextWindow"]').value, "128000");
  for(const [key,value] of [["contextWindow","128000"],["maxOutputTokens","4096"],["temperature","0.7"],["topP","0.9"]]){
    const input = ui.query(`[data-param="${key}"]`); input.value = value;
    input.dispatchEvent(new ui.document.defaultView.Event("input", { bubbles:true }));
  }
  ui.query("#settingsSaveBtn").click();
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1).state;
  assert.deepEqual(saved.settings.modelOptions["model-a"], { contextWindow:128000, maxOutputTokens:4096, temperature:0.7, topP:0.9 });
  ui.query("#terminalInput").value = "发送";
  ui.query("#terminalInput").dispatchEvent(new ui.document.defaultView.Event("input", { bubbles:true }));
  ui.query("#terminalSendBtn").click();
  assert.deepEqual(ui.messages.filter(message => message.type === "gateway-chat-request").at(-1).modelOptions,
    { contextWindow:128000, maxOutputTokens:4096, temperature:0.7, topP:0.9 });
});

test("background completion becomes unread and opening the conversation clears it", t => {
  const ui = page(t);
  const first = baseState().conversations[0];
  const second = { id:"c2", workspaceId:"ws-a", title:"第二个", messages:[{role:"user", text:"其他"}], createdAt:2, updatedAt:2 };
  ui.receive({ type:"ui-state", state:baseState({ conversations:[first,second], activeConversationId:"c1" }) });
  ui.query('[data-page-pill="terminal"]').click();
  ui.receive({ type:"gateway-chat-started", requestId:"unread", prompt:"后台任务", cancellable:true });
  ui.query('[data-conversation="c2"]').click();
  ui.receive({ type:"gateway-chat-response", requestId:"unread", success:true, response:"完成" });
  assert.equal(ui.query('[data-conversation="c1"]').dataset.unread, "true");
  ui.query('[data-conversation="c1"]').click();
  assert.equal(ui.query('[data-conversation="c1"]').dataset.unread, undefined);
});

test("streaming updates reuse thread nodes instead of rebuilding the whole thread", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.receive({ type: "gateway-chat-started", requestId: "keep", prompt: "增量渲染", cancellable: true });
  ui.receive({ type: "gateway-chat-delta", requestId: "keep", kind: "reasoning", text: "第一步" });

  const turn = ui.query("#terminalThread .turn");
  const think = ui.query("#terminalThread .node--think");
  const pre = think.querySelector(".node__pre");

  ui.receive({ type: "gateway-chat-delta", requestId: "keep", kind: "reasoning", text: "，第二步" });
  ui.receive({ type: "gateway-chat-delta", requestId: "keep", kind: "text", text: "回复" });

  assert.equal(ui.query("#terminalThread .turn"), turn, "turn container must be reused, not rebuilt");
  assert.equal(ui.query("#terminalThread .node--think"), think, "thinking node must be reused");
  assert.equal(think.querySelector(".node__pre"), pre, "thinking text node must be reused");
  assert.match(pre.textContent, /第一步，第二步/);

  think.querySelector(".node__head").click();
  ui.receive({ type: "gateway-chat-delta", requestId: "keep", kind: "text", text: "继续" });
  assert.equal(think.classList.contains("node--open"), true, "expanded fold survives streaming updates");

  ui.receive({ type: "gateway-chat-response", requestId: "keep", success: true, response: "回复" });
  ui.receive({ type: "gateway-chat-started", requestId: "next", prompt: "第二轮", cancellable: true });
  assert.equal(ui.document.querySelectorAll("#terminalThread .turn").length, 3,
    "each user prompt opens its own parent turn");
  const lastTurn = ui.document.querySelectorAll("#terminalThread .turn")[2];
  assert.match(lastTurn.querySelector(".turn__ask-text").textContent, /第二轮/);
});

test("loading and tool timeline follow events, even after switching conversations", t => {
  const ui = page(t);
  const first = baseState().conversations[0];
  const second = { id: "c2", workspaceId: "ws-a", title: "另一会话", messages: [{ role: "user", text: "other", at: 2 }], createdAt: 2, updatedAt: 2 };
  ui.receive({ type: "ui-state", state: baseState({ conversations: [first, second], activeConversationId: "c1" }) });
  ui.receive({ type: "gateway-chat-started", requestId: "ordered", prompt: "task", cancellable: true });
  assert.match(ui.query(".terminal-loading").textContent, /正在等待模型响应/);
  ui.receive({ type: "gateway-chat-delta", requestId: "ordered", kind: "reasoning", text: "先分析" });
  ui.receive({ type: "gateway-chat-delta", requestId: "ordered", kind: "text", text: "先看文件" });
  ui.receive({ type: "reasoning-done", requestId: "ordered", text: "先分析" });
  ui.receive({ type: "tool-call", requestId: "ordered", callId: "one", name: "glob", arguments: "pattern=*" });
  assert.match(ui.query(".node--tool .node__meta").textContent, /glob/);
  ui.query('[data-conversation="c2"]').click();
  assert.equal(ui.document.querySelector(".terminal-loading"), null);
  ui.receive({ type: "tool-result", requestId: "ordered", callId: "one", ok: true, result: "a.cs" });
  ui.receive({ type: "mcp-plugin-status", requestId: "ordered", server: "remote", success: false, message: "连接失败" });
  ui.receive({ type: "tool-call", requestId: "ordered", callId: "two", name: "read_file", arguments: "path=a.cs" });
  ui.receive({ type: "tool-result", requestId: "ordered", callId: "two", ok: false, result: "未找到" });
  ui.receive({ type: "gateway-chat-delta", requestId: "ordered", kind: "text", text: "最终答复" });
  ui.receive({ type: "gateway-chat-response", requestId: "ordered", success: true, response: "最终答复" });
  let saved = ui.messages.filter(message => message.type === "save-state").at(-1).state;
  const owner = saved.conversations.find(item => item.id === "c1");
  assert.deepEqual(owner.messages.slice(-7).map(item => item.role), ["user", "reasoning", "agent", "tool", "system", "tool", "agent"]);
  assert.equal(owner.messages.at(-1).text, "最终答复");
  assert.equal(owner.messages.filter(item => item.role === "reasoning").length, 1);
  assert.equal(owner.messages.filter(item => item.role === "tool")[1].status, "error");
  assert.ok(owner.messages.some(item => item.who === "MCP · remote" && item.isError && item.text === "连接失败"));
  assert.equal(saved.conversations.find(item => item.id === "c2").messages.length, 1);
  ui.query('[data-conversation="c1"]').click();
  assert.equal(ui.document.querySelector(".terminal-loading"), null);
  const cards = [...ui.document.querySelectorAll(".node--tool")];
  assert.equal(cards.length, 2);
  assert.deepEqual(cards.map(card => card.querySelector(".node__meta").textContent.split(" · ")[0]), ["glob", "read_file"]);
  assert.equal(cards[0].classList.contains("node--open"), false, "tool folds by default");
  cards[0].querySelector("button").click();
  assert.equal(cards[0].querySelector("button").getAttribute("aria-expanded"), "true");
});

test("assistant markdown and terminal output render without executing embedded markup", t => {
  const ui = page(t);
  ui.receive({ type:"ui-state", state:baseState({ activeConversationId:"c1" }) });
  ui.receive({ type:"gateway-chat-started", requestId:"safe", prompt:"test", cancellable:true });
  ui.receive({ type:"tool-call", requestId:"safe", callId:"t", name:"exec_command", arguments:"tty=true" });
  ui.receive({ type:"tool-result", requestId:"safe", callId:"t", ok:true, result:"\u001b[31mred\u001b[0m" });
  assert.doesNotMatch(ui.query(".node--tool .node__pre").textContent, /\u001b/);
  assert.match(ui.query(".node--tool .node__pre").textContent, /red/);
  ui.receive({ type:"gateway-chat-response", requestId:"safe", success:true, response:"**bold** and [site](https://example.com)\n\n```html\n<script>alert(1)</script>\n```" });
  assert.equal(ui.query(".chat-markdown strong").textContent, "bold");
  assert.equal(ui.query(".chat-markdown a").getAttribute("href"), "https://example.com");
  assert.match(ui.query(".chat-markdown pre").textContent, /<script>/);
  assert.equal(ui.document.querySelector(".chat-markdown script"), null);
});

test("assistant file references open the workspace preview and emphasis is highlighted", t => {
  const ui = page(t);
  ui.receive({ type:"ui-state", state:baseState({ activeConversationId:"c1" }) });
  ui.receive({ type:"gateway-chat-started", requestId:"file-link", prompt:"检查文件", cancellable:true });
  ui.receive({ type:"gateway-chat-response", requestId:"file-link", success:true,
    response:"请查看 [README.md](README.md)，重点是 ==这一段==，代码文件是 `src/App.cs`，外链 [站点](https://example.com)。" });
  assert.equal(ui.query(".chat-markdown mark").textContent, "这一段");
  assert.equal(ui.queryAll ? ui.queryAll(".chat-markdown .file-link").length : ui.document.querySelectorAll(".chat-markdown .file-link").length, 2);
  assert.equal(ui.query(".chat-markdown a:not(.file-link)").getAttribute("href"), "https://example.com");
  ui.query(".chat-markdown .file-link").click();
  const request = ui.messages.filter(message => message.type === "read-workspace-file").at(-1);
  assert.equal(request.root, "C://Work//Alpha");
  assert.equal(request.path, "C:\\Work\\Alpha\\README.md");
  ui.receive({ type:"workspace-file", path:request.path, requestId:request.requestId, kind:"text", content:"preview" });
  assert.equal(ui.query("#filesPreviewText").textContent, "preview");
  assert.equal(ui.query("#filesPanel").hidden, false);
});

test("subagent lifecycle is tracked in the conversation tree and files panel", t => {
  const ui = page(t);
  ui.receive({ type:"ui-state", state:baseState({ activeConversationId:"c1" }) });
  ui.receive({ type:"gateway-chat-started", requestId:"agents", prompt:"拆分检查", cancellable:true });
  ui.receive({ type:"subagent-update", requestId:"agents", conversationId:"c1", agentId:"agent-123456", task:"检查日志", status:"running" });
  assert.match(ui.query(".node--subagent").textContent, /检查日志/);
  assert.match(ui.query(".node--subagent__status").textContent, /运行中/);
  ui.query("#filesPanelToggle").click();
  ui.query("#filesTraceToggle").click();
  assert.match(ui.query("#subagentTrace").textContent, /检查日志/);
  ui.receive({ type:"subagent-update", requestId:"agents", conversationId:"c1", agentId:"agent-123456", task:"检查日志", status:"completed", result:"发现 2 个告警" });
  assert.match(ui.query("#subagentTrace").textContent, /发现 2 个告警/);
  assert.match(ui.query("#subagentTrace").textContent, /已完成/);
});

test("command executor panels preserve output, status and history without extra controls", t => {
  const ui = page(t);
  ui.receive({ type:"ui-state", state:baseState({ activeConversationId:"c1" }) });
  ui.receive({ type:"gateway-chat-started", requestId:"executor", prompt:"检查", cancellable:true });
  ui.receive({ type:"tool-call", requestId:"executor", callId:"one", name:"exec_command", arguments:'cmd=echo <script>alert(1)</script>' });
  const tool = ui.query(".node--tool"), panel = tool.querySelector(".executor-panel");
  assert.ok(panel, "command output is wrapped in an executor panel");
  assert.equal(panel.querySelector(".executor-panel__status").textContent, "调用中");
  assert.equal(panel.querySelector(".executor-panel__args").textContent, 'cmd=echo <script>alert(1)</script>');
  assert.equal(panel.querySelector("script"), null, "command summaries are displayed as text");
  tool.querySelector(".node__head").click();
  assert.equal(tool.querySelector(".node__head").getAttribute("aria-expanded"), "true");

  ui.receive({ type:"tool-result", requestId:"executor", callId:"one", ok:false, result:"第一行\n<script>alert(1)</script>" });
  assert.equal(panel.querySelector(".executor-panel__status").textContent, "调用失败");
  assert.equal(panel.querySelector(".node__pre").textContent, "第一行\n<script>alert(1)</script>");
  assert.equal(panel.querySelector("script"), null, "output is displayed as text");
  assert.equal(panel.querySelector(".executor-panel__copy"), null);
  assert.equal(panel.querySelector(".executor-panel__icon"), null);

  ui.receive({ type:"tool-call", requestId:"executor", callId:"two", name:"write_stdin", arguments:"" });
  const inputPanel = [...ui.document.querySelectorAll(".executor-panel")].at(-1);
  assert.equal(inputPanel.querySelector(".executor-panel__summary").hidden, true);
  ui.receive({ type:"tool-result", requestId:"executor", callId:"two", ok:true, result:"收到" });
  assert.equal(inputPanel.querySelector(".executor-panel__status").textContent, "调用完成");
  ui.receive({ type:"tool-call", requestId:"executor", callId:"other", name:"list_files", arguments:"path=." });
  assert.equal([...ui.document.querySelectorAll(".node--tool")].at(-1).querySelector(".executor-panel"), null);
  ui.receive({ type:"gateway-chat-response", requestId:"executor", success:true, response:"结束" });
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1).state;
  ui.receive({ type:"ui-state", state:saved });
  assert.equal(ui.document.querySelectorAll(".executor-panel").length, 2, "saved execution history restores the panels");
  assert.equal(ui.query(".executor-panel .node__pre").textContent, "第一行\n<script>alert(1)</script>");
});

test("effort slider announces each actual selected level", t => {
  const ui = page(t);
  ui.query("#effortChip").click();
  const slider = ui.query("#effortRange");
  for(const [index, value, hint] of [[0, "低", /适合简单/], [1, "中", /平衡/], [2, "高", /复杂/], [3, "极高", /更多/], [4, "Max", /最高档/]]){
    slider.value = String(index);
    slider.dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
    assert.equal(slider.getAttribute("aria-valuetext"), value);
    assert.match(ui.query("#effortPopHint").textContent, hint);
    assert.equal(ui.query('#effortTicks span[aria-current="true"]').textContent, value);
  }
  assert.equal(ui.messages.filter(message => message.type === "save-state").at(-1).state.settings.effort, "max");
});

test("conversation tree persists fork ancestry and can collapse descendants", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.query('[data-conversation="c1"]').dispatchEvent(new ui.document.defaultView.MouseEvent("contextmenu", { bubbles: true }));
  ui.query('#convMenu [data-action="fork"]').click();
  let saved = ui.messages.filter(message => message.type === "save-state").at(-1).state;
  const fork = saved.conversations.find(item => item.id !== "c1");
  assert.equal(fork.parentId, "c1");
  ui.query("#sessionViewBtn").click();
  [...ui.query("#convMenu").querySelectorAll(".context-menu__item")].find(node => node.textContent.includes("按对话树")).click();
  assert.ok(ui.query('[data-branch="c1"]'));
  assert.equal(ui.document.querySelector('[data-branch="' + fork.id + '"]'), null,
    "forked branches stay collapsed until the user expands them");
  ui.query('[data-branch="c1"] .conversation-branch__toggle').click();
  assert.ok(ui.query('[data-branch="' + fork.id + '"]'), "expanding the ancestor shows the fork");
  saved = ui.messages.filter(message => message.type === "save-state").at(-1).state;
  assert.equal(saved.expanded["conversation:c1"], true, "the expanded branch is persisted");
  assert.equal(saved.view.grouping, "conversation");
  ui.query('[data-branch="c1"] .conversation-branch__toggle').click();
  assert.equal(ui.document.querySelector('[data-branch="' + fork.id + '"]'), null);
  saved = ui.messages.filter(message => message.type === "save-state").at(-1).state;
  ui.receive({ type: "ui-state", state: saved });
  assert.equal(ui.messages.filter(message => message.type === "save-state").at(-1).state.conversations.find(item => item.id === fork.id).parentId, "c1");
});

test("draft text follows its conversation and workspace collapse survives restoration", t => {
  const ui = page(t);
  const first = baseState().conversations[0];
  const second = { id:"c2", workspaceId:"ws-a", title:"第二个", messages:[{role:"user", text:"第二条"}], createdAt:2, updatedAt:2 };
  ui.receive({ type:"ui-state", state:baseState({ conversations:[first, second], activeConversationId:"c1" }) });
  const input = ui.query("#terminalInput");
  input.value = "第一个对话的草稿";
  input.dispatchEvent(new ui.document.defaultView.Event("input", { bubbles:true }));
  const image = { id:"draft-image", name:"draft.png", mime:"image/png", dataUrl:"data:image/png;base64,YQ==" };
  ui.receive({ type:"gateway-images-selected", images:[image] });
  ui.query('[data-conversation="c2"]').click();
  assert.equal(input.value, "", "another conversation starts with its own draft");
  assert.equal(ui.document.querySelectorAll("#chatAttachments img").length, 0);
  input.value = "第二个对话的草稿";
  input.dispatchEvent(new ui.document.defaultView.Event("input", { bubbles:true }));
  ui.query('[data-conversation="c1"]').click();
  assert.equal(input.value, "第一个对话的草稿");
  assert.equal(ui.document.querySelectorAll("#chatAttachments img").length, 1);
  const toggle = ui.query('[data-workspace="ws-a"] .workspace__toggle');
  toggle.click();
  assert.equal(ui.query('[data-workspace="ws-a"] .ws-items').hidden, true);
  let saved = ui.messages.filter(message => message.type === "save-state").at(-1).state;
  assert.equal(saved.drafts["conversation:c1"].text, "第一个对话的草稿");
  assert.equal(saved.drafts["conversation:c2"].text, "第二个对话的草稿");
  ui.receive({ type:"ui-state", state:saved });
  assert.equal(ui.query('[data-workspace="ws-a"] .ws-items').hidden, true);
  assert.equal(input.value, "第一个对话的草稿");
  ui.query('[data-workspace="ws-a"] .workspace__toggle').click();
  ui.query('[data-conversation="c2"]').click();
  assert.equal(input.value, "第二个对话的草稿");
});

test("editing rolls back the current conversation without a branch", t => {
  const ui = page(t);
  const conversation = baseState().conversations[0];
  conversation.messages = [{ role:"user", text:"问题一" }, { role:"agent", text:"回答一" },
    { role:"user", text:"问题二", worktreeCommit:"a".repeat(40), worktreeRoot:"C:/Work/Alpha/.remotecli/worktrees/thread" }, { role:"agent", text:"原回答二" }];
  const settings = { relay:relayBaseUrl, replySource:"gateway", gatewayUrl:"https://gateway.example/v1", models:["model"], defaultModel:"model", tools:[] };
  ui.receive({ type:"ui-state", state:baseState({ conversations:[conversation], settings, activeConversationId:"c1" }) });
  ui.query('[data-turn-index="2"] [aria-label="编辑并回退到此消息"]').click();
  assert.equal(ui.query("#editTurnText").value, "问题二");
  ui.query("#editTurnWorkspace").checked = false;
  ui.query("#editTurnText").value = "修改后的问题二";
  ui.query("#editTurnApply").click();
  ui.query("#confirmOkBtn").click();
  assert.equal(ui.query("#terminalInput").value, "修改后的问题二");
  let saved = ui.messages.filter(message => message.type === "save-state").at(-1).state;
  assert.equal(saved.conversations.length, 1);
  assert.equal(saved.activeConversationId, "c1");
  assert.deepEqual(saved.conversations[0].messages.map(message => message.text), ["问题一", "回答一"]);
  ui.query("#terminalSendBtn").click();
  const request = ui.messages.filter(message => message.type === "gateway-chat-request").at(-1);
  assert.equal(request.conversationId, "c1");
  assert.deepEqual(request.messages.map(message => message.content), ["问题一", "回答一", "修改后的问题二"]);
  ui.receive({ type:"gateway-chat-started", requestId:request.requestId, prompt:request.prompt, worktreeCommit:"a".repeat(32), cancellable:true });
  saved = ui.messages.filter(message => message.type === "save-state").at(-1).state;
  assert.equal(saved.conversations.find(item => item.id === "c1").messages.at(-1).worktreeCommit, "a".repeat(32));
  assert.equal(saved.conversations.find(item => item.id === "c1").editingDraft, false);
});

test("combined rollback previews workspace and deletes later chat only after success", t => {
  const ui = page(t);
  const conversation = baseState().conversations[0];
  conversation.messages[0].worktreeCommit = "a".repeat(40);
  conversation.messages[0].worktreeRoot = "C:/Work/Alpha/.remotecli/worktrees/old";
  ui.receive({ type:"ui-state", state:baseState({ conversations:[conversation], activeConversationId:"c1" }) });
  ui.query('[data-turn-index="0"] [aria-label="编辑并回退到此消息"]').click();
  assert.equal(ui.query("#editTurnWorkspace").checked, true);
  ui.query("#editTurnText").value = "修改后";
  ui.query("#editTurnApply").click();
  const preview = ui.messages.filter(message => message.type === "worktree-preview").at(-1);
  assert.equal(preview.worktreeCommit, "a".repeat(40));
  assert.equal(preview.conversationId,"c1");
  ui.receive({ type:"worktree-preview", requestId:preview.requestId, success:true, fingerprint:"abc", added:1, changed:1, removed:0, directoryChanges:0,
    changes:["将删除：new.txt", "将还原：old.txt"] });
  assert.equal(ui.query("#confirmTitle").textContent, "回退对话与工作区");
  assert.match(ui.query("#confirmText").textContent, /new\.txt/);
  assert.equal(ui.messages.some(message => message.type === "worktree-restore"), false);
  ui.query("#confirmOkBtn").click();
  const restore = ui.messages.filter(message => message.type === "worktree-restore").at(-1);
  assert.equal(restore.fingerprint, "abc");
  assert.ok(ui.document.querySelector('[data-turn-index="0"]'), "chat stays intact until workspace restore succeeds");
  ui.receive({ type:"worktree-restored", requestId:restore.requestId, success:true, worktreeRoot:"C:/Work/Alpha/.remotecli/worktrees/restored", previousWorktreeRoot:"C:/Work/Alpha/.remotecli/worktrees/old", branch:"codex/restored", head:"a".repeat(40) });
  assert.equal(ui.messages.filter(message => message.type === "save-state").at(-1).state.conversations[0].messages.length, 0);
  assert.equal(ui.query("#terminalInput").value, "修改后");
  assert.equal(ui.messages.filter(message => message.type === "save-state").at(-1).state.conversations.length, 1);
});

test("native worktree readiness enables supported nodes and uses the actual worktree for files", t => {
  const ui=page(t);ui.receive({type:"ui-state",state:baseState({activeConversationId:"c1"})});
  ui.receive({type:"gateway-chat-started",requestId:"worktree",prompt:"检查",cancellable:true});
  const action=()=>[...ui.document.querySelectorAll(".turn__ask-action")].at(-1);
  assert.equal(action().disabled,true,"no Git commit means unsupported and grey");
  assert.doesNotMatch(ui.query(".terminal-loading").textContent,/快照|保存工作区/);
  ui.receive({type:"worktree-ready",requestId:"worktree",conversationId:"c1",worktreeRoot:"C:/Work/Alpha/.remotecli/worktrees/thread",branch:"codex/thread",worktreeCommit:"a".repeat(40)});
  assert.equal(action().disabled,false);
  assert.match(ui.query("#topbarWorkspace").textContent,/remotecli.*worktrees/);
  ui.query("#filesToggle").click();
  assert.equal(ui.messages.filter(message=>message.type==="list-workspace-files").at(-1).root,"C:/Work/Alpha/.remotecli/worktrees/thread");
  ui.receive({type:"worktree-started",requestId:"worktree"});
  assert.match(ui.query(".terminal-loading").textContent,/Git/);
  ui.receive({type:"worktree-committed",requestId:"other",worktreeCommit:"b".repeat(40)});
  ui.receive({type:"worktree-committed",requestId:"worktree",worktreeCommit:"c".repeat(40)});
  assert.equal(lastSavedState(ui).conversations[0].messages.at(-1).worktreeCommit,"c".repeat(40));
});

test("old or unsupported rollback nodes stay disabled and do not open the dialog", t => {
  const ui=page(t), snapshot=baseState({activeConversationId:"c1"});
  snapshot.conversations[0].messages[0].checkpointId="a".repeat(32);
  ui.receive({type:"ui-state",state:snapshot});
  const button=ui.query(".turn__ask-action"); assert.equal(button.disabled,true); assert.equal(button.getAttribute("aria-disabled"),"true");
  button.click();assert.equal(ui.query("#editTurnScrim").hidden,true);
});

test("MCP configuration is validated, persisted and passed to the gateway", t => {
  const ui = page(t);
  addWorkspace(ui);
  ui.query("#accountBtn").click();
  ui.query('input[name="replySource"][value="gateway"]').click();
  ui.query("#gatewayUrl").value = "https://gateway.example/v1";
  ui.query('[data-settings-pane="models"]').click(); ui.query("#addModelBtn").click();
  const model = ui.query("#modelList input"); model.value = "test-model"; model.dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  const servers = [{ id:"local", name:"Local", transport:"stdio", command:"C:\\Tools\\server.exe", args:["serve"], env:{API_KEY:"LOCAL_MCP_KEY"}, enabled:true },
    { id:"web", name:"Web", transport:"http", url:"https://mcp.example/mcp", headers:{Authorization:"MCP_AUTH_HEADER"}, enabled:true }];
  ui.query("#mcpServersJson").value = JSON.stringify(servers);
  ui.query("#settingsSaveBtn").click();
  let saved = ui.messages.filter(message => message.type === "save-state").at(-1).state;
  assert.deepEqual(saved.settings.mcpServers, servers);
  ui.query("#terminalInput").value = "test";
  ui.query("#terminalInput").dispatchEvent(new ui.document.defaultView.Event("input", { bubbles: true }));
  ui.query("#terminalSendBtn").click();
  assert.deepEqual(ui.messages.filter(message => message.type === "gateway-chat-request").at(-1).mcpServers, servers);
  ui.query("#accountBtn").click();
  ui.query("#mcpServersJson").value = JSON.stringify([{ id:"bad", name:"Bad", transport:"http", url:"https://mcp.example/?token=secret", enabled:true }]);
  ui.query("#settingsSaveBtn").click();
  assert.equal(ui.query("#mcpConfigError").hidden, false);
  assert.match(ui.query("#mcpConfigError").textContent, /查询参数/);
});

test("image-only messages preserve attachments and bound historical images", t => {
  const ui = page(t);
  const image = id => ({ id, name:id + ".png", mime:"image/png", dataUrl:"data:image/png;base64,YQ==" });
  const settings = { relay:relayBaseUrl, replySource:"gateway", gatewayUrl:"https://gateway.example/v1", models:["model"], defaultModel:"model", tools:[] };
  const previous = { id:"c1", workspaceId:"ws-a", title:"已有", messages:[{ role:"user", text:"earlier", images:[image("old1"), image("old2"), image("old3"), image("old4")] }], createdAt:1, updatedAt:1 };
  ui.receive({ type:"ui-state", state:baseState({ settings, conversations:[previous], activeConversationId:"c1" }) });
  ui.receive({ type:"gateway-images-selected", images:[image("new1"), image("new2")] });
  assert.equal(ui.messages.some(message => message.type === "gateway-image-select"), false,
    "the composer must not open a native image picker");
  assert.equal(ui.document.querySelectorAll("#chatAttachments img").length, 2);
  ui.query("#terminalSendBtn").click();
  const request = ui.messages.filter(message => message.type === "gateway-chat-request").at(-1);
  assert.equal(request.prompt, "");
  assert.equal(request.messages.at(-1).images.length, 2);
  assert.equal(request.messages.at(-1).images[1].id, "new2");
  assert.equal(request.messages.flatMap(message => message.images || []).length, 4);
  ui.receive({ type:"gateway-chat-started", requestId:request.requestId, prompt:"", cancellable:true });
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1).state;
  assert.equal(saved.conversations[0].messages.at(-1).images.length, 2);
  assert.equal(ui.document.querySelectorAll("#chatAttachments img").length, 0);
});

test("later turns include bounded untrusted tool history and PTY session ids in order", t => {
  const ui = page(t);
  const settings = { relay:relayBaseUrl, replySource:"gateway", gatewayUrl:"https://gateway.example/v1", models:["model"], defaultModel:"model", tools:["exec_command"] };
  const conversation = { id:"c1", workspaceId:"ws-a", title:"进程", createdAt:1, updatedAt:1, messages:[
    { role:"user", text:"启动服务" }, { role:"agent", text:"开始运行" },
    { role:"tool", name:"exec_command", arguments:"tty=true", status:"ok", text:"x".repeat(2500) + "\n[运行中 session_id: abc123]" },
    { role:"tool", name:"old_pending", status:"running", text:"skip" },
    { role:"agent", text:"服务仍在运行" }
  ] };
  ui.receive({ type:"ui-state", state:baseState({ settings, conversations:[conversation], activeConversationId:"c1" }) });
  ui.query("#terminalInput").value = "继续查看";
  ui.query("#terminalInput").dispatchEvent(new ui.document.defaultView.Event("input", { bubbles:true }));
  ui.query("#terminalSendBtn").click();
  const request = ui.messages.filter(message => message.type === "gateway-chat-request").at(-1);
  assert.deepEqual(request.messages.map(message => message.role), ["user", "assistant", "user", "assistant", "user"]);
  assert.match(request.messages[2].content, /不可信的历史工具输出/);
  assert.match(request.messages[2].content, /工具：exec_command/);
  assert.match(request.messages[2].content, /session_id: abc123/);
  assert.ok(request.messages[2].content.length < 2400);
  assert.equal(request.messages.at(-1).content, "继续查看");
  assert.equal(ui.messages.filter(message => message.type === "gateway-chat-request").length, 1);
});

test("sidebar splitter resizes and persists the width", t => {
  const ui = page(t);
  const split = ui.query("#bodySplit");
  const body = ui.query(".body");
  assert.ok(split);
  assert.equal(body.style.getPropertyValue("--side-width"), "");

  split.dispatchEvent(new ui.document.defaultView.PointerEvent("pointerdown", { bubbles: true, clientX: 212 }));
  split.dispatchEvent(new ui.document.defaultView.PointerEvent("pointermove", { bubbles: true, clientX: 312 }));
  const width = body.style.getPropertyValue("--side-width");
  assert.match(width, /^312px$/);
  assert.ok(split.classList.contains("is-dragging"));

  split.dispatchEvent(new ui.document.defaultView.PointerEvent("pointerup", { bubbles: true, clientX: 312 }));
  assert.equal(split.classList.contains("is-dragging"), false);
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(saved.state.view.sideWidth, 212);

  // 键盘微调
  split.dispatchEvent(new ui.document.defaultView.KeyboardEvent("keydown", { bubbles: true, key: "ArrowRight" }));
  assert.match(body.style.getPropertyValue("--side-width"), /^228px$/);

  // 双击复位
  split.dispatchEvent(new ui.document.defaultView.MouseEvent("dblclick", { bubbles: true }));
  assert.equal(body.style.getPropertyValue("--side-width"), "");
  const restored = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(restored.state.view.sideWidth, null);
});

test("file preview splitter supports dragging, keyboard and saved width", t => {
  const ui = page(t);
  ui.query("#filesPanelToggle").click();
  const split = ui.query("#filesSplit"), body = ui.query(".body");
  split.dispatchEvent(new ui.document.defaultView.PointerEvent("pointerdown", { bubbles:true, clientX:700, button:0 }));
  split.dispatchEvent(new ui.document.defaultView.PointerEvent("pointermove", { bubbles:true, clientX:630, button:0 }));
  assert.ok(parseFloat(body.style.getPropertyValue("--files-width")) > 420);
  split.dispatchEvent(new ui.document.defaultView.PointerEvent("pointerup", { bubbles:true, clientX:630, button:0 }));
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.ok(saved.state.view.filesWidth > 420);
  const replay = page(t); replay.receive({ type:"ui-state", state:saved.state });
  assert.equal(replay.query(".body").style.getPropertyValue("--files-width"), saved.state.view.filesWidth + "px");
  replay.query("#filesPanelToggle").click();
  replay.query("#filesSplit").dispatchEvent(new replay.document.defaultView.KeyboardEvent("keydown", { bubbles:true, key:"ArrowRight" }));
  assert.ok(parseFloat(replay.query(".body").style.getPropertyValue("--files-width")) < saved.state.view.filesWidth);
});

test("real terminal emulator preserves VT state, user input, and saved screen restoration", { timeout: 60000 }, async t => {
  if (process.platform !== "win32") {
    t.skip("the shipped desktop terminal targets Windows WebView2");
    return;
  }
  const edgePath = process.env.REMOTECLI_TEST_BROWSER || "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
  assert.ok(fs.existsSync(edgePath), `Edge is required for the real terminal test: ${edgePath}`);
  const { chromium } = require("playwright-core");
  const readAsset = name => fs.readFileSync(path.join(repo, "src/RemoteCli.Desktop/UI/vendor", name), "utf8");
  const assembled = html
    .replace("/*LONGSHELL_XTERM_CSS*/", () => readAsset("xterm.css"))
    .replace("/*LONGSHELL_XTERM_JS*/", () => readAsset("xterm.js"))
    .replace("/*LONGSHELL_XTERM_SERIALIZE_JS*/", () => readAsset("addon-serialize.js"));
  const browser = await chromium.launch({ executablePath: edgePath, headless: true });
  const browserErrors = [];
  t.after(async () => { await browser.close(); assert.deepEqual(browserErrors, []); });

  async function openPage() {
    const browserPage = await browser.newPage({ viewport: { width: 1100, height: 760 } });
    browserPage.on("pageerror", error => browserErrors.push(error.message));
    await browserPage.addInitScript(() => {
      const listeners = [];
      window.__longshellMessages = [];
      window.__longshellReceive = message => listeners.forEach(listener => listener({ data: message }));
      window.chrome = { webview: {
        postMessage(message) { window.__longshellMessages.push(JSON.parse(JSON.stringify(message))); },
        addEventListener(type, listener) { if (type === "message") listeners.push(listener); }
      } };
    });
    await browserPage.route("https://longshell.test/", route => route.fulfill({ status: 200, contentType: "text/html", body: assembled }));
    await browserPage.goto("https://longshell.test/", { waitUntil: "load" });
    await receive(browserPage, { type: "init", userName: "controller", machineName: "test-host", relay: relayBaseUrl });
    return browserPage;
  }
  function receive(browserPage, message) {
    return browserPage.evaluate(value => window.__longshellReceive(value), message);
  }
  function messages(browserPage, type) {
    return browserPage.evaluate(value => window.__longshellMessages.filter(message => message.type === value), type);
  }
  async function ack(browserPage, request, conversationId) {
    await receive(browserPage, { type: "pty-ack", conversationId, sessionId: "ptybrowser1",
      requestId: request.requestId, success: true });
  }

  const terminalPage = await openPage();
  await terminalPage.click("#addWorkspaceBtn");
  await receive(terminalPage, { type: "workspace-folder", path: "D:\\Work\\Terminal" });
  await terminalPage.fill("#trustWorkspaceName", "Terminal");
  await terminalPage.click("#trustWorkspaceBtn");
  await receive(terminalPage, { type: "ui-state", state: { settings: {
    replySource: "gateway", gatewayUrl: "https://gateway.example.invalid", gatewayApiKey: "test",
    backend: "openai", models: ["test-model"], defaultModel: "test-model", effort: "high",
    tools: ["exec_command"], mcpServers: []
  } } });
  await terminalPage.click('[data-page-pill="terminal"]');
  await terminalPage.fill("#terminalInput", "run a terminal");
  await terminalPage.click("#terminalSendBtn");
  const chatRequest = (await messages(terminalPage, "gateway-chat-request")).at(-1);
  assert.ok(chatRequest);
  const conversationId = chatRequest.conversationId;
  await receive(terminalPage, { type: "gateway-chat-started", requestId: chatRequest.requestId,
    prompt: chatRequest.prompt, cancellable: true });
  await receive(terminalPage, { type: "tool-call", requestId: chatRequest.requestId,
    callId: "call-pty", name: "exec_command", arguments: "interactive test" });
  await receive(terminalPage, { type: "pty-session", conversationId, requestId: chatRequest.requestId,
    callId: "call-pty", sessionId: "ptybrowser1", columns: 80, rows: 24, tty: true });
  await terminalPage.waitForSelector(".pty-panel .xterm-rows");
  await terminalPage.waitForFunction(() => window.__longshellMessages.some(message => message.type === "pty-watch"));
  assert.equal((await messages(terminalPage, "pty-user-input")).length, 0,
    "terminal initialization and output replies must not become user input");

  const first = "one\r\ntwo\r\n\u001b[";
  const second = "3;4H中😀\u001b[1;1HONE\u001b[2;1H\u001b[2K替换";
  let watch = (await messages(terminalPage, "pty-watch")).at(-1);
  await receive(terminalPage, { type: "pty-output", conversationId, sessionId: "ptybrowser1",
    requestId: watch.requestId, cursor: 0, nextCursor: first.length, output: first, done: false, truncated: false });
  await terminalPage.waitForFunction(count => window.__longshellMessages.filter(message => message.type === "pty-watch").length > count,
    (await messages(terminalPage, "pty-watch")).length);
  watch = (await messages(terminalPage, "pty-watch")).at(-1);
  await receive(terminalPage, { type: "pty-output", conversationId, sessionId: "ptybrowser1",
    requestId: watch.requestId, cursor: first.length, nextCursor: first.length + second.length,
    output: second, done: false, truncated: false });
  await terminalPage.waitForFunction(() => {
    const rows = document.querySelector(".pty-panel .xterm-rows");
    return rows && rows.textContent.includes("ONE") && rows.textContent.includes("中😀") && rows.textContent.includes("替换");
  });
  const rendered = await terminalPage.locator(".pty-panel .xterm-rows").textContent();
  assert.ok(!rendered.includes("two"), "CSI erase must change the screen instead of appending simplified text");

  await terminalPage.locator(".pty-panel .xterm-helper-textarea").focus();
  await terminalPage.keyboard.type("A");
  await terminalPage.fill(".pty-panel__manual", "hello");
  await terminalPage.press(".pty-panel__manual", "Enter");
  const inputs = await messages(terminalPage, "pty-user-input");
  assert.deepEqual(inputs.map(message => message.input), ["A", "hello\r"]);
  for (const input of inputs) await ack(terminalPage, input, conversationId);

  await terminalPage.fill('.pty-panel input[aria-label="终端列数"]', "100");
  await terminalPage.fill('.pty-panel input[aria-label="终端行数"]', "28");
  await terminalPage.getByRole("button", { name: "调整尺寸", exact: true }).click();
  const resize = (await messages(terminalPage, "pty-resize")).at(-1);
  assert.equal(resize.columns, 100);
  assert.equal(resize.rows, 28);
  await ack(terminalPage, resize, conversationId);

  await terminalPage.getByRole("button", { name: "停止终端", exact: true }).click();
  const stop = (await messages(terminalPage, "pty-user-stop")).at(-1);
  assert.ok(stop);
  await ack(terminalPage, stop, conversationId);
  await terminalPage.waitForFunction(count => window.__longshellMessages.filter(message => message.type === "pty-watch").length > count,
    (await messages(terminalPage, "pty-watch")).length);
  watch = (await messages(terminalPage, "pty-watch")).at(-1);
  const finalCursor = first.length + second.length;
  await receive(terminalPage, { type: "pty-output", conversationId, sessionId: "ptybrowser1",
    requestId: watch.requestId, cursor: finalCursor, nextCursor: finalCursor, output: "", done: true, truncated: false });
  await terminalPage.waitForFunction(() => {
    const saves = window.__longshellMessages.filter(message => message.type === "save-state");
    const conversation = saves.length && saves.at(-1).state.conversations[0];
    return conversation && conversation.ptySessions && conversation.ptySessions[0] && conversation.ptySessions[0].done;
  });
  const saved = (await messages(terminalPage, "save-state")).at(-1).state;
  const savedPty = saved.conversations[0].ptySessions[0];
  assert.equal(savedPty.columns, 100);
  assert.equal(savedPty.rows, 28);
  assert.equal(savedPty.cursor, finalCursor);
  assert.ok(savedPty.snapshot.length > 0 || savedPty.segments.some(segment => segment.text.includes("替换")));

  const restoredPage = await openPage();
  await receive(restoredPage, { type: "ui-state", state: saved });
  await restoredPage.waitForFunction(() => {
    const rows = document.querySelector(".pty-panel .xterm-rows");
    return rows && rows.textContent.includes("ONE") && rows.textContent.includes("中😀") && rows.textContent.includes("替换");
  });
  assert.equal((await messages(restoredPage, "pty-watch")).length, 0,
    "a restored completed session must replay locally without contacting a new backend");
  assert.equal((await messages(restoredPage, "pty-user-input")).length, 0,
    "replaying untrusted terminal output must not send input");
});

test("restored terminal sessions are validated, bounded, and disconnected from the old backend", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: {
    workspaces: [{ id: "w1", name: "Alpha", path: "C:\\Work\\Alpha", trusted: true }],
    conversations: [{
      id: "c1", workspaceId: "w1", title: "terminal history", messages: [{ role: "pty", sessionId: "pty1", at: 1 }],
      ptySessions: [
        { id: "pty1", columns: 80, rows: 24, cursor: 12, done: false, error: "", snapshot: "",
          snapshotColumns: 80, snapshotRows: 24, segments: [{ columns: 80, rows: 24, text: "saved" }], tailLength: 5 },
        { id: "bad-size", columns: 5, rows: 24, cursor: 0, done: false, snapshot: "", segments: [] },
        { id: "bad-cursor", columns: 80, rows: 24, cursor: -1, done: false, snapshot: "", segments: [] },
        { id: "bad-snapshot", columns: 80, rows: 24, cursor: 0, done: false, snapshot: "x".repeat(1048577), segments: [] },
        { id: "bad-segment", columns: 80, rows: 24, cursor: 0, done: false, snapshot: "",
          segments: [{ columns: 80, rows: 24, text: 5 }] }
      ]
    }],
    activeWorkspaceId: "w1",
    activeConversationId: "c1"
  } });
  assert.ok(ui.query(".pty-panel"), "a saved terminal must still render after the interface reloads");

  addWorkspace(ui, "Beta", "C:////Work////Beta");
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  const savedPty = saved.state.conversations.find(conversation => conversation.id === "c1").ptySessions;
  assert.deepEqual(savedPty.map(session => session.id), ["pty1"]);
  assert.equal(savedPty[0].done, true);
  assert.match(savedPty[0].error, /界面重启后已断开/);
  assert.equal(savedPty[0].tailLength, 5);
  assert.equal(ui.messages.filter(message => message.type === "pty-watch").length, 0,
    "restored sessions must not watch a backend terminal from a previous process");
});

test("deleting a conversation disposes its terminal panel and closes the gateway session", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: {
    workspaces: [{ id: "w1", name: "Alpha", path: "C:\\Work\\Alpha", trusted: true }],
    conversations: [{
      id: "c1", workspaceId: "w1", title: "terminal history", messages: [{ role: "pty", sessionId: "pty1", at: 1 }],
      ptySessions: [{ id: "pty1", columns: 80, rows: 24, cursor: 0, done: false, error: "", snapshot: "",
        snapshotColumns: 80, snapshotRows: 24, segments: [{ columns: 80, rows: 24, text: "saved" }], tailLength: 5 }]
    }],
    activeWorkspaceId: "w1",
    activeConversationId: "c1"
  } });
  assert.ok(ui.query("#terminalThread .pty-panel"));

  ui.query('[data-conversation="c1"]').dispatchEvent(new ui.document.defaultView.MouseEvent("contextmenu", { bubbles: true, clientX: 100, clientY: 100 }));
  ui.query('#convMenu [data-action="delete"]').click();
  ui.query("#confirmOkBtn").click();

  assert.equal(ui.document.querySelector("#terminalThread .pty-panel"), null, "deleting a conversation must dispose its terminal emulator");
  assert.deepEqual(ui.messages.filter(message => message.type === "gateway-session-close"), [{ type: "gateway-session-close", conversationId: "c1" }]);
  const saved = ui.messages.filter(message => message.type === "save-state").at(-1);
  assert.equal(saved.state.conversations.some(conversation => conversation.id === "c1"), false);
});

test("code highlight plugin installs from a local package and toggles in settings", t => {
  const ui = page(t);
  ui.receive({ type: "plugin-settings", plugins: [{ id: "office-preview", installed: false }, { id: "code-highlight", installed: false }] });
  ui.query("#accountBtn").click();
  ui.query('[data-settings-pane="tools"]').click();

  const stateEl = ui.query("#pluginState-code-highlight");
  const button = ui.query("#pluginToggle-code-highlight");
  assert.equal(stateEl.textContent, "未安装");
  assert.equal(button.textContent, "安装");

  button.click();
  assert.deepEqual(ui.messages.at(-1), { type: "install-plugin", id: "code-highlight" });
  assert.equal(button.textContent, "取消");
  assert.equal(button.classList.contains("is-danger"), true);
  ui.receive({ type: "plugin-status", id: "code-highlight", state: "installing", phase: "download", percent: 12 });
  assert.equal(stateEl.textContent, "正在下载代码高亮插件… 12%");

  ui.receive({ type: "plugin-settings", plugins: [{ id: "code-highlight", installed: true }] });
  assert.equal(stateEl.textContent, "已安装");
  assert.equal(button.textContent, "卸载");
  assert.equal(button.disabled, false);

  ui.query("#pluginInstallFromFile").click();
  assert.deepEqual(ui.messages.at(-1), { type: "install-plugin-file", id: "code-highlight" });

  button.click();
  assert.deepEqual(ui.messages.at(-1), { type: "uninstall-plugin", id: "code-highlight" });
  assert.equal(stateEl.textContent, "正在卸载代码高亮插件…");
});

test("code highlighting colors completed chat blocks, stays plain while streaming, and falls back when removed", t => {
  const ui = page(t);
  const window = ui.document.defaultView;
  window.LongShellPlugins = { "code-highlight": { api: 1, version: "test", highlight(code, label) {
    return { language: label, lines: code.split("\n").map(line => [{ text: line, color: "#ff0000", fontStyle: 0 }]) };
  } } };
  window.document.dispatchEvent(new window.CustomEvent("longshell-plugin-ready", { detail: { id: "code-highlight" } }));

  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.query('[data-page-pill="terminal"]').click();
  ui.receive({ type: "gateway-chat-started", requestId: "hl", prompt: "写点代码", cancellable: true });
  const text = "```ts\nconst x = 1;\n```";
  ui.receive({ type: "gateway-chat-delta", requestId: "hl", text });
  let code = ui.query(".chat-markdown pre code");
  assert.equal(code.textContent, "const x = 1;");
  assert.equal(code.dataset.highlighted, undefined, "streaming code blocks stay plain");

  ui.receive({ type: "gateway-chat-response", requestId: "hl", success: true, response: text });
  code = ui.query(".chat-markdown pre code");
  assert.equal(code.dataset.highlighted, "done");
  assert.equal(code.querySelectorAll("span").length, 1);
  assert.match(code.querySelector("span").getAttribute("style"), /color:\s*(#ff0000|rgb\(255, 0, 0\))/);

  ui.receive({ type: "plugin-settings", plugins: [{ id: "code-highlight", installed: false }] });
  code = ui.query(".chat-markdown pre code");
  assert.equal(code.textContent, "const x = 1;");
  assert.equal(code.querySelectorAll("span").length, 0, "uninstalling the plugin falls back to plain text");
});

test("text preview pages by line with gutter, wrap, retry and end state", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.query("#filesToggle").click();
  const list = ui.messages.at(-1);
  ui.receive({
    type: "workspace-files", path: "C://Work//Alpha", requestId: list.requestId,
    entries: [{ name: "big.log", path: "C://Work//Alpha//big.log", isDirectory: false, size: 42 }]
  });
  ui.query(".file-row").click();
  const read = ui.messages.at(-1);
  ui.receive({
    type: "workspace-file", path: "C://Work//Alpha//big.log", requestId: read.requestId,
    kind: "text", content: "line 1\nline 2", offset: 1, lines: 2, eof: false, version: "10:1"
  });
  assert.equal(ui.query("#filesPreviewText").textContent, "line 1\nline 2");
  assert.equal(ui.query("#filesPreviewReader").hidden, false);
  assert.equal(ui.document.querySelectorAll("#filesPreviewLines .files-line").length, 2);
  assert.equal(ui.query("#filesPreviewLines .files-line__no").textContent, "1");
  assert.match(ui.query("#filesPreviewReaderStatus").textContent, /已加载 2 行/);
  assert.equal(ui.query("#filesPreviewReaderFoot").hidden, false);

  ui.query("#filesPreview").dispatchEvent(new ui.document.defaultView.Event("scroll"));
  const pageRequest = ui.messages.at(-1);
  assert.equal(pageRequest.type, "read-text-page");
  assert.equal(pageRequest.offset, 3);
  assert.equal(pageRequest.limit, 2000);
  assert.equal(pageRequest.root, "C://Work//Alpha");

  ui.receive({ type: "text-page", path: "C://Work//Alpha//big.log", requestId: pageRequest.requestId, error: "读取失败，请重试。" });
  assert.equal(ui.query("#filesPreviewReaderStatus").textContent, "读取失败，请重试。");
  assert.equal(ui.query("#filesPreviewMoreBtn").textContent, "重试");

  ui.query("#filesPreviewMoreBtn").click();
  const retry = ui.messages.at(-1);
  assert.equal(retry.type, "read-text-page");
  assert.equal(retry.offset, 3);
  ui.receive({
    type: "text-page", path: "C://Work//Alpha//big.log", requestId: retry.requestId,
    offset: 3, lines: 1, content: "line 3", eof: true, version: "10:1"
  });
  assert.equal(ui.document.querySelectorAll("#filesPreviewLines .files-line").length, 3);
  assert.equal(ui.query("#filesPreviewLines .files-line:last-child .files-line__no").textContent, "3");
  assert.match(ui.query("#filesPreviewReaderStatus").textContent, /已到末尾/);
  assert.equal(ui.query("#filesPreviewReaderFoot").hidden, true);

  ui.query("#filesPreviewWrapBtn").click();
  assert.equal(ui.query("#filesPreviewLines").classList.contains("is-wrap"), true);
  assert.equal(ui.query("#filesPreviewWrapBtn").getAttribute("aria-pressed"), "true");
});

test("html previews are sandboxed and http links open the built-in browser", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.query("#browserToggle").click();
  assert.deepEqual(ui.messages.at(-1), { type: "open-browser" });

  ui.query("#filesToggle").click();
  const list = ui.messages.at(-1);
  ui.receive({
    type: "workspace-files", path: "C://Work//Alpha", requestId: list.requestId,
    entries: [{ name: "a.html", path: "C://Work//Alpha//a.html", isDirectory: false, size: 210 }]
  });
  ui.query(".file-row").click();
  const read = ui.messages.at(-1);
  ui.receive({
    type: "workspace-file", path: "C://Work//Alpha//a.html", requestId: read.requestId, kind: "html",
    content: "<html><head><script>alert(1)</script><base href=\"https://evil.example/\"></head>" +
      "<body onload=\"alert(2)\"><a href=\"https://example.com\">x</a>" +
      "<img src=\"data:image/png;base64,AA\"><img src=\"https://evil.example/x.png\"></body></html>"
  });
  const frame = ui.query("#filesPreviewHtml");
  assert.equal(frame.hidden, false);
  assert.equal(frame.getAttribute("sandbox"), "");
  assert.match(frame.srcdoc, /Content-Security-Policy/);
  assert.doesNotMatch(frame.srcdoc, /<script|alert\(1\)|base href/i);
  assert.doesNotMatch(frame.srcdoc, /https:\/\/evil\.example/);
  assert.match(frame.srcdoc, /data:image\/png/);

  ui.receive({ type: "gateway-chat-started", requestId: "link", prompt: "看这里", cancellable: true });
  ui.receive({ type: "gateway-chat-response", requestId: "link", success: true, response: "[站点](https://example.com)" });
  ui.query(".chat-markdown a").click();
  assert.deepEqual(ui.messages.at(-1), { type: "open-browser", url: "https://example.com" });
});

function gatewayState(settingsExtra = {}) {
  return baseState({
    activeConversationId: "c1",
    settings: {
      relay: relayBaseUrl, replySource: "gateway", gatewayUrl: "https://gw.example.com/v1", gatewayApiKey: "gw-key",
      backend: "openai", models: ["model-a"], defaultModel: "model-a", ...settingsExtra
    }
  });
}

function lastSavedState(ui) {
  return ui.messages.filter(message => message.type === "save-state").at(-1).state;
}

test("provider edits survive settings save and selecting another provider never copies old credentials", t => {
  const ui = page(t);
  const providers = [
    {id:"a",name:"A",baseUrl:"https://a.example/v1",apiKey:"dummy-a",backend:"openai",enabled:true},
    {id:"b",name:"B",baseUrl:"https://b.example/v1",apiKey:"dummy-b",backend:"anthropic",enabled:true}
  ];
  ui.receive({type:"ui-state",state:gatewayState({providers,activeProviderId:"a",gatewayUrl:providers[0].baseUrl,gatewayApiKey:providers[0].apiKey})});
  ui.query("#accountBtn").click();
  const edit = (selector,value) => {
    const input=ui.query(selector); input.value=value;
    input.dispatchEvent(new ui.document.defaultView.Event("change",{bubbles:true}));
  };
  edit('[data-provider-id="a"] .provider-row__url',"https://edited.example/v1");
  edit('[data-provider-id="a"] .provider-row__key',"dummy-edited");
  edit('[data-provider-id="a"] .provider-row__backend',"azure");
  ui.query("#settingsSaveBtn").click();
  let saved=lastSavedState(ui);
  assert.equal(saved.settings.providers[0].baseUrl,"https://edited.example/v1");
  assert.equal(saved.settings.providers[0].apiKey,"dummy-edited");
  assert.equal(saved.settings.providers[0].backend,"azure");
  ui.receive({type:"ui-state",state:saved}); ui.query("#accountBtn").click();
  ui.query('[data-provider-id="b"] .provider-row__use').click();
  ui.query("#settingsSaveBtn").click(); saved=lastSavedState(ui);
  assert.deepEqual(saved.settings.providers[1],providers[1]);
  ui.receive({type:"ui-state",state:saved}); ui.query("#accountBtn").click();
  ui.query('[data-provider-id="b"] .provider-row__remove').click();
  ui.query("#settingsSaveBtn").click(); saved=lastSavedState(ui);
  assert.equal(saved.settings.providers.length,1);
  assert.equal(saved.settings.providers[0].apiKey,"dummy-edited");
});

function sendGatewayText(ui,text){
  ui.query('[data-page-pill="terminal"]').click();
  ui.query("#terminalInput").value=text;
  ui.query("#terminalInput").dispatchEvent(new ui.document.defaultView.Event("input",{bubbles:true}));
  ui.query("#terminalSendBtn").click();
  return ui.messages.filter(message=>message.type==="gateway-chat-request").at(-1);
}

test("gateway bootstrap retains early constraints beyond twenty messages", t => {
  const ui=page(t), snapshot=gatewayState();
  snapshot.conversations[0].messages=Array.from({length:30},(_,i)=>({role:i%2?"agent":"user",text:i===0?"EARLY_CONSTRAINT":"message "+i,at:i+1}));
  ui.receive({type:"ui-state",state:snapshot});
  const request=sendGatewayText(ui,"continue");
  assert.equal(request.messages.length,31);
  assert.equal(request.messages[0].content,"EARLY_CONSTRAINT");
});

test("gateway history revision carries compacted backend context across turns and reload", t => {
  const ui=page(t);ui.receive({type:"ui-state",state:gatewayState()});
  ui.receive({type:"gateway-chat-started",requestId:"first",prompt:"start"});
  ui.receive({type:"context-compression-started",requestId:"first",removedMessages:10,keptMessages:6});
  ui.receive({type:"context-compression-finished",requestId:"first",removedMessages:10,tokensBefore:90000,tokensAfter:10000,summary:"PRIVATE_SUMMARY",changes:[]});
  ui.receive({type:"gateway-chat-response",requestId:"first",success:true,response:"done",historyRevision:"revision-one"});
  let request=sendGatewayText(ui,"next");
  assert.equal(request.historyRevision,"revision-one");
  assert.equal(request.messages.length,1,"backend revision supplies the previous history");
  assert.doesNotMatch(ui.query("#terminalThread").textContent,/PRIVATE_SUMMARY/);
  ui.receive({type:"gateway-chat-started",requestId:request.requestId,prompt:"next"});
  ui.receive({type:"gateway-chat-response",requestId:request.requestId,success:true,response:"next done",historyRevision:"revision-two"});
  ui.receive({type:"ui-state",state:lastSavedState(ui)});
  request=sendGatewayText(ui,"after reload");
  assert.equal(request.historyRevision,"revision-two");
  assert.equal(request.messages.length,1);
});

test("gateway history mismatch retries from full UI history instead of losing old context", t => {
  const ui=page(t), snapshot=gatewayState();
  snapshot.conversations[0].gatewayHistoryRevision="missing";
  snapshot.conversations[0].gatewayHistoryMessageCount=snapshot.conversations[0].messages.length;
  ui.receive({type:"ui-state",state:snapshot});
  const request=sendGatewayText(ui,"retry me");
  ui.receive({type:"gateway-chat-response",requestId:request.requestId,success:false,resetHistory:true,error:"历史不可用，请重试"});
  const next=sendGatewayText(ui,"retry me");
  assert.equal(next.historyRevision,"");
  assert.ok(next.messages.length>1);
  assert.match(next.messages[0].content,/ssh/);
});

test("executing a plan from Q&A uses execution tools and tracks backend acceptance failure and retry", t => {
  const ui=page(t);ui.receive({type:"ui-state",state:gatewayState({globalTools:["write_file"]})});
  ui.receive({type:"gateway-chat-started",requestId:"proposal",prompt:"plan"});
  ui.receive({type:"plan-proposed",requestId:"proposal",planId:"plan",planHash:"hash",title:"Implement",markdown:"change file"});
  ui.receive({type:"gateway-chat-response",requestId:"proposal",success:true,response:"ready"});
  ui.query("#composerQaBtn").click(); ui.query("#planExecuteBtn").click();
  let request=ui.messages.filter(message=>message.type==="gateway-chat-request").at(-1);
  const plan=()=>lastSavedState(ui).conversations[0].messages.find(message=>message.role==="plan");
  assert.equal(request.composerMode,"default"); assert.ok(request.enabledTools.includes("write_file"));
  assert.equal(request.planHash,"hash"); assert.equal(plan().status,"submitting");
  ui.receive({type:"plan-execution-accepted",requestId:request.requestId,planId:"plan"});
  assert.equal(plan().status,"running");
  ui.receive({type:"gateway-chat-response",requestId:request.requestId,success:false,error:"validation failed"});
  assert.equal(plan().status,"failed");
  ui.query(".plan-card__execute").click(); assert.equal(plan().status,"pending");
  ui.query("#planExecuteBtn").click(); request=ui.messages.filter(message=>message.type==="gateway-chat-request").at(-1);
  ui.receive({type:"gateway-chat-started",requestId:request.requestId,prompt:"execute"});
  ui.receive({type:"plan-execution-accepted",requestId:request.requestId,planId:"plan"});
  ui.receive({type:"gateway-chat-response",requestId:request.requestId,success:true,response:"implemented"});
  assert.equal(plan().status,"succeeded");
});

test("automatic approval tier survives UI state reload", t => {
  const ui=page(t), snapshot=gatewayState();snapshot.composer={approval:"auto",tier:"high",mode:"default"};
  ui.receive({type:"ui-state",state:snapshot});
  ui.query("#accountBtn").click();ui.query("#settingsSaveBtn").click();
  let saved=lastSavedState(ui);assert.equal(saved.composer.approval,"auto");assert.equal(saved.composer.tier,"high");
  ui.receive({type:"ui-state",state:saved});ui.query("#accountBtn").click();ui.query("#settingsSaveBtn").click();
  saved=lastSavedState(ui);assert.equal(saved.composer.approval,"auto");assert.equal(saved.composer.tier,"high");
});

test("interrupted plan execution becomes retryable after reload", t => {
  const ui=page(t), snapshot=gatewayState();
  snapshot.conversations[0].messages.push({role:"plan",planId:"interrupted",title:"Plan",markdown:"steps",status:"running",at:2});
  ui.receive({type:"ui-state",state:snapshot});
  assert.equal(ui.query(".plan-card__execute").disabled,false);
  ui.query(".plan-card__execute").click();
  assert.equal(lastSavedState(ui).conversations[0].messages.find(message=>message.role==="plan").status,"pending");
});

test("waiting and thinking retain the horizontal glow and disappear when output starts", t => {
  const ui=page(t);ui.receive({type:"ui-state",state:baseState({activeConversationId:"c1"})});
  ui.receive({type:"gateway-chat-started",requestId:"glow",prompt:"整理日志",cancellable:true});
  const loading=ui.query("#terminalThread > .terminal-loading");
  assert.equal(loading.getAttribute("role"),"status");assert.equal(loading.getAttribute("aria-live"),"polite");
  assert.equal(loading.querySelector(".terminal-loading__orb").getAttribute("aria-hidden"),"true");
  assert.equal(loading.querySelector(".terminal-loading__caption").textContent,"正在等待模型响应");
  assert.equal(loading.querySelector(".terminal-loading__ring"),null);
  assert.match(html,/prefers-reduced-motion:reduce.*terminal-loading__orb/);
  ui.receive({type:"gateway-chat-delta",requestId:"glow",kind:"reasoning",text:"先看文件"});
  assert.equal(ui.query(".terminal-loading__caption").textContent,"正在思考");
  assert.equal(ui.query(".terminal-loading"),loading);
  ui.receive({type:"gateway-chat-delta",requestId:"glow",kind:"text",text:"看完了"});
  assert.equal(ui.document.querySelector(".terminal-loading"),null);
  ui.receive({type:"gateway-chat-response",requestId:"glow",success:true,response:"看完了"});
  assert.equal(ui.document.querySelector(".terminal-loading"),null);
});

test("process tree starts collapsed and keeps the user's manual choice across redraws", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.receive({ type: "gateway-chat-started", requestId: "fold", prompt: "检查工作区", cancellable: true });
  ui.receive({ type: "tool-call", requestId: "fold", callId: "t1", name: "list_files", arguments: "path=." });

  const turn = () => [...ui.document.querySelectorAll("#terminalThread .turn:not(.turn--bare)")].at(-1);
  const process = () => turn().querySelector(".turn__process");
  assert.equal(process().hidden, true, "the process is collapsed the first time it appears");
  assert.equal(turn().querySelector(".turn__toggle").getAttribute("aria-expanded"), "false");

  turn().querySelector(".turn__toggle").click();
  assert.equal(process().hidden, false, "clicking the toggle opens the process");
  assert.equal(lastSavedState(ui).conversations[0].messages.filter(message => message.role === "user").at(-1).processOpen, true, "the choice lives on the user message");

  ui.receive({ type: "tool-result", requestId: "fold", callId: "t1", ok: true, result: "a.txt" });
  ui.receive({ type: "gateway-chat-delta", requestId: "fold", kind: "text", text: "继续" });
  assert.equal(process().hidden, false, "streaming refreshes no longer force it closed");
  assert.equal(turn().querySelector(".turn__toggle").getAttribute("aria-expanded"), "true");

  ui.receive({ type: "gateway-chat-response", requestId: "fold", success: true, response: "完成" });
  assert.equal(process().hidden, false, "completion does not close a process the user opened");

  let saved = lastSavedState(ui);
  assert.equal(saved.conversations[0].messages.filter(message => message.role === "user").at(-1).processOpen, true, "the expanded choice is saved with ui-state");
  ui.receive({ type: "ui-state", state: saved });
  assert.equal(process().hidden, false, "the rebuilt thread restores the expanded process");

  turn().querySelector(".turn__toggle").click();
  assert.equal(process().hidden, true);
  ui.receive({ type: "tool-call", requestId: "fold-2", callId: "t2", name: "read_file", arguments: "path=a.txt" });
  assert.equal(process().hidden, true, "a later redraw does not re-open a process the user closed");
  saved = lastSavedState(ui);
  assert.equal(saved.conversations[0].messages.filter(message => message.role === "user").at(-1).processOpen, false, "the collapsed choice is saved as well");
  ui.receive({ type: "ui-state", state: saved });
  assert.equal(process().hidden, true, "the collapsed choice survives a restart");
});

test("action groups stay collapsed by default and remember a manual expansion", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: baseState({ activeConversationId: "c1" }) });
  ui.receive({ type: "gateway-chat-started", requestId: "group", prompt: "跑命令", cancellable: true });
  ui.receive({ type: "tool-call", requestId: "group", callId: "g1", name: "list_files", arguments: "path=." });

  const processOf = () => [...ui.document.querySelectorAll("#terminalThread .turn:not(.turn--bare)")].at(-1).querySelector(".turn__process");
  const head = () => processOf().querySelector(".action-group__head");
  assert.equal(head().getAttribute("aria-expanded"), "false", "action groups start collapsed while streaming");

  [...ui.document.querySelectorAll("#terminalThread .turn:not(.turn--bare)")].at(-1).querySelector(".turn__toggle").click();
  head().click();
  assert.equal(head().getAttribute("aria-expanded"), "true");
  assert.equal(lastSavedState(ui).conversations[0].messages.find(message => message.role === "tool").actionOpen, true, "the manual choice is stored on the message");

  const saved = lastSavedState(ui);
  ui.receive({ type: "ui-state", state: saved });
  assert.equal(head().getAttribute("aria-expanded"), "true", "the rebuilt thread keeps the action group open");
});

test("gateway providers validate, persist, and only ticked providers rotate", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: gatewayState() });
  ui.query('[data-page-pill="terminal"]').click();
  ui.query("#accountBtn").click();

  const rows = () => [...ui.document.querySelectorAll("#providerList .provider-row")];
  const fire = (element, type) => element.dispatchEvent(new ui.document.defaultView.Event(type, { bubbles: true }));
  // 迁移：providers 为空但已有 gatewayUrl 时自动生成“自定义网关”。
  assert.equal(rows().length, 1);
  assert.equal(rows()[0].querySelector(".provider-row__name").value, "自定义网关");
  assert.equal(rows()[0].querySelector(".provider-row__enabled").checked, true);
  assert.equal(rows()[0].querySelector(".provider-row__badge").textContent, "当前供应商");
  assert.equal(rows()[0].querySelector(".provider-row__key").type, "password");

  ui.query("#providerAddBtn").click();
  assert.equal(rows().length, 2);
  const rename = value => {
    const name = rows()[1].querySelector(".provider-row__name");
    name.value = value;
    fire(name, "change");
  };
  const setUrl = value => {
    const url = rows()[1].querySelector(".provider-row__url");
    url.value = value;
    fire(url, "change");
  };
  rename("备用网关");
  setUrl("http://insecure.example.com/v1");
  assert.equal(rows()[1].querySelector(".provider-row__error").hidden, false, "http addresses outside loopback are rejected");
  assert.equal(rows()[1].querySelector(".provider-row__url").value, "", "a rejected address is not written");
  setUrl("https://backup.example.com/v1");
  assert.equal(rows()[1].querySelector(".provider-row__error").hidden, true);
  assert.equal(rows()[1].querySelector(".provider-row__enabled").checked, false, "a new provider joins no rotation yet");
  rows()[1].querySelector(".provider-row__key").value = "backup-key";
  fire(rows()[1].querySelector(".provider-row__key"), "change");
  rows()[1].querySelector(".provider-row__backend").value = "anthropic";
  fire(rows()[1].querySelector(".provider-row__backend"), "change");

  ui.query("#settingsSaveBtn").click();
  let saved = lastSavedState(ui);
  assert.equal(saved.settings.providers.length, 2, "both providers are written back");
  assert.deepEqual(saved.settings.providers.map(provider => provider.name), ["自定义网关", "备用网关"]);
  assert.equal(saved.settings.providers.filter(provider => provider.enabled).length, 1);
  assert.equal(saved.settings.gatewayUrl, "https://gw.example.com/v1", "the current provider is mirrored to the legacy fields");

  let chip = ui.query("#providerChip");
  assert.equal(chip.hidden, false, "the chip shows in gateway mode");
  assert.equal(chip.textContent, "自定义网关");
  assert.equal(chip.disabled, true, "one rotating provider cannot cycle");

  // 勾选第二个供应商后 chip 可以轮换。
  ui.query("#accountBtn").click();
  const toggle = rows()[1].querySelector(".provider-row__enabled");
  toggle.checked = true;
  fire(toggle, "change");
  ui.query("#settingsSaveBtn").click();
  chip = ui.query("#providerChip");
  assert.equal(chip.disabled, false);
  chip.click();
  chip = ui.query("#providerChip");
  assert.equal(chip.textContent, "备用网关", "clicking the chip moves to the next rotating provider");
  assert.equal(lastSavedState(ui).settings.gatewayUrl, "https://backup.example.com/v1", "cycling mirrors the new provider");
  assert.ok(ui.document.querySelector(".toast"), "cycling reports a toast");
  chip.click();
  assert.equal(ui.query("#providerChip").textContent, "自定义网关");

  // 取消勾选后不再进入轮换。
  ui.query("#accountBtn").click();
  const second = rows()[1].querySelector(".provider-row__enabled");
  second.checked = false;
  fire(second, "change");
  ui.query("#settingsSaveBtn").click();
  chip = ui.query("#providerChip");
  assert.equal(chip.disabled, true, "the unticked provider leaves the rotation");
  assert.equal(chip.textContent, "自定义网关");
  chip.click();
  assert.equal(ui.query("#providerChip").textContent, "自定义网关", "an unticked provider is never cycled to");
  saved = lastSavedState(ui);
  assert.equal(saved.settings.providers.filter(provider => provider.enabled).length, 1);
});

test("plan cards stay readable and the plan bar executes, cancels and reopens them", t => {
  const ui = page(t);
  ui.receive({ type: "ui-state", state: gatewayState() });
  ui.query('[data-page-pill="terminal"]').click();
  ui.receive({ type: "gateway-chat-started", requestId: "plan-run", prompt: "规划重构", cancellable: true });
  ui.receive({
    type: "plan-proposed", requestId: "plan-run", planId: "p1", title: "重构计划",
    markdown: "1. 拆分模块\n2. 补测试", path: "C:////plans////p1.md"
  });

  const card = () => ui.query("#terminalThread .plan-card");
  const planMessage = () => lastSavedState(ui).conversations[0].messages.find(message => message.role === "plan");
  assert.equal(card().closest(".turn__process"), null, "the plan card lives outside the collapsible process tree");
  assert.match(card().querySelector(".plan-card__body").textContent, /拆分模块/);
  assert.equal(card().querySelector(".plan-card__body").hidden, false, "the plan body is expanded by default");
  assert.equal(ui.query("#planBar").hidden, false, "the plan bar appears with the proposal");
  assert.match(ui.query("#planBarTitle").textContent, /重构计划/);
  assert.equal(card().dataset.status, "pending");

  card().querySelector(".plan-card__toggle").click();
  assert.equal(card().querySelector(".plan-card__body").hidden, true);
  assert.equal(lastSavedState(ui).collapsed["plan:p1"], true, "the collapse choice is remembered per plan id");
  card().querySelector(".plan-card__toggle").click();
  assert.equal(card().querySelector(".plan-card__body").hidden, false);

  ui.receive({ type: "gateway-chat-response", requestId: "plan-run", success: true, response: "计划已给出" });
  assert.equal(ui.query("#planBar").hidden, false, "the plan bar survives the end of the request");

  // 取消：状态改为 cancelled，执行栏隐藏，卡片保留。
  ui.query("#planCancelBtn").click();
  assert.equal(ui.query("#planBar").hidden, true, "cancelling hides the plan bar");
  assert.ok(ui.query("#terminalThread .plan-card"), "the plan card stays after cancelling");
  assert.equal(planMessage().status, "cancelled", "cancelling writes the cancelled status");
  assert.equal(card().dataset.status, "cancelled");
  assert.equal(ui.messages.filter(message => message.type === "gateway-chat-request").length, 0, "cancelling sends no request");
  assert.equal(card().querySelector(".plan-card__execute").textContent, "再次执行计划");

  // 卡片上的“再次执行计划”把计划恢复为待执行，执行栏重新出现。
  card().querySelector(".plan-card__execute").click();
  assert.equal(planMessage().status, "pending", "reopening restores the pending status");
  assert.equal(ui.query("#planBar").hidden, false, "reopening brings the plan bar back");
  assert.equal(ui.messages.filter(message => message.type === "gateway-chat-request").length, 0, "reopening does not execute yet");

  // 执行栏执行：请求带 planId，状态变 approved，执行栏隐藏。
  ui.query("#planExecuteBtn").click();
  const request = ui.messages.filter(message => message.type === "gateway-chat-request").at(-1);
  assert.equal(request.type, "gateway-chat-request");
  assert.equal(request.planId, "p1", "executing sends the plan id");
  assert.equal(request.planMode, false);
  assert.equal(planMessage().status, "submitting", "submission waits for backend validation");
  assert.equal(ui.query("#planBar").hidden, true, "the plan bar is cleared once the plan is approved");
  assert.ok(ui.query("#terminalThread .plan-card"), "the approved plan card stays readable");
  assert.equal(card().querySelector(".plan-card__execute").textContent, "再次执行计划");

  // 重复提交会被拦住。
  ui.query("#planExecuteBtn").click();
  assert.equal(ui.messages.filter(message => message.type === "gateway-chat-request").length, 1, "one plan is submitted once");
});

test("system prompts are configured separately for gateway and remote models", t => {
  const ui = page(t);
  const fire = (element, type) => element.dispatchEvent(new ui.document.defaultView.Event(type, { bubbles: true }));
  ui.receive({ type: "ui-state", state: baseState() });
  ui.query("#accountBtn").click();
  assert.equal(ui.query('[data-pane="connection"]').hidden, false, "the prompts live in the connection pane");

  const gateway = ui.query("#gatewaySystemPrompt");
  const remote = ui.query("#remoteSystemPrompt");
  assert.equal(gateway.value, "", "no prompt is configured by default");
  assert.equal(remote.value, "");
  assert.equal(ui.query("#gatewaySystemPromptCount").textContent, "0 / 8000");

  gateway.value = "网关补充规则";
  fire(gateway, "input");
  remote.value = "远端补充规则，安全规则优先。";
  fire(remote, "input");
  assert.equal(ui.query("#gatewaySystemPromptCount").textContent, "6 / 8000");
  assert.equal(ui.query("#remoteSystemPromptCount").textContent, "14 / 8000");

  ui.query("#settingsSaveBtn").click();
  let saved = lastSavedState(ui);
  assert.equal(saved.settings.gatewaySystemPrompt, "网关补充规则", "the gateway prompt is written separately");
  assert.equal(saved.settings.remoteSystemPrompt, "远端补充规则，安全规则优先。", "the remote prompt is written separately");

  // 重启后仍从 ui-state 恢复。
  ui.receive({ type: "ui-state", state: saved });
  ui.query("#accountBtn").click();
  assert.equal(ui.query("#gatewaySystemPrompt").value, "网关补充规则");
  assert.equal(ui.query("#remoteSystemPrompt").value, "远端补充规则，安全规则优先。");

  // 超过 8000 字符时不保存，设置窗口保持打开。
  ui.query("#remoteSystemPrompt").value = "x".repeat(8001);
  fire(ui.query("#remoteSystemPrompt"), "input");
  assert.ok(ui.query("#remoteSystemPromptCount").classList.contains("is-over"), "the counter marks the overflow");
  ui.query("#settingsSaveBtn").click();
  assert.equal(ui.query("#settingsScrim").hidden, false, "an over-limit prompt blocks saving");
  assert.equal(ui.query("#systemPromptError").hidden, false);
  assert.match(ui.query("#systemPromptError").textContent, /8000/);
  assert.equal(lastSavedState(ui).settings.remoteSystemPrompt, "远端补充规则，安全规则优先。", "the stored prompt is untouched");

  // 恢复默认只清空输入框，应用后写回空字符串（使用内置提示）。
  ui.query("#gatewaySystemPromptReset").click();
  assert.equal(ui.query("#gatewaySystemPrompt").value, "");
  assert.equal(ui.query("#gatewaySystemPromptCount").textContent, "0 / 8000");
  assert.equal(ui.query("#systemPromptError").hidden, true, "restoring clears the overflow message");
  ui.query("#remoteSystemPrompt").value = "远端补充规则";
  fire(ui.query("#remoteSystemPrompt"), "input");
  ui.query("#settingsSaveBtn").click();
  saved = lastSavedState(ui);
  assert.equal(saved.settings.gatewaySystemPrompt, "");
  assert.equal(saved.settings.remoteSystemPrompt, "远端补充规则");
});
