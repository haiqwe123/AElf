﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AElf.Kernel.Node.Network.Config;
using AElf.Kernel.Node.Network.Data;
using AElf.Kernel.Node.Network.Exceptions;
using AElf.Kernel.Node.Network.Peers;
using NLog;

namespace AElf.Kernel.Node.Network
{
    /// <summary>
    /// The event that's raised when a new connection is
    /// transformed into a Peer.
    /// </summary>
    public class ClientConnectedArgs : EventArgs
    {
        public Peer NewPeer { get; set; }
    }
    
    /// <summary>
    /// This class is a tcp server implementation. Its main functionality
    /// is to listen for incoming tcp connections and transform them into
    /// Peers.
    /// </summary>
    [LoggerName("Server")]
    public class AElfTcpServer : IAElfServer
    {
        private const int PeerBufferLength = 1024;
        
        public event EventHandler ClientConnected;
        
        private readonly IAElfNetworkConfig _config;
        private readonly ILogger _logger;
        
        private TcpListener _listener;
        private readonly NodeData _nodeData;
        
        private CancellationTokenSource _tokenSource;
        private CancellationToken _token;

        public AElfTcpServer(IAElfNetworkConfig config, ILogger logger)
        {
            _config = config;
            _logger = logger;
            
            if (config != null && !string.IsNullOrEmpty(config.Host))
                _nodeData = new NodeData { IpAddress = config.Host, Port = config.Port };
        }

        /// <summary>
        /// Starts the server based on the information contained
        /// in the <see cref="IAElfServerConfig"/> object.
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public async Task StartAsync(CancellationToken? token = null)
        {
            if (_config == null)
                throw new ServerConfigurationException("Could not start the server, config object is null.");

            if (!IPAddress.TryParse(_config.Host, out var listenAddress))
                throw new ServerConfigurationException("Could not start the server, invalid ip.");
                
            try
            {
                _logger?.Trace("Starting server on : " + _config.Host + ":" + _config.Port);
                _listener = new TcpListener(listenAddress, _config.Port);
                _listener.Start();
            }
            catch (Exception e)
            {
                _logger?.Error(e, "An error occurred while starting the server");
            }
            
            _logger?.Info("Server listening on " + _listener.LocalEndpoint);
            
            _tokenSource = CancellationTokenSource.CreateLinkedTokenSource(token ?? new CancellationToken());
            _token = _tokenSource.Token;
            
            try
            {
                while (!_token.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    
                    Task.Run(async () =>
                    {
                        await ProcessConnectionRequest(client);

                    }, _token);
                }
            }
            finally
            {
                _listener.Stop();
            }
        }
        
        /// <summary>
        /// Processes a connection after the tcp client is accepted.
        /// </summary>
        /// <param name="client"></param>
        /// <returns></returns>
        private async Task ProcessConnectionRequest(TcpClient client)
        {
            if (client == null)
            {
                _logger?.Error("Connection refused: null client");
                return;
            }
            
            _logger?.Trace("Connection established : " + client.Client.RemoteEndPoint);
            
            Peer connected = await FinalizeConnect(client, client.GetStream());

            if (connected == null)
                return;
            
            ClientConnectedArgs args = new ClientConnectedArgs();
            args.NewPeer = connected;
            
            ClientConnected?.Invoke(this, args);
        }
        
        /// <summary>
        /// Reads the initial data sent by the remote peer after a
        /// successful connection.
        /// </summary>
        public async Task<Peer> FinalizeConnect(TcpClient tcpClient, NetworkStream stream)
        {
            try
            {
                // todo : better error management and logging
                
                // read the initial data
                byte[] bytes = new byte[1024];// todo not every call
                
                int bytesRead = await stream.ReadAsync(bytes, 0, 1024);

                if (bytesRead > 0)
                {
                    NodeData n = NodeData.Parser.ParseFrom(bytes, 0, bytesRead);
                    Peer p = new Peer(_nodeData, n, tcpClient);
                    
                    await p.WriteConnectInfoAsync();
                    
                    return p;
                }
                
                return null;
            }
            catch (Exception e)
            {
                _logger?.Warn("Error creating the connection");
                return null;
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Server.Disconnect(true);
            _tokenSource?.Dispose();
        }
    }
}