using MatchLogic;
using Omukade.Cheyenne.ClientConnections;
using Omukade.Cheyenne.Encoding;
using Omukade.Cheyenne.Tests.Helpers;
using Omukade.Cheyenne.Tests.MultiInstanceStatic;
using ClientNetworking.Models.GameServer;
using ClientNetworking.Models.Matchmaking;
using ClientNetworking.Models.Query;
using RainierClientSDK;
using RainierClientSDK.source.Player;
using SharedLogicUtils.DataTypes;
using SharedSDKUtils;
using System.Data.Common;
using System.Text.RegularExpressions;

namespace Omukade.Cheyenne.Tests
{
    public class BasicGameTestsFullStack
    {
        struct StateOfStates
        {
            internal CardInfoStaticState cardInfoState;
            internal MatchServiceStaticState matchServiceStaticState;
            internal ClientHandlerStaticState clientHandlerState;

            public void ApplyAll()
            {
                this.cardInfoState.ApplyState();
                this.matchServiceStaticState.ApplyState();
                this.clientHandlerState.ApplyState();
            }


            public void SaveAll()
            {
                this.cardInfoState.SaveState();
                this.matchServiceStaticState.SaveState();
                this.clientHandlerState.SaveState();
            }

            public void InstantiateNewReferences()
            {
                this.cardInfoState.InstantiateNewReferences();
                this.matchServiceStaticState.InstantiateNewReferences();
                this.clientHandlerState.InstantiateNewReferences();
            }

            public StateApplier UseState() => new StateApplier(this);
        }

        struct StateApplier : IDisposable
        {
            private StateOfStates metastate;

            public StateApplier() { metastate = default; }
            public StateApplier(StateOfStates metastate)
            {
                this.metastate = metastate;
                metastate.ApplyAll();
            }


            public void Dispose()
            {
                metastate.SaveAll();
            }
        }

        GameServerCore gsc;
        DebugClientConnection p1connection;
        DebugClientConnection p2connection;

        StateOfStates p1state;
        StateOfStates p2state;

        Dictionary<string, StateOfStates> staticStates = new(2);

        public BasicGameTestsFullStack()
        {
            ConfigSettings cheyenneConfig = new ConfigSettings
            {
                CardDataDirectory = Path.Combine(AutoPAR.Rainier.RainierSharedDataHelper.GetSharedDataDirectory(), "PTCGL-CardDefinitions"),
                DisablePlayerOrderRandomization = true,
                DebugEnableDeterministicDecklistPreperation = true,
            };

            WhitelistedSerializeContractResolver.ReplaceContractResolvers();
            GameServerCore.RefreshSharedGameRules(cheyenneConfig);
            gsc = new GameServerCore(cheyenneConfig);

            Omukade.Cheyenne.Patching.MatchOperationGetRandomSeedIsDeterministic.Rng = new(11233453);

            this.p1connection = new DebugClientConnection();
            this.p1connection.Tag = new() { PlayerConnectionHelens = p1connection };

            this.p2connection = new DebugClientConnection();
            this.p2connection.Tag = new() { PlayerConnectionHelens = p2connection };

            // Do the clearing twice to make sure P1 + P2 each have different references so when one tries to clear, it doesn't impact the other player's data/perspective.
            p1state = default;
            p1state.clientHandlerState.accountId = "player1";
            p1state.InstantiateNewReferences();
            // CardCache + GameDataCache is shared with the server; IF YOU CLEAR THESE, THE SERVER BECOMES BRAINDEAD
            // CardCache.Clear();
            // GameDataCache.Clear();
            MatchService.CleanUpAllMatches();
            p1state.SaveAll();
            staticStates.Add("player1", p1state);

            p2state = default;
            p2state.clientHandlerState.accountId = "player2";
            p2state.InstantiateNewReferences();
            // CardCache + GameDataCache is shared with the server; IF YOU CLEAR THESE, THE SERVER BECOMES BRAINDEAD
            // CardCache.Clear();
            // GameDataCache.Clear();
            MatchService.CleanUpAllMatches();
            p2state.SaveAll();
            staticStates.Add("player2", p2state);
        }

        [Fact]
        public void FullStackPoc()
        {
            Dictionary<string, int> decklist = new() {
                                  {"swsh4_121",1},
                                  {"swsh1_128",2},
                                  {"swsh2_126",2},
                                  {"swsh1_148",2},
                                  {"swsh3_104",1},
                                  {"swsh1_178",4},
                                  {"swsh1_169",4},
                                  {"swsh2_154",3},
                                  {"swsh4_163",3},
                                  {"swsh1_179",4},
                                  {"swsh1_156",3},
                                  {"swsh1_171",1},
                                  {"swsh1_174",2},
                                  {"swsh3_159",2},
                                  {"swsh1_170",4},
                                  {"swsh1_183",4},
                                  {"ec_43",13},
                                  {"swsh1_138",1},
                                  {"swsh1_139",2},
                                  {"swsh3_160",2}
            };
            SendDeckDetailsForBothPlayers(decklist, decklist);

            string matchId = gsc.StartGameBetweenTwoPlayers(p1connection.Tag!, p2connection.Tag!);

            // Assert P1 is now being asked for coinflip choice. PlayerOrderRandomization is disabled, so P1 will always choose.
            GameStateDelta deltas = CompleteOpeningCoinflipForExpectedPlayer(PlayerNumber.Player1, coinflipResult: CoinFlipOutcome.Heads, wantToGoFirst: true);
            SetupPokemon(deltas, p1gofirst: true);
        }

        GameStateDelta CompleteOpeningCoinflipForExpectedPlayer(PlayerNumber expectedOpeningPlayer, CoinFlipOutcome coinflipResult, bool wantToGoFirst)
        {
            // Process each player's messages
            GameStateDelta deltas = ProcessInboundMessages();

            List<PlayerSelection> selectionsForOpeningPlayer = expectedOpeningPlayer switch
            {
                PlayerNumber.Player1 => deltas.p1selections,
                PlayerNumber.Player2 => deltas.p2selections,
                _ => throw new ArgumentOutOfRangeException(nameof(expectedOpeningPlayer), "playerNumber must be P1 or P2")
            };

            string playerId = gsc.ActiveGamesById.Values.First().playerInfos[expectedOpeningPlayer == PlayerNumber.Player1 ? 0 : 1].playerID;

            AssertCoinflipDialog(expectedOpeningPlayer, selectionsForOpeningPlayer, "pl_calling_starting_coin");

            PlayerSelection coinFlipSelection = selectionsForOpeningPlayer[0];
            ((TextSelection)coinFlipSelection.variableSelection).selectedIndex = 0;

            // Fix the coin
            gsc.ActiveGamesById.Values.First().CurrentOperation.workingBoard.SetCoinState(coinflipResult);

            GameMessage coinFlipSelectionResponse = new ServerMessage(MessageType.MatchInput, coinFlipSelection, accountID: playerId, operationID: coinFlipSelection.operationID, matchID: coinFlipSelection.matchID).AsGameMessage();
            gsc.HandleRainerMessage(p1connection.Tag!, coinFlipSelectionResponse);
            deltas = ProcessInboundMessages();

            // Reset the coin
            gsc.ActiveGamesById.Values.First().CurrentOperation.workingBoard.SetCoinState(CoinFlipOutcome.NONE);

            // Assert we are now being asked for first-vs-second
            selectionsForOpeningPlayer = expectedOpeningPlayer switch
            {
                PlayerNumber.Player1 => deltas.p1selections,
                PlayerNumber.Player2 => deltas.p2selections,
                _ => throw new ArgumentOutOfRangeException(nameof(expectedOpeningPlayer), "playerNumber must be P1 or P2")
            };

            AssertCoinflipDialog(expectedOpeningPlayer, selectionsForOpeningPlayer, "pl_calling_turn");
            PlayerSelection goFirstSelection = selectionsForOpeningPlayer[0];
            ((TextSelection)goFirstSelection.variableSelection).selectedIndex = wantToGoFirst ? 0 : 1;

            GameMessage goFirstSelectionResponse = new ServerMessage(MessageType.MatchInput, goFirstSelection, accountID: playerId, operationID: goFirstSelection.operationID, matchID: goFirstSelection.matchID).AsGameMessage();
            gsc.HandleRainerMessage(p1connection.Tag!, goFirstSelectionResponse);
            deltas = ProcessInboundMessages();

            return deltas;
        }

        private static void AssertCoinflipDialog(PlayerNumber expectedOpeningPlayer, List<PlayerSelection> selectionsForOpeningPlayer, string dialogLocalizationId)
        {
            Assert.Collection(selectionsForOpeningPlayer, coinflip =>
            {
                Assert.IsType<TextSelection>(coinflip.variableSelection);
                TextSelection tsel = (TextSelection)coinflip.variableSelection;
                Assert.Equal(expectedOpeningPlayer, tsel.SelectingPlayer);
                Assert.Equal(VariableSelectionSettings.SelectionMethod.Coin, tsel.selectionMethod);
                Assert.Equal(dialogLocalizationId, tsel.variableSelectionSettings.plLocID);
            });
        }

        private void SetupPokemon(GameStateDelta deltas, bool p1gofirst)
        {
            AssertSetupMessageBar(deltas.p1deltas, p1gofirst, p1gofirst);
            AssertSetupMessageBar(deltas.p2deltas, !p1gofirst, p1gofirst);

            List<CardEntity> validP1Basics;
            List<CardEntity> validP2Basics;

            using (p1state.UseState())
            {
                validP1Basics = p1state.matchServiceStaticState.activeMatches[0].boardState.p1Hand.Where(entity => MatchUserOperations.CanPlaceCard(entity.entityID, BoardPos.Player1Active))
                    .ToList();
            }

            using(p2state.UseState())
            {
                validP2Basics = p2state.matchServiceStaticState.activeMatches[0].boardState.p2Hand.Where(entity => MatchUserOperations.CanPlaceCard(entity.entityID, BoardPos.Player2Active))
                    .ToList();
            }

            Assert.NotEmpty(validP1Basics);
            Assert.NotEmpty(validP2Basics);

            // Place active Pokemon
            string activePokemonP1;
            string activePokemonP2;
            using (p1state.UseState())
            {
                CardEntity p1basic = validP1Basics.First();
                activePokemonP1 = p1basic.entityID;

                PlayerOperation placeCardOperation = MatchUserOperations.CreatePlaceCardOperation(p1basic.entityID, BoardPos.Player1Active, MatchService.activeMatches[0], ClientHandler.instance.accountID);
                SendOperation(placeCardOperation, MatchService.activeMatches[0]);
            }

            using (p2state.UseState())
            {
                CardEntity p2basic = validP2Basics.First();
                activePokemonP2 = p2basic.entityID;

                PlayerOperation placeCardOperation = MatchUserOperations.CreatePlaceCardOperation(p2basic.entityID, BoardPos.Player2Active, MatchService.activeMatches[0], ClientHandler.instance.accountID);
                SendOperation(placeCardOperation, MatchService.activeMatches[0]);
            }

            using (p1state.UseState())
            {
                GameStateDelta postBasicPlacementDeltas = ProcessInboundMessages();
                Assert.Equal(activePokemonP1, p1state.matchServiceStaticState.activeMatches[0].boardState.p1Active?.entityID);
            }

            using (p2state.UseState())
            {
                GameStateDelta postBasicPlacementDeltas = ProcessInboundMessages();
                Assert.Equal(activePokemonP2, p2state.matchServiceStaticState.activeMatches[0].boardState.p2Active?.entityID);
            }

            // Place benched pokemon, if any available
#warning TODO: Placing benched Pokemon isn't working right now; game thinks there are no valid Pokemon to bench.
            /*
            using(p1state.UseState())
            {
                validP1Basics = p1state.matchServiceStaticState.activeMatches[0].boardState.p1Hand.Where(entity => MatchUserOperations.CanPlaceCard(entity.entityID, BoardPos.Player1Bench))
                    .ToList();

                foreach(var validBasic in validP1Basics)
                {
                    p1state.ApplyAll();
                    PlayerOperation placeCardOperation = MatchUserOperations.CreatePlaceCardOperation(validBasic.entityID, BoardPos.Player1Bench, MatchService.activeMatches[0], ClientHandler.instance.accountID);
                    SendOperation(placeCardOperation, MatchService.activeMatches[0]);

                    ProcessInboundMessages();
                }
            }

            using (p2state.UseState())
            {
                validP2Basics = p2state.matchServiceStaticState.activeMatches[0].boardState.p2Hand.Where(entity => MatchUserOperations.CanPlaceCard(entity.entityID, BoardPos.Player2Bench))
                    .ToList();

                foreach (var validBasic in validP2Basics)
                {
                    p2state.ApplyAll();
                    PlayerOperation placeCardOperation = MatchUserOperations.CreatePlaceCardOperation(validBasic.entityID, BoardPos.Player2Bench, MatchService.activeMatches[0], ClientHandler.instance.accountID);
                    SendOperation(placeCardOperation, MatchService.activeMatches[0]);

                    ProcessInboundMessages();
                }
            }

            using(p1state.UseState())
            {
                Assert.Equal(validP1Basics, p1state.matchServiceStaticState.activeMatches[0].boardState.p1Bench, new EntityIdComparator());
            }

            using (p2state.UseState())
            {
                Assert.Equal(validP2Basics, p2state.matchServiceStaticState.activeMatches[0].boardState.p2Bench, new EntityIdComparator());
            }
            */
        }

        private void SendOperation(PlayerOperation pop, MatchInfo match)
        {
            if(match.matchState != 0 && match.myPlayerInfo.isMyTurn)
            {
                throw new InvalidOperationException("Unable to SendOperation while not idle.");
            }

            if(match.sendingMsg)
            {
                throw new InvalidOperationException("Cannot send message while the last one is still processing.");
            }

            match.sendingMsg = true;
            ServerMessage smg = new ServerMessage(MessageType.MatchOperation, pop, ClientHandler.instance.accountID, pop.operationID, match.matchID);
            match.matchState = MatchInfo.MatchState.Processing;
            match.ClearUsableCards();

            GameMessage smgGameMessage = smg.AsGameMessage();
            gsc.HandleRainerMessage(gsc.UserMetadata[ClientHandler.instance.accountID], smgGameMessage);
        }

        private static void AssertSetupMessageBar(List<ActionModification> actionDeltas, bool thisPlayerIsGoingFirst, bool p1goFirst)
        {
            Assert.Single(actionDeltas, setup => 
                setup is CreateUITriggerModification cutm && 
                (p1goFirst && thisPlayerIsGoingFirst ? (cutm.revealToP1 && !cutm.revealToP2) : (!cutm.revealToP1 && cutm.revealToP2)) && 
                cutm.uiTriggerDelta.triggerKey == ClientTrigger.ShowMessageBar && 
                cutm.uiTriggerDelta.body == (p1goFirst && thisPlayerIsGoingFirst ? "setup_pl_turn" : "setup_op_turn"));
        }

        GameStateDelta ProcessInboundMessages()
        {
            GameStateDelta rtrn = new GameStateDelta();

            using(p1state.UseState())
            {
                while (this.p1connection.MessagesToClient.TryDequeue(out object? message))
                {
                    var actionsFromThisMessage = ProcessSingleMessage(message, p1connection.Tag!);
                    foreach (var action in actionsFromThisMessage.actions) rtrn.p1deltas.Add(action);

                    if (actionsFromThisMessage.selection != null) rtrn.p1selections.Add(actionsFromThisMessage.selection);
                }
            }

            using(p2state.UseState())
            {
                while (this.p2connection.MessagesToClient.TryDequeue(out object? message))
                {
                    var actionsFromThisMessage = ProcessSingleMessage(message, p2connection.Tag!);
                    foreach (var action in actionsFromThisMessage.actions) rtrn.p2deltas.Add(action);

                    if (actionsFromThisMessage.selection != null) rtrn.p2selections.Add(actionsFromThisMessage.selection);
                }
            }

            return rtrn;
        }

        (IEnumerable<ActionModification> actions, PlayerSelection? selection) ProcessSingleMessage(object decodedMessage, PlayerMetadata pmd)
        {
            string currentPlayerAccountId = pmd.PlayerId!;

            /*if (decodedMessage is JoinGameResponse jgr)
            {
            // CardCache + GameDataCache is shared with the server; IF YOU CLEAR THESE, THE SERVER BECOMES BRAINDEAD
            // CardCache.Clear();
            // GameDataCache.Clear();
                MatchService._gameID = jgr.gameId;
            }*/
            // In RNOC, the client's JoinGame is morphed into a JoinGameResponse
            if (decodedMessage is JoinGame jg)
            {
                // CardCache + GameDataCache is shared with the server; IF YOU CLEAR THESE, THE SERVER BECOMES BRAINDEAD
                // CardCache.Clear();
                // GameDataCache.Clear();
                MatchService._gameID = jg.ticket;

                return (Enumerable.Empty<ActionModification>(), null);
            }
            else if (decodedMessage is PlayerMessage pm)
            {
                ServerMessage sm = ServerMessage.GetServerMessage(pm.message);

                if (sm.messageType == MessageType.MatchCreated)
                {
                    MatchCreated mc = sm.GetValueEfficently<MatchCreated>();
                    Console.WriteLine($"\tReceived MatchCreated; {mc.players[0].playerName} v. {mc.players[1].playerName}");

                    MatchService.cardDataBuildDate = mc.cardDataBuildDate;
                    MatchInfo match = new MatchInfo(currentPlayerAccountId, MatchService._gameID, mc);
                    MatchService.activeMatches.Add(match);

                    return (Enumerable.Empty<ActionModification>(), null);
                }
                else if (sm.messageType == MessageType.GameData)
                {
                    // Swallow gamerules, as GameDataCache is shared between client+server instances
                    // GameDataCache.AddCachedObjects(sm.GetValueEfficently<GameDataCacheMessage>());

                    return (Enumerable.Empty<ActionModification>(), null);
                }
                else if (sm.messageType == MessageType.CardCache)
                {
                    // Swallow cardcache, as CardCache is shared between client+server instances
                    // CardCache.Add(sm.GetValueEfficently<List<CardSource>>());

                    return (Enumerable.Empty<ActionModification>(), null);
                }
                else if (sm.messageType == MessageType.MatchRequestReady)
                {
                    Console.WriteLine("Asked if I'm ready; SEND READY");
                    throw new NotImplementedException("Not yet capable of sending MatchReady responses");
                }
                else if (sm.messageType == MessageType.MatchOperation)
                {
                    MatchOperationResult mor = sm.GetValueEfficently<MatchOperationResult>();

                    Console.WriteLine("Received MOR");

                    MatchInfo match = MatchService.activeMatches.Last();
                    match.sendingMsg = false;
                    if (mor.operationStatus != OperationStatus.WaitingForInput)
                    {
                        match.mySelectionID = string.Empty;
                    }

                    match.UpdateWithResults(mor);
                    match.UpdateMatchInfo(mor);
                    match.CheckIfGameIsOver(mor);
                    MatchService.PrivatizeMatchOperationResultIfNotPrivatized(match, mor);

                    List<ActionModification> deltasToPerform = match.DeltasToPerform(mor);
                    return (deltasToPerform, null);
                }
                else if (sm.messageType == MessageType.OPMatchInput)
                {
                    OpponentSelectionInfo osi = sm.GetValueEfficently<OpponentSelectionInfo>();
                    MatchInfo match = MatchService.activeMatches.Last();
                    if (match.opSelectionID == osi.selectionID && !osi.KeepOpenThroughFlaggedInterrupts)
                    {
                        return (Enumerable.Empty<ActionModification>(), null);
                    }
                    match.UpdateMatchInfo(osi);
                    match.opSelectionID = osi.selectionID;

                    return (Enumerable.Empty<ActionModification>(), null);
                }
                else if (sm.messageType == MessageType.MatchInput)
                {
                    PlayerSelectionInfo psi = sm.GetValueEfficently<PlayerSelectionInfo>();
                    MatchInfo match = MatchService.activeMatches.Last();
                    match.sendingMsg = false;
                    match.mySelectionID = psi.playerSelection.selectionID;
                    match.UpdateMatchInfo(psi);

                    PlayerSelection ps = psi.playerSelection;
                    if (ps?.variableSelection is ActionSelection actionSelect && actionSelect.subActionType == SubAction.SubActionType.AddAction)
                    {
                        ICardInfoUpdater ciu = new CardInfoUpdater();
                        ciu.AddActionOutcomesForSelection(ps.originCardEntityID, actionSelect, match, new CardInfoRetriever());
                    }

                    return (Enumerable.Empty<ActionModification>(), psi.playerSelection);
                }
                else if (sm.messageType == MessageType.MatchCatchUp)
                {
                    CatchUpData cud = sm.GetValueEfficently<CatchUpData>();
                    // TODO: ParsePlacementWithCatchUpData
                    Console.WriteLine("\tReceived CatchUpData; NYI");

                    return (Enumerable.Empty<ActionModification>(), null);
                }
                else
                {
                    Console.WriteLine($"\tUnhandled SM: {sm.messageType}");
                    throw new NotImplementedException($"Unhandled SM: {sm.messageType}");
                }
            }
            else if (decodedMessage is GameMessage gm)
            {
                // Console.WriteLine("Sent GM");
                // match.matchState = MatchInfo.MatchState.Processing;
                // match.ClearUsableCards();
                throw new NotImplementedException($"GMs are not implemented");
            }
            else if(decodedMessage is QueryMessage)
            {
                // ignore the dummy query message
                return (Enumerable.Empty<ActionModification>(), null);
            }
            else
            {
                throw new NotImplementedException($"Unknown message type");
            }
        }

        void SendDeckDetailsForBothPlayers(Dictionary<string, int> deck1, Dictionary<string, int> deck2)
        {
            gsc.HandleSupplementalDataMessage(new CustomMessages.SupplementalDataMessageV2
            {
                PlayerId = "player1",
                CurrentRegion = "foo",
                OutfitInformation = new SharedLogicUtils.DataTypes.Outfit(),
                PlayerDisplayName = "P1",
                DeckInformation = CreateTestDeck(deck1)
            }, this.p1connection.Tag!);

            gsc.HandleSupplementalDataMessage(new CustomMessages.SupplementalDataMessageV2
            {
                PlayerId = "player2",
                CurrentRegion = "foo",
                OutfitInformation = new SharedLogicUtils.DataTypes.Outfit(),
                PlayerDisplayName = "P2",
                DeckInformation = CreateTestDeck(deck2)
            }, this.p2connection.Tag!);
        }

        private static CollectionData CreateTestDeck(Dictionary<string, int> decklist) => new SharedLogicUtils.DataTypes.CollectionData()
        {
            items = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(decklist),
            name = "ftue_fall2021_zacian-zamazenta",
            id = Guid.NewGuid().ToString(),
            metadata = ",,,,false,2023-04-19"
        };
    }
}
