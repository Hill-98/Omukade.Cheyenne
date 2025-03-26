/*************************************************************************
* Tests for Omukade Cheyenne - A PTCGL "Rainier" Standalone Server
* (c) 2022 Hastwell/Electrosheep Networks 
* 
* This program is free software: you can redistribute it and/or modify
* it under the terms of the GNU Affero General Public License as published
* by the Free Software Foundation, either version 3 of the License, or
* (at your option) any later version.
* 
* This program is distributed in the hope that it will be useful,
* but WITHOUT ANY WARRANTY; without even the implied warranty of
* MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
* GNU Affero General Public License for more details.
* 
* You should have received a copy of the GNU Affero General Public License
* along with this program.  If not, see <http://www.gnu.org/licenses/>.
**************************************************************************/

using Google.FlatBuffers;
using Microsoft.AspNetCore.Builder;
using MonoMod.Utils;
using Omukade.Cheyenne.Miniserver.Controllers;
using Omukade.Cheyenne.Tests.BakedData;
using ClientNetworking;
using ClientNetworking.Codecs;
using ClientNetworking.Flatbuffers;
using ClientNetworking.Models.Query;
using ClientNetworking.Models.WebSocket;
using ClientNetworking.Stomp;
using ClientNetworking.Util;
using System.Collections.Concurrent;
using System.Data;
using System.Text;
using SerializationFormat = ClientNetworking.SerializationFormat;

namespace Omukade.Cheyenne.Tests
{
    public class WebsocketBasicTests : IDisposable
    {
        WebApplication app;

        static HttpRouter router = new HttpRouter(new BakedData.BakedStage
        {
            BypassValidation = true,
            GlobalRoute = new BakedData.BakedRoute
            {
                WebsocketUrl = new Uri("ws://localhost:10850/websocket/v1/external/stomp")
            },
            name = "LOCAL",
            SupportsRouting = false
        }, new NopClientLogger());

        static WebSocketSettings wsSettings = new WebSocketSettings
        {
            ConnectAttemptTimeout = new TimeSpan(0, 0, 5),
            HeartbeatTimeout = new TimeSpan(0, 0, 30),
            InactivityHeartbeat = new TimeSpan(0, 0, 30),
            ReconnectTimeout = new TimeSpan(0, 0, 30),
            StifleHeartbeat = true
        };

        static TokenHolder fakeTokenHolder = new TokenHolder(accessKey: null, client: null);

        public WebsocketBasicTests()
        {
            RegisterEncoders();

            // Initialize the BufferPool or the FlatBuffer serializer will do dumb shit like get stuck in a while(true) loop
            // while working on a zero-length buffer.
            BufferPool.Init(new BufferPoolSettings());

            Omukade.Cheyenne.Program.InitAutoPar();
            app = Omukade.Cheyenne.Program.PrepareWsServer();
            app.StartAsync().Wait();
            Omukade.Cheyenne.Program.StartWsProcessorThread();
        }

        private static void RegisterEncoders()
        {
            Dictionary<Type, Action<object, FlatBufferBuilder>> encoderMap = (Dictionary<Type, Action<object, FlatBufferBuilder>>)typeof(Encoders).GetField("Map", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
                .GetValue(null)!;

            encoderMap[typeof(QueryMessage)] = new Action<object, FlatBufferBuilder>(EncodeQueryMessage);
        }

        static void EncodeQueryMessage(object obj, FlatBufferBuilder builder)
        {
            QueryMessage qm = (QueryMessage)obj;

            StringOffset queryIdOffset = builder.CreateStringOrNull(qm.queryId);
            VectorOffset dataOffset = builder.CreateByteArray(qm.message);

            com.pokemon.studio.contracts.client_gameserver.QueryMessage.StartQueryMessage(builder);
            com.pokemon.studio.contracts.client_gameserver.QueryMessage.AddQueryId(builder, queryIdOffset);
            com.pokemon.studio.contracts.client_gameserver.QueryMessage.AddMessage(builder, dataOffset);
            builder.Finish(com.pokemon.studio.contracts.client_gameserver.QueryMessage.EndQueryMessage(builder).Value);
        }

        public void Dispose()
        {
            Omukade.Cheyenne.Program.continueRunningWsServer = false;
            app.StopAsync().Wait();
            app.DisposeAsync().AsTask().Wait();
        }

        [Theory]
        [InlineData(SerializationFormat.JSON)]
        [InlineData(SerializationFormat.FlatBuffers)]
        public void SingleClientCanCommunicate(SerializationFormat FORMAT_TO_USE)
        {
            const string QUERY_ID_TO_USE = Omukade.Cheyenne.Program.REFLECT_MESSAGE_MAGIC;
            byte[] PAYLOAD_TO_USE = new byte[] { (byte)FORMAT_TO_USE }; //System.Text.Encoding.UTF8.GetBytes(QUERY_ID_TO_USE);

            ManualResetEvent serverToClientMessageEvent = new ManualResetEvent(false);

            ICodec CODEC_TO_USE = CodecUtil.Codec(FORMAT_TO_USE);

            bool receivedServerToClientMessage = false;

            WebsocketWrapper.MessageDispatcher md = (ref StompFrame frame, ReusableBuffer buffer) =>
            {
                QueryMessage payload = CODEC_TO_USE.Deserialize<QueryMessage>(buffer);
                if (payload.queryId == QUERY_ID_TO_USE && PAYLOAD_TO_USE.SequenceEqual(payload.message))
                {
                    serverToClientMessageEvent.Set();
                    receivedServerToClientMessage = true;
                }
            };

            WebsocketPersistent wsp = new WebsocketPersistent(enableHeartbeats: false, enableMessageReceipts: false, enableVerboseLogging: false);

            WebsocketWrapper wsw = new WebsocketWrapper(logger: new NopClientLogger(), router, token: fakeTokenHolder, dispatcher: md, CODEC_TO_USE, settings: wsSettings, persistent: wsp,
                onNetworkStatusChange: null, onDisconnect: null, onServerTimeAvailable: null, userAgentString: "Omukade/Tests 1.0");

            try
            {
                wsw.OpenAsync().Wait();
                wsw.SendCommand(new Command<QueryMessage>("/app/foo", false), new QueryMessage { queryId = QUERY_ID_TO_USE, message = PAYLOAD_TO_USE });

                serverToClientMessageEvent.WaitOne(2_000 /*ms*/);
            }
            finally
            {
                wsw.CloseAsync().Wait();
            }

            Assert.True(receivedServerToClientMessage, "Server to client message was not received");
        }

        [Theory]
        [InlineData(SerializationFormat.JSON)]
        [InlineData(SerializationFormat.FlatBuffers)]
        public void SerializationRoundTrip(SerializationFormat FORMAT_TO_USE)
        {
            const string QUERY_ID_TO_USE = Omukade.Cheyenne.Program.REFLECT_MESSAGE_MAGIC;
            byte[] PAYLOAD_TO_USE = new byte[] { (byte)FORMAT_TO_USE };

            ICodec codecToUse = CodecUtil.Codec(FORMAT_TO_USE);

            ReusableBuffer encodeRub = codecToUse.Serialize(new QueryMessage { queryId = QUERY_ID_TO_USE, message = PAYLOAD_TO_USE });

            ArraySegment<byte> serializedContent = encodeRub.ContentSegment;

            ReusableBuffer decodeRub = new ReusableBuffer(encodeRub.ContentSegment.Count);
            decodeRub.Stream.Write(serializedContent);
            decodeRub.Position = 0;

            QueryMessage actualPayload = codecToUse.Deserialize<QueryMessage>(decodeRub);
            Assert.Equal(QUERY_ID_TO_USE, actualPayload.queryId);
            Assert.Equal(PAYLOAD_TO_USE, actualPayload.message);
        }


        [Fact]
        public void SingleClientCanCommunicateMultiplePackets()
        {
            const int NUM_COMMANDS_TO_SEND = 10;
            const string QUERY_ID_TO_USE = Omukade.Cheyenne.Program.REFLECT_MESSAGE_MAGIC;

            ManualResetEvent allMessagesReceived = new ManualResetEvent(false);

            ICodec CODEC_TO_USE = CodecUtil.Codec(SerializationFormat.JSON);

            Queue<byte> messagesYetToBeReceived = new Queue<byte>();
            for (byte i = 0; i < NUM_COMMANDS_TO_SEND; i++) messagesYetToBeReceived.Enqueue((byte) i);

            WebsocketWrapper.MessageDispatcher md = (ref StompFrame frame, ReusableBuffer buffer) =>
            {
                QueryMessage payload = CODEC_TO_USE.Deserialize<QueryMessage>(buffer);
                if (payload.queryId == QUERY_ID_TO_USE && messagesYetToBeReceived.Peek() == payload.message[1])
                {
                    messagesYetToBeReceived.Dequeue();
                }
                else
                {
                    throw new InvalidOperationException($"Received out-of-order message - got {payload.message[1]}, but was expecting {messagesYetToBeReceived.Peek()}");
                }

                if (messagesYetToBeReceived.Count == 0) allMessagesReceived.Set();
            };


            WebsocketPersistent wsp = new WebsocketPersistent(enableHeartbeats: false, enableMessageReceipts: false, enableVerboseLogging: false);

            WebsocketWrapper wsw = new WebsocketWrapper(logger: new NopClientLogger(), router, token: fakeTokenHolder, dispatcher: md, CODEC_TO_USE, settings: wsSettings, persistent: wsp,
                onNetworkStatusChange: null, onDisconnect: null, onServerTimeAvailable: null, userAgentString: "Omukade/Tests 1.0");

            bool receivedSignal = false;
            try
            {
                wsw.OpenAsync().Wait();
                for(int i = 0; i < NUM_COMMANDS_TO_SEND; i++)
                {
                    wsw.SendCommand(new Command<QueryMessage>("/app/foo", false), new QueryMessage { queryId = QUERY_ID_TO_USE, message = new byte[] { (byte) SerializationFormat.JSON, (byte) i } });
                }

                receivedSignal = allMessagesReceived.WaitOne(2_000 /*ms*/);
            }
            finally
            {
                wsw.CloseAsync().Wait();
            }

            Assert.True(receivedSignal, $"Not all messages received, or received out-of-order; {messagesYetToBeReceived.Count} messages remain: {string.Join(", ", messagesYetToBeReceived)}");
        }
    }
}