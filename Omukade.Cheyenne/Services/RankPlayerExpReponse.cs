using Newtonsoft.Json;

namespace Omukade.Cheyenne.Services
{
    public class RankPlayerExpReponse
    {
        [JsonProperty]
        public uint exp { get; set; }

        [JsonProperty]
        public uint highestExp { get; set; }
    }
}
