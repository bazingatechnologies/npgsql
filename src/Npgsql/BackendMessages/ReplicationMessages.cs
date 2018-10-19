﻿using System;
using NpgsqlTypes;

namespace Npgsql.BackendMessages
{
    // NOTE: see https://www.postgresql.org/docs/current/static/protocol-replication.html for details.

    /// <summary>
    /// Note that this message doesn't actually contain the data, but only the length.
    /// </summary>
    class WalDataResponseMessage : IBackendMessage
    {
        public BackendMessageCode Code => BackendMessageCode.WalData;

        /// <summary>
        /// The starting point of the WAL data in this message.
        /// </summary>
        internal NpgsqlLsn StartLsn { get; private set; }

        /// <summary>
        /// The current end of WAL on the server.
        /// </summary>
        internal NpgsqlLsn EndLsn { get; private set; }

        /// <summary>
        /// The server's system clock at the time of transmission, as microseconds since midnight on 2000-01-01.
        /// </summary>
        internal long SystemClock { get; private set; }

        internal int MessageLength => 
            1    // Code
            + 8  // StartLsn
            + 8  // EndLsn
            + 8; // SystemClock

        internal void Load(NpgsqlReadBuffer buffer)
        {
            var upper = buffer.ReadUInt32();
            var lower = buffer.ReadUInt32();
            StartLsn = new NpgsqlLsn(upper, lower);

            upper = buffer.ReadUInt32();
            lower = buffer.ReadUInt32();
            EndLsn = new NpgsqlLsn(upper, lower);

            SystemClock = buffer.ReadInt64();
        }
    }

    class PrimaryKeepAliveResponseMessage : IBackendMessage
    {
        public BackendMessageCode Code => BackendMessageCode.PrimaryKeepAlive;

        /// <summary>
        /// The current end of WAL on the server.
        /// </summary>
        internal NpgsqlLsn EndLsn { get; private set; }

        /// <summary>
        /// The server's system clock at the time of transmission, as microseconds since midnight on 2000-01-01.
        /// </summary>
        internal long SystemClock { get; private set; }

        /// <summary>
        /// Indicates that the client should reply to this message as soon as possible, to avoid a timeout disconnect.
        /// </summary>
        internal bool ReplyImmediately { get; private set; }

        internal int MessageLength =>
            1    // Code
            + 8  // EndLsn
            + 8  // SystemClock
            + 1; // ReplyImmediately

        internal void Load(NpgsqlReadBuffer buffer)
        {
            var upper = buffer.ReadUInt32();
            var lower = buffer.ReadUInt32();
            EndLsn = new NpgsqlLsn(upper, lower);

            SystemClock = buffer.ReadInt64();

            var replyAsap = buffer.ReadByte();
            switch (replyAsap)
            {
            case 0:
                ReplyImmediately = false;
                break;
            case 1:
                ReplyImmediately = true;
                break;
            default:
                throw new Exception("Invalid \"reply immediately\" indicator in PrimaryKeepAliveResponse message: " + replyAsap);
            }
        }
    }
}
