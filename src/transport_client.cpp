#include "vh/transport_client.h"

#include <cstring>
#include <filesystem>
#include <iostream>
#include <utility>

#ifdef _WIN32
    #include <WinSock2.h>
    #include <WS2tcpip.h>
#else
    #include <arpa/inet.h>
    #include <netinet/in.h>
    #include <sys/socket.h>
    #include <unistd.h>
#endif

namespace vh {
namespace {

#ifdef _WIN32
SOCKET toNativeSocket(std::uintptr_t s) {
    return static_cast<SOCKET>(s);
}

// 返回当前 Winsock 错误码的字符串描述
std::string wsaErrorString(int code) {
    char buf[256]{};
    FormatMessageA(
        FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr,
        static_cast<DWORD>(code),
        MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
        buf, static_cast<DWORD>(sizeof(buf) - 1),
        nullptr);
    // 去掉末尾换行
    for (int i = static_cast<int>(strlen(buf)) - 1; i >= 0; --i) {
        if (buf[i] == '\r' || buf[i] == '\n') buf[i] = '\0';
        else break;
    }
    return buf;
}
#else
int toNativeSocket(int s) {
    return s;
}
#endif

} // namespace

// ---------------------------------------------------------------------------
// StdoutTransportClient
// ---------------------------------------------------------------------------

bool StdoutTransportClient::connect() {
    return true;
}

bool StdoutTransportClient::sendText(const std::string& payload) {
    std::cout << payload << std::endl;
    return true;
}

void StdoutTransportClient::close() {}

// ---------------------------------------------------------------------------
// FileTransportClient
// ---------------------------------------------------------------------------

FileTransportClient::FileTransportClient(std::string path)
    : path_(std::move(path)) {}

bool FileTransportClient::connect() {
    const std::filesystem::path p(path_);
    if (p.has_parent_path()) {
        std::error_code ec;
        std::filesystem::create_directories(p.parent_path(), ec);
        if (ec) {
            std::cerr << "[File] Failed to create output directory '"
                      << p.parent_path().string() << "': " << ec.message() << "\n";
            return false;
        }
    }
    out_.open(path_, std::ios::app | std::ios::binary);
    if (!out_) {
        std::cerr << "[File] Failed to open output file: " << path_ << "\n";
        return false;
    }
    return true;
}

bool FileTransportClient::sendText(const std::string& payload) {
    if (!out_) {
        return false;
    }
    out_ << payload << '\n';
    out_.flush();
    return static_cast<bool>(out_);
}

void FileTransportClient::close() {
    if (out_) {
        out_.close();
    }
}

// ---------------------------------------------------------------------------
// TcpTextTransportClient
// ---------------------------------------------------------------------------

TcpTextTransportClient::TcpTextTransportClient(std::string host, std::uint16_t port)
    : host_(std::move(host)), port_(port) {}

TcpTextTransportClient::~TcpTextTransportClient() {
    close();
}

bool TcpTextTransportClient::connect() {
#ifdef _WIN32
    WSADATA data{};
    if (WSAStartup(MAKEWORD(2, 2), &data) != 0) {
        const int err = WSAGetLastError();
        std::cerr << "[TCP] WSAStartup failed (WSAError=" << err << ")\n";
        return false;
    }
    wsa_started_ = true;
#endif

    // 创建 socket
#ifdef _WIN32
    const SOCKET native_socket = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (native_socket == INVALID_SOCKET) {
        const int err = WSAGetLastError();
        std::cerr << "[TCP] socket() failed (WSAError=" << err
                  << " " << wsaErrorString(err) << ")\n";
        close();
        return false;
    }
    socket_ = static_cast<SocketHandle>(native_socket);
#else
    const int native_socket = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (native_socket < 0) {
        std::cerr << "[TCP] socket() failed (errno=" << errno << ")\n";
        close();
        return false;
    }
    socket_ = static_cast<SocketHandle>(native_socket);
#endif

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_port   = htons(port_);

    if (::inet_pton(AF_INET, host_.c_str(), &addr.sin_addr) != 1) {
        std::cerr << "[TCP] inet_pton() failed for host: '" << host_ << "'\n";
        close();
        return false;
    }

    std::cerr << "[TCP] Connecting to " << host_ << ":" << port_ << " ...\n";

    if (::connect(toNativeSocket(socket_),
                  reinterpret_cast<sockaddr*>(&addr),
                  sizeof(addr)) != 0) {
#ifdef _WIN32
        const int err = WSAGetLastError();
        std::cerr << "[TCP] connect() failed to " << host_ << ":" << port_
                  << " (WSAError=" << err << " " << wsaErrorString(err) << ")\n";
#else
        const int err = errno;
        std::cerr << "[TCP] connect() failed to " << host_ << ":" << port_
                  << " (errno=" << err << ")\n";
#endif
        close();
        return false;
    }

    connected_ = true;
    std::cerr << "[TCP] Connected to " << host_ << ":" << port_ << "\n";
    return true;
}

bool TcpTextTransportClient::sendAll(const char* data, std::size_t size) {
    std::size_t sent_total = 0;
    while (sent_total < size) {
#ifdef _WIN32
        const int sent = ::send(
            toNativeSocket(socket_),
            data + sent_total,
            static_cast<int>(size - sent_total),
            0);
#else
        const ssize_t sent = ::send(
            toNativeSocket(socket_),
            data + sent_total,
            size - sent_total,
            0);
#endif
        if (sent <= 0) {
#ifdef _WIN32
            const int err = WSAGetLastError();
            std::cerr << "[TCP] send() failed at offset " << sent_total
                      << "/" << size
                      << " (WSAError=" << err << " " << wsaErrorString(err) << ")\n";
#else
            const int err = errno;
            std::cerr << "[TCP] send() failed at offset " << sent_total
                      << "/" << size
                      << " (errno=" << err << ")\n";
#endif
            return false;
        }
        sent_total += static_cast<std::size_t>(sent);
    }
    return true;
}

bool TcpTextTransportClient::sendText(const std::string& payload) {
    if (!connected_) {
        std::cerr << "[TCP] sendText() called but not connected.\n";
        return false;
    }
    const std::string line = payload + "\n";
    return sendAll(line.data(), line.size());
}

void TcpTextTransportClient::close() {
    const SocketHandle invalid = static_cast<SocketHandle>(-1);

#ifdef _WIN32
    if (socket_ != invalid) {
        // 先 shutdown(SD_SEND) 发送 FIN，让对端能优雅感知关闭
        ::shutdown(toNativeSocket(socket_), SD_SEND);
        ::closesocket(toNativeSocket(socket_));
        socket_ = invalid;
        std::cerr << "[TCP] Socket closed (graceful shutdown).\n";
    }
#else
    if (socket_ != invalid) {
        ::shutdown(toNativeSocket(socket_), SHUT_WR);
        ::close(toNativeSocket(socket_));
        socket_ = invalid;
        std::cerr << "[TCP] Socket closed (graceful shutdown).\n";
    }
#endif

    connected_ = false;

#ifdef _WIN32
    if (wsa_started_) {
        WSACleanup();
        wsa_started_ = false;
    }
#endif
}

// ---------------------------------------------------------------------------
// Factory
// ---------------------------------------------------------------------------

std::unique_ptr<ITransportClient> createTransport(const std::string& mode,
                                                  const std::string& host,
                                                  std::uint16_t port,
                                                  const std::string& output_path) {
    if (mode == "stdout") {
        return std::make_unique<StdoutTransportClient>();
    }
    if (mode == "file") {
        return std::make_unique<FileTransportClient>(output_path);
    }
    if (mode == "tcp") {
        return std::make_unique<TcpTextTransportClient>(host, port);
    }
    return nullptr;
}

} // namespace vh
