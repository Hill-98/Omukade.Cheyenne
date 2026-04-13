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

//#define BAKE_RNG

using HarmonyLib;
using MatchLogic;
using Omukade.Cheyenne.Model;
using RainierClientSDK;
using SharedSDKUtils;
using Spectre.Console;
using System.Reflection;
using System.Reflection.Emit;
using LogLevel = MatchLogic.RainierServiceLogger.LogLevel;

namespace Omukade.Cheyenne.Patching
{
    [HarmonyPatch(typeof(RainierServiceLogger), nameof(RainierServiceLogger.Log))]
    static class RainierServiceLoggerLogEverything
    {
        public static bool BE_QUIET = true;

        static bool Prefix(string logValue, LogLevel logLevel)
        {
            if (BE_QUIET) return false;

            Logging.WriteDebug(logValue);
            return false;
        }
    }

    [HarmonyPatch(typeof(OfflineAdapter))]
    static class OfflineAdapterHax
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(OfflineAdapter.LogMsg))]
        static bool QuietLogMsg() => false;

        [HarmonyPrefix]
        [HarmonyPatch(nameof(OfflineAdapter.ResolveOperation))]
        static bool ResolveOperationViaCheyenne(ref bool __result, string accountID, MatchOperation currentOperation, GameState state, string messageID)
        {
            GameStateOmukade omuState = (GameStateOmukade)state;
            __result = omuState.parentServerInstance.ResolveOperation(omuState, currentOperation, isInputUpdate: false);
            return false;
        }
    }

    [HarmonyPatch]
    public static class OfflineAdapterUsesOmuSendMessage
    {
        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> TargetMethods() => new string[]
            {
                nameof(OfflineAdapter.ReceiveOperation),
                nameof(OfflineAdapter.CreateOperation),
                nameof(OfflineAdapter.ResolveOperation),
                nameof(OfflineAdapter.LoadBoardState),
            }.Select(name => AccessTools.Method(typeof(OfflineAdapter), name));

        [HarmonyTranspiler]
        [HarmonyPatch]
        static IEnumerable<CodeInstruction> UseOmuStateParentServerInstanceToSendMessages(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            MethodInfo SEND_MESSAGE_SINGLE = AccessTools.Method(typeof(OfflineAdapter), nameof(OfflineAdapter.SendMessage), parameters: new Type[] { typeof(ServerMessage) });
            MethodInfo OMU_SEND_MESSAGE_SINGLE = AccessTools.Method(typeof(OfflineAdapterUsesOmuSendMessage), nameof(OfflineAdapterUsesOmuSendMessage.SendMessage), parameters: new Type[] { typeof(ServerMessage), typeof(GameState) });

            MethodInfo SEND_MESSAGE_MULTIPLE = AccessTools.Method(typeof(OfflineAdapter), nameof(OfflineAdapter.SendMessage), parameters: new Type[] { typeof(List<ServerMessage>) });
            MethodInfo OMU_SEND_MESSAGE_MULTIPLE = AccessTools.Method(typeof(OfflineAdapterUsesOmuSendMessage), nameof(OfflineAdapterUsesOmuSendMessage.SendMessage), parameters: new Type[] { typeof(IEnumerable<ServerMessage>), typeof(GameState) });

            ParameterInfo gameStateParam = __originalMethod.GetParameters().First(param => param.ParameterType == typeof(GameState));
            int indexOfGameStateArg = gameStateParam.Position;

            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(SEND_MESSAGE_SINGLE))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg, indexOfGameStateArg);
                    yield return new CodeInstruction(OpCodes.Call, OMU_SEND_MESSAGE_SINGLE);
                }
                else if (instruction.Calls(SEND_MESSAGE_MULTIPLE))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg, indexOfGameStateArg);
                    yield return new CodeInstruction(OpCodes.Call, OMU_SEND_MESSAGE_MULTIPLE);
                }
                else
                {
                    yield return instruction;
                }
            }
        }

        public static void SendMessage(IEnumerable<ServerMessage> messages, GameState gameState)
        {
            foreach (ServerMessage sm in messages)
            {
                SendMessage(sm, gameState);
            }
        }
        public static void SendMessage(ServerMessage message, GameState gameState)
        {
            if (gameState is not GameStateOmukade gso)
            {
                throw new ArgumentException("GameState must be GameStateOmukade");
            }

            try
            {
                GameServerCore.SendPacketToClient(gso.parentServerInstance.UserMetadata.GetValueOrDefault(message.accountID), message.AsPlayerMessage());
            }
            catch (Exception e)
            {
                gso.parentServerInstance.OnErrorHandler($"SendMessage Error :: {e.GetType().FullName} - {e.Message}", null);
                throw;
            }
        }
    }

    [HarmonyPatch(typeof(GameState), nameof(GameState.CopyState))]
    static class GameStateCopyStateCrashes
    {
        // The information loss mentioned here is PlayerMetadata containing connection information used to send players messages.
        [HarmonyPrefix]
        [HarmonyPatch]
        static void Prefix() => throw new InvalidOperationException("Omukade: Use of GameState.CopyState is not valid and causes information loss. Ensure the caller of this method uses GameStateOmukade instances and isn't creating its own GameState objects.");
    }

    /// <summary>
    /// OfflineAdapter: when a TimeoutForceQuit is sent during an ongoing operation, the message is rejected instead of being forwarded to the ongoing game.
    /// Patch OfflineAdaper's check so it always allows TimeoutForceQuit messsages.
    /// </summary>
    [HarmonyPatch(typeof(OfflineAdapter), nameof(OfflineAdapter.CreateOperation))]
    public static class OfflineAdapter_CreateOperation_AllowsTimeoutMessages
    {
        enum PatchState
        {
            Searching,
            ReplaceLoadEnum,
            ReplaceIfCondition,
            Done,
        }

        [HarmonyPatch, HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> newInstructions = new List<CodeInstruction>();

            PatchState state = PatchState.Searching;
            FieldInfo playerOperationTypeField = typeof(PlayerOperation).GetField(nameof(PlayerOperation.operationType))!;
            MethodInfo isQuitOperationMethod = typeof(OfflineAdapter_CreateOperation_AllowsTimeoutMessages).GetMethod(nameof(IsQuitOperation))!;

            foreach (CodeInstruction instruction in instructions)
            {
                switch(state)
                {
                    case PatchState.Searching:
                        newInstructions.Add(instruction);

                        if(instruction.LoadsField(playerOperationTypeField))
                        {
                            state = PatchState.ReplaceLoadEnum;
                        }
                        break;
                    case PatchState.ReplaceLoadEnum:
                        if(instruction.opcode == OpCodes.Ldc_I4_8)
                        {
                            newInstructions.Add(new CodeInstruction(OpCodes.Call, isQuitOperationMethod));
                            state = PatchState.ReplaceIfCondition;
                        }
                        else
                        {
                            throw new ArgumentException("RuntimeIlPatcher: AllowTimeoutMessages patch failed - method has changed at ReplaceLoadEnum");
                        }

                        break;
                    case PatchState.ReplaceIfCondition:
                        if(instruction.opcode == OpCodes.Beq_S)
                        {
                            newInstructions.Add(new CodeInstruction(OpCodes.Brtrue_S, instruction.operand));
                            state = PatchState.Done;
                        }
                        else
                        {
                            throw new ArgumentException("RuntimeIlPatcher: AllowTimeoutMessages patch failed - method has changed at ReplaceIfCondition");
                        }
                        break;
                    case PatchState.Done:
                        newInstructions.Add(instruction);
                        break;
                }
            }

            if(state != PatchState.Done)
            {
                throw new ArgumentException("RuntimeIlPatcher: AllowTimeoutMessages patch failed - method has changed; reached end-of-method without finishing patch.");
            }

            return newInstructions;
        }

        public static bool IsQuitOperation(OperationType operationType) => operationType == OperationType.Quit || operationType == OperationType.TimeoutForceQuit;
    }

    [HarmonyPatch(typeof(StringConditional), "StringCompare")]
    public static class StringConditional_StringCompare_Fix_BallGuy
    {
        [HarmonyPrefix]
        [HarmonyPatch]
        private static bool Prefix(StringConditional.ConditionalType type, string value1, string value2, ref bool __result)
        {
            if (value1 == "Air Balloon" && value2 == "Ball")
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
