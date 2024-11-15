﻿using NETServer.Infrastructure.Configuration;
using NETServer.Infrastructure.Interfaces;
using NETServer.Infrastructure.Security;
using NETServer.Infrastructure.Logging;
using NETServer.Application.Handlers;

using System.Net.Sockets;
using System.Collections.Concurrent;
using NETServer.Application.Enums;

namespace NETServer.Application.Network
{
    internal class SessionController : ISessionController
    {
        private readonly SslSecurity _sslSecurity;
        private readonly RequestLimiter _requestLimiter;
        private readonly CommandHandler _commandHandler;
        private readonly ConnectionLimiter _connectionLimiter;

        public readonly ConcurrentDictionary<Guid, WeakReference<ClientSession>> ActiveSessions = new();

        public SessionController()
        {
            _sslSecurity = new SslSecurity();
            _commandHandler = new CommandHandler();
            _connectionLimiter = new ConnectionLimiter(Setting.MaxConnections);
            _requestLimiter = new RequestLimiter(Setting.RateLimit, Setting.ConnectionLockoutDuration);
        }

        public async Task HandleClient(TcpClient client, CancellationToken cancellationToken)
        {
            var session = new ClientSession(client, _requestLimiter, _connectionLimiter, _sslSecurity);

            if (!await session.AuthorizeClientSession())
                return;

            await session.Connect();
            ActiveSessions[session.Id] = new WeakReference<ClientSession>(session);

            Cmd command;
            byte[] data;

            try
            {
                if (session.Transport == null)
                {
                    throw new InvalidOperationException("DataTransport is null. The session is not properly initialized.");
                }

                while (session.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    // Kiểm tra timeout session
                    if (session.IsSessionTimedOut()) break;

                    // Kiểm tra cancellationToken nếu server muốn dừng vòng lặp này
                    if (cancellationToken.IsCancellationRequested) break;

                    // Nhận dữ liệu từ client
                    (command, data) = await session.Transport.ReceiveAsync(cancellationToken);

                    if (command == default || data.Length == 0)
                    {
                        await Task.Delay(50, cancellationToken);
                        continue;
                    }

                    // Update last activity time each time a command is received
                    session.UpdateLastActivityTime();

                    await _commandHandler.HandleCommand(session, command, data);
                }
            }
            catch (IOException ioEx)
            {
                NLog.Error($"I/O error in client session {session.ClientAddress}: {ioEx.Message}");
            }
            catch (SocketException sockEx)
            {
                NLog.Error($"Socket error in client session {session.ClientAddress}: {sockEx.Message}");
            }
            catch (Exception ex)
            {
                NLog.Error($"General error in client session {session.ClientAddress}: {ex.Message}");
            }
            finally
            {
                await CloseConnection(session);
            }
        }

        private async Task CloseConnection(ClientSession? session)
        {
            if (session == null || !session.IsConnected) return;

            try
            {
                await session.Disconnect();
                // Dọn dẹp WeakReference khi kết thúc phiên
                ActiveSessions.TryRemove(session.Id, out _);
            }
            catch (Exception e)
            {
                NLog.Error($"Error while closing connection for session {session.Id}: {e}");
            }
        }

        public async Task CloseAllConnections()
        {
            if (ActiveSessions.IsEmpty) return;

            var closeTasks = ActiveSessions.Values
                .Select(sessionRef => sessionRef.TryGetTarget(out var session) && session?.IsConnected == true
                    ? CloseConnection(session)
                    : Task.CompletedTask);

            await Task.WhenAll(closeTasks);

            NLog.Info("All connections closed successfully.");
        }

        public void CleanUpInactiveSessions()
        {
            // Lọc ra các session không còn kết nối và xóa khỏi ActiveSessions
            var inactiveSessions = ActiveSessions
                .Where(sessionRef => !sessionRef.Value.TryGetTarget(out var session) || session == null || !session.IsConnected)
                .ToList();

            foreach (var session in inactiveSessions)
            {
                ActiveSessions.TryRemove(session.Key, out _); // Xóa session khỏi ActiveSessions
            }
        }
    }
}