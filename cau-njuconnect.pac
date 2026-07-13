function FindProxyForURL(url, host) {
  host = host.toLowerCase();

  if (host === "newjw.cau.edu.cn") {
    return "SOCKS5 127.0.0.1:1080";
  }

  return "DIRECT";
}
