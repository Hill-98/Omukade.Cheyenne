﻿/*************************************************************************
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

using Google.FlatBuffers;
using HarmonyLib;
using ICSharpCode.SharpZipLib.GZip;
using MatchLogic;
using MatchLogic.Utils;
using Newtonsoft.Json;
using Omukade.Cheyenne.Encoding;
using Omukade.Cheyenne.Model;
using ClientNetworking.Models;
using RainierClientSDK;
using RainierClientSDK.source.OfflineMatch;
using SharedLogicUtils.source.Services.Query.Responses;
using SharedSDKUtils;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using static MatchLogic.RainierServiceLogger;
using LogLevel = MatchLogic.RainierServiceLogger.LogLevel;

namespace Omukade.Cheyenne.Patching
{
    [HarmonyPatch(typeof(MatchOperation), nameof(MatchOperation.GetRandomSeed))]
    public static class MatchOperationGetRandomSeedIsDeterministic
    {
        public const int RngSeed = 654654564;

        /// <summary>
        /// Controls if this patch is applied. This has no effect once Harmony has finished patching. Once patched, RNG baking can be controlled using <see cref="UseInjectedRng"/>.
        /// Do not enable this patch unless needed (eg, testing), as there is minor performance impact from the extra method call.
        /// </summary>
        public static bool InjectRngPatchAtAll = false;

        /// <summary>
        /// If <see cref="InjectRngPatchAtAll"/> is set, RNG calls for games will be controlled using this patch instead of using the built-in RNG. The built-in RNG can be enabled/disabled at any time by toggling this field.
        /// </summary>
        public static bool UseInjectedRng = true;

        public static Random Rng = new Random(RngSeed);

        public static void ResetRng() => Rng = new Random(RngSeed);

        static bool Prepare(MethodBase original) => InjectRngPatchAtAll;

        [HarmonyPatch, HarmonyPrefix]
        static bool Prefix(ref int __result)
        {
            if(!UseInjectedRng)
            {
                return true;
            }

            __result = Rng.Next();
            return false;
        }
    }

    [HarmonyPatch(typeof(MatchOperationRandomSeedGenerator), nameof(MatchOperationRandomSeedGenerator.GetRandomSeed))]
    public static class MatchOperationRandomSeedGeneratorIsDeterministic
    {
        static bool Prepare(MethodBase original) => MatchOperationGetRandomSeedIsDeterministic.InjectRngPatchAtAll;

        [HarmonyPatch]
        [HarmonyPrefix]
        static bool Prefix(ref int __result)
        {
            if (!MatchOperationGetRandomSeedIsDeterministic.UseInjectedRng)
            {
                return true;
            }

            __result = MatchOperationGetRandomSeedIsDeterministic.Rng.Next();
            return false;
        }
    }

    [HarmonyPatch(typeof(SystemRandomNumberGenerator))]
    public static class SystemRandomNumberGeneratorIsDeterministic
    {
        static bool Prepare(MethodBase original) => MatchOperationGetRandomSeedIsDeterministic.InjectRngPatchAtAll;

        [HarmonyPatch(MethodType.Constructor, typeof(int))]
        [HarmonyPrefix]
        static bool Prefix(ref Random ____random)
        {
            if (!MatchOperationGetRandomSeedIsDeterministic.UseInjectedRng)
            {
                return true;
            }

            ____random = MatchOperationGetRandomSeedIsDeterministic.Rng;
            return false;
        }
    }

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
                nameof(OfflineAdapter.LoadBoardState)
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

/*  [HarmonyPatch(typeof(OfflineAdapter), nameof(OfflineAdapter.ReceiveOperation))]
    static class ReceiveOperationUsesCopyStateVirtual
    {
        [HarmonyTranspiler]
        [HarmonyPatch]
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo COPY_STATE = AccessTools.Method(typeof(GameState), nameof(GameState.CopyState));
            MethodInfo COPY_STATE_GSO = AccessTools.Method(typeof(GameStateOmukade), nameof(GameState.CopyState));

            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(COPY_STATE))
                {
                    // Replace original copystate with callvirt so derived copystates are used.
                    // I have no idea why callvirt isn't resolving the overriden CopyState in GameStateOmukade.
                    yield return new CodeInstruction(OpCodes.Callvirt, COPY_STATE_GSO);
                }
                else
                {
                    yield return instruction;
                }
            }
        }
    }*/

    [HarmonyPatch(typeof(GameState), nameof(GameState.CopyState))]
    static class GameStateCopyStateCrashes
    {
        // The information loss mentioned here is PlayerMetadata containing connection information used to send players messages.
        [HarmonyPrefix]
        [HarmonyPatch]
        static void Prefix() => throw new InvalidOperationException("Omukade: Use of GameState.CopyState is not valid and causes information loss. Ensure the caller of this method uses GameStateOmukade instances and isn't creating its own GameState objects.");
    }

    [HarmonyPatch(typeof(OfflineAdapter), nameof(OfflineAdapter.ReceiveOperation))]
    public static class ReceiveOperationShowsIlOffsetsInErrors
    {
        [HarmonyTranspiler]
        [HarmonyPatch]
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo EXCEPTION_GET_STACKTRACE = AccessTools.PropertyGetter(typeof(Exception), nameof(Exception.StackTrace));
            MethodInfo ENCHANCED_STACKTRACE = AccessTools.Method(typeof(ReceiveOperationShowsIlOffsetsInErrors), nameof(ReceiveOperationShowsIlOffsetsInErrors.GetStackTraceWithIlOffsets));

            foreach (CodeInstruction instruction in instructions)
            {
                if(instruction.Calls(EXCEPTION_GET_STACKTRACE))
                {
                    // replace stacktrace call with out enchanced stackframe method that returns IL offsets
                    yield return new CodeInstruction(OpCodes.Call, ENCHANCED_STACKTRACE);
                }
                else
                {
                    yield return instruction;
                }
            }
        }

        public static string GetStackTraceWithIlOffsets(Exception ex)
        {
            System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(ex);
            StringBuilder sb = new StringBuilder();
            PrepareStacktraceString(sb, st);
            string preparedStacktrace = sb.ToString();

            Program.ReportError(ex);

            if (System.Diagnostics.Debugger.IsAttached)
            {
                System.Diagnostics.Debugger.Break();
            }

            return preparedStacktrace;
        }

        private static void PrepareStacktraceString(StringBuilder sb, StackTrace st)
        {
            foreach (System.Diagnostics.StackFrame frame in st.GetFrames())
            {
                try
                {
                    MethodBase? frameMethod = frame.GetMethod();
                    if (frameMethod == null)
                    {
                        sb.Append("[null method] - at IL_");
                    }
                    else
                    {
                        string frameClass = frameMethod.DeclaringType?.FullName ?? "(null)";
                        string frameMethodDisplayName = frameMethod.Name;
                        int frameMetadataToken = frameMethod.MetadataToken;
                        sb.Append($"{frameClass}::{frameMethodDisplayName} @{frameMetadataToken:X8} - at IL_");
                    }
                    sb.Append(frame.GetILOffset().ToString("X4"));
                    sb.AppendLine();
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"[error on this frame - {ex.GetType().FullName} - {ex.Message}]");
                }
            }
        }
    }

    [HarmonyPatch]
    static class UseWhitelistedResolverMatchOperation
    {
        static readonly WhitelistedContractResolver resolver = new WhitelistedContractResolver();

        static IEnumerable<MethodBase> TargetMethods() => typeof(MatchOperation)
            .GetConstructors();

        [HarmonyPostfix]
        [HarmonyPatch]
        static void Postfix(MatchOperation __instance)
        {
			SerializeResolver.settings.ContractResolver = UseWhitelistedResolverMatchOperation.resolver;
        }
    }

    [HarmonyPatch]
    static class UseWhitelistedResolverMatchBoard
    {
        static readonly WhitelistedContractResolver resolver = new WhitelistedContractResolver();

        static IEnumerable<MethodBase> TargetMethods() => typeof(MatchBoard)
            .GetConstructors();

        [HarmonyPostfix]
        [HarmonyPatch]
        static void Postfix(MatchBoard __instance)
        {
            SerializeResolver.settings.ContractResolver = UseWhitelistedResolverMatchBoard.resolver;
        }
    }

    [HarmonyPatch]
    static class UseWhitelistedResolverGameState
    {
        static readonly WhitelistedContractResolver resolver = new WhitelistedContractResolver();

        static IEnumerable<MethodBase> TargetMethods() => typeof(GameState)
            .GetConstructors();

        [HarmonyPostfix]
        [HarmonyPatch]
        static void Postfix(GameState __instance)
        {
            __instance.settings.ContractResolver = resolver;
        }
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
