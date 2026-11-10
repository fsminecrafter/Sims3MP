// Sims3MPNative - the only native code that runs inside The Sims 3.
//
// The game's script runtime has no sockets, so this 32-bit DLL (loaded as a
// MonoPatcher plugin from Game\Bin\MonoPatcher\plugins) gives the script mod
// one thing: a non-blocking, framed loopback connection to sims3mp-bridge.exe,
// which it also launches. Everything else (server protocol, file transfer,
// voice) lives in the bridge process, outside the game's cramped 32-bit
// address space and unable to stall or crash the game.
//
// Every export is non-blocking and is called from the game's own thread.
//
// Build (one line):
//   i686-w64-mingw32-g++ -O2 -shared -static -o Sims3MPNative.dll Sims3MPNative.cpp -lws2_32 -Wl,--kill-at
#include <winsock2.h>
#include <windows.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <vector>

#define MP_EXPORT extern "C" __declspec(dllexport)

namespace {

const unsigned short kDefaultBridgePort = 38921;
const size_t kMaxFrame = 1 << 20;
const size_t kMaxBacklog = 8 << 20;

enum State { Idle, Connecting, Connected };

State g_state = Idle;
SOCKET g_sock = INVALID_SOCKET;
bool g_wsa = false;
DWORD g_lastAttempt = 0;
DWORD g_lastSpawn = 0;
HMODULE g_self = NULL;
std::vector<uint8_t> g_out; // bytes not yet accepted by the socket
std::vector<uint8_t> g_in;  // bytes received but not yet returned as frames

// The bridge port can be overridden per game instance with the environment
// variable SIMS3MP_BRIDGE_PORT. Test mode uses this to run two copies of the
// game on one PC, each with its own bridge.
unsigned short BridgePort() {
    static unsigned short port = 0;
    if (port == 0) {
        char buf[16];
        DWORD n = GetEnvironmentVariableA("SIMS3MP_BRIDGE_PORT", buf, sizeof buf);
        int v = (n > 0 && n < sizeof buf) ? atoi(buf) : 0;
        port = (v > 1023 && v < 65536) ? (unsigned short)v : kDefaultBridgePort;
    }
    return port;
}

void Drop() {
    if (g_sock != INVALID_SOCKET) closesocket(g_sock);
    g_sock = INVALID_SOCKET;
    g_state = Idle;
    g_out.clear();
    g_in.clear();
}

// Start the bridge that sits next to this DLL. Harmless if one is already
// running: the second instance fails to bind the port and exits.
void SpawnBridge() {
    DWORD now = GetTickCount();
    if (g_lastSpawn != 0 && now - g_lastSpawn < 15000) return;
    g_lastSpawn = now ? now : 1;

    wchar_t dir[MAX_PATH];
    DWORD n = GetModuleFileNameW(g_self, dir, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return;
    wchar_t* slash = wcsrchr(dir, L'\\');
    if (!slash) return;
    *slash = 0;

    wchar_t cmd[MAX_PATH + 96];
    if (lstrlenW(dir) > MAX_PATH - 32) return;
    wsprintfW(cmd, L"\"%s\\sims3mp-bridge.exe\" -local 127.0.0.1:%u", dir, (unsigned)BridgePort());

    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof si);
    si.cb = sizeof si;
    if (CreateProcessW(NULL, cmd, NULL, NULL, FALSE, CREATE_NO_WINDOW | DETACHED_PROCESS, NULL, dir, &si, &pi)) {
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
    }
}

void BeginConnect() {
    DWORD now = GetTickCount();
    if (g_lastAttempt != 0 && now - g_lastAttempt < 1000) return;
    g_lastAttempt = now ? now : 1;

    if (!g_wsa) {
        WSADATA d;
        if (WSAStartup(MAKEWORD(2, 2), &d) != 0) return;
        g_wsa = true;
    }
    g_sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (g_sock == INVALID_SOCKET) return;
    u_long nb = 1;
    ioctlsocket(g_sock, FIONBIO, &nb);
    BOOL nodelay = TRUE;
    setsockopt(g_sock, IPPROTO_TCP, TCP_NODELAY, (const char*)&nodelay, sizeof nodelay);

    sockaddr_in a;
    ZeroMemory(&a, sizeof a);
    a.sin_family = AF_INET;
    a.sin_port = htons(BridgePort());
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (connect(g_sock, (sockaddr*)&a, sizeof a) == 0) {
        g_state = Connected;
    } else if (WSAGetLastError() == WSAEWOULDBLOCK) {
        g_state = Connecting;
    } else {
        Drop();
        SpawnBridge();
    }
}

void PollConnect() {
    fd_set w, e;
    FD_ZERO(&w);
    FD_ZERO(&e);
    FD_SET(g_sock, &w);
    FD_SET(g_sock, &e);
    timeval tv = {0, 0};
    if (select(0, NULL, &w, &e, &tv) <= 0) {
        if (GetTickCount() - g_lastAttempt > 5000) Drop();
        return;
    }
    if (FD_ISSET(g_sock, &e)) { // refused: no bridge yet
        Drop();
        SpawnBridge();
    } else if (FD_ISSET(g_sock, &w)) {
        g_state = Connected;
    }
}

void Flush() {
    while (g_state == Connected && !g_out.empty()) {
        int n = send(g_sock, (const char*)g_out.data(), (int)(g_out.size() > 65536 ? 65536 : g_out.size()), 0);
        if (n > 0) {
            g_out.erase(g_out.begin(), g_out.begin() + n);
        } else {
            if (WSAGetLastError() != WSAEWOULDBLOCK) Drop();
            return;
        }
    }
}

void Pump() {
    uint8_t buf[16384];
    while (g_state == Connected && g_in.size() < kMaxBacklog) {
        int n = recv(g_sock, (char*)buf, sizeof buf, 0);
        if (n > 0) {
            g_in.insert(g_in.end(), buf, buf + n);
        } else {
            if (n == 0 || WSAGetLastError() != WSAEWOULDBLOCK) Drop();
            return;
        }
    }
}

} // namespace

// Returns 1 while connected to the bridge. Call once per frame; it drives
// (re)connection and starts the bridge when needed.
MP_EXPORT int __stdcall MP_Poll() {
    if (g_state == Idle) BeginConnect();
    else if (g_state == Connecting) PollConnect();
    if (g_state == Connected) Flush();
    return g_state == Connected ? 1 : 0;
}

// Queues one message (type byte + payload). Returns 1 if queued.
MP_EXPORT int __stdcall MP_Send(const uint8_t* body, int len) {
    if (g_state != Connected || !body || len < 1 || (size_t)len > kMaxFrame) return 0;
    if (g_out.size() + len + 4 > kMaxBacklog) return 0;
    uint32_t n = (uint32_t)len;
    const uint8_t* p = (const uint8_t*)&n; // x86 is little endian, as the protocol requires
    g_out.insert(g_out.end(), p, p + 4);
    g_out.insert(g_out.end(), body, body + len);
    Flush();
    return 1;
}

// Copies the next complete message (type byte + payload) into buf.
// Returns its length, 0 if none is pending, -1 if not connected.
MP_EXPORT int __stdcall MP_Recv(uint8_t* buf, int cap) {
    if (g_state != Connected) return -1;
    if (g_in.size() < 4) Pump();
    if (g_state != Connected) return -1;
    if (g_in.size() < 4) return 0;
    uint32_t n;
    memcpy(&n, g_in.data(), 4);
    if (n < 1 || n > kMaxFrame) {
        Drop();
        return -1;
    }
    if (g_in.size() < 4 + (size_t)n) {
        Pump();
        if (g_state != Connected) return -1;
        if (g_in.size() < 4 + (size_t)n) return 0;
    }
    if (!buf || (uint32_t)cap < n) { // caller's buffer too small: skip the message
        g_in.erase(g_in.begin(), g_in.begin() + 4 + n);
        return 0;
    }
    memcpy(buf, g_in.data() + 4, n);
    g_in.erase(g_in.begin(), g_in.begin() + 4 + n);
    return (int)n;
}

MP_EXPORT int __stdcall MP_Version() { return 1; }

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_self = inst;
        DisableThreadLibraryCalls(inst);
    }
    return TRUE;
}
