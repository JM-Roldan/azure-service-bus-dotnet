// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.ServiceBus.UnitTests
{
    using System;
    using System.Text;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Core;
    using Xunit;
    using System.Diagnostics;

    public class SenderReceiverTests : SenderReceiverClientTestBase
    {
        private static TimeSpan TwoSeconds = TimeSpan.FromSeconds(2);

        public static IEnumerable<object[]> TestPermutations => new object[][]
        {
            new object[] {TestConstants.NonPartitionedQueueName},
            new object[] {TestConstants.PartitionedQueueName}
        };

        public static IEnumerable<object[]> TestPermutationsSession => new object[][]
        {
            new object[] {TestConstants.SessionNonPartitionedQueueName},
            new object[] {TestConstants.SessionPartitionedQueueName}
        };

        [Theory]
        [MemberData(nameof(TestPermutations))]
        [DisplayTestMethodName]
        async Task MessageReceiverAndMessageSenderCreationWorksAsExpected(string queueName, int messageCount = 10)
        {
            var sender = new MessageSender(TestUtility.NamespaceConnectionString, queueName);
            var receiver = new MessageReceiver(TestUtility.NamespaceConnectionString, queueName, receiveMode: ReceiveMode.PeekLock);

            try
            {
                await this.PeekLockTestCase(sender, receiver, messageCount);
            }
            finally
            {
                await sender.CloseAsync().ConfigureAwait(false);
                await receiver.CloseAsync().ConfigureAwait(false);
            }
        }

        [Theory]
        [MemberData(nameof(TestPermutations))]
        [DisplayTestMethodName]
        async Task TopicClientPeekLockDeferTestCase(string queueName, int messageCount = 10)
        {
            var sender = new MessageSender(TestUtility.NamespaceConnectionString, queueName);
            var receiver = new MessageReceiver(TestUtility.NamespaceConnectionString, queueName, receiveMode: ReceiveMode.PeekLock);

            try
            {
                await
                    this.PeekLockDeferTestCase(sender, receiver, messageCount);
            }
            finally
            {
                await sender.CloseAsync().ConfigureAwait(false);
                await receiver.CloseAsync().ConfigureAwait(false);
            }
        }

        [Theory]
        [MemberData(nameof(TestPermutations))]
        [DisplayTestMethodName]
        async Task PeekAsyncTest(string queueName, int messageCount = 10)
        {
            var sender = new MessageSender(TestUtility.NamespaceConnectionString, queueName);
            var receiver = new MessageReceiver(TestUtility.NamespaceConnectionString, queueName, receiveMode: ReceiveMode.ReceiveAndDelete);

            try
            {
                await this.PeekAsyncTestCase(sender, receiver, messageCount);
            }
            finally
            {
                await sender.CloseAsync().ConfigureAwait(false);
                await receiver.CloseAsync().ConfigureAwait(false);
            }
        }

        [Theory]
        [MemberData(nameof(TestPermutations))]
        [DisplayTestMethodName]
        async Task ReceiveShouldReturnNoLaterThanServerWaitTimeTest(string queueName, int messageCount = 1)
        {
            var sender = new MessageSender(TestUtility.NamespaceConnectionString, queueName);
            var receiver = new MessageReceiver(TestUtility.NamespaceConnectionString, queueName, receiveMode: ReceiveMode.ReceiveAndDelete);

            try
            {
                await this.ReceiveShouldReturnNoLaterThanServerWaitTimeTestCase(sender, receiver, messageCount);
            }
            finally
            {
                await sender.CloseAsync().ConfigureAwait(false);
                await receiver.CloseAsync().ConfigureAwait(false);
            }
        }

        [Theory]
        [MemberData(nameof(TestPermutations))]
        [DisplayTestMethodName]
        async Task ReceiverShouldBeAbleToStopTheMessagePump(string queueName)
        {
            var sender = new MessageSender(TestUtility.NamespaceConnectionString, queueName);
            var receiver = new MessageReceiver(TestUtility.NamespaceConnectionString, queueName, receiveMode: ReceiveMode.PeekLock);
            var count = 0;

            try
            {
                await TestUtility.SendMessagesAsync(sender, 12);

                receiver.RegisterMessageHandler(
                async (receivedMessage, token) =>
                {
                    Interlocked.Increment(ref count);
                    Thread.Sleep(2000);
                    await receiver.CompleteAsync(new[] { receivedMessage.SystemProperties.LockToken });
                },
                new MessageHandlerOptions(ExceptionReceivedHandler) { MaxConcurrentCalls = 4, AutoComplete = false });

                Thread.Sleep(1900);
                await receiver.StopReceivingAsync().ConfigureAwait(false);
                Assert.Equal(4, count);

                receiver.RegisterMessageHandler(
                async (receivedMessage, token) =>
                {
                    Interlocked.Increment(ref count);
                    await receiver.CompleteAsync(new[] { receivedMessage.SystemProperties.LockToken });
                },
                new MessageHandlerOptions(base.ExceptionReceivedHandler) { MaxConcurrentCalls = 4, AutoComplete = false });

                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed.TotalSeconds <= 20)
                {
                    if (count == 12)
                    {
                        TestUtility.Log($"All messages Received.");
                        break;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }

                Assert.Equal(12, count);
            }
            finally
            {
                await sender.CloseAsync().ConfigureAwait(false);
                await receiver.CloseAsync().ConfigureAwait(false);
            }
        }

        [Theory]
        [MemberData(nameof(TestPermutations))]
        [DisplayTestMethodName]
        async Task ReceiveShouldThrowForServerTimeoutZeroTest(string queueName)
        {
            var receiver = new MessageReceiver(TestUtility.NamespaceConnectionString, queueName, receiveMode: ReceiveMode.ReceiveAndDelete);

            try
            {
                await this.ReceiveShouldThrowForServerTimeoutZero(receiver);
            }
            finally
            {
                await receiver.CloseAsync().ConfigureAwait(false);
            }
        }

        [Fact]
        [DisplayTestMethodName]
        async Task ReceiverShouldUseTheLatestPrefetchCount()
        {
            var queueName = TestConstants.NonPartitionedQueueName;

            var sender = new MessageSender(TestUtility.NamespaceConnectionString, queueName);

            var receiver1 = new MessageReceiver(TestUtility.NamespaceConnectionString, queueName, receiveMode: ReceiveMode.ReceiveAndDelete);
            var receiver2 = new MessageReceiver(TestUtility.NamespaceConnectionString, queueName, receiveMode: ReceiveMode.ReceiveAndDelete, prefetchCount: 1);

            Assert.Equal(0, receiver1.PrefetchCount);
            Assert.Equal(1, receiver2.PrefetchCount);

            try
            {
                for (var i = 0; i < 9; i++)
                {
                    var message = new Message(Encoding.UTF8.GetBytes("test" + i))
                    {
                        Label = "prefetch" + i
                    };
                    await sender.SendAsync(message).ConfigureAwait(false);
                }

                // Default prefetch count should be 0 for receiver 1.
                Assert.Equal("prefetch0", (await receiver1.ReceiveAsync().ConfigureAwait(false)).Label);

                // The first ReceiveAsync() would initialize the link and block prefetch2 for receiver2
                Assert.Equal("prefetch1", (await receiver2.ReceiveAsync().ConfigureAwait(false)).Label);
                await Task.Delay(TwoSeconds);

                // Updating prefetch count on receiver1.
                receiver1.PrefetchCount = 2;
                await Task.Delay(TwoSeconds);

                // The next operation should fetch prefetch3 and prefetch4.
                Assert.Equal("prefetch3", (await receiver1.ReceiveAsync().ConfigureAwait(false)).Label);
                await Task.Delay(TwoSeconds);

                Assert.Equal("prefetch2", (await receiver2.ReceiveAsync().ConfigureAwait(false)).Label);
                await Task.Delay(TwoSeconds);

                // The next operation should block prefetch6 for receiver2.
                Assert.Equal("prefetch5", (await receiver2.ReceiveAsync().ConfigureAwait(false)).Label);
                await Task.Delay(TwoSeconds);

                // Updates in prefetch count of receiver1 should not affect receiver2.
                // Receiver2 should continue with 1 prefetch.
                Assert.Equal("prefetch4", (await receiver1.ReceiveAsync().ConfigureAwait(false)).Label);
                Assert.Equal("prefetch7", (await receiver1.ReceiveAsync().ConfigureAwait(false)).Label);
                Assert.Equal("prefetch8", (await receiver1.ReceiveAsync().ConfigureAwait(false)).Label);
            }
            catch (Exception)
            {
                // Cleanup
                Message message;
                do
                {
                    message = await receiver1.ReceiveAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                } while (message != null);
            }
            finally
            {
                await sender.CloseAsync().ConfigureAwait(false);
                await receiver1.CloseAsync().ConfigureAwait(false);
                await receiver2.CloseAsync().ConfigureAwait(false);
            }
        }

        [Fact(Skip = "Flaky Test in Appveyor, fix and enable back")]
        [DisplayTestMethodName]
        public async Task WaitingReceiveShouldReturnImmediatelyWhenReceiverIsClosed()
        {
            var receiver = new MessageReceiver(TestUtility.NamespaceConnectionString, TestConstants.NonPartitionedQueueName, ReceiveMode.ReceiveAndDelete);

            TestUtility.Log("Begin to receive from an empty queue.");
            Task quickTask;
            try
            {
                quickTask = Task.Run(async () =>
                {
                    try
                    {
                        await receiver.ReceiveAsync(TimeSpan.FromSeconds(40));
                    }
                    catch (Exception e)
                    {
                        TestUtility.Log("Unexpected exception: " + e);
                    }
                });
                await Task.Delay(2000);
                TestUtility.Log("Waited for 2 Seconds for the ReceiveAsync to establish connection.");
            }
            finally
            {
                await receiver.CloseAsync().ConfigureAwait(false);
                TestUtility.Log("Closed Receiver");
            }

            TestUtility.Log("Waiting for maximum 10 Secs");
            bool receiverReturnedInTime = false;
            using (var timeoutCancellationTokenSource = new CancellationTokenSource())
            {

                var completedTask = await Task.WhenAny(quickTask, Task.Delay(10000, timeoutCancellationTokenSource.Token));
                if (completedTask == quickTask)
                {
                    timeoutCancellationTokenSource.Cancel();
                    receiverReturnedInTime = true;
                    TestUtility.Log("The Receiver closed in time.");
                }
                else
                {
                    TestUtility.Log("The Receiver did not close in time.");
                }
            }

            Assert.True(receiverReturnedInTime);
        }

        [Fact]
        [DisplayTestMethodName]
        public async Task DeadLetterReasonShouldPropagateToTheReceivedMessage()
        {
            var queueName = TestConstants.NonPartitionedQueueName;

            var sender = new MessageSender(TestUtility.NamespaceConnectionString, queueName);
            var receiver = new MessageReceiver(TestUtility.NamespaceConnectionString, queueName);
            var dlqReceiver = new MessageReceiver(TestUtility.NamespaceConnectionString, EntityNameHelper.FormatDeadLetterPath(queueName), ReceiveMode.ReceiveAndDelete);

            try
            {
                await sender.SendAsync(new Message(Encoding.UTF8.GetBytes("deadLetterTest2")));
                var message = await receiver.ReceiveAsync();
                Assert.NotNull(message);

                await receiver.DeadLetterAsync(
                    message.SystemProperties.LockToken,
                    "deadLetterReason",
                    "deadLetterDescription");
                var dlqMessage = await dlqReceiver.ReceiveAsync();

                Assert.NotNull(dlqMessage);
                Assert.True(dlqMessage.UserProperties.ContainsKey(Message.DeadLetterReasonHeader));
                Assert.True(dlqMessage.UserProperties.ContainsKey(Message.DeadLetterErrorDescriptionHeader));
                Assert.Equal("deadLetterReason", dlqMessage.UserProperties[Message.DeadLetterReasonHeader]);
                Assert.Equal("deadLetterDescription", dlqMessage.UserProperties[Message.DeadLetterErrorDescriptionHeader]);
            }
            finally
            {
                await sender.CloseAsync();
                await receiver.CloseAsync();
                await dlqReceiver.CloseAsync();
            }
        }

        [Fact]
        [DisplayTestMethodName]
        public async Task DispositionWithUpdatedPropertiesShouldPropagateToReceivedMessage()
        {
            var queueName = TestConstants.NonPartitionedQueueName;

            var sender = new MessageSender(TestUtility.NamespaceConnectionString, queueName);
            var receiver = new MessageReceiver(TestUtility.NamespaceConnectionString, queueName);

            try
            {
                await sender.SendAsync(new Message(Encoding.UTF8.GetBytes("propertiesToUpdate")));

                var message = await receiver.ReceiveAsync();
                Assert.NotNull(message);
                await receiver.AbandonAsync(message.SystemProperties.LockToken, new Dictionary<string, object>
                {
                    {"key", "value1"}
                });

                message = await receiver.ReceiveAsync();
                Assert.NotNull(message);
                Assert.True(message.UserProperties.ContainsKey("key"));
                Assert.Equal("value1", message.UserProperties["key"]);

                long sequenceNumber = message.SystemProperties.SequenceNumber;
                await receiver.DeferAsync(message.SystemProperties.LockToken, new Dictionary<string, object>
                {
                    {"key", "value2"}
                });

                message = await receiver.ReceiveDeferredMessageAsync(sequenceNumber);
                Assert.NotNull(message);
                Assert.True(message.UserProperties.ContainsKey("key"));
                Assert.Equal("value2", message.UserProperties["key"]);

                await receiver.CompleteAsync(message.SystemProperties.LockToken);
            }
            finally
            {
                await sender.CloseAsync();
                await receiver.CloseAsync();
            }
        }

        [Fact]
        [DisplayTestMethodName]
        public async Task CancelScheduledMessageShouldThrowMessageNotFoundException()
        {
            var sender = new MessageSender(TestUtility.NamespaceConnectionString, TestConstants.NonPartitionedQueueName);

            try
            {
                long nonExistingSequenceNumber = 1000;
                await Assert.ThrowsAsync<MessageNotFoundException>(
                    async () => await sender.CancelScheduledMessageAsync(nonExistingSequenceNumber));
            }
            finally
            {
                await sender.CloseAsync().ConfigureAwait(false);
            }
        }

        [Fact]
        [DisplayTestMethodName]
        public async Task ClientThrowsUnauthorizedExceptionWhenUserDoesntHaveAccess()
        {
            var csb = new ServiceBusConnectionStringBuilder(TestUtility.NamespaceConnectionString);
            csb.SasKeyName = "nonExistingKey";
            csb.EntityPath = TestConstants.NonPartitionedQueueName;

            var sender = new MessageSender(csb);

            try
            {
                await Assert.ThrowsAsync<UnauthorizedException>(
                    async () => await sender.SendAsync(new Message()));

                long nonExistingSequenceNumber = 1000;
                await Assert.ThrowsAsync<UnauthorizedException>(
                    async () => await sender.CancelScheduledMessageAsync(nonExistingSequenceNumber));
            }
            finally
            {
                await sender.CloseAsync().ConfigureAwait(false);
            }
        }

        [Fact]
        [DisplayTestMethodName]
        public async Task ClientThrowsObjectDisposedExceptionWhenUserCloseConnectionAndWouldUseOldSeviceBusConnection()
        {
            var sender = new MessageSender(TestUtility.NamespaceConnectionString, TestConstants.PartitionedQueueName);
            var receiver = new MessageReceiver(TestUtility.NamespaceConnectionString, TestConstants.PartitionedQueueName, receiveMode: ReceiveMode.ReceiveAndDelete);
            try
            {
                var messageBody = Encoding.UTF8.GetBytes("Message");
                var message = new Message(messageBody);

                await sender.SendAsync(message);
                await sender.CloseAsync();

                var recivedMessage = await receiver.ReceiveAsync().ConfigureAwait(false);
                Assert.True(Encoding.UTF8.GetString(recivedMessage.Body) == Encoding.UTF8.GetString(messageBody));

                var connection = sender.ServiceBusConnection;
                Assert.Throws<ObjectDisposedException>(() => new MessageSender(connection, TestConstants.PartitionedQueueName));
            }
            finally
            {
                await sender.CloseAsync().ConfigureAwait(false);
                await receiver.CloseAsync().ConfigureAwait(false);
            }
        }

        [Fact]
        [DisplayTestMethodName]
        public async Task SendMesageCloseConnectionCreateAnotherConnectionSendAgainMessage()
        {
            var sender = new MessageSender(TestUtility.NamespaceConnectionString, TestConstants.PartitionedQueueName);
            var receiver = new MessageReceiver(TestUtility.NamespaceConnectionString, TestConstants.PartitionedQueueName, receiveMode: ReceiveMode.ReceiveAndDelete);
            try
            {
                var messageBody = Encoding.UTF8.GetBytes("Message");
                var message = new Message(messageBody);

                await sender.SendAsync(message);
                await sender.CloseAsync();

                var recivedMessage = await receiver.ReceiveAsync().ConfigureAwait(false);
                Assert.True(Encoding.UTF8.GetString(recivedMessage.Body) == Encoding.UTF8.GetString(messageBody));

                var csb = new ServiceBusConnectionStringBuilder(TestUtility.NamespaceConnectionString)
                {
                    EntityPath = TestConstants.PartitionedQueueName
                };
                ServiceBusConnection connection = new ServiceBusConnection(csb);
                sender = new MessageSender(connection, TestConstants.PartitionedQueueName);

                messageBody = Encoding.UTF8.GetBytes("Message 2");
                message = new Message(messageBody);
                await sender.SendAsync(message);

                recivedMessage = await receiver.ReceiveAsync().ConfigureAwait(false);
                Assert.True(Encoding.UTF8.GetString(recivedMessage.Body) == Encoding.UTF8.GetString(messageBody));
            }
            finally
            {
                await sender.CloseAsync().ConfigureAwait(false);
                await receiver.CloseAsync().ConfigureAwait(false);
            }
        }

        [Fact]
        [DisplayTestMethodName]
        public async Task ClientsUseGlobalConnectionCloseFirstClientSecoundClientShouldSendMessage()
        {
            var csb = new ServiceBusConnectionStringBuilder(TestUtility.NamespaceConnectionString);
            var connection = new ServiceBusConnection(csb);
            var sender = new MessageSender(connection, TestConstants.PartitionedQueueName);
            var receiver = new MessageReceiver(TestUtility.NamespaceConnectionString, TestConstants.PartitionedQueueName, receiveMode: ReceiveMode.ReceiveAndDelete);
            try
            {
                var messageBody = Encoding.UTF8.GetBytes("Message");
                var message = new Message(messageBody);

                await sender.SendAsync(message);
                await sender.CloseAsync();

                var recivedMessage = await receiver.ReceiveAsync().ConfigureAwait(false);
                Assert.True(Encoding.UTF8.GetString(recivedMessage.Body) == Encoding.UTF8.GetString(messageBody));

                connection = sender.ServiceBusConnection;
                sender = new MessageSender(connection, TestConstants.PartitionedQueueName);
                messageBody = Encoding.UTF8.GetBytes("Message 2");
                message = new Message(messageBody);
                await sender.SendAsync(message);
                recivedMessage = await receiver.ReceiveAsync().ConfigureAwait(false);
                Assert.True(Encoding.UTF8.GetString(recivedMessage.Body) == Encoding.UTF8.GetString(messageBody));

            }
            finally
            {
                await sender.CloseAsync().ConfigureAwait(false);
                await receiver.CloseAsync().ConfigureAwait(false);
            }
        }

        [Theory]
        [MemberData(nameof(TestPermutationsSession))]
        [DisplayTestMethodName]
        async Task QueueClientShouldBeAbleToStopTheMessagePump(string queueName, int sessions = 2, int messagesPerSession = 3)
        {
            var sender = new MessageSender(TestUtility.NamespaceConnectionString, queueName);
            var queueClient = new QueueClient(TestUtility.NamespaceConnectionString, queueName);

            var count = 0;
            var messagesWhenStopping = 0;
            var totalReceived = 0;

            try
            {
                await TestUtility.SendSessionMessagesAsync(sender, sessions, messagesPerSession).ConfigureAwait(false);

                queueClient.RegisterSessionHandler(
                    async (messageSession, receivedMessage, token) =>
                    {
                        Interlocked.Increment(ref count);
                        Thread.Sleep(3000);
                        await messageSession.CompleteAsync(new[] { receivedMessage.SystemProperties.LockToken });
                    },
                    new SessionHandlerOptions(ExceptionReceivedHandler) { MaxConcurrentAcceptSessionCalls = 1, MaxConcurrentSessions = 2, AutoComplete = false });

                Thread.Sleep(5000);
                await queueClient.StopReceivingAsync().ConfigureAwait(false);
                messagesWhenStopping = count;

                queueClient.RegisterSessionHandler(
                async (messageSession, receivedMessage, token) =>
                {
                    Interlocked.Increment(ref count);
                    await messageSession.CompleteAsync(new[] { receivedMessage.SystemProperties.LockToken });
                },
                new SessionHandlerOptions(ExceptionReceivedHandler) { MaxConcurrentAcceptSessionCalls = 1, MaxConcurrentSessions = 2, AutoComplete = false });

                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed.TotalSeconds <= 60)
                {
                    if (count == 6)
                    {
                        TestUtility.Log($"All messages Received.");
                        break;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
                totalReceived = count;

            }
            finally
            {
                await queueClient.StopReceivingAsync().ConfigureAwait(false);
                await sender.CloseAsync().ConfigureAwait(false);
                await queueClient.CloseAsync().ConfigureAwait(false);
                queueClient = null;
                Assert.Equal(4, messagesWhenStopping);
                Assert.Equal(messagesPerSession * sessions, totalReceived);
            }
        }
    }
}