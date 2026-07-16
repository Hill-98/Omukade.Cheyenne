using MatchLogic;

namespace Omukade.Cheyenne.Matchmaking
{
    internal class BasicMatchmakingSwimlane : IMatchmakingSwimlane
    {
        public static uint GetFormatKey(GameplayType gameplayType, GameMode format)
        {
            return unchecked((uint)gameplayType << 0xffff | (uint)format);
        }

        public GameplayType gameplayType { get; init; }
        public GameMode format { get; init; }

        private Mutex mut = new();

        private Dictionary<string, string> lastOpponents = new Dictionary<string, string>();

        internal Queue<PlayerMetadata> enqueuedPlayers = new Queue<PlayerMetadata>();

        public MatchmakingCompleteCallback MatchMakingCompleteCallback { get; init; }

        public BasicMatchmakingSwimlane(GameplayType gameplayType, GameMode format, MatchmakingCompleteCallback matchmakingCompleteCallback)
        {
            this.gameplayType = gameplayType;
            this.format = format;
            MatchMakingCompleteCallback = matchmakingCompleteCallback;
        }

        public void EnqueuePlayer(PlayerMetadata playerMetadata)
        {
            foreach (PlayerMetadata player in enqueuedPlayers)
            {
                if (playerMetadata == player || playerMetadata.PlayerId == player.PlayerId)
                {
                    throw new InvalidOperationException("Player is already enqueued for matchmaking");
                }
            }

            enqueuedPlayers.Enqueue(playerMetadata);
            playerMetadata.JoinMatchTime = DateTime.Now;
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

        private void TryMatchPlayers()
        {
            if (enqueuedPlayers.Count < 2)
            {
                return;
            }
            PlayerMetadata playerMetadata = enqueuedPlayers.Dequeue();
            PlayerMetadata playerMetadata2 = enqueuedPlayers.Dequeue();
            string p1Id = playerMetadata.PlayerId ?? "x";
            string p2Id = playerMetadata2.PlayerId ?? "x";
            string? player1LastOpponent;
            lastOpponents.TryGetValue(p1Id, out player1LastOpponent);
            double tSec = (DateTime.Now - playerMetadata.JoinMatchTime).TotalSeconds;
            if (player1LastOpponent != p2Id || tSec >= 15 )
            {
                lastOpponents[p1Id] = p2Id;
                lastOpponents[p2Id] = p2Id;
                MatchMakingCompleteCallback(this, playerMetadata, playerMetadata2);
                return;
            }
            enqueuedPlayers.Enqueue(playerMetadata);
            enqueuedPlayers.Enqueue(playerMetadata2);
        }

        public void RemovePlayerFromMatchmaking(PlayerMetadata playerMetadata)
        {
            bool playerIsQueued = false;
            bool doPlayersMatch(PlayerMetadata queuedPlayer, PlayerMetadata playerToRemove) => queuedPlayer == playerToRemove || queuedPlayer.PlayerId == playerToRemove.PlayerId;

            // Determine if the player is in queue. If they're not, don't bother trying to rebuild the queue.
            foreach (PlayerMetadata player in enqueuedPlayers)
            {
                if (doPlayersMatch(player, playerMetadata))
                {
                    playerIsQueued = true;
                    break;
                }
            }

            if (!playerIsQueued) return;

            mut.WaitOne();
            Queue<PlayerMetadata> newQueue = new Queue<PlayerMetadata>();
            foreach (PlayerMetadata player in enqueuedPlayers)
            {
                if (doPlayersMatch(player, playerMetadata))
                {
                    continue;
                }
                else
                {
                    newQueue.Enqueue(player);
                }
            }

            enqueuedPlayers = newQueue;
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
