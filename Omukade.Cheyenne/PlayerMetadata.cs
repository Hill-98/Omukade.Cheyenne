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

using Omukade.Cheyenne.ClientConnections;
using Omukade.Cheyenne.Miniserver.Controllers;
using ClientNetworking.Models.Inventory;
using SharedLogicUtils.DataTypes;
using SharedSDKUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omukade.Cheyenne
{
    public class PlayerMetadata
    {
        public Outfit? PlayerOutfit;
        public CollectionData? CurrentDeck;

        public IClientConnection PlayerConnectionHelens;
        public string? PlayerDisplayName;
        public string? PlayerId;

        public bool MuteAvatarEmote = false;

        public GameState? CurrentGame;

        // We don't really care what the client's current region is. However, during matchmaking,
        // the client will DC and reconnect if it needs to join a different region, which screws up IMEX..
        // Store the client's expected region so it can be parroted back as needed.
        public string? PlayerCurrentRegion;

        public long DirectMatchMakingTransactionToken;
        public string? DirectMatchMakingToken;
        public string? DirectMatchCurrentlySendingTo;
        public HashSet<string>? DirectMatchCurrentlyReceivingFrom = new HashSet<string>(1);

        public void RemovePlayerFromDirectMatchReceivingFrom(string? playerId)
        {
            if (this.DirectMatchCurrentlyReceivingFrom == null) return;
            if(playerId == null) return;

            if (this.DirectMatchCurrentlyReceivingFrom.Contains(playerId))
            {
                if (this.DirectMatchCurrentlyReceivingFrom.Count == 1) this.DirectMatchCurrentlyReceivingFrom = null;
                else this.DirectMatchCurrentlyReceivingFrom.Remove(playerId);
            }
        }
    }
}
