﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AElf.ChainController.EventMessages;
using AElf.ChainController.TxMemPool;
using AElf.Common.Attributes;
using AElf.Common.ByteArrayHelpers;
using AElf.Common.Collections;
using AElf.Common.Extensions;
using AElf.Configuration;
using AElf.Kernel;
using AElf.Network;
using AElf.Network.Connection;
using AElf.Network.Data;
using AElf.Network.Eventing;
using AElf.Network.Peers;
using AElf.Node.Protocol.Events;
using Easy.MessageHub;
using Google.Protobuf;
using NLog;

[assembly:InternalsVisibleTo("AElf.Network.Tests")]
namespace AElf.Node.Protocol
{
    [LoggerName(nameof(NetworkManager))]
    public class NetworkManager : INetworkManager
    {
        #region Settings

        public const int DefaultMaxBlockHistory = 15;
        public const int DefaultMaxTransactionHistory = 15;
        
        public const int DefaultRequestTimeout = 2000;
        public const int DefaultRequestMaxRetry = TimeoutRequest.DefaultMaxRetry;
        
        public int MaxBlockHistory { get; set; } = DefaultMaxBlockHistory;
        public int MaxTransactionHistory { get; set; } = DefaultMaxTransactionHistory;
        
        public int RequestTimeout { get; set; } = DefaultRequestTimeout;
        public int RequestMaxRetry { get; set; } = DefaultRequestMaxRetry;

        #endregion
        
        public event EventHandler MessageReceived;
        public event EventHandler RequestFailed;
        public event EventHandler BlockReceived;
        public event EventHandler TransactionsReceived;

        private readonly ITxPoolService _transactionPoolService;
        private readonly IPeerManager _peerManager;
        private readonly ILogger _logger;
        
        private readonly List<IPeer> _peers = new List<IPeer>();

        private readonly Object _pendingRequestsLock = new Object();
        private readonly List<TimeoutRequest> _pendingRequests;

        private BoundedByteArrayQueue _lastBlocksReceived;
        private BoundedByteArrayQueue _lastTxReceived;

        private readonly BlockingPriorityQueue<PeerMessageReceivedArgs> _incomingJobs;
        
        private readonly List<byte[]> _bpKeys;

        private byte[] _nodeKey;
        private string _nodeName;

        private bool _isBp;

        public NetworkManager(ITxPoolService transactionPoolService, IPeerManager peerManager, ILogger logger)
        {
            _incomingJobs = new BlockingPriorityQueue<PeerMessageReceivedArgs>();
            _pendingRequests = new List<TimeoutRequest>();
            _bpKeys = new List<byte[]>();

            _transactionPoolService = transactionPoolService;
            _peerManager = peerManager;
            _logger = logger;

            _nodeName = NodeConfig.Instance.NodeName;
            
            peerManager.PeerEvent += PeerManagerOnPeerAdded;

            SetBpConfig();

            MessageHub.Instance.Subscribe<TransactionAddedToPool>(async inTx =>
                {
                    await BroadcastMessage(AElfProtocolMsgType.NewTransaction, inTx.Transaction.Serialize());
                    _logger?.Trace($"[event] tx added to the pool {inTx?.Transaction?.GetHashBytes()?.ToHex()}.");
                });
            
            MessageHub.Instance.Subscribe<BlockMinedMessage>(async b =>
                {
                    var serializedBlock = b.Block.Serialize();
                    await BroadcastBlock(b.Block.GetHash().GetHashBytes(), serializedBlock);
                    _logger?.Trace($"[event] Broadcasted block \"{b.Block.GetHash().GetHashBytes().ToHex()}\" to peers with {b.Block.Body.TransactionsCount} tx(s). Block height: [{b.Block.Header.Index}].");
                });
        }

        private void SetBpConfig()
        {
            var producers = MinersConfig.Instance.Producers;

            // Set the list of block producers
            try
            {
                foreach (var bp in producers.Values)
                {
                    byte[] key = ByteArrayHelpers.FromHexString(bp["address"]);
                    _bpKeys.Add(key);
                }
            }
            catch (Exception e)
            {
                _logger?.Warn(e, "Error while reading mining info.");
            }
            
            // This nodes key
            _nodeKey = ByteArrayHelpers.FromHexString(NodeConfig.Instance.NodeAccount);
            _isBp = _bpKeys.Any(k => k.BytesEqual(_nodeKey));
        }

        #region Eventing

        private void PeerManagerOnPeerAdded(object sender, EventArgs eventArgs)
        {
            if (eventArgs is PeerEventArgs peer && peer.Peer != null && peer.Actiontype == PeerEventType.Added)
            {
                _peers.Add(peer.Peer);

                peer.Peer.MessageReceived += HandleNewMessage;
                peer.Peer.PeerDisconnected += ProcessClientDisconnection;
            }
        }
        
        /// <summary>
        /// Callback for when a Peer fires a <see cref="PeerDisconnected"/> event. It unsubscribes
        /// the manager from the events and removes it from the list.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ProcessClientDisconnection(object sender, EventArgs e)
        {
            if (sender != null && e is PeerDisconnectedArgs args && args.Peer != null)
            {
                IPeer peer = args.Peer;
                
                peer.MessageReceived -= HandleNewMessage;
                peer.PeerDisconnected -= ProcessClientDisconnection;
                
                _peers.Remove(args.Peer);
            }
        }
        
        private void HandleNewMessage(object sender, EventArgs e)
        {
            if (e is PeerMessageReceivedArgs args)
            {
                _incomingJobs.Enqueue(args, 0);
            }
        }

        #endregion

        /// <summary>
        /// This method start the server that listens for incoming
        /// connections and sets up the manager.
        /// </summary>
        public void Start()
        {
            // init the queue
            _lastBlocksReceived = new BoundedByteArrayQueue(MaxBlockHistory);
            _lastTxReceived = new BoundedByteArrayQueue(MaxTransactionHistory);
            
            _peerManager.Start();
            
            Task.Run(() => StartProcessingIncoming()).ConfigureAwait(false);
        }
        
        #region Message processing

        private void StartProcessingIncoming()
        {
            while (true)
            {
                try
                {
                    PeerMessageReceivedArgs msg = _incomingJobs.Take();
                    ProcessPeerMessage(msg);
                }
                catch (Exception e)
                {
                    _logger?.Error(e, "Error while processing incoming messages");
                }
            }
        }
        
        private void ProcessPeerMessage(PeerMessageReceivedArgs args)
        {
            if (args?.Peer == null || args.Message == null)
            {
                _logger.Warn("Invalid message from peer.");
                return;
            }
            
            AElfProtocolMsgType msgType = (AElfProtocolMsgType) args.Message.Type;
            
            switch (msgType)
            {
                // New blocks and requested blocks will be added to the sync
                // Subscribe to the BlockReceived event.
                case AElfProtocolMsgType.NewBlock:
                case AElfProtocolMsgType.Block:
                    HandleBlockReception(msgType, args.Message, args.Peer);
                    break;
                // Transactions requested from the sync.
                case AElfProtocolMsgType.Transactions:
                    HandleTransactionsMessage(msgType, args.Message, args.Peer);
                    break;
                // New transaction issue from a broadcast.
                case AElfProtocolMsgType.NewTransaction:
                    HandleNewTransaction(msgType, args.Message, args.Peer);
                    break;
            }
            
            // Re-fire the event for higher levels if needed.
            BubbleMessageReceivedEvent(args);
        }
        
        private void BubbleMessageReceivedEvent(PeerMessageReceivedArgs args)
        {
            MessageReceived?.Invoke(this, new NetMessageReceivedEventArgs(args.Message, args));
        }

        private void HandleTransactionsMessage(AElfProtocolMsgType msgType, Message msg, Peer peer)
        {
            try
            {
                if (msg.HasId)
                    GetAndClearRequest(msg);
                
                TransactionList txList = TransactionList.Parser.ParseFrom(msg.Payload);
                
                // The sync should subscribe to this and add to pool
                TransactionsReceived?.Invoke(this, new TransactionsReceivedEventArgs(txList, peer, msgType));
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while deserializing transaction list.");
            }
        }

        private void HandleNewTransaction(AElfProtocolMsgType msgType, Message msg, Peer peer)
        {
            try
            {
                Transaction tx = Transaction.Parser.ParseFrom(msg.Payload);
                
                byte[] txHash = tx.GetHashBytes();

                if (_lastTxReceived.Contains(txHash))
                    return;

                _lastTxReceived.Enqueue(txHash);

                // Add to the pool; if valid, rebroadcast.
                var addResult = _transactionPoolService.AddTxAsync(tx).GetAwaiter().GetResult();

                if (addResult == TxValidation.TxInsertionAndBroadcastingError.Success)
                {
                    _logger?.Debug($"Transaction (new) with hash {txHash.ToHex()} added to the pool.");
                        
                    foreach (var p in _peers.Where(p => !p.Equals(peer)))
                        p.EnqueueOutgoing(msg);
                }
                else
                {
                    _logger?.Debug($"New transaction from {peer} not added to the pool: {addResult}");
                }
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while handling new transaction reception");
            }
        }

        private void HandleBlockReception(AElfProtocolMsgType msgType, Message msg, Peer peer)
        {
            try
            {
                Block block = Block.Parser.ParseFrom(msg.Payload);
            
                byte[] blockHash = block.GetHashBytes();

                if (_lastBlocksReceived.Contains(blockHash))
                    return;
                
                _lastBlocksReceived.Enqueue(blockHash);
                    
                // Rebroadcast to peers - note that the block has not been validated
                foreach (var p in _peers.Where(p => !p.Equals(peer)))
                    p.EnqueueOutgoing(msg);
                
                BlockReceived?.Invoke(this, new BlockReceivedEventArgs(block, peer));
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while handling block reception");
            }
        }

        #endregion
        
        public void QueueTransactionRequest(List<byte[]> transactionHashes, IPeer hint)
        {
            try
            {
                IPeer selectedPeer = hint ?? _peers.FirstOrDefault();
            
                if(selectedPeer == null)
                    return;
            
                // Create the message
                TxRequest br = new TxRequest();
                br.TxHashes.Add(transactionHashes.Select(h => ByteString.CopyFrom(h)).ToList());
                var msg = NetRequestFactory.CreateMessage(AElfProtocolMsgType.TxRequest, br.ToByteArray());
                
                // Identification
                msg.HasId = true;
                msg.Id = Guid.NewGuid().ToByteArray();
            
                // Select peer for request
                TimeoutRequest request = new TimeoutRequest(transactionHashes, msg, RequestTimeout);
                request.MaxRetryCount = RequestMaxRetry;
            
                lock (_pendingRequestsLock)
                {
                    _pendingRequests.Add(request);
                }
            
                request.RequestTimedOut += RequestOnRequestTimedOut;
                request.TryPeer(selectedPeer);
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while requesting transactions.");
            }
        }
        
        public void QueueBlockRequestByIndex(int index)
        {
            try
            {
                Peer selectedPeer = (Peer)_peers.FirstOrDefault();
            
                if(selectedPeer == null)
                    return;
                
                // Create the request object
                BlockRequest br = new BlockRequest { Height = index };
                Message message = NetRequestFactory.CreateMessage(AElfProtocolMsgType.RequestBlock, br.ToByteArray());
                
                // Select peer for request
                TimeoutRequest request = new TimeoutRequest(index, message, RequestTimeout);
                request.MaxRetryCount = RequestMaxRetry;
                
                lock (_pendingRequestsLock)
                {
                    _pendingRequests.Add(request);
                }

                request.TryPeer(selectedPeer);
            }
            catch (Exception e)
            {
                _logger?.Error(e, $"Error while requesting block for index {index}.");
            }
        }

        /// <summary>
        /// Callback called when the requests internal timer has executed.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="eventArgs"></param>
        private void RequestOnRequestTimedOut(object sender, EventArgs eventArgs)
        {
            if (sender == null)
            {
                _logger?.Warn("Request timeout - sender null.");
                return;
            }

            if (sender is TimeoutRequest req)
            {
                _logger?.Trace("Request timeout : " + req.IsBlockRequest + $", with {req.Peer} and timeout : {TimeSpan.FromMilliseconds(req.Timeout)}.");
                
                if (req.IsTxRequest && req.TransactionHashes != null && req.TransactionHashes.Any())
                {
                    _logger?.Trace("Hashes : [" + string.Join(", ", req.TransactionHashes.Select(kvp => kvp.ToHex())) + "]");
                }
                
                if (req.HasReachedMaxRetry)
                {
                    lock (_pendingRequestsLock)
                    {
                        _pendingRequests.Remove(req);
                    }
                    
                    req.RequestTimedOut -= RequestOnRequestTimedOut;
                    FireRequestFailed(req);
                    return;
                }
                
                IPeer nextPeer = _peers.FirstOrDefault(p => !p.Equals(req.Peer));
                
                if (nextPeer != null)
                {
                    _logger?.Trace("Trying another peer : " + req.RequestMessage.RequestLogString + $", next : {nextPeer}.");
                    req.TryPeer(nextPeer);
                }
            }
            else
            {
                _logger?.Trace("Request timeout - sender wrong type.");
            }
        }

        private void FireRequestFailed(TimeoutRequest req)
        {
            RequestFailedEventArgs reqFailedEventArgs = new RequestFailedEventArgs
            {
                RequestMessage = req.RequestMessage,
                TriedPeers = req.TriedPeers.ToList()
            };

            _logger?.Warn("Request failed : " + req.RequestMessage.RequestLogString + $" after {req.TriedPeers.Count} tries. Max tries : {req.MaxRetryCount}.");
                    
            RequestFailed?.Invoke(this, reqFailedEventArgs);
        }
        
        internal TimeoutRequest GetAndClearRequest(Message msg)
        {
            if (msg == null)
            {
                _logger?.Warn("Handle message : peer or message null.");
                return null;
            }
            
            try
            {
                TimeoutRequest request;
                
                lock (_pendingRequestsLock)
                {
                    request = _pendingRequests.FirstOrDefault(r => r.Id.BytesEqual(msg.Id));
                }

                if (request != null)
                {
                    request.RequestTimedOut -= RequestOnRequestTimedOut;
                    request.Stop();
                    
                    lock (_pendingRequestsLock)
                    {
                        _pendingRequests.Remove(request);
                    }
                    
                    if (request.IsTxRequest && request.TransactionHashes != null && request.TransactionHashes.Any())
                    {
                        _logger?.Debug("Matched : [" + string.Join(", ", request.TransactionHashes.Select(kvp => kvp.ToHex()).ToList()) + "]");
                    }
                }
                else
                {
                    _logger?.Warn($"Request not found. Index : {msg.Id.ToHex()}.");
                }

                return request;
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Exception while handling request message.");
                return null;
            }
        }

        public async Task<int> BroadcastBlock(byte[] hash, byte[] payload)
        {
            _lastBlocksReceived.Enqueue(hash);
            return await BroadcastMessage(AElfProtocolMsgType.NewBlock, payload);
        }

        /// <summary>
        /// This message broadcasts data to all of its peers. This creates and
        /// sends a <see cref="AElfPacketData"/> object with the provided pay-
        /// load and message type.
        /// </summary>
        /// <param name="messageMsgType"></param>
        /// <param name="payload"></param>
        /// <param name="messageId"></param>
        /// <returns></returns>
        public async Task<int> BroadcastMessage(AElfProtocolMsgType messageMsgType, byte[] payload)
        {
            try
            {
                
                Message packet = NetRequestFactory.CreateMessage(messageMsgType, payload);
                return BroadcastMessage(packet);
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while sending a message to the peers.");
                return 0;
            }
        }

        public int BroadcastMessage(Message message)
        {
            if (_peers == null || !_peers.Any())
                return 0;

            int count = 0;
            
            try
            {
                foreach (var peer in _peers)
                {
                    try
                    {
                        peer.EnqueueOutgoing(message); //todo
                        count++;
                    }
                    catch (Exception e) { }
                }
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while sending a message to the peers.");
            }

            return count;
        }
    }
}