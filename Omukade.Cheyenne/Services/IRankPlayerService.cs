namespace Omukade.Cheyenne.Services
{
    public interface IRankPlayerService
    {
        public Task<RankPlayerExpReponse> GetPlayerExp(string id);
    }
}