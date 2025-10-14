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

using ClientNetworking;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Omukade.AutoPAR;
using Omukade.Cheyenne.Encoding;
using Omukade.Cheyenne.Miniserver.Controllers;
using Spectre.Console;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

[assembly: InternalsVisibleTo("Omukade.Cheyenne.Tests")]

namespace Omukade.Cheyenne
{
    internal partial class Program
    {
        static internal ConfigSettings config;
        static GameServerCore serverCore;

        static internal void Main(string[] args)
        {
            Console.WriteLine("Omukade Cheyenne");
            Console.WriteLine("(c) 2022 Electrosheep Networks");

            InitAutoPar();

            if(args.Contains("--daemon"))
            {
                config.RunAsDaemon = true;
            }

            CheckForCardUpdates();
            Init();

            app = PrepareWsServer();
            StartWsProcessorThread();
            app.Start();

            Console.WriteLine("Http port: " + config.HttpPort.ToString());

            if (config.RunAsDaemon)
            {
                Console.CancelKeyPress += Console_CancelKeyPress;
                app.WaitForShutdown();
            }
            else
            {
                CmdShell();
            }
        }

        private static void SetFirstChanceExceptionHandler()
        {
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        }

        private static void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            Type type = e.Exception.GetType();
            Exception exception = e.Exception;
            if (!(exception is BadHttpRequestException) && !(exception is ConnectionAbortedException) && !(exception is SocketException) && !(exception is ConnectionResetException))
            {
                WebSocketException ex = exception as WebSocketException;
                if (ex == null || ex.WebSocketErrorCode != WebSocketError.ConnectionClosedPrematurely)
                {
                    StackTrace stackTrace = new StackTrace(e.Exception);
                    StackFrame[] frames = stackTrace.GetFrames();
                    if (frames.Any(delegate (StackFrame frame)
                    {
                        MethodBase method = frame.GetMethod();
                        return ((method != null) ? method.Name : null) == "OnFirstChanceException";
                    }))
                    {
                        Console.WriteLine("An exception has occured in OnFirstChanceException; to prevent further issues, only basic info is available for this exception.");
                        Console.WriteLine(e.GetType().FullName + " : " + e.Exception.Message);
                        if (e.Exception.StackTrace != null)
                        {
                            Console.WriteLine(e.Exception.StackTrace);
                        }
                        return;
                    }
                    AnsiConsole.WriteException(e.Exception, 0);
                    return;
                }
            }
        }

        private static void CheckForCardUpdates()
        {
            if(!config.CardDefinitionFetchOnStart)
            {
                Console.WriteLine("Fetching card definition updates on start is disabled; continuing");
                return;
            }

            Console.WriteLine("Attempting to check for card + rule updates...");
        }

        static internal void InitAutoPar()
        {
            if (File.Exists("config.json"))
            {
                config = JsonConvert.DeserializeObject<ConfigSettings>(File.ReadAllText("config.json"))!;
            }
            else
            {
                Console.WriteLine("Config file not found; loading defaults");
                config = new ConfigSettings();
            }

            // If no search folder defined OR local PTCGL install, try to fetch the current game update and use that.
            string? rainierDirectory;
            if(config.AutoparAutodetectRainier == true)
            {
                rainierDirectory = AutoPAR.InstallationFinder.FindPtcglInstallAssemblyDirectory();

                if(rainierDirectory == null)
                {
                    throw new Exception("Autodetection of Rainier was requested, but could not detect the PTCGL install directory. Cannot start.");
                }

                if(!Directory.Exists(rainierDirectory))
                {
                    throw new Exception($"Autodetection of Rainier was requested, but the installation directory discovered does not exist - [${rainierDirectory}]. Cannot start.");
                }
                else
                {
                    Console.WriteLine($"Autodetected Rainier installation - [{rainierDirectory}]");
                }
            }
            else if(config.AutoparGameInstallOverrideDirectory != null)
            {
                rainierDirectory = config.AutoparGameInstallOverrideDirectory;

                if (!Directory.Exists(rainierDirectory))
                {
                    throw new Exception($"Rainier install location was manually specified, but does not exist - [${rainierDirectory}]. Cannot start.");
                }

                Console.WriteLine($"Rainier installation manually configured as [{rainierDirectory}]");
            }
            else
            {
                rainierDirectory = AutoPAR.Rainier.RainierFetcher.UpdateDirectory;

                Console.WriteLine("Checking for update...");

                AutoPAR.Rainier.UpdaterManifest updateManifest = AutoPAR.Rainier.RainierFetcher.GetUpdateManifestAsync().Result;
                if (AutoPAR.Rainier.RainierFetcher.DoesNeedUpdate(updateManifest))
                {
                    AutoPAR.Rainier.LocalizedReleaseNote releaseNote = AutoPAR.Rainier.RainierFetcher.GetLocalizedReleaseNoteAsync(updateManifest).Result;
                    Console.WriteLine($"Downloading update {releaseNote.Version} ({releaseNote.DateRaw})...");

                    AutoPAR.Rainier.RainierFetcher.DownloadUpdateFile(updateManifest).Wait();
                    AutoPAR.Rainier.RainierFetcher.ExtractUpdateFile(deleteExistingUpdateFolder: true);
                }
                else
                {
                    Console.WriteLine("Current update is latest");
                }
            }

            Console.WriteLine("Injecting AutoPAR...");
            AssemblyLoadInterceptor.ParCore.CecilProcessors.Add(Omukade.AutoPAR.Rainier.RainierSpecificPatches.MakeGameStateCloneVirtual);
            AssemblyLoadInterceptor.Initialize(rainierDirectory);
        }

        static private void Init()
        {
            serverCore = new GameServerCore(config);

            if(config.DebugFixedRngSeed)
            {
                Console.WriteLine("WARNING: The debug setting DebugFixedRngSeed is enabled; all games will use the same RNG seed.");
                Console.WriteLine("This setting should be DISABLED IMMEDIATELY except for testing/debugging.");
                Patching.MatchOperationGetRandomSeedIsDeterministic.InjectRngPatchAtAll = true;
            }

            Console.WriteLine("Patching Rainier...");
            GameServerCore.PatchRainier();

            // Set this after Harmony as it always spits out a bunch of safe "Type must derive from Delegate. (Parameter 'type')" messages that are internally handled.
            SetFirstChanceExceptionHandler();

            Console.WriteLine("Updating Serializers...");
            WhitelistedSerializeContractResolver.ReplaceContractResolvers();

            Console.WriteLine("Preloading/Precompressing Heavy Data...");
            GameServerCore.RefreshSharedGameRules(config);

            serverCore.ErrorHandler += ReportUserError;
        }

        internal static void ReportUserError(string? userMessage, Exception? ex)
        {
            if(userMessage != null)
            {
                Logging.WriteError(userMessage);
            }
            if(ex != null)
            {
                AnsiConsole.WriteException(ex);
            }

            if (config.EnableDiscordErrorWebhook && config.DiscordErrorWebhookUrl != null)
            {
                StringBuilder messageToSend = new StringBuilder();

                messageToSend.Append("Error on ");
                messageToSend.Append(System.Net.Dns.GetHostName());

                if(userMessage != null)
                {
                    messageToSend.Append(" : ");
                    messageToSend.Append(userMessage);
                }
                if (ex != null)
                {
                    messageToSend.AppendLine(" : " + ex.GetType().Name);
                    if (ex.Message != null) messageToSend.AppendLine(ex.Message);
                    WriteInnerExceptionToStringbuider(ex, messageToSend);
                    if (ex.StackTrace != null)
                    {
                        messageToSend.Append("```");
                        messageToSend.Append(ex.StackTrace);
                        messageToSend.Append("```");
                    }
                }


                SendDiscordAlert(messageToSend.ToString(), config.DiscordErrorWebhookUrl);
            }
        }

        /// <summary>
        /// Converts a string to either a GUID (if the string could be parsed as a GUID), or a GUID derived from the MD5 hash of the string. This method is deterministic.
        /// </summary>
        /// <param name="input">A string that either is a string GUID, or an arbitrary string.</param>
        /// <returns>A GUID.</returns>
        private static Guid StringToGuidOrHash(string input)
        {
            if (Guid.TryParse(input, out Guid parsedGuid)) return parsedGuid;

            int byteLen = System.Text.Encoding.UTF8.GetByteCount(input);
            Span<byte> inputBytes = byteLen < 1024 ? stackalloc byte[byteLen] : new byte[byteLen];
            System.Text.Encoding.UTF8.GetBytes(input, inputBytes);

            Span<byte> hashBytes = stackalloc byte[128 / 8];
            MD5.HashData(inputBytes, hashBytes);

            Guid rtrn = new Guid(hashBytes);
            return rtrn;
        }

        internal static void ReportError(Exception ex) => ReportUserError(null, ex);

        static void WriteInnerExceptionToStringbuider(Exception ex, StringBuilder sb)
        {
            if (ex is AggregateException aex && aex.InnerExceptions != null)
            {
                foreach (var innerEx in aex.InnerExceptions)
                {
                    sb.Append("Inner: ");
                    sb.Append(innerEx.GetType().FullName);
                    if (innerEx.Message != null)
                    {
                        sb.Append(" - ");
                        sb.AppendLine(innerEx.Message);
                    }
                    else
                    {
                        sb.AppendLine();
                    }
                }
            }
        }

        static void SendDiscordAlert(string message, string? webhookEndpoint)
        {
            if (!config.EnableDiscordErrorWebhook || webhookEndpoint == null) return;
            string payload = JsonConvert.SerializeObject(new { content = message });
            StringContent content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            HttpClient myClient = new HttpClient();

            HttpResponseMessage httpResponse = myClient.PostAsync(webhookEndpoint, content).Result;
        }
    }
}
