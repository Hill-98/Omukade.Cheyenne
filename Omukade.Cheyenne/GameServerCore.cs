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

using MatchLogic;
using Newtonsoft.Json;
using Omukade.Cheyenne.CustomMessages;
using Omukade.Cheyenne.Model;
using Omukade.Cheyenne.Patching;
using ClientNetworking.Models;
using ClientNetworking.Models.GameServer;
using ClientNetworking.Models.Matchmaking;
using ClientNetworking.Models.Query;
using RainierClientSDK;
using RainierClientSDK.source.Player;
using SharedLogicUtils.DataTypes;
using SharedLogicUtils.source.Services.Query.Contexts;
using SharedLogicUtils.source.Services.Query.Responses;
using SharedSDKUtils;
using Omukade.Cheyenne.Encoding;
using Omukade.Cheyenne.Matchmaking;

namespace Omukade.Cheyenne
{
    public class GameServerCore
    {
        static GameServerCore()
        {
            // Default behavior is log only WARN | ERROR to console. Creating an instance sets the global logger to this instance, and buffers log messages.
            // RainierServiceLogger.instance = new RainierServiceLogger(RainierServiceLogger.LogLevel.WARNING | RainierServiceLogger.LogLevel.ERROR, _ => RainierServiceLogger.Clear());

            // Setting CardCache.isServer causes it to bypass a mutex lock that is unneeded in the single-thread Cheyenne
            CardCache.isServer = true;
        }

        static bool rainerAlreadyPatched;
        public static bool GameRulesAlreadyLoaded => precompressedGameRules != null;

        /// <summary>
        /// Globally configures all messages to be sent as JSON rather than autodetecting if a message is supported by the flatbuffer codec. Defaults to false.
        /// </summary>
        public static bool ForceJsonForAllSentMessages = false;

        static readonly JsonSerializerSettings jsonResolverToDealWithEnums = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Auto,
            ContractResolver = DeserializeResolver.settings.ContractResolver,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate,
            Converters = new List<JsonConverter>() { new Newtonsoft.Json.Converters.StringEnumConverter(new Newtonsoft.Json.Serialization.DefaultNamingStrategy(), allowIntegerValues: true) }
        };

        public static Dictionary<string, bool> FeatureFlags = new Dictionary<string, bool>(0);

        private static readonly byte[] DUMMY_EMPTY_SIGNATURE = Array.Empty<byte>();
        private static byte[] precompressedGameRules;
        const int PLAYER_ONE = 0, PLAYER_TWO = 1;

        public ConfigSettings config { get; init; }
        public event Action<string, Exception?>? ErrorHandler;

        internal Dictionary<string, PlayerMetadata> UserMetadata = new Dictionary<string, PlayerMetadata>(2);

        internal Dictionary<string, GameStateOmukade> ActiveGamesById = new Dictionary<string, GameStateOmukade>(10);
        Dictionary<uint, BasicMatchmakingSwimlane> MatchmakingSwimlanes;

        private ImplementedExpandedCardsV1 expandedImplementedCards_ChecksumMatchesResponse;
        private ImplementedExpandedCardsV1 expandedImplementedCards_FullDataResponse;

        public GameServerCore(ConfigSettings settings)
        {
            this.config = settings;

            // Prepare compressed Implemented Cards message
            IEnumerable<string> implementedCardNames;
            
            if(this.config.EnableReportingAllImplementedCards)
            {
                implementedCardNames = Directory.GetDirectories(config.CardDataDirectory)
                .Where(dir => !dir.StartsWith('.'))
                .SelectMany(dir => Directory.GetFiles(dir, "*.json"))
                .Select(fname => Path.GetFileNameWithoutExtension(fname));
            }
            else
            {
                implementedCardNames = new string[1] { "feature-disabled" };
            }

            this.expandedImplementedCards_FullDataResponse = new ImplementedExpandedCardsV1()
            {
                ImplementedCardNames = implementedCardNames
            };
            this.expandedImplementedCards_ChecksumMatchesResponse = new ImplementedExpandedCardsV1 { Checksum = this.expandedImplementedCards_FullDataResponse.Checksum };

            BasicMatchmakingSwimlane standardSwimlane = new(GameplayType.Casual, GameMode.Standard, SwimlaneCompletedMatchMakingCallback);
            BasicMatchmakingSwimlane expandedSwimlane = new(GameplayType.Casual, GameMode.Expanded, SwimlaneCompletedMatchMakingCallback);
            if (config.OneGameModeMatch)
            {
                standardSwimlane = expandedSwimlane;
            }
            MatchmakingSwimlanes = new(2)
            {
                { BasicMatchmakingSwimlane.GetFormatKey(GameplayType.Casual, GameMode.Standard), standardSwimlane },
                { BasicMatchmakingSwimlane.GetFormatKey(GameplayType.Casual, GameMode.Expanded), expandedSwimlane },
            };

            BanPlayers.OnUpdate += BanPlayers_OnUpdate;
        }

        private void BanPlayers_OnUpdate(object? sender, EventArgs e)
        {
            foreach (var item in UserMetadata)
            {
                var player = item.Value;
                if (BanPlayers.IsBan(player.PlayerDisplayName))
                {
                    ForcePlayerToQuit(player);
                }
            }
        }

        private void SwimlaneCompletedMatchMakingCallback(IMatchmakingSwimlane swimlane, PlayerMetadata player1, PlayerMetadata player2)
        {
            this.StartGameBetweenTwoPlayers(player1, player2);
        }

        public static void PatchRainier()
        {
            if (rainerAlreadyPatched) return;
            HarmonyLib.Harmony harmony = new("OmukadeCheyenne");
            harmony.PatchAll();

            rainerAlreadyPatched = true;
        }
        
        public static void RefreshSharedGameRules(ConfigSettings settings)
        {
            GameDataCache.Clear();
            GameDataCacheMessage gameDataCache = GetBakedGameDataForMode(settings, GameMode.Base);
            GameDataCache.AddCachedObjects(gameDataCache);
            precompressedGameRules = MessageExtensions.PrecompressObject(gameDataCache);

            LoadFeatureFlags(settings);
        }

        private static void LoadFeatureFlags(ConfigSettings config)
        {
            Dictionary<string, Dictionary<string, bool>> rawNestedDoc = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(File.ReadAllText(Path.Combine(config.CardDataDirectory, "feature-flags.json")))!;
            FeatureFlags = rawNestedDoc["featureMap"];
            FeatureFlags[FeatureFlag.RuleChanges2023] = Program.config.EnableRuleChanges2023Patch;
            FeatureFlags[FeatureFlag.UsePassiveEffectsStoredOnCardEntity] = false;
            Console.WriteLine("RuleChanges2023: " + FeatureFlags["RuleChanges2023"].ToString());
        }

        private static void EnsurePlayerDataForMatch(PlayerMetadata playerData)
        {
            if (playerData.CurrentDeck == null)
            {
                throw new InvalidOperationException("Cannot start or propose a game; decklist not sent yet.");
            }
            if (playerData.PlayerOutfit == null)
            {
                throw new InvalidOperationException("Cannot start or propose a game; player outfit not sent yet.");
            }
        }

        static GameDataCacheMessage GetBakedGameDataForMode(ConfigSettings config, GameMode mode)
        {
            GameDataCacheMessage gdcm = JsonConvert.DeserializeObject<GameDataCacheMessage>(File.ReadAllText(Path.Combine(config.CardDataDirectory, $"game-data-{mode.ToString()}.json")), DeserializeResolver.settings)!;
            return gdcm;
        }

        internal static void SendPacketToClient(PlayerMetadata? client, object payload, bool isPrecompressed = false)
        {
            if (client == null)
            {
                Program.ReportError(new InvalidOperationException("Can't send message to client - client is null (probably already closed)"));
                return;
            }

            if (client.PlayerConnectionHelens != null)
            {
                ClientNetworking.SerializationFormat formatToUse;
                if (ForceJsonForAllSentMessages)
                {
                    formatToUse = ClientNetworking.SerializationFormat.JSON;
                }
                else
                {
                    bool isSupportedByFlatbufferEncoder = ClientNetworking.Flatbuffers.Encoders.Map.ContainsKey(payload.GetType());
                    formatToUse = isSupportedByFlatbufferEncoder ? ClientNetworking.SerializationFormat.FlatBuffers : ClientNetworking.SerializationFormat.JSON;
                }

                client.PlayerConnectionHelens.SendMessageEnquued_EXPERIMENTAL(payload, formatToUse);
            }
        }

        public void HandleSupplementalDataMessage(SupplementalDataMessageV2 sdm, PlayerMetadata dataForConnection)
        {
            if (sdm.CurrentRegion != null) dataForConnection.PlayerCurrentRegion = sdm.CurrentRegion;
            if (sdm.PlayerId != null)
            {
                if (sdm.PlayerId == dataForConnection.PlayerId)
                {
                    // ignore retransmissions of same player ID.
                }
                else if (dataForConnection.PlayerId != null)
                {
                    throw new InvalidOperationException("Trying to change player ID of a player that already sent a different player ID.");
                }
                else
                {
                    dataForConnection.PlayerId = sdm.PlayerId;
                    if (UserMetadata.TryGetValue(sdm.PlayerId, out PlayerMetadata existingPlayer))
                    {
                        this.ErrorHandler?.Invoke($"WARN: Trampling already-existing player metadata for PID {sdm.PlayerId} - display name {existingPlayer.PlayerDisplayName ?? "[no displayname]"}", null);
#warning Due to the async nature of websockets, this might get messy.
                        existingPlayer.PlayerConnectionHelens.DisconnectClientImmediately();
                        existingPlayer.PlayerId = null; // FIXME: hack so we don't try to double-dispose of this player.
                    }
                    UserMetadata[sdm.PlayerId] = dataForConnection;
                }
            }
            if (sdm.PlayerDisplayName != null) dataForConnection.PlayerDisplayName = sdm.PlayerDisplayName;
            if (sdm.DeckInformation != null)
            {
                dataForConnection.CurrentDeck = sdm.DeckInformation;
            }
            if (sdm.OutfitInformation != null)
            {
                dataForConnection.PlayerOutfit = sdm.OutfitInformation;
            }
        }

        /// <summary>
        /// Forcibly adds a player's metadata to the list of known players, without needing an SDM message. Used only for unit testing purposes where this information is pre-baked.
        /// </summary>
        /// <param name="dataForConnection"></param>
        public void ForciblyAddMetadataToKnownPlayersList(PlayerMetadata dataForConnection)
        {
            UserMetadata[dataForConnection.PlayerId] = dataForConnection;
        }

        void HandleRainierGameMessage(PlayerMetadata player, GameMessage gm)
        {
            if (player.CurrentGame == null)
            {
                throw new InvalidOperationException("Cannot process received game message; no gamestate associated with user.");
            }
            if (gm.message == null)
            {
                throw new ArgumentNullException("GameMessage payload is null");
            }

            // OfflineAdapter::ReceiveOperation
            ServerMessage? smg = FasterJson.FastDeserializeFromBytes<ServerMessage>(gm.message);
            if (smg == null)
            {
                throw new ArgumentNullException("GameMessage's SMG is null");
            }

            GameStateOmukade currentGame = (GameStateOmukade) player.CurrentGame;
            PlayerMetadata? opponentPlayerData = currentGame.player1metadata?.PlayerId == player.PlayerId ? currentGame.player2metadata : currentGame.player1metadata;

            if(smg.messageType is MessageType.ChangeCoinState or MessageType.ChangeDeckOrder)
            {
                throw new InvalidOperationException($"Player {player.PlayerDisplayName ?? "[null]"} sent the single-player-only cheat operation {smg.messageType}.");
            }

            switch (smg.messageType)
            {
                case MessageType.MatchOperation:
                case MessageType.MatchInput:
                case MessageType.MatchInputUpdate:
                // case MessageType.ChangeCoinState:
                // case MessageType.ChangeDeckOrder:
                    OfflineAdapter.ReceiveOperation(player.PlayerId, currentGame, smg);
                    break;
                case MessageType.SendEmote:
                    if (opponentPlayerData?.MuteAvatarEmote == true)
                    {
                        break;
                    }
                    string? rawPayload = smg.compressedValue == null ? null : Compression.Unzip(smg.compressedValue);
                    if (rawPayload == null) throw new ArgumentNullException("Emote payload is null");

                    string? emoteName = JsonConvert.DeserializeObject<string>(rawPayload);

                    if (emoteName == null) throw new ArgumentNullException("Cannot send a null emote");

                    foreach (SharedSDKUtils.PlayerInfo playerInGame in currentGame.playerInfos)
                    {
                        // Don't send our own emote to ourselves
                        if (playerInGame.playerID == player.PlayerId) continue;

                        SendPacketToClient(UserMetadata.GetValueOrDefault(playerInGame.playerID), new ServerMessage(MessageType.SendEmote, emoteName, playerInGame.playerID, smg.operationID, currentGame.matchId).AsPlayerMessage());
                    }
                    break;
                case MessageType.MatchReadyTimeOut:
                    // concede this player
                    ForcePlayerToQuit(player);
                    break;
                case MessageType.OpponentMatchTimeOut:
                case MessageType.OpponentOperationTimeOut:
                    // concede the opponent
                    PlayerMetadata? opponentPlayerData = currentGame.player1metadata?.PlayerId == player.PlayerId ? currentGame.player2metadata : currentGame.player1metadata;
                    if (opponentPlayerData != null)
                    {
                        ForcePlayerToQuit(player);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Player {player.PlayerDisplayName ?? "[null]"} tried to timeout their opponent via {smg.messageType}, but the opponent is null.");
                    }
                    break;
                case MessageType.MuteMyAvatarEmote:
                case MessageType.MuteOpponentAvatarEmote:
                    player.MuteAvatarEmote = !player.MuteAvatarEmote;
                    break;
                default:
                    throw new NotImplementedException($"Unsupported Game Message type received from client :: {smg.messageType}");
            }

            if (currentGame.CurrentOperation.workingBoard.isGameOver)
            {
                foreach (SharedSDKUtils.PlayerInfo playerInGame in currentGame.playerInfos)
                {
                    if (UserMetadata.TryGetValue(playerInGame.playerID, out PlayerMetadata playerInGameMetadata))
                    {
                        playerInGameMetadata.CurrentGame = null;
                        ActiveGamesById.Remove(currentGame.matchId);
                    }
                }
            }
        }

        public void HandleRainerMessage(PlayerMetadata player, object message)
        {
            if (message is BeginMatchmaking bm)
            {
                EnsurePlayerDataForMatch(player);
                MatchmakingContext mmc = FasterJson.FastDeserializeFromBytes<MatchmakingContext>(bm.context);

                if(mmc == null)
                {
                    throw new ArgumentNullException("Tried to begin matchmaking with a null " + nameof(MatchmakingContext));
                }

                if (BanPlayers.IsBan(player.PlayerDisplayName))
                {
                    SendPacketToClient(player, new MatchmakingCancelled(bm.txid));
                    return;
                }

                uint swimlaneKey = BasicMatchmakingSwimlane.GetFormatKey(GameplayType.Casual, mmc.gameMode);

                if(this.MatchmakingSwimlanes.TryGetValue(swimlaneKey, out BasicMatchmakingSwimlane queueToUse))
                {
                    queueToUse.EnqueuePlayer(player);
                }
                else
                {
                    throw new ArgumentOutOfRangeException($"Tried to begin matching with a gamemode that isn't supported - {mmc.gameMode}");
                }
            }
            else if (message is CancelMatchmaking cm)
            {
                RemovePlayerFromAllMatchmaking(player);
                SendPacketToClient(player, new MatchmakingCancelled(cm.txid));
            }
            else if (message is GameMessage gm)
            {
                HandleRainierGameMessage(player, gm);
            }
            else if (message is ProposeDirectMatch pdm)
            {
                if(pdm.targetAccountId?.accountId == null)
                {
                    throw new ArgumentException("Received " + nameof(ProposeDirectMatch) + " with null accountId");
                }

                EnsurePlayerDataForMatch(player);

                if (!UserMetadata.TryGetValue(pdm.targetAccountId.accountId, out PlayerMetadata targetPlayerData))
                {
                    Program.ReportUserError($"Cannot battle with offline player - {player.PlayerDisplayName} vs {pdm.targetAccountId.accountId}", null);

                    RemovePlayerFromAllMatchmaking(player);

                    RainierResponse errorResponse = new RainierResponse()
                    {
                        error = 42055,
                        message = "Cannot battle with offline player."
                    };
                    MatchmakingDenied denied = new MatchmakingDenied(pdm.txid, JsonConvert.SerializeObject(errorResponse), 16100);
                    SendPacketToClient(player, denied);
                    return;
                    // throw new ArgumentException("Sending a match request to an offline/non-existent player.");
                }

                player.DirectMatchCurrentlySendingTo = pdm.targetAccountId.accountId;
                player.DirectMatchMakingToken = Guid.NewGuid().ToString();
                player.DirectMatchMakingTransactionToken = pdm.txid;
                targetPlayerData.DirectMatchCurrentlyReceivingFrom ??= new HashSet<string>(1);
                targetPlayerData.DirectMatchCurrentlyReceivingFrom.Add(player.PlayerId!);

                // The SignedMatchContext or it's data for the sender doesn't seem to matter, just that the client has one, as it does a null check before allowing the CancelDirectMatch to be sent.
                SignedMatchContext smcForSender = new SignedMatchContext(player.DirectMatchMakingToken, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), matchContext: null, signature: DUMMY_EMPTY_SIGNATURE);
                SendPacketToClient(player, smcForSender);

                FriendDirectMatchContext fdmc = ClientNetworking.Util.Utils.FromJsonBytes<FriendDirectMatchContext>(pdm.context);
                MatchSharedContext msc = new MatchSharedContext { gameMode = fdmc.gameMode, matchTime = fdmc.matchTime, useAutoSelect = fdmc.useAutoSelect, useMatchTimer = fdmc.useMatchTimer, useOperationTimer = fdmc.useOperationTimer };
                byte[] mscBytes = ClientNetworking.Util.Utils.ToJsonBytes(msc);
                SendPacketToClient(targetPlayerData, new DirectMatchInvitation(sourceAccountId: player.PlayerId, mmToken: player.DirectMatchMakingToken, issuedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), sharedContext: mscBytes, signature: DUMMY_EMPTY_SIGNATURE, clientVersion: string.Empty, timeOffsetSeconds: 0L));
            }
            else if (message is CancelDirectMatch cdm)
            {
                // I think this can only be sent by the initiating player
                CancelDirectMatchmakingInitatedBy(player);
            }
            else if (message is AcceptDirectMatch adm)
            {
                if (adm.invitation?.sourceAccountId == null)
                {
                    throw new ArgumentException("Received " + nameof(AcceptDirectMatch) + " with null accountId");
                }

                EnsurePlayerDataForMatch(player);

                if (!UserMetadata.TryGetValue(adm.invitation.sourceAccountId, out PlayerMetadata initiatingPlayerMetadata))
                {
                    throw new ArgumentException("Trying to accept a direct match for an offline player.");
                }

                if (initiatingPlayerMetadata.DirectMatchCurrentlySendingTo != player.PlayerId)
                {
                    throw new ArgumentException("Trying to accept a direct match against a player matchmaking a different/no opponent.");
                }
                if (adm.invitation.mmToken != initiatingPlayerMetadata.DirectMatchMakingToken)
                {
                    throw new ArgumentException("Trying to start a direct match against a player with a different matchmaking token.");
                }

                initiatingPlayerMetadata.DirectMatchMakingTransactionToken = default;
                initiatingPlayerMetadata.DirectMatchMakingToken = default;
                initiatingPlayerMetadata.DirectMatchCurrentlySendingTo = default;
                player.DirectMatchCurrentlyReceivingFrom = default;

                // TODO: send the matchaccepted message to the initiating player
                StartGameBetweenTwoPlayers(initiatingPlayerMetadata, player);
            }
        }

        public void HandleGetSupportedExpandedCards(PlayerMetadata? player, GetImplementedExpandedCardsV1 giecV1)
        {
            if (giecV1.Checksum == this.expandedImplementedCards_FullDataResponse.Checksum)
            {
                SendPacketToClient(player, expandedImplementedCards_ChecksumMatchesResponse);
            }
            else
            {
                SendPacketToClient(player, expandedImplementedCards_FullDataResponse);
            }
        }

        private void RemovePlayerFromAllMatchmaking(PlayerMetadata playerData)
        {
            string? concernedPlayerId = playerData.PlayerId;
            if (concernedPlayerId == null) return;

            foreach (BasicMatchmakingSwimlane swimlane in this.MatchmakingSwimlanes.Values)
            {
                swimlane.RemovePlayerFromMatchmaking(playerData);
            }
            foreach (PlayerMetadata pmd in UserMetadata.Values)
            {
                // Cancel players that received a match from this player
                if (pmd.DirectMatchCurrentlySendingTo == playerData.PlayerId)
                {
                    CancelDirectMatchmakingInitatedBy(pmd);
                }

                // Cancel players initiating a match against this player
                if (playerData.PlayerId != null)
                {
                    pmd.RemovePlayerFromDirectMatchReceivingFrom(playerData.PlayerId);
                }
            }
        }

        private void CancelDirectMatchmakingInitatedBy(PlayerMetadata initiatingPlayer)
        {
            initiatingPlayer.DirectMatchCurrentlySendingTo = null;
            SendPacketToClient(initiatingPlayer, new MatchmakingCancelled(txid: initiatingPlayer.DirectMatchMakingTransactionToken));

            foreach (PlayerMetadata pmd in UserMetadata.Values)
            {
                if (pmd.DirectMatchCurrentlyReceivingFrom != null && pmd.DirectMatchCurrentlyReceivingFrom.Contains(initiatingPlayer.PlayerId!))
                {
                    SendPacketToClient(pmd, new ClientNetworking.Models.Matchmaking.CancellationToken(initiatingPlayer.DirectMatchMakingToken, issuedAt: default, signature: DUMMY_EMPTY_SIGNATURE));
                    pmd.RemovePlayerFromDirectMatchReceivingFrom(initiatingPlayer.PlayerId!);
                }
            }

            initiatingPlayer.DirectMatchMakingToken = default;
            initiatingPlayer.DirectMatchMakingTransactionToken = default;
        }

        /// <summary>
        /// Converts a player's deckinfo to a decklist suitable for bootstrapping a Rainier game.
        /// </summary>
        /// <param name="playerInfo">The player containing deck information to flatten.</param>
        /// <param name="allCardsThatAppear">A hashset that will be populated with all cards found in the decklist.</param>
        /// <param name="deterministic">If the decklist should be prepared in a deterministic manner. Minor performance impact; not usually needed except testing where specific deck ordering is required.</param>
        /// <returns>A flattened list of all cards in the deck.</returns>
        private static List<string> DeckinfoToDecklist(SharedSDKUtils.PlayerInfo playerInfo, HashSet<string> allCardsThatAppear, bool deterministic)
        {
            List<string> deck = new(60);

            IEnumerable<string> deckInfoKeys = playerInfo.settings.deckInfo.cards.Keys;

            if(deterministic)
            {
                deckInfoKeys = deckInfoKeys.OrderBy(k => k);
            }

            foreach (string card in deckInfoKeys)
            {
                allCardsThatAppear.Add(card);
                int cardCount = playerInfo.settings.deckInfo.cards[card];
                for (int i = 0; i < cardCount; i++)
                {
                    deck.Add(card);
                }
            }

            return deck;
        }

        internal string StartGameBetweenTwoPlayers(PlayerMetadata playerOneMetadata, PlayerMetadata playerTwoMetadata)
        {
            if(config.DebugFixedRngSeed)
            {
                Patching.MatchOperationGetRandomSeedIsDeterministic.ResetRng();
            }

            GameStateOmukade gameState = new GameStateOmukade(parentServerInstance: this)
            {
                matchId = Guid.NewGuid().ToString(),
            };

            // By default, the game rules always have Player 1 pick the opening coin flip. To "fix" this, crudely randomize the player order.
            if (!config.DisablePlayerOrderRandomization)
            {
                int msForPlayerRandomization = DateTime.UtcNow.Millisecond;
                if (msForPlayerRandomization % 2 == 0)
                {
                    PlayerMetadata p1scratch = playerOneMetadata;
                    playerOneMetadata = playerTwoMetadata;
                    playerTwoMetadata = p1scratch;
                }
            }

            gameState.player1metadata = playerOneMetadata;
            gameState.playerInfos[PLAYER_ONE].playerName = playerOneMetadata.PlayerDisplayName;
            gameState.playerInfos[PLAYER_ONE].playerID = playerOneMetadata.PlayerId;
            gameState.playerInfos[PLAYER_ONE].sentPlayerInfo = true;
            gameState.playerInfos[PLAYER_ONE].settings = new PlayerSettings { gameMode = GameMode.Standard, gameplayType = GameplayType.Friend, useAutoSelect = false, useMatchTimer = config.EnableGameTimers, useOperationTimer = config.EnableGameTimers, matchMode = MatchMode.Standard, matchTime = config.DebugGameTimerTime ?? 1500 };
            gameState.playerInfos[PLAYER_ONE].settings.name = playerOneMetadata.PlayerDisplayName;
            gameState.playerInfos[PLAYER_ONE].settings.outfit = playerOneMetadata.PlayerOutfit;
            DeckInfo.ImportMetadata(ref gameState.playerInfos[PLAYER_ONE].settings.deckInfo, playerOneMetadata.CurrentDeck!.Value.metadata, e => throw new Exception($"Error parsing deck for {playerOneMetadata.PlayerDisplayName}: {e}"));
            gameState.playerInfos[PLAYER_ONE].settings.deckInfo.cards = new Dictionary<string, int>(playerOneMetadata.CurrentDeck.Value.items);

            gameState.player2metadata = playerTwoMetadata;
            gameState.playerInfos[PLAYER_TWO].playerName = playerTwoMetadata.PlayerDisplayName;
            gameState.playerInfos[PLAYER_TWO].playerID = playerTwoMetadata.PlayerId;
            gameState.playerInfos[PLAYER_TWO].sentPlayerInfo = true;
            gameState.playerInfos[PLAYER_TWO].settings = new PlayerSettings { gameMode = GameMode.Standard, gameplayType = GameplayType.Friend, useAutoSelect = false, useMatchTimer = config.EnableGameTimers, useOperationTimer = config.EnableGameTimers, matchMode = MatchMode.Standard, matchTime = config.DebugGameTimerTime ?? 1500 };
            gameState.playerInfos[PLAYER_TWO].settings.outfit = playerTwoMetadata.PlayerOutfit;
            DeckInfo.ImportMetadata(ref gameState.playerInfos[PLAYER_TWO].settings.deckInfo, playerTwoMetadata.CurrentDeck!.Value.metadata, e => throw new Exception($"Error parsing deck for {playerTwoMetadata.PlayerDisplayName}: {e}"));
            gameState.playerInfos[PLAYER_TWO].settings.name = playerTwoMetadata.PlayerDisplayName;
            gameState.playerInfos[PLAYER_TWO].settings.deckInfo.cards = new Dictionary<string, int>(playerTwoMetadata.CurrentDeck.Value.items);

            ActiveGamesById.Add(gameState.matchId, gameState);
            playerOneMetadata.CurrentGame = gameState;
            playerTwoMetadata.CurrentGame = gameState;

            // Flatten the decklists

            // OfflineAdapter.CreateGame
            HashSet<string> allCardsThatAppear = new();
            bool deterministicDecklistPrep = config.DebugEnableDeterministicDecklistPreperation;
            List<string> deckListP1 = DeckinfoToDecklist(gameState.playerInfos[PLAYER_ONE], allCardsThatAppear, deterministicDecklistPrep);
            List<string> deckListP2 = DeckinfoToDecklist(gameState.playerInfos[PLAYER_TWO], allCardsThatAppear, deterministicDecklistPrep);

            // Prepare Caches
            List<CardSource> allCardData = GetCardData(allCardsThatAppear);
            CardCache.Add(allCardData);
            byte[] prebakedCardData = MessageExtensions.PrecompressObject(allCardData);

            MatchOperation bootstrapOperation = new MatchOperation(gameState.matchId, gameMode: GameMode.Standard, GameplayType.Offline,
            gameState.playerInfos[PLAYER_ONE].playerID, gameState.playerInfos[PLAYER_ONE].playerName,
            deckListP1.ToArray(), p1MatchTime: gameState.playerInfos[PLAYER_ONE].settings.matchTime, p1UseMatchTimer: gameState.playerInfos[PLAYER_ONE].settings.useMatchTimer, p1UseOperationTimer: gameState.playerInfos[PLAYER_ONE].settings.useOperationTimer, gameState.playerInfos[PLAYER_ONE].settings.useAutoSelect,

            gameState.playerInfos[PLAYER_TWO].playerID, gameState.playerInfos[PLAYER_TWO].playerName,
            deckListP2.ToArray(), p2MatchTime: gameState.playerInfos[PLAYER_TWO].settings.matchTime, p2UseMatchTimer: gameState.playerInfos[PLAYER_TWO].settings.useMatchTimer, p2UseOperationTimer: gameState.playerInfos[PLAYER_TWO].settings.useOperationTimer, gameState.playerInfos[PLAYER_TWO].settings.useAutoSelect,
            MatchMode.Standard, prizeCount: config.DebugPrizesPerPlayer ?? 6, debug: gameState.CanUseDebug,
            featureFlags: FeatureFlags);

            bootstrapOperation.QueuePlayerOperation();
            bootstrapOperation.Handle();
            gameState.CurrentOperation = bootstrapOperation;
            gameState.PreviousOperations.Add(bootstrapOperation);

            // Send JoinGame to each player
            foreach (SharedSDKUtils.PlayerInfo currentPlayerInfo in gameState.playerInfos)
            {
                PlayerMetadata currentPlayerMetadata = currentPlayerInfo.playerID == playerOneMetadata.PlayerId ? playerOneMetadata : playerTwoMetadata;
                SendPacketToClient(currentPlayerMetadata, new JoinGame(txid: default, mmToken: default, region: UserMetadata[currentPlayerInfo.playerID].PlayerCurrentRegion, ticket: gameState.matchId));

                MatchCreated matchCreated = new MatchCreated()
                {
                    gameDataBuildDate = "todo",
                    cardDataBuildDate = "todo",
                    matchId = gameState.matchId,
                    players = gameState.playerInfos.Select(
                    pi => new PlayerDetails(playerId: pi.playerID, playerEntityId: gameState.GetPlayerEntityId(pi.playerID), playerName: pi.playerName, pi.playerExp, pi.settings.deckInfo, pi.settings.outfit)).ToArray(),
                    readyUpTimeout = 60,
                    offlineMatch = false,
                    useAI = false,
                    clearCache = true,
                    featureSet = FeatureFlags
                };

                ServerMessage prebakedRulesMessage = new ServerMessage(MessageType.GameData, string.Empty, currentPlayerInfo.playerID, bootstrapOperation.operationID, gameState.matchId)
                {
                    compressedValue = precompressedGameRules
                };

                ServerMessage prebakedCardCacheMessage = new ServerMessage(MessageType.CardCache, string.Empty, currentPlayerInfo.playerID, bootstrapOperation.operationID, gameState.matchId)
                {
                    compressedValue = prebakedCardData
                };
                

                SendPacketToClient(currentPlayerMetadata, new ServerMessage(MessageType.MatchCreated, matchCreated, "SERVER", bootstrapOperation.operationID, gameState.matchId).AsPlayerMessage());
                SendPacketToClient(currentPlayerMetadata, prebakedRulesMessage.AsPlayerMessage(), isPrecompressed: true);
                SendPacketToClient(currentPlayerMetadata, prebakedCardCacheMessage.AsPlayerMessage(), isPrecompressed: true);
            }

            ResolveOperation(gameState, bootstrapOperation);
            return gameState.matchId;
        }

        private static ServerMessage CreatePlayerSelectionHelper(MatchOperation mo, PlayerSelection ps, string playerId, GameState state)
        {
            bool forPlayer1 = ps.ForPlayer1();
            if ((forPlayer1 && mo.workingBoard.player1.ownerPlayerId == playerId) || (!forPlayer1 && mo.workingBoard.player2.ownerPlayerId == playerId))
            {
                PlayerSelectionInfo psi = new PlayerSelectionInfo(ps.CopySelection(), mo, isOffline: false);
                return new ServerMessage(MessageType.MatchInput, psi, playerId, mo.operationID, mo.matchID);
            }
            else
            {
                OpponentSelectionInfo osi = new OpponentSelectionInfo(ps, mo, isOffline: false);
                return new ServerMessage(MessageType.OPMatchInput, osi, playerId, mo.operationID, mo.matchID);
            }
        }

        internal bool ResolveOperation(GameStateOmukade currentGameState, MatchOperation currentOperation, bool isInputUpdate = false)
        {
            switch (currentOperation.status)
            {
                case OperationStatus.Error:
                    string invalidOperationErrorMessage = $"Error performing operation {currentOperation.operationID} on game {currentGameState.matchId} ({string.Join(" vs ", currentGameState.playerInfos.Select(p => p.playerName))})";
                    this.OnErrorHandler(invalidOperationErrorMessage, null);
                    return false;

                case OperationStatus.WaitingForInput:
                    MatchOperationResult morToSendToClient = new MatchOperationResult(currentOperation, deltaIndex: currentOperation.lastSentDeltaIndex, isInputUpdate: isInputUpdate);

                    foreach (SharedSDKUtils.PlayerInfo currentPlayer in currentGameState.playerInfos)
                    {
                        ServerMessage morSmg = new ServerMessage(MessageType.MatchOperation, morToSendToClient, currentPlayer.playerID, morToSendToClient.operationID, currentGameState.matchId);
                        PlayerMessage morPmg = morSmg.AsPlayerMessage();
                        SendPacketToClient(UserMetadata.GetValueOrDefault(currentPlayer.playerID), morPmg);

                        ServerMessage psiSmg = CreatePlayerSelectionHelper(currentOperation, currentOperation.GetPreviousSelection(), currentPlayer.playerID, currentGameState);
                        PlayerMessage psiPmg = psiSmg.AsPlayerMessage();
                        SendPacketToClient(UserMetadata.GetValueOrDefault(currentPlayer.playerID), psiPmg);
                    }

                    currentOperation.lastSentDeltaIndex = currentOperation.actionModifications.Count;
                    return true;
                default:
                    MatchOperationResult mor = new MatchOperationResult(currentOperation, deltaIndex: currentOperation.lastSentDeltaIndex, isInputUpdate: isInputUpdate);
                    byte[] precompressedMor = MessageExtensions.PrecompressObject(mor, compressionLevel: 9);

                    /*bool gameOver = false;
                    // IDK which one is the authoritative "game over", so just try them all. So far, all 3 have been tripped when a game is over.
                    if(mor.actionModifications?.Any(a => a is EndGameModification) == true)
                    {
                        Console.WriteLine($"Game Over (by Contains EGM) - {currentOperation.matchID}");
                        gameOver = true;
                    }
                    if(currentOperation.workingBoard.isGameOver)
                    {
                        Console.WriteLine($"Game Over (by isGameOver property) - {currentOperation.matchID}");
                        gameOver = true;
                    }
                    if(currentOperation.workingBoard.IsGameOver())
                    {
                        Console.WriteLine($"Game Over (by IsGameOver method) - {currentOperation.matchID}");
                        gameOver = true;
                    }*/

                    foreach (SharedSDKUtils.PlayerInfo currentPlayer in currentGameState.playerInfos)
                    {
                        ServerMessage smg = new ServerMessage(MessageType.MatchOperation, string.Empty, currentPlayer.playerID, mor.operationID, currentGameState.matchId);
                        smg.compressedValue = precompressedMor;

                        PlayerMessage pmg = smg.AsPlayerMessage();
                        SendPacketToClient(UserMetadata.GetValueOrDefault(currentPlayer.playerID), pmg, isPrecompressed: true);
                    }

                    currentOperation.lastSentDeltaIndex = currentOperation.actionModifications.Count;
                    return true;
            }
        }

        public void PurgePlayerFromActivePlayers(PlayerMetadata playerData)
        {
            Console.WriteLine($"Player {playerData.PlayerDisplayName ?? "[no displayname]"} disconnected. Is in game: {playerData.CurrentGame != null}");

            ForcePlayerToQuit(playerData);

            RemovePlayerFromAllMatchmaking(playerData);
            UserMetadata.Remove(playerData.PlayerId!);
        }

        public void ForcePlayerToQuit(PlayerMetadata playerData)
        {
            // If the player is in a battle, forfit.
            if (playerData.CurrentGame != null)
            {
                string originPlayerEntityId = playerData.CurrentGame.GetPlayerEntityId(playerData.PlayerId!);
                PlayerOperation pop = new PlayerOperation(OperationType.TimeoutForceQuit, playerData.CurrentGame.matchId, originEntityID: originPlayerEntityId, actionGuid: "", targetID: "", BoardPos.Board, playerData.PlayerId);
                ServerMessage smg = new ServerMessage(MessageType.MatchOperation, pop, playerData.PlayerId, pop.operationID, playerData.CurrentGame.matchId);
                OfflineAdapter.ReceiveOperation(playerData.PlayerId, playerData.CurrentGame, smg);
            }
        }

        private List<CardSource> GetCardData(IEnumerable<string> cardsToFetch)
        {
            return cardsToFetch.Select(cardname =>
            {
                if (config.CardSourceOverridesEnable && config.CardSourceOverridesDirectory != null)
                {
                    string overrideFname = Path.Combine(config.CardSourceOverridesDirectory, cardname + ".json");
                    if (File.Exists(overrideFname))
                    {
                        Console.WriteLine("Using found CardData override for " + cardname);
                        return JsonConvert.DeserializeObject<CardSource>(File.ReadAllText(overrideFname), jsonResolverToDealWithEnums)!;
                    }
                }

                string setFolder = cardname.Split(new char[] { '_' }, 2)[0];
                string cardDataFilename = Path.Combine(config.CardDataDirectory, setFolder, cardname + ".json");
                if (!File.Exists(cardDataFilename))
                {
                    throw new ArgumentException($"Cannot fetch card data for card that doesn't exist - {cardname}", nameof(cardsToFetch));
                }
                string cardDataContents = File.ReadAllText(cardDataFilename);
                return JsonConvert.DeserializeObject<CardSource>(cardDataContents, DeserializeResolver.settings)!;
            })
            .ToList();
        }

        internal void OnErrorHandler(string message, Exception? e) => this.ErrorHandler?.Invoke(message, e);
    }
}
