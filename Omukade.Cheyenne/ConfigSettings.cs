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

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omukade.Cheyenne
{
    public class ConfigSettings
    {
        [JsonProperty(PropertyName = "carddata-directory")]
        public string CardDataDirectory = Path.Combine(AutoPAR.Rainier.RainierSharedDataHelper.GetSharedDataDirectory(), "PTCGL-CardDefinitions");

        [JsonProperty(PropertyName = "ws-port")]
        public int HttpPort = 10850;

        [JsonProperty(PropertyName = "ws-secure-port")]
        public int HttpSecurePort = 10851;

        [JsonProperty(PropertyName = "cardsource-overrides-enable")]
        public bool CardSourceOverridesEnable = false;

        [JsonProperty(PropertyName = "cardsource-overrides-directory")]
        public string? CardSourceOverridesDirectory;

        [JsonProperty(PropertyName = "disable-player-order-randomization")]
        public bool DisablePlayerOrderRandomization = false;

        [JsonProperty(PropertyName = "enable-rule-changes-2023-patch")]
        public bool EnableRuleChanges2023Patch = true;

        [JsonProperty(PropertyName = "discord-error-webhook-enable")]
        public bool EnableDiscordErrorWebhook = false;

        [JsonProperty(PropertyName = "discord-error-webhook-url")]
        public string? DiscordErrorWebhookUrl = null;

        [JsonProperty(PropertyName = "enable-reporting-all-implemented-cards")]
        public bool EnableReportingAllImplementedCards = true;

        [JsonProperty(PropertyName = "debug-fixed-rng-seed")]
        public bool DebugFixedRngSeed = false;

        public const string CardDefinitionFetcherJsonPropertyName = "card-definition-fetcher-path";
        [JsonProperty(PropertyName = CardDefinitionFetcherJsonPropertyName)]
        public string? CardDefinitionFetcherPath;

        [JsonProperty(PropertyName = "autopar-autodetect-rainier-install-directory")]
        public bool? AutoparAutodetectRainier = false;

        [JsonProperty(PropertyName = "autopar-search-directory")]
        public string? AutoparGameInstallOverrideDirectory;

        [JsonProperty(PropertyName = "run-as-daemon")]
        public bool RunAsDaemon = false;

        [JsonProperty(PropertyName = "enable-game-timers")]
        public bool EnableGameTimers = false;

        [JsonProperty(PropertyName = "debug-enable-deterministic-decklist-preperation")]
        public bool DebugEnableDeterministicDecklistPreperation = false;

        [JsonProperty(PropertyName = "debug-prizes-per-player")]
        public int? DebugPrizesPerPlayer = null;
    }
}
