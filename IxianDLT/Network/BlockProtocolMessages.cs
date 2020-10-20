﻿using DLT.Meta;
using IXICore;
using IXICore.Inventory;
using IXICore.Meta;
using IXICore.Network;
using IXICore.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DLT
{
    namespace Network
    {
        class BlockProtocolMessages
        {
            // Broadcast the current block height. Called after accepting a new block once the node is fully synced
            // Returns false when no RemoteEndpoints found to send the message to
            public static bool broadcastBlockHeight(ulong blockNum, byte[] checksum)
            {
                using (MemoryStream mw = new MemoryStream())
                {
                    using (BinaryWriter writerw = new BinaryWriter(mw))
                    {
                        Block tmp_block = IxianHandler.getLastBlock();

                        // Send the block height
                        writerw.Write(blockNum);

                        // Send the block checksum for this balance
                        writerw.Write(checksum.Length);
                        writerw.Write(checksum);

                        return CoreProtocolMessage.broadcastProtocolMessage(new char[] { 'C' }, ProtocolMessageCode.blockHeight, mw.ToArray(), null, null);
                    }
                }
            }

            public static void handleGetNextSuperBlock(byte[] data, RemoteEndpoint endpoint)
            {
                using (MemoryStream m = new MemoryStream(data))
                {
                    using (BinaryReader reader = new BinaryReader(m))
                    {
                        ulong include_segments = reader.ReadUInt64();

                        bool full_header = reader.ReadBoolean();

                        Block block = null;

                        int checksum_len = reader.ReadInt32();
                        byte[] checksum = reader.ReadBytes(checksum_len);

                        block = Node.storage.getBlockByLastSBHash(checksum);

                        if (block != null)
                        {
                            endpoint.sendData(ProtocolMessageCode.blockData, block.getBytes(full_header), BitConverter.GetBytes(block.blockNum));

                            if (include_segments > 0)
                            {
                                foreach (var entry in block.superBlockSegments.OrderBy(x => x.Key))
                                {
                                    SuperBlockSegment segment = entry.Value;
                                    if (segment.blockNum < include_segments)
                                    {
                                        continue;
                                    }

                                    Block segment_block = Node.blockChain.getBlock(segment.blockNum, true);

                                    endpoint.sendData(ProtocolMessageCode.blockData, segment_block.getBytes(), BitConverter.GetBytes(segment.blockNum));
                                }
                            }
                        }
                    }
                }
            }

            public static void handleGetBlockHeaders(byte[] data, RemoteEndpoint endpoint)
            {
                using (MemoryStream m = new MemoryStream(data))
                {
                    using (BinaryReader reader = new BinaryReader(m))
                    {
                        ulong from = reader.ReadUInt64();
                        ulong to = reader.ReadUInt64();

                        ulong totalCount = to - from;
                        if (totalCount < 1)
                            return;

                        ulong lastBlockNum = Node.blockChain.getLastBlockNum();

                        if (from > lastBlockNum - 1)
                            return;

                        if (to > lastBlockNum)
                            to = lastBlockNum;

                        // Adjust total count if necessary
                        totalCount = to - from;
                        if (totalCount < 1)
                            return;

                        // Cap total block headers sent
                        if (totalCount > 1000)
                            totalCount = 1000;

                        if (endpoint != null)
                        {
                            if (endpoint.isConnected())
                            {
                                using (MemoryStream mOut = new MemoryStream())
                                {
                                    bool found = false;
                                    using (BinaryWriter writer = new BinaryWriter(mOut))
                                    {
                                        for (ulong i = 0; i < totalCount; i++)
                                        {
                                            // TODO TODO TODO block headers should be read from a separate storage and every node should keep a full copy
                                            Block block = Node.blockChain.getBlock(from + i, true, true);
                                            if (block == null)
                                                break;

                                            found = true;
                                            BlockHeader header = new BlockHeader(block);
                                            byte[] headerBytes = header.getBytes();
                                            writer.Write(headerBytes.Length);
                                            writer.Write(headerBytes);

                                            broadcastBlockHeaderTransactions(block, endpoint);
                                        }
                                    }
                                    if (found)
                                    {
                                        // Send the blockheaders
                                        endpoint.sendData(ProtocolMessageCode.blockHeaders, mOut.ToArray());
                                    }
                                }
                            }
                        }
                    }
                }
            }

            public static void handleGetPIT(byte[] data, RemoteEndpoint endpoint)
            {
                MemoryStream ms = new MemoryStream(data);
                using (BinaryReader r = new BinaryReader(ms))
                {
                    ulong block_num = r.ReadUInt64();
                    int filter_len = r.ReadInt32();
                    byte[] filter = r.ReadBytes(filter_len);
                    Cuckoo cf;
                    try
                    {
                        cf = new Cuckoo(filter);
                    }
                    catch (Exception)
                    {
                        Logging.warn("The Cuckoo filter in the getPIT message was invalid or corrupted!");
                        return;
                    }
                    Block b = Node.blockChain.getBlock(block_num, true, true);
                    if (b is null)
                    {
                        return;
                    }
                    if (b.version < BlockVer.v6)
                    {
                        Logging.warn("Neighbor {0} requested PIT information for block {0}, which was below the minimal PIT version.", endpoint.fullAddress, block_num);
                        return;
                    }
                    PrefixInclusionTree pit = new PrefixInclusionTree(44, 3);
                    List<string> interesting_transactions = new List<string>();
                    foreach (var tx in b.transactions)
                    {
                        pit.add(tx);
                        if (cf.Contains(Encoding.UTF8.GetBytes(tx)))
                        {
                            interesting_transactions.Add(tx);
                        }
                    }
                    // make sure we ended up with the correct PIT
                    if (!b.pitChecksum.SequenceEqual(pit.calculateTreeHash()))
                    {
                        // This is a serious error, but I am not sure how to respond to it right now.
                        Logging.error("Reconstructed PIT for block {0} does not match the checksum in block header!", block_num);
                        return;
                    }
                    byte[] minimal_pit = pit.getMinimumTreeTXList(interesting_transactions);
                    MemoryStream mOut = new MemoryStream(minimal_pit.Length + 12);
                    using (BinaryWriter w = new BinaryWriter(mOut, Encoding.UTF8, true))
                    {
                        w.Write(block_num);
                        w.Write(minimal_pit.Length);
                        w.Write(minimal_pit);
                    }
                    endpoint.sendData(ProtocolMessageCode.pitData, mOut.ToArray());
                }
            }

            private static void broadcastBlockHeaderTransactions(Block b, RemoteEndpoint endpoint)
            {
                if (!endpoint.isConnected())
                {
                    return;
                }

                foreach (var txid in b.transactions)
                {
                    Transaction t = TransactionPool.getAppliedTransaction(txid, b.blockNum, true);

                    if (endpoint.isSubscribedToAddress(NetworkEvents.Type.transactionFrom, new Address(t.pubKey).address))
                    {
                        endpoint.sendData(ProtocolMessageCode.transactionData, t.getBytes(true), null);
                    }
                    else
                    {
                        foreach (var entry in t.toList)
                        {
                            if (endpoint.isSubscribedToAddress(NetworkEvents.Type.transactionTo, entry.Key))
                            {
                                endpoint.sendData(ProtocolMessageCode.transactionData, t.getBytes(true), null);
                            }
                        }
                    }
                }
            }


            // Requests block with specified block height from the network, include_segments_from_block value can be 0 for no segments, and a positive value for segments bigger than the specified value
            public static bool broadcastGetNextSuperBlock(ulong block_num, byte[] block_checksum, ulong include_segments = 0, bool full_header = false, RemoteEndpoint skipEndpoint = null, RemoteEndpoint endpoint = null)
            {
                using (MemoryStream mw = new MemoryStream())
                {
                    using (BinaryWriter writerw = new BinaryWriter(mw))
                    {
                        writerw.Write(include_segments);
                        writerw.Write(full_header);
                        writerw.Write(block_checksum.Length);
                        writerw.Write(block_checksum);
#if TRACE_MEMSTREAM_SIZES
                        Logging.info(String.Format("NetworkProtocol::broadcastGetNextSuperBlock: {0}", mw.Length));
#endif

                        if (endpoint != null)
                        {
                            if (endpoint.isConnected())
                            {
                                endpoint.sendData(ProtocolMessageCode.getNextSuperBlock, mw.ToArray());
                                return true;
                            }
                        }
                        return CoreProtocolMessage.broadcastProtocolMessageToSingleRandomNode(new char[] { 'M', 'H' }, ProtocolMessageCode.getNextSuperBlock, mw.ToArray(), block_num, skipEndpoint);
                    }
                }
            }

            // Requests block with specified block height from the network, include_transactions value can be 0 - don't include transactions, 1 - include all but staking transactions or 2 - include all, including staking transactions
            public static bool broadcastGetBlock(ulong block_num, RemoteEndpoint skipEndpoint = null, RemoteEndpoint endpoint = null, byte include_transactions = 0, bool full_header = false)
            {
                using (MemoryStream mw = new MemoryStream())
                {
                    using (BinaryWriter writerw = new BinaryWriter(mw))
                    {
                        writerw.Write(block_num);
                        writerw.Write(include_transactions);
                        writerw.Write(full_header);
#if TRACE_MEMSTREAM_SIZES
                        Logging.info(String.Format("NetworkProtocol::broadcastGetBlock: {0}", mw.Length));
#endif

                        if (endpoint != null)
                        {
                            if (endpoint.isConnected())
                            {
                                endpoint.sendData(ProtocolMessageCode.getBlock, mw.ToArray());
                                return true;
                            }
                        }
                        return CoreProtocolMessage.broadcastProtocolMessageToSingleRandomNode(new char[] { 'M', 'H' }, ProtocolMessageCode.getBlock, mw.ToArray(), block_num, skipEndpoint);
                    }
                }
            }

            public static bool broadcastNewBlock(Block b, RemoteEndpoint skipEndpoint = null, RemoteEndpoint endpoint = null)
            {
                if (!Node.isMasterNode())
                {
                    return true;
                }
                if (endpoint != null)
                {
                    if (endpoint.isConnected())
                    {
                        endpoint.sendData(ProtocolMessageCode.blockData, b.getBytes(false), BitConverter.GetBytes(b.blockNum));
                        return true;
                    }
                    return false;
                }
                else
                {
                    return CoreProtocolMessage.addToInventory(new char[] { 'M', 'H' }, new InventoryItemBlock(b.blockChecksum, b.blockNum), skipEndpoint, ProtocolMessageCode.blockData, b.getBytes(false), BitConverter.GetBytes(b.blockNum));
                }
            }

            public static void handleBlockTransactionsChunk(byte[] data, RemoteEndpoint endpoint)
            {
                using (MemoryStream m = new MemoryStream(data))
                {
                    using (BinaryReader reader = new BinaryReader(m))
                    {
                        var sw = new System.Diagnostics.Stopwatch();
                        sw.Start();
                        int processedTxCount = 0;
                        int totalTxCount = 0;
                        while (m.Length > m.Position)
                        {
                            int len = reader.ReadInt32();
                            if (m.Position + len > m.Length)
                            {
                                // TODO blacklist
                                Logging.warn(String.Format("A node is sending invalid transaction chunks (tx byte len > received data len)."));
                                break;
                            }
                            byte[] txData = reader.ReadBytes(len);
                            Transaction tx = new Transaction(txData);
                            Node.inventoryCache.setProcessedFlag(InventoryItemTypes.transaction, UTF8Encoding.UTF8.GetBytes(tx.id), true);
                            totalTxCount++;
                            if (tx.type == (int)Transaction.Type.StakingReward && !Node.blockSync.synchronizing)
                            {
                                continue;
                            }
                            if (!TransactionPool.addTransaction(tx, true))
                            {
                                Logging.error(String.Format("Error adding transaction {0} received in a chunk to the transaction pool.", tx.id));
                            }
                            else
                            {
                                processedTxCount++;
                            }
                        }
                        sw.Stop();
                        TimeSpan elapsed = sw.Elapsed;
                        Logging.info(string.Format("Processed {0}/{1} txs in {2}ms", processedTxCount, totalTxCount, elapsed.TotalMilliseconds));
                    }
                }
            }

            public static void handleGetBlock(byte[] data, RemoteEndpoint endpoint)
            {
                if (!Node.isMasterNode())
                {
                    Logging.warn("Block data was requested, but this node isn't a master node");
                    return;
                }

                if (Node.blockSync.synchronizing)
                {
                    return;
                }
                using (MemoryStream m = new MemoryStream(data))
                {
                    using (BinaryReader reader = new BinaryReader(m))
                    {
                        ulong block_number = reader.ReadUInt64();
                        byte include_transactions = reader.ReadByte();
                        bool full_header = false;
                        try
                        {
                            full_header = reader.ReadBoolean();
                        }
                        catch (Exception)
                        {

                        }

                        //Logging.info(String.Format("Block #{0} has been requested.", block_number));

                        ulong last_block_height = IxianHandler.getLastBlockHeight() + 1;

                        if (block_number > last_block_height)
                        {
                            return;
                        }

                        Block block = null;
                        if (block_number == last_block_height)
                        {
                            bool haveLock = false;
                            try
                            {
                                Monitor.TryEnter(Node.blockProcessor.localBlockLock, 1000, ref haveLock);
                                if (!haveLock)
                                {
                                    throw new TimeoutException();
                                }

                                Block tmp = Node.blockProcessor.getLocalBlock();
                                if (tmp != null && tmp.blockNum == last_block_height)
                                {
                                    block = tmp;
                                }
                            }
                            finally
                            {
                                if (haveLock)
                                {
                                    Monitor.Exit(Node.blockProcessor.localBlockLock);
                                }
                            }
                        }
                        else
                        {
                            block = Node.blockChain.getBlock(block_number, Config.storeFullHistory);
                        }

                        if (block == null)
                        {
                            Logging.warn("Unable to find block #{0} in the chain!", block_number);
                            return;
                        }
                        //Logging.info(String.Format("Block #{0} ({1}) found, transmitting...", block_number, Crypto.hashToString(block.blockChecksum.Take(4).ToArray())));
                        // Send the block

                        if (include_transactions == 1)
                        {
                            TransactionProtocolMessages.handleGetBlockTransactions(block_number, false, endpoint);
                        }
                        else if (include_transactions == 2)
                        {
                            TransactionProtocolMessages.handleGetBlockTransactions(block_number, true, endpoint);
                        }

                        if (!Node.blockProcessor.verifySigFreezedBlock(block))
                        {
                            Logging.warn("Sigfreezed block {0} was requested. but we don't have the correct sigfreeze!", block.blockNum);
                        }

                        bool frozen_sigs_only = true;

                        if (block_number + 5 > IxianHandler.getLastBlockHeight())
                        {
                            if (block.getFrozenSignatureCount() < Node.blockChain.getRequiredConsensus(block_number))
                            {
                                frozen_sigs_only = false;
                            }
                        }

                        endpoint.sendData(ProtocolMessageCode.blockData, block.getBytes(full_header, frozen_sigs_only), BitConverter.GetBytes(block.blockNum));
                    }
                }
            }

            static public void handleBlockData(byte[] data, RemoteEndpoint endpoint)
            {
                Block block = new Block(data);
                Node.inventoryCache.setProcessedFlag(InventoryItemTypes.block, block.blockChecksum, true);
                if (endpoint.blockHeight < block.blockNum)
                {
                    endpoint.blockHeight = block.blockNum;
                }

                Node.blockSync.onBlockReceived(block, endpoint);
                Node.blockProcessor.onBlockReceived(block, endpoint);
            }
        }
    }
}