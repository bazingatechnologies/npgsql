﻿using System.Diagnostics;
using System.IO;
using System.Threading;
using NpgsqlTypes;
using NUnit.Framework;

namespace Npgsql.Tests
{
    [TestFixture]
    public class ReplicationTests : TestBase
    {
        const string TestSlotName = "npgsql_repl_test";

        /// <summary>
        /// default logical decoding plugin
        /// </summary>
        const string TestPlugin = "test_decoding";

        [Test]
        public void IncorrectCommand()
        {
            var csb = new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                ReplicationMode = ReplicationMode.Logical
            };

            const string incorrectCmdText = "START_REPLICATION SLOT " + TestSlotName + "42 LOGICAL 0/0";
            using (var connection = OpenConnection(csb))
            {
                Assert.Throws<PostgresException>(() => connection.BeginReplication(incorrectCmdText, new NpgsqlLsn(0, 0)));
            }
        }
        
        [Test]
        public void ReplicationCommands()
        {
            var csb = new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                ReplicationMode = ReplicationMode.Logical
            };
            const string createCmd = "CREATE_REPLICATION_SLOT " + TestSlotName + " LOGICAL " + TestPlugin;
            const string dropCmd = "DROP_REPLICATION_SLOT " + TestSlotName;
            using (var connection = OpenConnection(csb))
            {
                connection.ExecuteNonQuery(createCmd);
                connection.ExecuteNonQuery(dropCmd);

                var res = connection.ExecuteScalar(createCmd);
                try
                {
                    Assert.That(res, Is.EqualTo(TestSlotName));
                }
                finally
                {
                    connection.ExecuteNonQuery(dropCmd);
                }

                var cmd = connection.CreateCommand();
                cmd.CommandText = createCmd;

                NpgsqlDataReader reader = null;
                var atLeastOneRow = false;
                try
                {
                    reader = cmd.ExecuteReader();
                    atLeastOneRow = reader.Read();
                    Assert.IsTrue(atLeastOneRow);
                    Assert.That(reader.FieldCount, Is.EqualTo(4));
                    Assert.That(reader.GetString(0), Is.EqualTo(TestSlotName));
                    var lsnStr = reader.GetString(1);
                    Assert.That(NpgsqlLsn.TryParse(lsnStr, out _));
                    Assert.That(reader.GetString(3), Is.EqualTo(TestPlugin));
                    Assert.IsFalse(reader.Read());
                }
                finally
                {
                    reader?.Dispose();
                    if (atLeastOneRow)
                        connection.ExecuteNonQuery(dropCmd);
                }
            }
        }

        [Test]
        public void LogicalReplicationStream()
        {
            var csb = new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                ReplicationMode = ReplicationMode.Logical
            };

            using (var connection = new NpgsqlConnection(csb.ToString()))
            {
                connection.Open();
                var dropSlot = false;
                try
                {
                    var cmd = connection.CreateCommand();
                    cmd.CommandText = "CREATE_REPLICATION_SLOT " + TestSlotName + " LOGICAL " + TestPlugin;
                    NpgsqlLsn lsn;
                    using (var reader = cmd.ExecuteReader())
                    {
                        dropSlot = true;
                        Assert.IsTrue(reader.Read());
                        lsn = NpgsqlLsn.Parse(reader.GetString(1));
                        Assert.IsFalse(reader.Read());
                    }

                    csb.ReplicationMode = ReplicationMode.None;
                    using (var normalConnection = new NpgsqlConnection(csb.ToString()))
                    {
                        normalConnection.Open();
                        normalConnection.ExecuteNonQuery("CREATE TABLE repl_table (id int NOT NULL PRIMARY KEY, value text NULL)");
                        for (var i = 0; i < 5; i++)
                        {
                            normalConnection.ExecuteNonQuery($"INSERT INTO repl_table (id, value) VALUES({i + 1}, 'str{i}')");
                        }
                        normalConnection.ExecuteNonQuery("DROP TABLE repl_table");
                    }

                    const int timeout = 60000, flushTimeout = 1000;
                    var flushTime = 0;
                    var sw = Stopwatch.StartNew();
                    using (var stream = connection.BeginReplication("START_REPLICATION SLOT " + TestSlotName + " LOGICAL " + lsn, lsn))
                    using (var reader = new StreamReader(stream))
                    {
                        var counter = 0;
                        var lastLsn = new NpgsqlLsn();
                        const int expectedMessages = 19;
                        while (true)
                        {
                            var status = stream.FetchNext();
                            Assert.That(status, Is.Not.EqualTo(NpgsqlReplicationStreamFetchStatus.Closed));
                            if (status == NpgsqlReplicationStreamFetchStatus.Data)
                            {
                                Assert.That(stream.StartLsn, Is.Not.Null);
                                lastLsn = stream.StartLsn;
                                var str = reader.ReadToEnd();
                                Trace.WriteLine(str);
                                counter++;
                                continue;
                            }

                            if (counter == expectedMessages)
                                break;

                            if (sw.ElapsedMilliseconds > flushTime + flushTimeout)
                            {
                                stream.Flush();
                                flushTime += flushTimeout;
                            }

                            if (sw.ElapsedMilliseconds > timeout)
                                Assert.Inconclusive($"Timeout expired. Messages expected: {expectedMessages}; received: {counter}.");
                            Thread.Sleep(50);
                        }

                        stream.Close();

                        Assert.IsTrue(stream.EndOfStream);
                        Assert.That(stream.StartLsn, Is.EqualTo(lastLsn));
                    }
                }
                finally
                {
                    if (dropSlot)
                        connection.ExecuteNonQuery("DROP_REPLICATION_SLOT " + TestSlotName);
                }
            }
        }

        [Test]
        public void MultipleOpenInOneConnection()
        {
            var csb = new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                ReplicationMode = ReplicationMode.Logical
            };

            using (var connection = new NpgsqlConnection(csb.ToString()))
            {
                connection.Open();
                var dropSlot = false;
                try
                {
                    var cmd = connection.CreateCommand();
                    cmd.CommandText = "CREATE_REPLICATION_SLOT " + TestSlotName + " LOGICAL " + TestPlugin;
                    NpgsqlLsn lsn;
                    using (var reader = cmd.ExecuteReader())
                    {
                        dropSlot = true;
                        Assert.IsTrue(reader.Read());
                        lsn = NpgsqlLsn.Parse(reader.GetString(1));
                        Assert.IsFalse(reader.Read());
                    }

                    const int flushTimeout = 1000;
                    Stopwatch sw;
                    for (var i = 0; i < 3; i++)
                    {
                        sw = Stopwatch.StartNew();
                        using (var stream = connection.BeginReplication("START_REPLICATION SLOT " + TestSlotName + " LOGICAL " + lsn, lsn))
                        {
                            var keepFetching = true;
                            while (keepFetching)
                            {
                                var status = stream.FetchNext();
                                switch (status)
                                {
                                case NpgsqlReplicationStreamFetchStatus.None:
                                    if (sw.ElapsedMilliseconds <= flushTimeout)
                                    {
                                        Thread.Sleep(50);
                                        break;
                                    }
                                    Assert.That(stream.Flush(true), Is.True);
                                    sw.Reset();
                                    break;
                                case NpgsqlReplicationStreamFetchStatus.KeepAlive:
                                    keepFetching = false;
                                    break;
                                default:
                                    Assert.That(status, Is.EqualTo(NpgsqlReplicationStreamFetchStatus.None).Or.EqualTo(NpgsqlReplicationStreamFetchStatus.KeepAlive), "Iteration {0}", i);
                                    break;
                                }
                            }
                        }
                    }

                    csb.ReplicationMode = ReplicationMode.None;
                    using (var normalConnection = new NpgsqlConnection(csb.ToString()))
                    {
                        normalConnection.Open();
                        normalConnection.ExecuteNonQuery("CREATE TABLE repl_table (id int NOT NULL PRIMARY KEY, value text NULL)");
                        for (var i = 0; i < 5; i++)
                        {
                            normalConnection.ExecuteNonQuery($"INSERT INTO repl_table (id, value) VALUES({i + 1}, 'str{i}')");
                        }
                        normalConnection.ExecuteNonQuery("DROP TABLE repl_table");
                    }

                    const int timeout = 60000;
                    var flushTime = 0;
                    sw = Stopwatch.StartNew();
                    using (var stream = connection.BeginReplication("START_REPLICATION SLOT " + TestSlotName + " LOGICAL " + lsn, lsn))
                    using (var reader = new StreamReader(stream))
                    {
                        var counter = 0;
                        var lastLsn = new NpgsqlLsn();
                        const int expectedMessages = 19;
                        while (true)
                        {
                            while (stream.FetchNext() == NpgsqlReplicationStreamFetchStatus.Data)
                            {
                                Assert.That(stream.StartLsn, Is.Not.Null);
                                lastLsn = stream.StartLsn;
                                var str = reader.ReadToEnd();
                                Trace.WriteLine(str);
                                counter++;
                            }
                            if (counter == expectedMessages)
                                break;

                            if (sw.ElapsedMilliseconds > flushTime + flushTimeout)
                            {
                                stream.Flush();
                                flushTime += flushTimeout;
                            }

                            if (sw.ElapsedMilliseconds > timeout)
                                Assert.Inconclusive($"Timeout expired. Messages expected: {expectedMessages}; received: {counter}.");
                            Thread.Sleep(50);
                        }

                        stream.Close();

                        Assert.IsTrue(stream.EndOfStream);
                        Assert.That(stream.StartLsn, Is.EqualTo(lastLsn));
                    }
                }
                finally
                {
                    if (dropSlot)
                        connection.ExecuteNonQuery("DROP_REPLICATION_SLOT " + TestSlotName);
                }
            }
        }

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            using (var connection = OpenConnection())
            {
                var replSlotsStr = (string)connection.ExecuteScalar("SHOW max_replication_slots");
                var replSlots = int.Parse(replSlotsStr);
                if (replSlots == 0)
                    TestUtil.IgnoreExceptOnBuildServer("max_replication_slots is set to 0 in your postgresql.conf");
            }
        }

        [TearDown]
        public void Teardown()
        {
            using (var connection = OpenConnection())
            {
                var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT count(*) FROM pg_replication_slots WHERE slot_name = '{TestSlotName}'";

                var value = (long)cmd.ExecuteScalar();

                if (value > 0)
                {
                    // cleanup after previously failed tests
                    cmd.CommandText = $"SELECT count(*) FROM pg_drop_replication_slot('{TestSlotName}')";
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }
}
