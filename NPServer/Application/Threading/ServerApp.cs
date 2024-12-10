﻿using NPServer.Application.Main;
using NPServer.Core.Helpers;
using NPServer.Core.Network.Listeners;
using NPServer.Core.Interfaces.Network;
using NPServer.Infrastructure.Config;
using NPServer.Infrastructure.Logging;
using NPServer.Infrastructure.Services;
using NPServer.Infrastructure.Settings;
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NPServer.Core.Network.Firewall;

namespace NPServer.Application.Threading
{
    internal class ServerApp
    {
        private int _isRunning;
        private bool _isInMaintenanceMode;

        private SessionController _controller;
        private SocketListener _networkListener;

        private CancellationTokenSource _ctokens;
        private readonly NetworkConfig networkConfig = ConfigManager.Instance.GetConfig<NetworkConfig>();
        private readonly FirewallRateLimit _requestLimiter = Singleton.GetInstance<FirewallRateLimit>();

        public ServerApp(CancellationTokenSource tokenSource)
        {
            _isRunning = 0;
            _isInMaintenanceMode = false;

            _ctokens = tokenSource;
            _networkListener = new SocketListener(
                AddressFamily.InterNetwork, SocketType.Stream,
                ProtocolType.Tcp, networkConfig.MaxConnections);
            _controller = new SessionController(networkConfig.TimeoutInSeconds, _ctokens.Token);
        }

        private void InitializeComponents()
        {
            _ctokens = new CancellationTokenSource();
            _networkListener = new SocketListener(
                AddressFamily.InterNetwork, SocketType.Stream,
                ProtocolType.Tcp, networkConfig.MaxConnections);
            _controller = new SessionController(networkConfig.TimeoutInSeconds, _ctokens.Token);
        }

        public void Run()
        {
            if (Interlocked.CompareExchange(ref _isRunning, 1, 0) == 1)
            {
                NPLog.Instance.Warning("Server is already running.");
                return;
            }

            // Khởi tạo lại các thành phần
            InitializeComponents();

            CancellationToken token = _ctokens.Token;

            _networkListener.StartListening(ipAddress: networkConfig.IP, port: networkConfig.Port);
            NPLog.Instance.Info<ServerApp>($"Starting network service at {networkConfig.IP}:{networkConfig.Port}");

            _ = Task.Run(async () =>
            {
                try
                {
                    await AcceptClientConnectionsAsync(token);
                }
                catch (OperationCanceledException)
                {
                    NPLog.Instance.Info("Accepting client connections was cancelled.");
                }
                catch (Exception ex)
                {
                    NPLog.Instance.Error(ex);
                    Shutdown();
                }
            }, token);
        }

        private async Task AcceptClientConnectionsAsync(CancellationToken token)
        {
            while (_isRunning == 1)
            {
                if (token.IsCancellationRequested)
                {
                    NPLog.Instance.Warning("Server stopping due to cancellation request.");
                    return;
                }

                if (_isInMaintenanceMode)
                {
                    NPLog.Instance.Warning("Server in maintenance mode.");
                    await Task.Delay(5000, token);
                    continue;
                }

                try
                {
                    if (!_networkListener.IsListening)
                    {
                        NPLog.Instance.Warning("Socket is no longer listening. Aborting connection accept.");
                        break;
                    }

                    Socket? acceptSocket = await _networkListener.AcceptClientAsync(token);

                    if (acceptSocket == null) continue;

                    if (!_requestLimiter.IsAllowed(NetworkHelper.GetClientIP(acceptSocket)))
                    {
                        acceptSocket.Close();
                        continue;
                    }

                    _controller.AcceptClient(acceptSocket);
                }
                catch (SocketException ex)
                {
                    NPLog.Instance.Error<ServerApp>($"Socket error: {ex.SocketErrorCode}, Message: {ex.Message}");
                }
                catch (Exception ex)
                {
                    NPLog.Instance.Error<ServerApp>($"Unexpected error: {ex.Message}");
                }
            }
        }

        public void Shutdown()
        {
            if (Interlocked.CompareExchange(ref _isRunning, 0, 1) == 0)
            {
                NPLog.Instance.Warning("Server is not running.");
                return;
            }

            _ctokens.Cancel();

            Task.Run(async () =>
            {
                try
                {
                    await _controller.DisconnectAllClientsAsync();
                }
                catch (Exception ex)
                {
                    NPLog.Instance.Error<ServerApp>($"Error during disconnecting clients: {ex.Message}");
                }

                try
                {
                    _networkListener.StopListening();
                    //_networkListener.Dispose();
                    //NPLog.Instance.Info("Socket resources disposed.");
                }
                catch (Exception ex)
                {
                    NPLog.Instance.Error<ServerApp>($"Error during socket cleanup: {ex.Message}");
                }
            });

            NPLog.Instance.Info<ServerApp>("Server stopped successfully.");
        }

        public void Reset()
        {
            if (_isRunning == 1)
            {
                NPLog.Instance.Warning("Server is still stopping, waiting for the stop process to complete.");

                this.Shutdown();
                Thread.Sleep(5000);

                this.Reset();
            }
            else
            {
                this.Run();
                NPLog.Instance.Info("Server reset successfully.");
            }
        }

        public void SetMaintenanceMode(bool isMaintenance)
        {
            _isInMaintenanceMode = isMaintenance;
            NPLog.Instance.Info(isMaintenance ? "Server is now in maintenance mode." : "Server has exited maintenance mode.");
        }

        public int GetActiveConnections()
        {
            return _controller.ActiveSessions();
        }

        public bool IsServerRunning()
        {
            return _isRunning == 1;
        }

        public void CancelOperation()
        {
            _ctokens.Cancel();
            _ctokens.Dispose(); // Disposes the token source
        }
    }
}