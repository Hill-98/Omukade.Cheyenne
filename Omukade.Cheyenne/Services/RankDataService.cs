using Newtonsoft.Json;
using SharedSDKUtils;
using Spectre.Console;

namespace Omukade.Cheyenne.Services
{
    public class RankDataService : IRankPlayerService
    {
        public string Endpoint { get; private set; }
        private readonly HttpClient _httpClient;

        public RankDataService(string endpoint)
        {
            this.Endpoint = endpoint;
            _httpClient = new HttpClient();
        }

        public async Task MatchEnd(GameState state)
        {
            try
            {
                var res = await _httpClient.PostAsync($"{this.Endpoint}/match/end", new StringContent(JsonConvert.SerializeObject(state), System.Text.Encoding.UTF8, "application/json"));
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
            }
        }

        public async Task<RankPlayerExpReponse> GetPlayerExp(string id)
        {
            try
            {
                var res = await _httpClient.GetAsync($"{this.Endpoint}/player/exp/query?id={id}");
                if (res.IsSuccessStatusCode)
                {
                    var body = JsonConvert.DeserializeObject<RankPlayerExpReponse>(await res.Content.ReadAsStringAsync());
                    return body!;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
            }
            return new RankPlayerExpReponse()
            {
                exp = 0,
                highestExp = 0,
            };
        }
    }
}