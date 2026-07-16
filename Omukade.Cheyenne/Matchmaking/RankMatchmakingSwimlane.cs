using MatchLogic;
using Omukade.Cheyenne.Services;
using RainierClientSDK.source.Player;
using System.ComponentModel;

namespace Omukade.Cheyenne.Matchmaking
{
    internal class RankMatchmakingSwimlane : IMatchmakingSwimlane
    {
        public GameplayType gameplayType { get; init; }
        public GameMode format { get; init; }

        internal Mutex mut = new();

        internal Dictionary<string, string> lastOpponents = [];

        internal List<PlayerMetadata> queuePlayers = [];

        public MatchmakingCompleteCallback MatchMakingCompleteCallback { get; init; }

        internal IRankPlayerService playerService;

        public RankMatchmakingSwimlane(GameplayType gameplayType, GameMode format, MatchmakingCompleteCallback matchmakingCompleteCallback, IRankPlayerService rankPlayerService)
        {
            this.gameplayType = gameplayType;
            this.format = format;
            MatchMakingCompleteCallback = matchmakingCompleteCallback;
            playerService = rankPlayerService;
        }

        public void EnqueuePlayer(PlayerMetadata playerMetadata)
        {
            foreach (PlayerMetadata player in queuePlayers)
            {
                if (playerMetadata == player || playerMetadata.PlayerId == player.PlayerId)
                {
                    throw new InvalidOperationException("Player is already enqueued for matchmaking");
                }
            }

            Task<RankPlayerExpReponse> task = playerService.GetPlayerExp(playerMetadata.PlayerId!);
            task.Wait();
            if (task.IsCompleted)
            {
                playerMetadata.exp = task.Result.exp;
                playerMetadata.highestExp = task.Result.highestExp;
            }

            playerMetadata.JoinMatchTime = DateTime.Now;
            mut.WaitOne();
            try
            {
                TryMatchPlayers(playerMetadata);
            }
            finally
            {
                mut.ReleaseMutex();
            }
        }

        private void StartMatch(PlayerMetadata player1, PlayerMetadata player2)
        {
            lastOpponents[player1.PlayerId!] = player2.PlayerId!;
            lastOpponents[player2.PlayerId!] = player1.PlayerId!;
            MatchMakingCompleteCallback(this, player1, player2);
        }

        private void TryMatchPlayers()
        {
            TryMatchPlayers(null);
        }

        private void TryMatchPlayers(PlayerMetadata? player)
        {
            if (player != null)
            {
                string opponentId = lastOpponents.GetValueOrDefault(player.PlayerId!, "");
                PlayerMetadata? opponent = queuePlayers.FirstOrDefault(opp => opp.PlayerId != opponentId && Math.Abs(player.exp - opp.exp) <= 200);
                if (opponent != null)
                {
                    queuePlayers.Remove(opponent);
                    StartMatch(opponent, player);
                    return;
                }
                queuePlayers.Add(player);
                queuePlayers = queuePlayers.OrderBy(p => p.exp).ToList();
                return;
            }

            DateTime now = DateTime.Now;
            HashSet<PlayerMetadata> matchedPlayers = [];

            // 等待 10 秒以内的玩家，匹配与自身相差200分以内的对手（可以匹配到于上次相同的对手）
            List<PlayerMetadata> wait10SecondPlayers = [.. queuePlayers.Where(p =>
            {
                double s = (DateTime.Now - p.JoinMatchTime).TotalSeconds;
                return s < 10;
            })];
            for (int i = 0; i < wait10SecondPlayers.Count; i++)
            {
                var p = wait10SecondPlayers[i];
                if (matchedPlayers.Contains(p))
                {
                    continue;
                }
                for (int j = i + 1; j < wait10SecondPlayers.Count; j++)
                {
                    var opponent = wait10SecondPlayers[j];
                    if (matchedPlayers.Contains(opponent))
                    {
                        continue;
                    }
                    if (opponent.exp - p.exp > 200)
                    {
                        break;
                    }
                    matchedPlayers.Add(p);
                    matchedPlayers.Add(opponent);
                    StartMatch(p, opponent);
                    break;
                }
            }
            foreach (var p in matchedPlayers)
            {
                queuePlayers.Remove(p);
            }
            matchedPlayers.Clear();

            // 等待 10-15 秒以内的玩家，匹配与自身相差400分以内的对手（可以匹配到于上次相同的对手）
            List<PlayerMetadata> wait15SecondPlayers = [.. queuePlayers.Where(p =>
            {
                double s = (DateTime.Now - p.JoinMatchTime).TotalSeconds;
                return s >= 10 && s < 15;
            })];
            for (int i = 0; i < wait15SecondPlayers.Count; i++)
            {
                var p = wait15SecondPlayers[i];
                if (matchedPlayers.Contains(p))
                {
                    continue;
                }
                for (int j = i + 1; j < wait15SecondPlayers.Count; j++)
                {
                    var opponent = wait15SecondPlayers[j];
                    if (matchedPlayers.Contains(opponent))
                    {
                        continue;
                    }
                    if (opponent.exp - p.exp > 400)
                    {
                        break;
                    }
                    matchedPlayers.Add(p);
                    matchedPlayers.Add(opponent);
                    StartMatch(p, opponent);
                    break;
                }
            }
            foreach (var p in matchedPlayers)
            {
                queuePlayers.Remove(p);
            }
            matchedPlayers.Clear();

            // 等待 15-20 秒的玩家，匹配与自身相差500分以内以及最高分相差300以内的对手（可以匹配到于上次相同的对手）
            List<PlayerMetadata> wait20SecondPlayers = [.. queuePlayers.Where(p =>
            {
                double s = (DateTime.Now - p.JoinMatchTime).TotalSeconds;
                return s >= 15 && s < 20;
            })];
            for (int i = 0; i < wait20SecondPlayers.Count; i++)
            {
                var p = wait20SecondPlayers[i];
                if (matchedPlayers.Contains(p))
                {
                    continue;
                }
                for (int j = i + 1; j < wait20SecondPlayers.Count; j++)
                {
                    var opponent = wait20SecondPlayers[j];
                    if (matchedPlayers.Contains(opponent))
                    {
                        continue;
                    }
                    if (Math.Abs(opponent.exp - p.exp) <= 500 || Math.Abs(opponent.highestExp - p.highestExp) <= 300)
                    {
                        matchedPlayers.Add(p);
                        matchedPlayers.Add(opponent);
                        StartMatch(p, opponent);
                        break;
                    }
                }
            }
            foreach (var p in matchedPlayers)
            {
                queuePlayers.Remove(p);
            }
            matchedPlayers.Clear();

            // 等待 20 秒以上的玩家，随机匹配。
            List<PlayerMetadata> waitLongSecondPlayers = [.. queuePlayers.Where(p =>
            {
                double s = (DateTime.Now - p.JoinMatchTime).TotalSeconds;
                return s >= 20;
            })];
            for (int i = 0; i < waitLongSecondPlayers.Count; i++)
            {
                var p = waitLongSecondPlayers[i];
                if (matchedPlayers.Contains(p))
                {
                    continue;
                }
                for (int j = i + 1; j < waitLongSecondPlayers.Count; j++)
                {
                    var opponent = waitLongSecondPlayers[j];
                    if (matchedPlayers.Contains(opponent))
                    {
                        continue;
                    }
                    matchedPlayers.Add(p);
                    matchedPlayers.Add(opponent);
                    StartMatch(p, opponent);
                    break;
                }
            }
            foreach (var p in matchedPlayers)
            {
                queuePlayers.Remove(p);
            }
            matchedPlayers.Clear();
        }

        public void RemovePlayerFromMatchmaking(PlayerMetadata playerMetadata)
        {
            bool doPlayersMatch(PlayerMetadata queuedPlayer, PlayerMetadata playerToRemove) => queuedPlayer == playerToRemove || queuedPlayer.PlayerId == playerToRemove.PlayerId;

            mut.WaitOne();
            foreach (PlayerMetadata player in queuePlayers)
            {
                if (doPlayersMatch(player, playerMetadata))
                {
                    queuePlayers.Remove(player);
                    break;
                }
            }
            mut.ReleaseMutex();
        }

        public void Tick()
        {
            mut.WaitOne();
            try
            {
                TryMatchPlayers();
            }
            finally
            {
                mut.ReleaseMutex();
            }
        }
    }
}
