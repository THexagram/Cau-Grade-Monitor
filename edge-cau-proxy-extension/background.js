const pacScript = `
function FindProxyForURL(url, host) {
  host = host.toLowerCase();

  if (host === "newjw.cau.edu.cn") {
    return "SOCKS5 127.0.0.1:1080";
  }

  return "DIRECT";
}
`;

function applyProxy() {
  chrome.proxy.settings.set({
    value: {
      mode: "pac_script",
      pacScript: {
        data: pacScript
      }
    },
    scope: "regular"
  });
}

chrome.runtime.onInstalled.addListener(applyProxy);
chrome.runtime.onStartup.addListener(applyProxy);
applyProxy();
