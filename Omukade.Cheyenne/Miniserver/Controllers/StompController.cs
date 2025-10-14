/*************************************************************************
* Omukade Cheyenne - A PTCGL "Rainier" Standalone Server
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
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Omukade.Cheyenne.ClientConnections;
using Omukade.Cheyenne.CustomMessages;
using Omukade.Cheyenne.Encoding;
using Omukade.Cheyenne.Extensions;
using Omukade.Cheyenne.Miniserver.Model;
using ClientNetworking;
using ClientNetworking.Codecs;
using ClientNetworking.Models.WebSocket;
using ClientNetworking.Stomp;
using ClientNetworking.Util;
using Spectre.Console;
using System.Net.WebSockets;

namespace Omukade.Cheyenne.Miniserver.Controllers
{
    [Route("websocket/v1")]
    [ApiController]
    public class StompController : ControllerBase, IDisposable, IClientConnection
    {
        const int WS_TIMEOUT_MS = 10_000;

        public StompController()
        {
            this.txTask.Start();
            this.txTask.Wait();
        }

        static internal int MESSAGE_ACCUMULATOR_BUFFER_SIZE = 32_768;

        /// <summary>
        /// The maximum tolerated length of the stomp verb in bytes that appears at the start of each stomp message, including the trailing newline
        /// </summary>
        const int MAX_COMMAND_LENGTH = 16;

        static HashSet<string> KNOWN_STOMP_VERBS = new HashSet<string>()
        {
            "SEND",
            "SUBSCRIBE",
            "UNSUBSCRIBE",
            "BEGIN",
            "COMMIT",
            "ABORT",
            "ACK",
            "NACK",
            "DISCONNECT",
            "CONNECT",
            "STOMP",
            "CONNECTED",
            "MESSAGE",
            "RECEIPT",
            "ERROR",
        };

        const string HEADER_CONTENT_LENGTH = "content-length";
        const string HEADER_CONTENT_TYPE = "content-type";
        const string HEADER_PAYLOAD = "pay";
        const string HEADER_RECEIPT = "receipt";
        const string HEADER_RECEIPT_ID = "receipt-id";
        const string HEADER_INSTANCEID = "x-instance";

        static byte[] ENCODED_CONTENT_LENGTH_HEADER = System.Text.Encoding.UTF8.GetBytes(HEADER_CONTENT_LENGTH + ":");
        static byte[] ENCODED_CONTENT_TYPE_HEADER = System.Text.Encoding.UTF8.GetBytes(HEADER_CONTENT_TYPE + ":");
        static byte[] ENCODED_PAYLOAD_HEADER = System.Text.Encoding.UTF8.GetBytes(HEADER_PAYLOAD + ":");
        static byte[] ENCODED_RECEIPT_HEADER = System.Text.Encoding.UTF8.GetBytes(HEADER_RECEIPT + ":");
        static byte[] ENCODED_RECEIPTID_HEADER = System.Text.Encoding.UTF8.GetBytes(HEADER_RECEIPT_ID + ":");
        static byte[] ENCODED_INSTANCEID_HEADER = System.Text.Encoding.UTF8.GetBytes(HEADER_INSTANCEID + ":");

        /// <summary>
        /// 
        /// </summary>
        public static Func<IClientConnection, object, Task>? MessageReceived;
        public static Func<IClientConnection, Task>? ClientConnected;
        public static Func<IClientConnection, Task>? ClientDisconnected;

        private static readonly Func<object, ReusableBuffer> encodeFlatbuffer =
            (Func<object, ReusableBuffer>) Delegate.CreateDelegate(typeof(Func<object, ReusableBuffer>),
                typeof(ICodec).Assembly.GetType("ClientNetworking.Flatbuffers.Encoders")!.GetMethod("Encode", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, new Type[] { typeof(object) })!);

        private WebSocket ws;
        public PlayerMetadata? Tag { get; set; }

        public DateTime LastMessageReceived = default;

        private async Task SendMessageAsync(StompFrameWithData message)
        {
            if (ws.State != WebSocketState.Open) throw new InvalidOperationException("Can't send a message with uninitialized or non-open WS");

            byte[] bytesToSend = EncodeStompFrame(message);

//#warning FIXME: There is no thread-safety here; 2 seperate parts of the app can totally try to send 2 different messages at once and you're screwed.
            CancellationTokenSource cts = new CancellationTokenSource(WS_TIMEOUT_MS);
            try
            {
                await ws.SendAsync(bytesToSend, WebSocketMessageType.Binary, endOfMessage: true, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch(Exception e)
            {
                AnsiConsole.WriteException(e);
                // throw;
            }
            finally
            {
                cts.Dispose();
            }
        }

        // This odd construct replicates how Task.CompletedTask creates its initial completed task instance.
        // We have to do this to avoid locking the shared task and causing random side effects if anyone else tries to do that somewhere.
        // TODO, at some point, potentially consider using pure Interlocked.CompareExchange as seen in CompilerGenerated += event handlers, at the cost of potential out-of-order messages
        private Task txTask = new Task(() => { });
        public void SendMessageEnquued_EXPERIMENTAL(object payload, SerializationFormat format = SerializationFormat.JSON)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (txTask.Status == TaskStatus.Faulted) throw new InvalidOperationException("Cannot send message to client due to faulted TX task");

            lock(txTask)
            {
                TaskContinuationOptions tco = TaskContinuationOptions.OnlyOnRanToCompletion;
                Task newChildTask = txTask.ContinueWith(_ => SendMessageAsync(payload, format), tco);
                Interlocked.Exchange(ref txTask, newChildTask);
            }
        }

        private async Task SendMessageAsync(object payload, SerializationFormat format = SerializationFormat.JSON)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            StompFrameWithData rawMessage;
            if (format == SerializationFormat.JSON)
            {
                byte[] payloadBytes = JsonConvert.SerializeObject(payload).GetUtf8Bytes();
                rawMessage = new StompFrameWithData
                {
                    frameHeaders = new StompFrame
                    {
                        Command = "MESSAGE",
                        ContentType = "application/json",
                        Payload = payload.GetType().Name
                    },
                    payload = payloadBytes
                };
            }
            else if(format == SerializationFormat.FlatBuffers)
            {
                ReusableBuffer rub = CodecUtil.FlatBuffers.Serialize(payload);
                rawMessage = new StompFrameWithData
                {
                    frameHeaders = new StompFrame
                    {
                        Command = "MESSAGE",
                        ContentType = "application/octet-stream",
                        Payload = payload.GetType().Name
                    },
                    payload = rub.ContentSegment.ToArray()
                };
            }
            else
            {
                throw new NotImplementedException($"Unknown serializer format {format} not implemented!");
            }

            await SendMessageAsync(rawMessage).ConfigureAwait(continueOnCapturedContext: false);
        }

        public void DisconnectClientImmediately()
        {
            this.Dispose();
        }

        public void Dispose()
        {
            try
            {
                this.ws.Abort();
                this.ws.Dispose();
            } catch { }
        }

        public bool IsOpen => ws.State == WebSocketState.Open;

        [HttpGet]
        [Route("external/stomp")]
        public async Task StompHandler()
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                Response.Headers["Omukade"] = Program.ServerVersionString;
                Response.Headers["X-Omukade-MatchLogicVersion"] = Program.AssemblyVersionMatchLogic;

                using (ws = await HttpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(continueOnCapturedContext: false))
                {
                    if (ClientConnected != null) await ClientConnected(this).ConfigureAwait(continueOnCapturedContext: false);

                    byte[] messageAccumulatorBuffer = new byte[MESSAGE_ACCUMULATOR_BUFFER_SIZE];
                    int messageAccumulatorPosition = 0;

                    CancellationTokenSource cts = null;
                    try
                    {
                        cts = new CancellationTokenSource(WS_TIMEOUT_MS);

                        WebSocketReceiveResult? receiveResult = await ws.ReceiveAsync(new ArraySegment<byte>(messageAccumulatorBuffer), cts.Token).ConfigureAwait(continueOnCapturedContext: false);
                        cts.Dispose();

                        while (!receiveResult.CloseStatus.HasValue && (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived))
                        {
                            messageAccumulatorPosition += receiveResult.Count;

                            if (receiveResult.EndOfMessage)
                            {
                                object? receivedMessage = ProcessReceivedMessage(messageAccumulatorBuffer, ref messageAccumulatorPosition);
                                if (receivedMessage != null)
                                {
                                    if(receivedMessage is HeartbeatPayload)
                                    {
                                        LastMessageReceived = DateTime.UtcNow;
                                        SendMessageEnquued_EXPERIMENTAL(new HeartbeatPayload { timeSent = new DateTimeOffset(LastMessageReceived).ToUnixTimeMilliseconds() });
                                    }
                                    else if (MessageReceived != null) await MessageReceived.Invoke(this, receivedMessage).ConfigureAwait(continueOnCapturedContext: false);
                                }
                            }

                            cts = new CancellationTokenSource(WS_TIMEOUT_MS);
                            receiveResult = await ws.ReceiveAsync(new ArraySegment<byte>(messageAccumulatorBuffer, messageAccumulatorPosition, MESSAGE_ACCUMULATOR_BUFFER_SIZE - messageAccumulatorPosition), CancellationToken.None)
                                .ConfigureAwait(continueOnCapturedContext: false);
                            cts.Dispose();
                        }
                    }
                    catch(WebSocketException e)
                    {
                        if(e.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                        {
                            // Swallow connection-closed-unexpectedly
                        }
                        else
                        {
                            throw;
                        }
                    }
                    finally
                    {
                        cts?.Dispose();
                        if (ClientDisconnected != null) await ClientDisconnected(this).ConfigureAwait(continueOnCapturedContext: false);
                    }
                }
            }
            else
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        }

        internal static object? ProcessReceivedMessage(byte[] accumulator, ref int accumulatorPosition)
        {
            const int MAX_HEADERS_TO_READ = 7;
            // Read off command

            Span<byte> receivedBuffer = accumulator.AsSpan(0, accumulatorPosition);

            int cursorPos = 0;
            string stompVerb = ReadLineFromByteBuffer(receivedBuffer, cursorPos, out cursorPos);

            if (!KNOWN_STOMP_VERBS.Contains(stompVerb))
            {
                throw new IllegalPacketReceivedException("Packet contains invalid stomp verb - " + stompVerb);
            }

            StompFrame frame = default;
            frame.Command = stompVerb;

            for (int headerNumber = 0; headerNumber <= MAX_HEADERS_TO_READ; headerNumber++)
            {
                if (headerNumber == MAX_HEADERS_TO_READ) throw new Exception("Packet contains way too many freaking headers");

                string headerRaw = ReadLineFromByteBuffer(receivedBuffer, cursorPos, out cursorPos);

                // End of headers; now at payload
                if (headerRaw == string.Empty) break;

                int colonOffset = headerRaw.IndexOf(':');

                // Covers both colon-at-start-of-string (invalid) and colon-not-found (invalid)
                if (colonOffset <= 0) throw new IllegalPacketReceivedException("Packet contains invalid header - " + headerRaw);

                string headerName = headerRaw.Substring(0, colonOffset);
                string headerValue = headerRaw.Substring(colonOffset + 1);

                switch (headerName)
                {
                    case HEADER_CONTENT_TYPE:
                        frame.ContentType = headerValue; break;
                    case HEADER_PAYLOAD:
                        frame.Payload = headerValue; break;
                    case HEADER_RECEIPT:
                        frame.Receipt = headerValue; break;
                    case HEADER_RECEIPT_ID:
                        if (int.TryParse(headerValue, out int parsedReceiptId))
                        {
                            frame.ReceiptId = parsedReceiptId;
                            break;
                        }
                        else
                        {
                            throw new Exception("Packet contains receipt-id without a valid int!");
                        }
                }
            }

            int payloadSize = accumulatorPosition - cursorPos;
            object? decodedMessage = DecodeStompMessageToObject(frame, accumulator, cursorPos, payloadSize);

            Array.Clear(accumulator, 0, payloadSize);
            accumulatorPosition = 0;

            return decodedMessage;
        }

        internal static string ReadLineFromByteBuffer(ReadOnlySpan<byte> array, int offset, out int newCursorPosition, int maximumLineLength = 256)
        {
            const byte CHAR_R = 13;
            const byte CHAR_N = 10;
            int arrayLen = array.Length;

            if (offset == arrayLen)
            {
                newCursorPosition = offset;
                return string.Empty;
            }
            if (offset > arrayLen) throw new ArgumentOutOfRangeException(nameof(offset), "Offset is larger than array");
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be zero");

            newCursorPosition = arrayLen;
            int pos = offset;
            int offsetPastMaximumLineLength = offset + maximumLineLength;
            for (; pos < arrayLen; pos++)
            {
                if (offset > offsetPastMaximumLineLength) throw new Exception("Packet contains line that is unexpectedly long");

                byte currentItem = array[pos];
                if (currentItem == CHAR_R)
                {
                    // \r ; test for \r\n
                    if (pos < arrayLen - 1 && array[pos + 1] == CHAR_N)
                    {
                        newCursorPosition = pos + 2;
                    }

                    // character is \r alone
                    else
                    {
                        newCursorPosition = pos + 1;
                    }

                    break;
                }
                else if (currentItem == CHAR_N)
                {
                    newCursorPosition = pos + 1;
                    break;
                }
            }

            if (pos == offset) return string.Empty;

            return System.Text.Encoding.UTF8.GetString(array.Slice(offset, pos - offset));
        }

        internal static byte[] EncodeStompFrame(StompFrameWithData frameWithData)
        {
            const byte NEWLINE_TO_USE = 10;

            // +1 to account for extra newline at end of headers
            // extra +1 to account for weird null-terminator Rainier likes to add for some...? reason.
            int neededBytes = (frameWithData.payload?.Length ?? 0) + 1 + 1;
            int verbBytes = frameWithData.frameHeaders.Command.GetUtf8Length();
            neededBytes += verbBytes + 1;

            string contentLengthValue = frameWithData.payload?.Length.ToString() ?? "0";
            int contentTypeBytes = -1, payloadBytes = -1, receiptBytes = -1, receiptIdBytes = -1, contentLengthBytes;
            if (frameWithData.frameHeaders.ContentType != null)
            {
                contentTypeBytes = frameWithData.frameHeaders.ContentType.GetUtf8Length();
                neededBytes += 13 + contentTypeBytes + 1;
            }
            if (frameWithData.frameHeaders.Payload != null)
            {
                payloadBytes = frameWithData.frameHeaders.Payload.GetUtf8Length();
                neededBytes += 4 + payloadBytes + 1;
            }
            if (frameWithData.frameHeaders.Receipt != null)
            {
                receiptBytes = frameWithData.frameHeaders.Receipt.GetUtf8Length();
                neededBytes += 8 + receiptBytes + 1;
            }

            // Content Length
            contentLengthBytes = contentLengthValue.Length;
            neededBytes += 15 + contentLengthBytes + 1;

            string? receiptIdAsString = frameWithData.frameHeaders.ReceiptId?.ToString();
            if (receiptIdAsString != null)
            {
                receiptIdBytes = receiptIdAsString.GetUtf8Length();
                neededBytes += 11 + receiptIdBytes + 1;
            }

            using MemoryStream ms = new MemoryStream(neededBytes);

            int largestBytes = Math.Max(contentTypeBytes, payloadBytes);
            largestBytes = Math.Max(largestBytes, receiptBytes);
            largestBytes = Math.Max(largestBytes, receiptIdBytes);
            largestBytes = Math.Max(largestBytes, verbBytes);
            largestBytes = Math.Max(largestBytes, contentLengthBytes);

            Span<byte> encodingScratchspace = stackalloc byte[largestBytes];
            System.Text.Encoding.UTF8.GetBytes(frameWithData.frameHeaders.Command, encodingScratchspace);
            ms.Write(encodingScratchspace.Slice(0, verbBytes));
            ms.WriteByte(NEWLINE_TO_USE);

            if (frameWithData.frameHeaders.ContentType != null)
            {
                System.Text.Encoding.UTF8.GetBytes(frameWithData.frameHeaders.ContentType, encodingScratchspace);
                ms.Write(ENCODED_CONTENT_TYPE_HEADER);
                ms.Write(encodingScratchspace.Slice(0, contentTypeBytes));
                ms.WriteByte(NEWLINE_TO_USE);
            }

            if (frameWithData.frameHeaders.Payload != null)
            {
                System.Text.Encoding.UTF8.GetBytes(frameWithData.frameHeaders.Payload, encodingScratchspace);
                ms.Write(ENCODED_PAYLOAD_HEADER);
                ms.Write(encodingScratchspace.Slice(0, payloadBytes));
                ms.WriteByte(NEWLINE_TO_USE);
            }

            if (frameWithData.frameHeaders.Receipt != null)
            {
                System.Text.Encoding.UTF8.GetBytes(frameWithData.frameHeaders.Receipt, encodingScratchspace);
                ms.Write(ENCODED_RECEIPT_HEADER);
                ms.Write(encodingScratchspace.Slice(0, receiptBytes));
                ms.WriteByte(NEWLINE_TO_USE);
            }

            if (receiptIdAsString != null)
            {
                System.Text.Encoding.UTF8.GetBytes(receiptIdAsString, encodingScratchspace);
                ms.Write(ENCODED_RECEIPTID_HEADER);
                ms.Write(encodingScratchspace.Slice(0, receiptIdBytes));
                ms.WriteByte(NEWLINE_TO_USE);
            }

            // Content Length
            System.Text.Encoding.UTF8.GetBytes(contentLengthValue, encodingScratchspace);
            ms.Write(ENCODED_CONTENT_LENGTH_HEADER);
            ms.Write(encodingScratchspace.Slice(0, contentLengthBytes));
            ms.WriteByte(NEWLINE_TO_USE);

            ms.WriteByte(NEWLINE_TO_USE);

            if (frameWithData.payload != null)
            {
                ms.Write(frameWithData.payload);
            }

            return ms.GetBuffer();
        }

        internal static object? DecodeStompMessageToObject(StompFrame header, byte[] originalArray, int offset, int length)
        {
            if(header.Command != "SEND")
            {
                return null;
            }

            if (header.ContentType == null || header.ContentType == "application/octet-stream")
            {
                ByteBuffer bb = new ByteBuffer(originalArray, offset);

                return header.Payload switch
                {
                    nameof(ClientNetworking.Models.GameServer.GameMessage) => ServersideFlatbufferEncoders.DecodeGameMessage(bb),
                    nameof(ClientNetworking.Models.Matchmaking.AcceptDirectMatch) => ServersideFlatbufferEncoders.DecodeAcceptDirectMatch(bb),
                    nameof(ClientNetworking.Models.Matchmaking.CancelDirectMatch) => ServersideFlatbufferEncoders.DecodeCancelDirectMatch(bb),
                    nameof(ClientNetworking.Models.Matchmaking.ProposeDirectMatch) => ServersideFlatbufferEncoders.DecodeProposeDirectMatch(bb),
                    nameof(ClientNetworking.Models.Matchmaking.BeginMatchmaking) => ServersideFlatbufferEncoders.DecodeBeginMatchmaking(bb),
                    nameof(ClientNetworking.Models.Matchmaking.CancelMatchmaking) => ServersideFlatbufferEncoders.DecodeCancelMatchmaking(bb),
                    nameof(ClientNetworking.Models.Query.QueryMessage) => ServersideFlatbufferEncoders.DecodeQueryMessage(bb),
                    nameof(ClientNetworking.Models.User.DataStoreSaveRequest) => ServersideFlatbufferEncoders.DecodeDataStoreSaveRequest(bb),
                    nameof(HeartbeatPayload) => ServersideFlatbufferEncoders.DecodeHeartbeatPayload(bb),
                    nameof(PingPayload) => ClientNetworking.Flatbuffers.Decoders.DecodePingPayload(bb),
                    _ => throw new IllegalPacketReceivedException($"Unknown Flatbuffer message type {header.Payload}")
                };
            }
            else if(header.ContentType == "application/json")
            {
                string jsonPayload = System.Text.Encoding.UTF8.GetString(originalArray, offset, length);

                return header.Payload switch
                {
                    nameof(ClientNetworking.Models.GameServer.GameMessage) => JsonConvert.DeserializeObject<ClientNetworking.Models.GameServer.GameMessage>(jsonPayload),
                    nameof(ClientNetworking.Models.Matchmaking.AcceptDirectMatch) => JsonConvert.DeserializeObject<ClientNetworking.Models.Matchmaking.AcceptDirectMatch>(jsonPayload),
                    nameof(ClientNetworking.Models.Matchmaking.CancelDirectMatch) => JsonConvert.DeserializeObject<ClientNetworking.Models.Matchmaking.CancelDirectMatch>(jsonPayload),
                    nameof(ClientNetworking.Models.Matchmaking.ProposeDirectMatch) => JsonConvert.DeserializeObject<ClientNetworking.Models.Matchmaking.ProposeDirectMatch>(jsonPayload),
                    nameof(ClientNetworking.Models.Matchmaking.BeginMatchmaking) => JsonConvert.DeserializeObject<ClientNetworking.Models.Matchmaking.BeginMatchmaking>(jsonPayload),
                    nameof(ClientNetworking.Models.Matchmaking.CancelMatchmaking) => JsonConvert.DeserializeObject<ClientNetworking.Models.Matchmaking.CancelMatchmaking>(jsonPayload),
                    nameof(ClientNetworking.Models.Query.QueryMessage) => JsonConvert.DeserializeObject<ClientNetworking.Models.Query.QueryMessage>(jsonPayload),
                    nameof(ClientNetworking.Models.User.DataStoreSaveRequest) => JsonConvert.DeserializeObject<ClientNetworking.Models.User.DataStoreSaveRequest>(jsonPayload),
                    nameof(SupplementalDataMessageV2) => JsonConvert.DeserializeObject<SupplementalDataMessageV2>(jsonPayload),
                    nameof(GetOnlineFriends) => JsonConvert.DeserializeObject<GetOnlineFriends>(jsonPayload),
                    nameof(HeartbeatPayload) => JsonConvert.DeserializeObject<HeartbeatPayload>(jsonPayload),
                    nameof(PingPayload) => JsonConvert.DeserializeObject<PingPayload>(jsonPayload),
                    nameof(GetImplementedExpandedCardsV1) => JsonConvert.DeserializeObject<GetImplementedExpandedCardsV1>(jsonPayload),
                    _ => throw new IllegalPacketReceivedException($"Unknown JSON message type {header.Payload}")
                };
            }
            else
            {
                throw new IllegalPacketReceivedException($"Unknown content type for STOMP frame: {header.ContentType}");
            }
        }
    }
}
