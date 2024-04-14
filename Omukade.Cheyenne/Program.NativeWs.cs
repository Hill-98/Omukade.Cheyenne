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

using MatchLogic.source.MatchLogic.Utils.SerializationBinder;
using Newtonsoft.Json;
using Omukade.Cheyenne.ClientConnections;
using Omukade.Cheyenne.CustomMessages;
using Omukade.Cheyenne.Miniserver.Controllers;
using Omukade.Cheyenne.Miniserver.ControlMessages;
using Omukade.Cheyenne.Miniserver.Model;
using Omukade.Cheyenne.Shell.Model;
using ClientNetworking.Models.GameServer;
using ClientNetworking.Models.Matchmaking;
using ClientNetworking.Models.Query;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using Omukade.Cheyenne.Encoding;
using MatchLogic;

namespace Omukade.Cheyenne
{
    partial class Program
    {
        internal const string REFLECT_MESSAGE_MAGIC = nameof(REFLECT_MESSAGE_MAGIC);
        const bool EnableHttps = false;
        const bool EnableHttp = true;

        static internal string AssemblyVersionMatchLogic;
        static internal string ServerVersionString;

        /// <summary>
        /// Sets the HTTPS certificate to use when <see cref="EnableHttps"/> is enabled.
        /// </summary>
        public static X509Certificate2 HttpsCertificate { get; set; }

        static WebApplication app;

        internal static Thread? wsMessageProcessorThread;

        internal static WebApplication PrepareWsServer(params Assembly[] additionalAssembliesToScanForControllers) => PrepareWsServer(additionalAssembliesToScanForControllers, enableProxyMiddleware: true);
        internal static WebApplication PrepareWsServer(Assembly[] additionalAssembliesToScanForControllers, bool enableProxyMiddleware = true)
        {
            if (EnableHttps && HttpsCertificate == null)
            {
                throw new InvalidOperationException("Cannot start server; HTTPS is enabled, but no certificate was provided.");
            }

            continueRunningWsServer = true;

            AssemblyVersionMatchLogic = ((AssemblyInformationalVersionAttribute)typeof(MatchLogic.CardSource).Assembly.GetCustomAttribute(typeof(AssemblyInformationalVersionAttribute)))?.InformationalVersion ?? "[unknown gamelogic version]";
            ServerVersionString = $"Cheyenne 1.1.0 (ML {AssemblyVersionMatchLogic})";

            // Prepare controller
            StompController.ClientConnected = new Func<IClientConnection, Task>(Stomp_NewConnection);
            StompController.ClientDisconnected = new Func<IClientConnection, Task>(Stomp_ConnectionClosed);
            StompController.MessageReceived = new Func<IClientConnection, object, Task>(Stomp_MessageReceived);

            WebApplicationBuilder builder = WebApplication.CreateBuilder();

            // Reduce logging verbosity
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            // Add services to the container.
            Assembly assemblyOfServerCore = Assembly.GetExecutingAssembly();
            var controllerBuilder = builder.Services.AddControllers().AddApplicationPart(assemblyOfServerCore);
            foreach (Assembly assem in additionalAssembliesToScanForControllers)
            {
                controllerBuilder = controllerBuilder.AddApplicationPart(assem);
            }
            controllerBuilder = controllerBuilder.AddControllersAsServices();
            controllerBuilder = controllerBuilder.AddJsonOptions(jsonOptions => jsonOptions.JsonSerializerOptions.IncludeFields = true);

            builder.WebHost.ConfigureKestrel((context, krestrelOptions) =>
            {
                if (EnableHttps)
                {
                    krestrelOptions.ListenAnyIP(443, lo =>
                    {
                        lo.UseHttps(https => https.ServerCertificate = HttpsCertificate);
                    });
                }
                if (EnableHttp)
                {
                    krestrelOptions.ListenAnyIP(config.HttpPort);
                }
            });

            var app = builder.Build();

            // Configure the HTTP request pipeline.

            var webSocketOptions = new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30)
            };
            app.UseWebSockets(webSocketOptions);
            app.MapControllers();

            return app;
        }

        internal static void StartWsProcessorThread()
        {
            if (wsMessageProcessorThread?.ThreadState == ThreadState.Running) throw new InvalidOperationException("Cannot start processor thread; another one is still running.");
            wsMessageProcessorThread = new Thread(WebsocketCheynneThread);
            wsMessageProcessorThread.Start();
        }

        private static Task Stomp_NewConnection(IClientConnection arg)
        {
            arg.Tag = new PlayerMetadata { PlayerConnectionHelens = arg };
            return Task.CompletedTask;
        }

        private static Task Stomp_MessageReceived(IClientConnection controller, object message)
        {
            receiveQueue.Enqueue(new ReceivedMessage { ReceivedFrom = controller, Message = message });
            return Task.CompletedTask;
        }

        private static Task Stomp_ConnectionClosed(IClientConnection controller)
        {
            receiveQueue.Enqueue(new ReceivedMessage { ReceivedFrom = controller, Message = ControlMessages.CLIENT_DISCONNECTED });
            return Task.CompletedTask;
        }

        static ConcurrentQueue<ReceivedMessage> receiveQueue = new ConcurrentQueue<ReceivedMessage>();
        internal static bool continueRunningWsServer = true;

        private static void WebsocketCheynneThread()
        {
            while(continueRunningWsServer)
            {
                // Process backlogged messages
                while (receiveQueue.TryDequeue(out ReceivedMessage messageWrapper))
                {
                    if (!messageWrapper.ReceivedFrom.IsOpen && !(messageWrapper.Message is ControlMessage)) continue;
                    ProcessSingleWsMessage(messageWrapper);
                }

                Thread.Sleep(10 /*ms*/);
            }
        }

        public static async ValueTask StopWsServer()
        {
            if (app == null) return;
            continueRunningWsServer = false;
            await app.StopAsync();
            await app.DisposeAsync();
            wsMessageProcessorThread?.Join(10_000);
        }

        static void ProcessSingleWsMessage(ReceivedMessage messageWrapper)
        {
            IClientConnection controller = messageWrapper.ReceivedFrom;
            object message = messageWrapper.Message;

            try
            {
                switch (message)
                {
                    case ControlMessage:
                        ProcessWsControlMessage(controller, message);
                        break;
                    case QueryMessage qm:
                        ProcessQueryMessageForEcho(controller, qm);
                        break;
                    case BeginMatchmaking:
                    case CancelMatchmaking:
                    case GameMessage:
                    case ProposeDirectMatch:
                    case CancelDirectMatch:
                    case AcceptDirectMatch:
                        serverCore.HandleRainerMessage(controller.Tag!, message);
                        break;
                    case SupplementalDataMessageV2 sdmv2:
                        if (controller.Tag == null)
                        {
                            throw new ArgumentNullException("Received SDM, but ClientMetadata Tag for connection is null.");
                        }
                        serverCore.HandleSupplementalDataMessage(sdmv2, controller.Tag);
                        break;
                    case GetImplementedExpandedCardsV1 giecV1:
                        serverCore.HandleGetSupportedExpandedCards(controller.Tag, giecV1);
                        break;
                    case GetOnlineFriends gof:
                        OnlineFriendsResponse ofr;
                        if (gof.FriendIds == null || gof.FriendIds.Count == 0)
                        {
                            ofr = new OnlineFriendsResponse { CurrentlyOnlineFriends = new List<string>(0), TransactionId = gof.TransactionId };
                        }
                        else
                        {
                            List<string> matchingOnlinePlayers = gof.FriendIds.Where(serverCore.UserMetadata.ContainsKey).ToList();
                            ofr = new OnlineFriendsResponse { CurrentlyOnlineFriends = matchingOnlinePlayers, TransactionId = gof.TransactionId };
                        }
                        controller.SendMessageEnquued_EXPERIMENTAL(ofr);
                        break;
                    case GetCurrentGamesRequest:
                        ProcessConsoleGetGamesMessage(controller);
                        break;
                    case GetOnlinePlayersRequest gopr:
                        ProcessConsoleGetOnlinePlayersMessage(controller, gopr);
                        break;
                    case DumpGameStateRequest dgsr:
                        if(controller.GetType() == typeof(DebugClientConnection))
                        {
                            ProcessDumpGameState(dgsr);
                        }
                        break;
                    default:
                        break;
                }
            }
            catch(Exception e)
            {
                AnsiConsole.WriteException(e);
                messageWrapper.ReceivedFrom?.DisconnectClientImmediately();
            }
        }

        private static void ProcessDumpGameState(DumpGameStateRequest dgsr)
        {
            if(serverCore.ActiveGamesById.TryGetValue(dgsr.gameId, out var concernedGame))
            {
                const string dumpFolder = "gamestate-dump";
                Directory.CreateDirectory(dumpFolder);

                string fname = Path.Combine(dumpFolder, $"gamestate-{dgsr.gameId}-{DateTime.UtcNow.Ticks}.json");
                using (StreamWriter sw = new StreamWriter(fname))
                {
                    JsonSerializerSettings jss = new JsonSerializerSettings
                    {
                        TypeNameHandling = TypeNameHandling.Auto,
                        ContractResolver = new WhitelistedContractResolver(),
                        NullValueHandling = NullValueHandling.Ignore,
                        DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate,
                        SerializationBinder = new JsonSerializationBinder(),
                        Formatting = Formatting.Indented,
                    };
                    jss.Converters.Add(new StringEnumConverter(new DefaultNamingStrategy(), allowIntegerValues: true));
                    JsonSerializer.Create(jss).Serialize(sw, concernedGame);
                }
            }
        }

        private static void ProcessConsoleGetOnlinePlayersMessage(IClientConnection controller, GetOnlinePlayersRequest request)
        {
            GetOnlinePlayersResponse allOnlinePlayerData = new GetOnlinePlayersResponse();
            allOnlinePlayerData.PlayerCount = serverCore.UserMetadata.Count;

            if(!request.PlayerCountOnly)
            {
                allOnlinePlayerData.OnlinePlayers = serverCore.UserMetadata.Values.Select(um => new GetOnlinePlayersResponse.OnlinePlayerInfo { DisplayName = um.PlayerDisplayName, PlayerId = um.PlayerId, CurrentGameId = um.CurrentGame?.matchId }).ToList();
            }
            controller.SendMessageEnquued_EXPERIMENTAL(allOnlinePlayerData);
        }

        static void ProcessWsControlMessage(IClientConnection controller, object rawMessage)
        {
            if(rawMessage == ControlMessages.CLIENT_DISCONNECTED)
            {
                Console.WriteLine($"Player disconnected - {controller.Tag?.PlayerDisplayName ?? "[no SDM]"}");
                PlayerMetadata? playerData = controller.Tag;
                if (playerData?.PlayerId == null)
                {
                    return;
                }

                serverCore.PurgePlayerFromActivePlayers(playerData);
            }
        }

        static void ProcessConsoleGetGamesMessage(IClientConnection controller)
        {
            bool PrizeNotNullCritertion(CardEntity entity) => entity != null;

            GetCurrentGamesResponse response = new GetCurrentGamesResponse();
            response.ongoingGames = serverCore.ActiveGamesById.Values.Select(game => new GetCurrentGamesResponse.GameSummary
            {
                GameId = game.matchId,
                Player1 = game.playerInfos[0].playerName,
                Player2 = game.playerInfos[1].playerName,
                PrizeCountPlayer1 = game.CurrentOperation?.workingBoard?.p1Prize?.Count(PrizeNotNullCritertion) ?? -1,
                PrizeCountPlayer2 = game.CurrentOperation?.workingBoard?.p2Prize?.Count(PrizeNotNullCritertion) ?? -1,
            }).ToList();

            controller.SendMessageEnquued_EXPERIMENTAL(response);
        }

        static void ProcessQueryMessageForEcho(IClientConnection controller, QueryMessage queryMsg)
        {
            if(queryMsg.queryId == REFLECT_MESSAGE_MAGIC && queryMsg.message?.Length <= 2 && 
               queryMsg.message[0] is (byte) ClientNetworking.SerializationFormat.FlatBuffers or (byte)ClientNetworking.SerializationFormat.JSON)
            {
                controller.SendMessageEnquued_EXPERIMENTAL(new QueryMessage { queryId = queryMsg.queryId, message = queryMsg.message }, (ClientNetworking.SerializationFormat)queryMsg.message[0]);
            }
        }
    }
}
