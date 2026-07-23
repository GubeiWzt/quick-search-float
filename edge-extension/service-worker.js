const BRIDGE_URL = "ws://127.0.0.1:17891/quicksearch/";
const RECONNECT_ALARM = "quick-search-reconnect";
const MAX_RESULT_PAGES = 20;
const requests = new Map();
let socket = null;
let reconnectTimer = null;
let heartbeatTimer = null;

function send(message) {
  if (!socket || socket.readyState !== WebSocket.OPEN) {
    throw new Error("本机快捷搜索程序未连接");
  }
  socket.send(message);
}

function field(value) {
  return encodeURIComponent(String(value || ""));
}

function scheduleReconnect() {
  clearTimeout(reconnectTimer);
  reconnectTimer = setTimeout(connect, 1500);
}

function connect() {
  if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) {
    return;
  }

  try {
    socket = new WebSocket(BRIDGE_URL);
    socket.onopen = () => {
      clearTimeout(reconnectTimer);
      clearInterval(heartbeatTimer);
      heartbeatTimer = setInterval(() => {
        if (socket && socket.readyState === WebSocket.OPEN) {
          socket.send("ping");
        }
      }, 20000);
    };
    socket.onmessage = event => handleCommand(String(event.data));
    socket.onclose = () => {
      clearInterval(heartbeatTimer);
      socket = null;
      scheduleReconnect();
    };
    socket.onerror = () => socket?.close();
  } catch {
    socket = null;
    scheduleReconnect();
  }
}

async function createBackgroundTab(url) {
  try {
    const target = await chrome.windows.getLastFocused({ windowTypes: ["normal"] });
    if (!target || target.id == null) {
      throw new Error("Edge 没有普通窗口");
    }
    const tab = await chrome.tabs.create({ windowId: target.id, url, active: false });
    return { tabId: tab.id, windowId: target.id };
  } catch {
    // Edge 没有可用窗口时直接创建最小化窗口，避免首帧闪现。
  }

  const created = await chrome.windows.create({
    url: "about:blank",
    type: "normal",
    state: "minimized",
    focused: false
  });
  const tab = created.tabs && created.tabs[0];
  if (!tab) {
    throw new Error("Edge 未返回新建标签页");
  }
  // ponytail: 首个窗口先确认最小化，再导航搜索页，避免搜索内容抢到前台。
  await chrome.windows.update(created.id, { state: "minimized", focused: false });
  await chrome.tabs.update(tab.id, { url, active: false });
  return { tabId: tab.id, windowId: created.id };
}

function detectEngine(url) {
  try {
    const host = new URL(url).hostname.toLowerCase();
    if (host === "google.com" || host === "www.google.com" || host.endsWith(".google.com")) {
      return "google";
    }
    if (host === "bing.com" || host === "www.bing.com" || host.endsWith(".bing.com")) {
      return "bing";
    }
    if (host === "baidu.com" || host === "www.baidu.com" || host.endsWith(".baidu.com")) {
      return "baidu";
    }
  } catch {
    return "";
  }
  return "";
}

function pageUrl(target, page) {
  const url = new URL(target.originalUrl);
  if (target.engine === "google") {
    url.searchParams.set("start", String(page * 10));
    url.searchParams.set("num", "10");
  } else if (target.engine === "bing") {
    url.searchParams.set("first", String(page * 10 + 1));
    url.searchParams.set("count", "10");
  } else if (target.engine === "baidu") {
    url.searchParams.set("pn", String(page * 10));
  }
  return url.href;
}

function extractSearchPage(engine, originalUrl, includeAi) {
  const clean = value => String(value || "").replace(/\s+/g, " ").trim();
  const shorten = (value, maximum) => {
    const text = clean(value);
    return text.length > maximum ? `${text.slice(0, maximum - 1).trim()}…` : text;
  };
  const visible = element => {
    if (!element || element.closest("[hidden], [aria-hidden='true']")) {
      return false;
    }
    const style = getComputedStyle(element);
    return style.display !== "none" && style.visibility !== "hidden";
  };
  const exactAdLabels = new Set(["广告", "广告·", "推广", "赞助", "ad", "ads", "sponsored"]);

  function hasExplicitAdLabel(container) {
    if (!container) {
      return false;
    }
    if (container.matches(".b_ad, [data-tuiguang], [data-text-ad], [aria-label*='Sponsored' i], [aria-label*='广告']")) {
      return true;
    }
    for (const element of container.querySelectorAll("span,div,label")) {
      const label = clean(element.textContent).toLowerCase();
      if (label.length <= 12 && exactAdLabels.has(label)) {
        return true;
      }
    }
    return false;
  }

  function normalizeUrl(value) {
    if (!value) {
      return "";
    }
    try {
      const url = new URL(value, location.href);
      if (engine === "google" && url.pathname === "/url") {
        const destination = url.searchParams.get("q") || url.searchParams.get("url");
        if (destination) {
          const target = new URL(destination, location.href);
          if (target.protocol === "http:" || target.protocol === "https:") {
            return target.href;
          }
        }
      }
      if (url.protocol !== "http:" && url.protocol !== "https:") {
        return "";
      }
      return url.href;
    } catch {
      return "";
    }
  }

  function findContainer(heading, selectors) {
    const known = heading.closest(selectors);
    if (known) {
      return known;
    }
    let current = heading.parentElement;
    for (let depth = 0; current && depth < 6; depth += 1, current = current.parentElement) {
      const length = clean(current.innerText).length;
      if (length >= clean(heading.innerText).length + 35 && length <= 1800) {
        return current;
      }
    }
    return heading.parentElement;
  }

  function snippetFrom(container, selectors, title) {
    for (const selector of selectors) {
      const candidate = container && container.querySelector(selector);
      const text = candidate && shorten(candidate.innerText, 520);
      if (text && text !== title && text.length > 20) {
        return text;
      }
    }
    const full = shorten(container && container.innerText, 620);
    if (!full) {
      return "";
    }
    return shorten(full.replace(title, "").trim(), 520);
  }

  function sourceFrom(url) {
    try {
      return new URL(url).hostname.replace(/^www\./, "");
    } catch {
      return "";
    }
  }

  function semanticLabel(element) {
    if (!element) {
      return "";
    }
    const direct = element.innerText || element.textContent ||
      element.getAttribute("aria-label") || element.getAttribute("title") ||
      element.querySelector("img[alt]")?.alt;
    if (clean(direct)) {
      return direct;
    }
    const references = String(element.getAttribute("aria-labelledby") || "")
      .split(/\s+/)
      .filter(Boolean)
      .map(id => document.getElementById(id))
      .filter(Boolean)
      .map(node => node.innerText || node.textContent || node.getAttribute("aria-label") || "")
      .filter(value => clean(value));
    return references.join("\n");
  }

  function findAiCard() {
    if (!includeAi) {
      return null;
    }
    const names = {
      google: ["AI Overview", "AI 概览", "AI 摘要"],
      bing: ["Copilot", "AI-powered answer", "AI 生成的答案", "AI 回答"],
      baidu: ["AI 智能回答", "AI 回答", "智能回答", "AI 摘要"]
    }[engine] || [];
    const preferred = {
      google: ["[data-attrid*='ai_overview' i]", "[data-mcpr]"],
      bing: ["#b_sydConvCont", "#b_sydTigerCont", "[data-tag*='copilot' i]"],
      baidu: ["[class*='ai-answer' i]", "[class*='ai_answer' i]"]
    }[engine] || [];

    let container = null;
    let label = names[0] || "AI 搜索结果";
    for (const selector of preferred) {
      const candidate = document.querySelector(selector);
      const text = shorten(candidate && candidate.innerText, 1400);
      if (visible(candidate) && text.length >= 40) {
        container = candidate;
        break;
      }
    }
    if (!container) {
      const elements = document.querySelectorAll("div,span,h1,h2,h3");
      for (const element of elements) {
        const text = clean(element.textContent);
        const matched = names.find(name => text === name || text.startsWith(`${name} `));
        if (!matched || !visible(element)) {
          continue;
        }
        label = matched;
        let current = element;
        for (let depth = 0; current && depth < 7; depth += 1, current = current.parentElement) {
          const body = shorten(current.innerText, 1400);
          if (body.length >= 60 && body.length <= 1400) {
            container = current;
            break;
          }
        }
        if (container) {
          break;
        }
      }
    }
    if (!container || hasExplicitAdLabel(container)) {
      return null;
    }
    const copy = container.cloneNode(true);
    copy.querySelectorAll("a, button, [role='link'], script, style, svg")
      .forEach(element => element.remove());
    let body = copy.innerText || copy.textContent || "";
    const repeatedLabels = Array.from(new Set([
      ...names, label, "AI Overview", "AI 概览", "AI 摘要", "AI 回答", "AI 智能回答"
    ])).sort((left, right) => right.length - left.length);
    for (const repeatedLabel of repeatedLabels) {
      const escaped = repeatedLabel.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
      body = body.replace(new RegExp(`${escaped}\\s*[:：·-]?\\s*`, "gi"), "");
    }
    const unavailableMessages = [
      /无法针对此搜索生成(?:\s*AI\s*概(?:览|要))?[。.!！]?/gi,
      /目前无法生成\s*AI\s*概(?:览|要)[。.!！]?/gi,
      /请稍后重试[。.!！]?/gi,
      /了解\s*AI\s*概览(?:的)?使用限制[。.!！]?/gi,
      /AI Overview (?:is )?not available for this search[.!]?/gi,
      /(?:cannot|can't) generate (?:an )?AI Overview (?:right now)?[.!]?/gi,
      /please try again later[.!]?/gi
    ];
    for (const message of unavailableMessages) {
      body = body.replace(message, "");
    }
    body = shorten(body.replace(/https?:\/\/\S+/gi, ""), 1100);
    if (body.length < 35) {
      return null;
    }
    return {
      kind: "ai",
      url: originalUrl,
      title: label,
      snippet: body,
      source: engine === "google" ? "Google" : engine === "bing" ? "Bing" : "百度"
    };
  }

  const results = [];
  const seen = new Set();
  const ai = findAiCard();
  if (ai) {
    results.push(ai);
  }

  let headings;
  let containerSelectors;
  let snippetSelectors;
  if (engine === "google") {
    headings = document.querySelectorAll("#search h3, #rso h3, main h3");
    if (!headings.length) {
      headings = document.querySelectorAll("h3");
    }
    containerSelectors = ".MjjYud, .g, [data-sokoban-container], [data-snhf]";
    snippetSelectors = [".VwiC3b", "[data-sncf]", "[data-snhf]", ".yXK7lf",
      "[style*='-webkit-line-clamp']"];
  } else if (engine === "bing") {
    headings = document.querySelectorAll("#b_results li.b_algo h2, #b_results h2");
    containerSelectors = "li.b_algo, .b_algo";
    snippetSelectors = [".b_caption p", ".b_lineclamp2", "p"];
  } else if (engine === "baidu") {
    headings = document.querySelectorAll("#content_left h3");
    containerSelectors = ".result, .c-container, [tpl]";
    snippetSelectors = [".c-abstract", "[class*='content-right']", ".c-span-last", "span"];
  } else {
    return { results: [], hasMore: false };
  }

  for (const heading of headings) {
    if (!visible(heading)) {
      continue;
    }
    const anchor = heading.closest("a") || heading.querySelector("a") ||
      heading.parentElement?.closest("a");
    const title = shorten(heading.innerText, 180);
    const url = normalizeUrl(anchor && anchor.href);
    if (!title || !url || seen.has(url)) {
      continue;
    }
    const container = findContainer(heading, containerSelectors);
    if (hasExplicitAdLabel(container)) {
      continue;
    }
    const snippet = snippetFrom(container, snippetSelectors, title);
    seen.add(url);
    results.push({
      kind: "web",
      url,
      title,
      snippet,
      source: sourceFrom(url)
    });
  }

  if (engine === "google" && !results.some(result => result.kind === "web")) {
    const searchRoot = document.querySelector("#search") || document.querySelector("main");
    const links = searchRoot ? searchRoot.querySelectorAll("a[href], [role='link']") : [];
    for (const link of links) {
      if (!visible(link)) {
        continue;
      }
      const anchor = link.matches("a[href]") ? link : link.querySelector("a[href]");
      const rawUrl = (anchor && anchor.href) || link.getAttribute("href") ||
        link.getAttribute("data-href") || link.getAttribute("data-url");
      const url = normalizeUrl(rawUrl);
      if (!url || seen.has(url)) {
        continue;
      }
      const hostname = new URL(url).hostname.toLowerCase();
      if (hostname === "google.com" || hostname.endsWith(".google.com")) {
        continue;
      }
      const label = semanticLabel(link);
      const lines = String(label)
        .split(/\r?\n/)
        .map(clean)
        .filter(line => line.length >= 5 && line.length <= 220);
      const title = shorten(lines.sort((left, right) => right.length - left.length)[0], 180);
      if (!title) {
        continue;
      }
      const container = findContainer(link, containerSelectors);
      if (hasExplicitAdLabel(container)) {
        continue;
      }
      const snippet = snippetFrom(container, snippetSelectors, title);
      seen.add(url);
      results.push({
        kind: "web",
        url,
        title,
        snippet,
        source: sourceFrom(url)
      });
    }
  }

  const nextSelectors = {
    google: ["#pnnext", "a[aria-label*='Next' i]", "a[aria-label*='下一页']"],
    bing: ["a.sb_pagN", "a[title*='Next page' i]", "a[title*='下一页']"],
    baidu: ["#page a.n", "a.n"]
  }[engine] || [];
  let nextUrl = "";
  for (const selector of nextSelectors) {
    const anchors = document.querySelectorAll(selector);
    for (const anchor of anchors) {
      const label = clean(`${anchor.textContent || ""} ${anchor.getAttribute("aria-label") || ""} ${anchor.title || ""}`);
      if (engine === "baidu" && !/下一页|next/i.test(label)) {
        continue;
      }
      const candidate = normalizeUrl(anchor.href);
      if (candidate && candidate !== location.href) {
        nextUrl = candidate;
        break;
      }
    }
    if (nextUrl) {
      break;
    }
  }

  return {
    results,
    hasMore: Boolean(nextUrl) || results.filter(result => result.kind === "web").length >= 8,
    nextUrl,
    diagnostic: {
      url: location.href,
      title: document.title,
      headingCount: headings.length,
      anchorCount: document.querySelectorAll("#search a, main a").length,
      anchors: Array.from(document.querySelectorAll("#search a, #search [role='link'], main a, main [role='link']"))
        .filter(anchor => clean(semanticLabel(anchor)).length >= 5)
        .slice(0, 10)
        .map(anchor => [
          shorten(semanticLabel(anchor), 90),
          shorten(anchor.getAttribute("href"), 180),
          shorten(anchor.getAttribute("data-href") || anchor.getAttribute("data-url"), 180),
          shorten(anchor.outerHTML, 220)
        ].join(" => "))
        .join(" || "),
      roleLinkCount: document.querySelectorAll("#search [role='link'], main [role='link']").length,
      roleLinks: Array.from(document.querySelectorAll("#search [role='link'], main [role='link']"))
        .slice(0, 8)
        .map(link => [
          shorten(semanticLabel(link), 90),
          shorten(link.getAttribute("href"), 160),
          shorten(link.getAttribute("data-href") || link.getAttribute("data-url"), 160),
          shorten(link.outerHTML, 260)
        ].join(" => "))
        .join(" || "),
      body: shorten(document.body && document.body.innerText, 260)
    }
  };
}

async function processPage(requestId, target, initial) {
  if (target.processing) {
    return;
  }
  target.processing = true;
  target.pendingLoad = false;
  try {
    if (!target.engine) {
      target.hasMore = false;
      send(`batch\t${requestId}\tfalse`);
      if (initial) {
        send(`ready\t${requestId}`);
      }
      return;
    }

    let payload = null;
    for (let attempt = 0; attempt < (initial ? 6 : 2); attempt += 1) {
      const execution = await chrome.scripting.executeScript({
        target: { tabId: target.tabId },
        world: "ISOLATED",
        func: extractSearchPage,
        args: [target.engine, target.originalUrl, initial]
      });
      payload = execution && execution[0] && execution[0].result;
      const attemptItems = payload && Array.isArray(payload.results) ? payload.results : [];
      if (attemptItems.some(item => item.kind !== "ai")) {
        break;
      }
      await new Promise(resolve => setTimeout(resolve, 500));
    }
    const items = payload && Array.isArray(payload.results) ? payload.results : [];
    if (initial && !items.some(item => item.kind !== "ai")) {
      const diagnostic = payload && payload.diagnostic ? payload.diagnostic : {};
      throw new Error([
        "未识别到搜索结果",
        diagnostic.title || "无标题",
        diagnostic.url || "无地址",
        `标题节点 ${diagnostic.headingCount || 0}`,
        `链接节点 ${diagnostic.anchorCount || 0}`,
        diagnostic.anchors || "链接无可读属性",
        `语义链接 ${diagnostic.roleLinkCount || 0}`,
        diagnostic.roleLinks || "无语义链接属性",
        diagnostic.body || "页面无正文"
      ].join(" | "));
    }
    let addedWebResults = 0;
    for (const item of items) {
      const url = String(item.url || "");
      if (!url || target.seenUrls.has(url)) {
        continue;
      }
      target.seenUrls.add(url);
      if (item.kind !== "ai") {
        addedWebResults += 1;
      }
      send(["result", requestId, item.kind === "ai" ? "ai" : "web",
        field(url), field(item.title), field(item.snippet), field(item.source)].join("\t"));
    }
    target.nextPageUrl = payload && payload.nextUrl ? payload.nextUrl : "";
    target.hasMore = Boolean(payload && payload.hasMore && addedWebResults > 0 &&
      target.page + 1 < MAX_RESULT_PAGES);
    send(`batch\t${requestId}\t${target.hasMore ? "true" : "false"}`);
    if (initial) {
      send(`ready\t${requestId}`);
    }
  } finally {
    target.processing = false;
    target.loadingMore = false;
  }
}

async function startSearch(requestId, url) {
  const created = await createBackgroundTab(url);
  const target = {
    ...created,
    originalUrl: url,
    engine: detectEngine(url),
    page: 0,
    hasMore: false,
    loadingMore: false,
    processing: false,
    pendingLoad: true,
    nextPageUrl: "",
    seenUrls: new Set()
  };
  requests.set(requestId, target);

  const tab = await chrome.tabs.get(target.tabId);
  if (tab.status === "complete") {
    await processPage(requestId, target, true);
  }
}

async function loadMore(requestId) {
  const target = requests.get(requestId);
  if (!target || target.loadingMore || target.processing) {
    return;
  }
  if (!target.hasMore) {
    send(`batch\t${requestId}\tfalse`);
    return;
  }
  target.loadingMore = true;
  target.page += 1;
  target.pendingLoad = true;
  const tab = await chrome.tabs.update(target.tabId, {
    url: target.nextPageUrl || pageUrl(target, target.page),
    active: false
  });
  if (tab.status === "complete") {
    await processPage(requestId, target, false);
  }
}

async function showSearch(requestId, encodedTargetUrl) {
  const target = requests.get(requestId);
  if (!target) {
    throw new Error("搜索结果标签页已不存在");
  }

  let targetUrl = "";
  if (encodedTargetUrl) {
    targetUrl = decodeURIComponent(encodedTargetUrl);
    const parsed = new URL(targetUrl);
    if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
      throw new Error("结果地址无效");
    }
  }

  const browserWindow = await chrome.windows.get(target.windowId);
  if (browserWindow.state === "minimized") {
    await chrome.windows.update(target.windowId, { state: "normal", focused: true });
  }
  await chrome.tabs.update(target.tabId,
    targetUrl ? { url: targetUrl, active: true } : { active: true });
  await chrome.windows.update(target.windowId, { focused: true });
  await new Promise(resolve => setTimeout(resolve, 60));
  requests.delete(requestId);
  send(`shown\t${requestId}`);
}

async function cancelSearch(requestId) {
  const target = requests.get(requestId);
  requests.delete(requestId);
  if (target) {
    try {
      await chrome.tabs.remove(target.tabId);
    } catch {
      // 标签页已经由用户关闭时无需再次处理。
    }
  }
}

function handleCommand(message) {
  const parts = message.split("\t");
  const command = parts[0];
  const requestId = parts[1];

  let operation;
  if (command === "search" && requestId && parts[2]) {
    operation = startSearch(requestId, parts.slice(2).join("\t"));
  } else if (command === "more" && requestId) {
    operation = loadMore(requestId);
  } else if (command === "show" && requestId) {
    operation = showSearch(requestId, parts[2] || "");
  } else if (command === "cancel" && requestId) {
    operation = cancelSearch(requestId);
  } else {
    return;
  }

  operation.catch(error => {
    const detail = String(error && error.message ? error.message : error).replace(/[\t\r\n]+/g, " ");
    if (requestId && socket && socket.readyState === WebSocket.OPEN) {
      socket.send(`error\t${requestId}\t${detail}`);
    }
  });
}

if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.id) {
chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.status !== "complete") {
    return;
  }
  for (const [requestId, target] of requests) {
    if (target.tabId === tabId && target.pendingLoad) {
      processPage(requestId, target, target.page === 0).catch(error => {
        const detail = String(error && error.message ? error.message : error)
          .replace(/[\t\r\n]+/g, " ");
        if (socket && socket.readyState === WebSocket.OPEN) {
          socket.send(`error\t${requestId}\t${detail}`);
        }
      });
      return;
    }
  }
});

chrome.tabs.onRemoved.addListener(tabId => {
  for (const [requestId, target] of requests) {
    if (target.tabId === tabId) {
      requests.delete(requestId);
      if (socket && socket.readyState === WebSocket.OPEN) {
        socket.send(`error\t${requestId}\t搜索结果标签页已关闭`);
      }
      return;
    }
  }
});

chrome.runtime.onInstalled.addListener(() => {
  chrome.alarms.create(RECONNECT_ALARM, { periodInMinutes: 0.5 });
  connect();
});
chrome.runtime.onStartup.addListener(connect);
chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === RECONNECT_ALARM) {
    connect();
  }
});
chrome.action.onClicked.addListener(connect);

chrome.alarms.create(RECONNECT_ALARM, { periodInMinutes: 0.5 });
connect();
}
