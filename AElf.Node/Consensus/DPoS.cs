﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AElf.ChainController;
using AElf.ChainController.EventMessages;
using AElf.Common;
using AElf.Configuration;
using AElf.Configuration.Config.Consensus;
using AElf.Cryptography.ECDSA;
using AElf.Kernel.Consensus;
using AElf.Miner.Miner;
using AElf.Node;
using AElf.Node.AElfChain;
using AElf.Types.CSharp;
using Easy.MessageHub;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NLog;
using AElf.Miner.EventMessages;
using AElf.Miner.TxMemPool;
using AElf.Synchronization.EventMessages;
using AElf.Kernel.Storages;
using AElf.Node.EventMessages;

// ReSharper disable once CheckNamespace
namespace AElf.Kernel.Node
{
    // ReSharper disable InconsistentNaming
    public class DPoS : IConsensus
    {
        /// <summary>
        /// Actually store the round number of DPoS processing.
        /// </summary>
        private ulong ConsensusMemory { get; set; }

        private static IDisposable ConsensusDisposable { get; set; }

        private bool isMining;

        private readonly ITxHub _txHub;
        private readonly IMiner _miner;
        private readonly IChainService _chainService;

        private IBlockChain _blockChain;

        private IBlockChain BlockChain => _blockChain ?? (_blockChain =
                                              _chainService.GetBlockChain(
                                                  Hash.LoadHex(NodeConfig.Instance.ChainId)));

        private readonly ILogger _logger;

        private static AElfDPoSHelper Helper;

        /// <summary>
        /// In Value and Out Value.
        /// </summary>
        private readonly Stack<Hash> _consensusData = new Stack<Hash>();

        private readonly NodeKeyPair _nodeKeyPair = new NodeKeyPair(NodeConfig.Instance.ECKeyPair);

        public Address ContractAddress => AddressHelpers.GetSystemContractAddress(
            Hash.LoadHex(NodeConfig.Instance.ChainId),
            SmartContractType.AElfDPoS.ToString());

        private int _flag;

        private AElfDPoSObserver AElfDPoSObserver => new AElfDPoSObserver(MiningWithInitializingAElfDPoSInformation,
            MiningWithPublishingOutValueAndSignature, PublishInValue, MiningWithUpdatingAElfDPoSInformation);

        public DPoS(IStateStore stateStore, ITxHub txHub, IMiner miner,
            IChainService chainService)
        {
            _txHub = txHub;
            _miner = miner;
            _chainService = chainService;

            _logger = LogManager.GetLogger(nameof(DPoS));

            Helper = new AElfDPoSHelper(Hash.LoadHex(NodeConfig.Instance.ChainId), Miners,
                ContractAddress, stateStore);

            var count = MinersConfig.Instance.Producers.Count;

            ConsensusConfig.Instance.BlockProducerNumber = count;

            _logger?.Info("Block Producer nodes count:" + ConsensusConfig.Instance.BlockProducerNumber);
            _logger?.Info("Blocks of one round:" + ConsensusConfig.Instance.BlockNumberOfEachRound);

            if (ConsensusConfig.Instance.BlockProducerNumber == 1 && NodeConfig.Instance.IsMiner)
            {
                AElfDPoSObserver.RecoverMining();
            }
        }

        private static Miners Miners
        {
            get
            {
                var dict = MinersConfig.Instance.Producers;
                var miners = new Miners();

                foreach (var bp in dict.Values)
                {
                    var b = bp["address"].RemoveHexPrefix();
                    miners.Nodes.Add(b);
                }

                return miners;
            }
        }

        public async Task Start()
        {
            if (ConsensusDisposable != null)
            {
                Recover();
                return;
            }

            if (isMining)
                return;

            isMining = true;

            // Check whether this node contained BP list.
            if (!Miners.Nodes.Contains(_nodeKeyPair.Address.DumpHex().RemoveHexPrefix()))
            {
                return;
            }

            if (NodeConfig.Instance.ConsensusInfoGenerator && !await Helper.HasGenerated())
            {
                AElfDPoSObserver.Initialization();
                return;
            }

            Helper.SyncMiningInterval();
            _logger?.Info($"Set AElf DPoS mining interval to: {ConsensusConfig.Instance.DPoSMiningInterval} ms.");

            if (Helper.CanRecoverDPoSInformation())
            {
                ConsensusDisposable = AElfDPoSObserver.RecoverMining();
            }
        }

        public void Stop()
        {
            ConsensusDisposable?.Dispose();
            _logger?.Trace("Mining stopped. Disposed previous consensus observables list.");
        }

        public void Hang()
        {
            Interlocked.CompareExchange(ref _flag, 1, 0);
        }

        public void Recover()
        {
            Interlocked.CompareExchange(ref _flag, 0, 1);
        }

        private async Task<IBlock> Mine()
        {
            try
            {
                MessageHub.Instance.Publish(new SyncUnfinishedBlock(await BlockChain.GetCurrentBlockHeightAsync()));

                var block = await _miner.Mine(Helper.GetCurrentRoundInfo());

                return block;
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Exception while mining.");
                return null;
            }
        }

        private async Task<Transaction> GenerateTransactionAsync(string methodName, IReadOnlyList<byte[]> parameters)
        {
            var bn = await BlockChain.GetCurrentBlockHeightAsync();
            bn = bn > 4 ? bn - 4 : 0;
            var bh = bn == 0 ? Hash.Genesis : (await BlockChain.GetHeaderByHeightAsync(bn)).GetHash();
            var bhPref = bh.Value.Where((x, i) => i < 4).ToArray();
            var tx = new Transaction
            {
                From = _nodeKeyPair.Address,
                To = ContractAddress,
                RefBlockNumber = bn,
                RefBlockPrefix = ByteString.CopyFrom(bhPref),
                MethodName = methodName,
                Sig = new Signature
                {
                    P = ByteString.CopyFrom(_nodeKeyPair.NonCompressedEncodedPublicKey)
                },
                Type = TransactionType.DposTransaction
            };

            switch (parameters.Count)
            {
                case 2:
                    tx.Params = ByteString.CopyFrom(ParamsPacker.Pack(parameters[0], parameters[1]));
                    break;
                case 3:
                    tx.Params = ByteString.CopyFrom(ParamsPacker.Pack(parameters[0], parameters[1], parameters[2]));
                    break;
                case 4:
                    tx.Params = ByteString.CopyFrom(ParamsPacker.Pack(parameters[0], parameters[1], parameters[2],
                        parameters[3]));
                    break;
                case 5:
                    tx.Params = ByteString.CopyFrom(ParamsPacker.Pack(parameters[0], parameters[1], parameters[2],
                        parameters[3], parameters[4]));
                    break;
            }

            var signer = new ECSigner();
            var signature = signer.Sign(_nodeKeyPair, tx.GetHash().DumpByteArray());

            // Update the signature
            tx.Sig.R = ByteString.CopyFrom(signature.R);
            tx.Sig.S = ByteString.CopyFrom(signature.S);

            return tx;
        }

        /// <summary>
        /// Related tx has 4 params:
        /// 1. Miners list
        /// 2. Information of first rounds
        /// 3. Mining interval
        /// 4. Log level
        /// </summary>
        /// <returns></returns>
        private async Task MiningWithInitializingAElfDPoSInformation()
        {
            var res = Interlocked.CompareExchange(ref _flag, 1, 0);
            if (res == 1)
                return;

            MessageHub.Instance.Publish(new MiningStateChanged(true));
            _logger?.Trace(
                $"Mine - Entered DPoS Mining Process - {nameof(MiningWithInitializingAElfDPoSInformation)}.");

            if (await Helper.HasGenerated())
            {
                MessageHub.Instance.Publish(new ConsensusGenerated(true));
                return;
            }

            var logLevel = new Int32Value {Value = LogManager.GlobalThreshold.Ordinal};
            var parameters = new List<byte[]>
            {
                Miners.ToByteArray(),
                Helper.GenerateInfoForFirstTwoRounds().ToByteArray(),
                new SInt32Value {Value = ConsensusConfig.Instance.DPoSMiningInterval}.ToByteArray(),
                logLevel.ToByteArray()
            };
            var txToInitializeAElfDPoS = await GenerateTransactionAsync("InitializeAElfDPoS", parameters);
            await BroadcastTransaction(txToInitializeAElfDPoS);

            MessageHub.Instance.Publish(new ConsensusStateChanged(ConsensusBehavior.InitializeAElfDPoS));

            await Mine();

            Interlocked.CompareExchange(ref _flag, 0, 1);
            MessageHub.Instance.Publish(new MiningStateChanged(false));
            _logger?.Trace(
                $"Mine - Leaving DPoS Mining Process - {nameof(MiningWithInitializingAElfDPoSInformation)}.");
        }

        /// <summary>
        /// Related tx has 5 params:
        /// 1. Current round number
        /// 2. BP Address
        /// 3. Out value
        /// 4. Signature
        /// 5. Round Id
        /// </summary>
        /// <returns></returns>
        private async Task MiningWithPublishingOutValueAndSignature()
        {
            var res = Interlocked.CompareExchange(ref _flag, 1, 0);
            if (res == 1)
                return;

            MessageHub.Instance.Publish(new MiningStateChanged(true));
            _logger?.Trace($"Mine - Entered DPoS Mining Process - {nameof(MiningWithPublishingOutValueAndSignature)}.");

            var inValue = Hash.Generate();
            if (_consensusData.Count <= 0)
            {
                _consensusData.Push(inValue);
                _consensusData.Push(Hash.FromMessage(inValue));
            }

            var currentRoundNumber = Helper.CurrentRoundNumber;
            var signature = Hash.Default;
            if (currentRoundNumber.Value > 1)
            {
                signature = Helper.CalculateSignature(inValue);
            }

            var parameters = new List<byte[]>
            {
                Helper.CurrentRoundNumber.ToByteArray(),
                new StringValue {Value = _nodeKeyPair.Address.DumpHex().RemoveHexPrefix()}.ToByteArray(),
                _consensusData.Pop().ToByteArray(),
                signature.ToByteArray(),
                new Int64Value {Value = Helper.GetCurrentRoundInfo().RoundId}.ToByteArray()
            };

            var txToPublishOutValueAndSignature =
                await GenerateTransactionAsync("PublishOutValueAndSignature", parameters);

            await BroadcastTransaction(txToPublishOutValueAndSignature);

            MessageHub.Instance.Publish(new ConsensusStateChanged(ConsensusBehavior.PublishOutValueAndSignature));

            await Mine();

            Interlocked.CompareExchange(ref _flag, 0, 1);
            MessageHub.Instance.Publish(new MiningStateChanged(false));
            _logger?.Trace($"Mine - Leaving DPoS Mining Process - {nameof(MiningWithPublishingOutValueAndSignature)}.");
        }

        /// <summary>
        /// Related tx has 3 params:
        /// 1. Current round number
        /// 2. BP Address
        /// 3. In value
        /// 4. Round Id
        /// </summary>
        /// <returns></returns>
        private async Task PublishInValue()
        {
            var res = Interlocked.CompareExchange(ref _flag, 1, 0);
            if (res == 1)
                return;

            _logger?.Trace($"Mine - Entered DPoS Mining Process - {nameof(PublishInValue)}.");

            var currentRoundNumber = Helper.CurrentRoundNumber;

            var parameters = new List<byte[]>
            {
                currentRoundNumber.ToByteArray(),
                new StringValue {Value = _nodeKeyPair.Address.DumpHex().RemoveHexPrefix()}.ToByteArray(),
                _consensusData.Pop().ToByteArray(),
                new Int64Value {Value = Helper.GetCurrentRoundInfo().RoundId}.ToByteArray()
            };

            var txToPublishInValue = await GenerateTransactionAsync("PublishInValue", parameters);

            await BroadcastTransaction(txToPublishInValue);

            Interlocked.CompareExchange(ref _flag, 0, 1);
            _logger?.Trace($"Mine - Leaving DPoS Mining Process - {nameof(PublishInValue)}.");
        }

        /// <summary>
        /// Related tx has 3 params:
        /// 1. Current round info
        /// 2. New round info
        /// 3. Extra block producer of new round
        /// </summary>
        /// <returns></returns>
        private async Task MiningWithUpdatingAElfDPoSInformation()
        {
            var res = Interlocked.CompareExchange(ref _flag, 1, 0);
            if (res == 1)
                return;

            MessageHub.Instance.Publish(new MiningStateChanged(true));
            _logger?.Trace($"Mine - Entered DPoS Mining Process - {nameof(MiningWithUpdatingAElfDPoSInformation)}.");

            var extraBlockResult = Helper.ExecuteTxsForExtraBlock();

            var parameters = new List<byte[]>
            {
                extraBlockResult.Item1.ToByteArray(),
                extraBlockResult.Item2.ToByteArray(),
                extraBlockResult.Item3.ToByteArray(),
                new Int64Value {Value = Helper.GetCurrentRoundInfo().RoundId}.ToByteArray()
            };

            var txForExtraBlock = await GenerateTransactionAsync("UpdateAElfDPoS", parameters);

            await BroadcastTransaction(txForExtraBlock);

            MessageHub.Instance.Publish(new ConsensusStateChanged(ConsensusBehavior.UpdateAElfDPoS));

            await Mine();

            Interlocked.CompareExchange(ref _flag, 0, 1);

            MessageHub.Instance.Publish(new MiningStateChanged(false));
            _logger?.Trace($"Mine - Leaving DPoS Mining Process - {nameof(MiningWithUpdatingAElfDPoSInformation)}.");
        }

        public async Task Update()
        {
            Helper.LogDPoSInformation(await BlockChain.GetCurrentBlockHeightAsync());

            if (ConsensusMemory == Helper.CurrentRoundNumber.Value)
                return;

            // Dispose previous observer.
            if (ConsensusDisposable != null)
            {
                ConsensusDisposable.Dispose();
                _logger?.Trace("Disposed previous consensus observables list. Will update DPoS information.");
            }

            // Update observer.
            var address = _nodeKeyPair.Address.DumpHex().RemoveHexPrefix();
            var miners = Helper.Miners;
            if (!miners.Nodes.Contains(address))
            {
                return;
            }

            var blockProducerInfoOfCurrentRound = Helper[address];
            ConsensusDisposable = AElfDPoSObserver.SubscribeAElfDPoSMiningProcess(blockProducerInfoOfCurrentRound,
                Helper.ExtraBlockTimeSlot);

            // Update current round number.
            ConsensusMemory = Helper.CurrentRoundNumber.Value;
        }

        public bool IsAlive()
        {
            var currentTime = DateTime.UtcNow;
            var currentRound = Helper.GetCurrentRoundInfo();
            var startTimeSlot = currentRound.BlockProducers.First(bp => bp.Value.Order == 1).Value.TimeSlot
                .ToDateTime();

            var endTimeSlot =
                startTimeSlot.AddMilliseconds(
                    ConsensusConfig.Instance.BlockProducerNumber * ConsensusConfig.Instance.DPoSMiningInterval * 2);

            return currentTime >
                   startTimeSlot.AddMilliseconds(
                       -ConsensusConfig.Instance.BlockProducerNumber * ConsensusConfig.Instance.DPoSMiningInterval) ||
                   currentTime < endTimeSlot.AddMilliseconds(ConsensusConfig.Instance.DPoSMiningInterval);
        }

        private async Task BroadcastTransaction(Transaction tx)
        {
            if (tx.Type == TransactionType.DposTransaction)
            {
                _logger?.Trace(
                    $"A DPoS tx has been generated: {tx.GetHash().DumpHex()} - {tx.MethodName} from {tx.From.DumpHex()}.");
            }

            if (tx.From.Equals(_nodeKeyPair.Address))
                _logger?.Trace(
                    $"Try to insert DPoS transaction to pool: {tx.GetHash().DumpHex()} " +
                    $"threadId: {Thread.CurrentThread.ManagedThreadId}");
            await _txHub.AddTransactionAsync(tx, true);
        }
    }
}