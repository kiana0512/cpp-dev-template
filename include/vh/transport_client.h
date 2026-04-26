#pragma once

#include <cstdint>
#include <fstream>
#include <memory>
#include <string>

namespace vh {

    class ITransportClient {
    public:
        virtual ~ITransportClient() = default;
        virtual bool connect() = 0;
        virtual bool sendText(const std::string& payload) = 0;
        virtual void close() = 0;
    };

    class StdoutTransportClient final : public ITransportClient {
    public:
        bool connect() override;
        bool sendText(const std::string& payload) override;
        void close() override;
    };

    class FileTransportClient final : public ITransportClient {
    public:
        explicit FileTransportClient(std::string path);
        bool connect() override;
        bool sendText(const std::string& payload) override;
        void close() override;

    private:
        std::string path_;
        std::ofstream out_;
    };

    class TcpTextTransportClient final : public ITransportClient {
    public:
        TcpTextTransportClient(std::string host, std::uint16_t port);
        ~TcpTextTransportClient() override;

        bool connect() override;
        bool sendText(const std::string& payload) override;
        void close() override;

    private:
        bool sendAll(const char* data, std::size_t size);

        std::string host_;
        std::uint16_t port_;

        // On Windows, SOCKET is UINT_PTR (unsigned). On POSIX, file descriptors
        // are int. Using uintptr_t on Windows keeps the cast to SOCKET lossless.
        // The sentinel static_cast<SocketHandle>(-1) equals INVALID_SOCKET on
        // Windows (UINTPTR_MAX) and -1 on POSIX, matching platform conventions.
#ifdef _WIN32
        using SocketHandle = std::uintptr_t;
#else
        using SocketHandle = int;
#endif

        SocketHandle socket_{static_cast<SocketHandle>(-1)};
        bool connected_{false};
        bool wsa_started_{false};
    };

    std::unique_ptr<ITransportClient> createTransport(const std::string& mode,
                                                      const std::string& host,
                                                      std::uint16_t port,
                                                      const std::string& output_path);

} // namespace vh