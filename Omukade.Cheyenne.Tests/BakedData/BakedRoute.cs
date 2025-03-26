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

using ClientNetworking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Route = ClientNetworking.Route;

namespace Omukade.Cheyenne.Tests.BakedData
{
    internal class BakedRoute : Route
    {
        public Uri WebsocketUrl { get; set; }

        public string Region { get; set; }

        public string ServiceGroup { get; set; }


        public Uri ApiUrlBase { get; set; }
        public Uri ApiUrl(string api) => new Uri(ApiUrlBase, api);

        public Uri PrimeRegionApiUrl(string api)
        {
            throw new NotImplementedException();
        }

        public void RotateSimulatedLoadBalancer()
        {
            
        }

        public void RoundRobinWebsocketServicePorts(params string[] ports)
        {
            
        }

        public void SelectWebsocketServicePort(string port)
        {
            
        }

        public void SelectWebsocketServicePort(string port, string suffix)
        {
            
        }

        public void SetServiceGroup(string serviceGroup) => this.ServiceGroup = serviceGroup;
    }
}
