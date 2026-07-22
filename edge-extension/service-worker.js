const BRIDGE_URL = "ws://127.0.0.1:17891/quicksearch/";
const RECONNECT_ALARM = "quick-search-reconnect";
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
    url,
    type: "normal",
    state: "minimized",
    focused: false
  });
  const tab = created.tabs && created.tabs[0];
  if (!tab) {
    throw new Error("Edge 未返回新建标签页");
  }
  return { tabId: tab.id, windowId: created.id };
}

async function startSearch(requestId, url) {
  const target = await createBackgroundTab(url);
  requests.set(requestId, target);

  const tab = await chrome.tabs.get(target.tabId);
  if (tab.status === "complete") {
    send(`ready\t${requestId}`);
  }
}

async function showSearch(requestId) {
  const target = requests.get(requestId);
  if (!target) {
    throw new Error("搜索结果标签页已不存在");
  }

  const browserWindow = await chrome.windows.get(target.windowId);
  if (browserWindow.state === "minimized") {
    await chrome.windows.update(target.windowId, { state: "normal", focused: true });
  }
  await chrome.tabs.update(target.tabId, { active: true });
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
  } else if (command === "show" && requestId) {
    operation = showSearch(requestId);
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

chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.status !== "complete") {
    return;
  }
  for (const [requestId, target] of requests) {
    if (target.tabId === tabId) {
      try {
        send(`ready\t${requestId}`);
      } catch {
        // 连接恢复前保留标签页，用户可以重新发起搜索。
      }
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
